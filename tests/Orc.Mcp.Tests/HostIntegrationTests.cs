using Orc.Game.Targeting;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Hosting;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 宿主集成面测试（R9：UI 在进程内共享服务层；三处仲裁的收敛验证）。
/// </summary>
public class HostIntegrationTests
{
    private const string SampleCardJson = """
{
  "schemaVersion": 1,
  "id": "host.infantry",
  "name": "宿主集成样例兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 0, "attack": 2, "defense": 3 },
    { "component": "tagData", "rarity": "standard" }
  ]
}
""";

    [Fact]
    public async Task SandboxMatch_ExposesUiProjectionProvider()
    {
        using var fixture = new SandboxFixture();

        var created = await fixture.InvokeAsync("sandbox.create");
        Assert.True(created.Ok, created.Message);

        var match = fixture.Service.Executor.Match;
        Assert.NotNull(match);

        // R9a：沙盒局必须装配 UI 插件包，否则投影注册面为空（UI 无法渲染）。
        Assert.True(match!.UiProjections.HasProvider, "沙盒局未装配 orc.builtin.ui —— UI 无法投影。");
    }

    [Fact]
    public async Task MatchChanged_FiresOnCreateAndDispose()
    {
        using var fixture = new SandboxFixture();
        var changes = new List<MatchLifecycleChange>();
        fixture.Service.MatchChanged += (_, args) => changes.Add(args.Change);

        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);
        Assert.True((await fixture.InvokeAsync("sandbox.dispose")).Ok);

        Assert.Equal(new[] { MatchLifecycleChange.Created, MatchLifecycleChange.Disposed }, changes);
        Assert.Null(fixture.Service.ActiveMatch);
    }

    [Fact]
    public async Task TargetingSubscription_AndClaim_AreFirstComeFirstServed()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Authoring);
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        // 写卡 → 注册 → 造到手牌（预打出会发起选位，从而进入待决）。
        File.WriteAllText(
            Path.Combine(fixture.Service.Sandbox.CardsDirectory, "host.infantry.card.json"),
            SampleCardJson);
        Assert.True((await fixture.InvokeAsync("card.load")).Ok);

        var spawned = await fixture.InvokeAsync("sandbox.spawn", """{"definitionId":"host.infantry","owner":0,"zone":"hand"}""");
        Assert.True(spawned.Ok, spawned.Message);
        var cardRef = spawned.EngineResult!["ref"]!.GetValue<string>();

        var received = new List<PendingSelection>();
        using var subscription = fixture.Service.SubscribeTargetingRequests(received.Add);

        // 预打出 → 内部发起部署选位（进入待决，动作挂起）
        var prePlay = await fixture.InvokeAsync("play.unit_pre", $"{{\"ref\":\"{cardRef}\"}}");
        Assert.True(prePlay.Ok, prePlay.Message);
        Assert.Equal(McpCodes.Targeting, prePlay.Code);
        Assert.Single(received);

        var pending = received[0];
        Assert.False(string.IsNullOrWhiteSpace(pending.SessionId));
        Assert.NotEmpty(pending.Candidates);
        // S6：选靶归属座位随请求过线（0 号座位打出的卡）。
        Assert.Equal(0, pending.Seat);

        // 宿主（UI）认领该请求 → agent 侧提交应被拒绝（先到先服务）
        Assert.True(fixture.Service.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out _));

        var agentSubmit = await fixture.InvokeAsync(
            "target.submit",
            $"{{\"sessionId\":\"{pending.SessionId}\",\"picks\":[1]}}");
        Assert.False(agentSubmit.Ok);
        Assert.Equal(McpCodes.InvalidArguments, agentSubmit.Code);

        // 宿主提交 → 动作推进（可能再次待决或完成）
        Assert.True(fixture.Service.TrySubmitTargeting(
            pending.SessionId, new[] { 1 }, false, TargeterBridgeAdapter.ClaimantHost, out _));

        // 收尾：确保对局可销毁（若仍待决则先取消）
        var state = await fixture.InvokeAsync("sandbox.state");
        if (state.Details!["busy"]!.GetValue<bool>())
        {
            var current = fixture.Service.Bridge.CurrentPending;
            if (current is not null)
            {
                fixture.Service.TrySubmitTargeting(
                    current.SessionId, Array.Empty<int>(), true, TargeterBridgeAdapter.ClaimantHost, out _);
            }
        }

        Assert.True((await fixture.InvokeAsync("sandbox.dispose")).Ok);
    }

    [Fact]
    public async Task SegmentConsumer_HostMode_StopsServicePulling()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        // 默认：服务层取段
        Assert.Equal(SegmentConsumer.Service, fixture.Service.SegmentConsumer);
        var pulled = await fixture.InvokeAsync("observe.segments");
        Assert.True(pulled.Ok);

        // 切换为宿主取段：服务层不再自行取段（只读宿主喂入的缓存）
        fixture.Service.SetSegmentConsumer(SegmentConsumer.Host);
        Assert.False(fixture.Service.ShouldServicePullSegments);

        var afterSwitch = await fixture.InvokeAsync("observe.segments");
        Assert.True(afterSwitch.Ok);
        Assert.Equal(0, afterSwitch.Details!["takenNow"]!.GetValue<int>());

        // 宿主喂段后可从服务层读到
        var engineSegments = fixture.Service.Executor.Match!.Engine.TakeSegments();
        fixture.Service.IngestSegments(engineSegments);
        var ingested = fixture.Service.PullSegments();
        Assert.Equal(fixture.Service.Segments.Count, ingested.Count);
    }

    [Fact]
    public async Task StrongTypedActions_ShareGateWithTools()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        var before = fixture.Service.ActiveMatch!.TurnNumber;

        // 强类型 API（UI 侧入口）
        var outcome = await fixture.Service.EndTurnAsync();
        Assert.Equal(ActionOutcome.StatusCompleted, outcome.Status);

        var after = fixture.Service.ActiveMatch!.TurnNumber;
        Assert.True(after >= before);

        // 与工具入口共用同一执行器：忙碌期会被同一门禁拦住
        Assert.False(fixture.Service.Executor.IsBusy);
    }

    [Fact]
    public async Task ToolAndActionFaces_StayInSync()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        // 工具入口：match.end_turn；强类型入口：EndTurnAsync —— 同一实现的两条入口
        var viaTool = await fixture.InvokeAsync("match.end_turn");
        Assert.True(viaTool.Ok);

        var viaAction = await fixture.Service.EndTurnAsync();
        Assert.Equal(ActionOutcome.StatusCompleted, viaAction.Status);
    }

    [Fact]
    public async Task TargetingCancel_IsReportedAsCancelledSuccess()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        File.WriteAllText(
            Path.Combine(fixture.Service.Sandbox.CardsDirectory, "host.infantry.card.json"),
            SampleCardJson);
        Assert.True((await fixture.InvokeAsync("card.load")).Ok);

        var spawned = await fixture.InvokeAsync("sandbox.spawn", """{"definitionId":"host.infantry","owner":0,"zone":"hand"}""");
        var cardRef = spawned.EngineResult!["ref"]!.GetValue<string>();

        var prePlay = await fixture.InvokeAsync("play.unit_pre", $"{{\"ref\":\"{cardRef}\"}}");
        Assert.Equal(McpCodes.Targeting, prePlay.Code);

        var sessionId = prePlay.Details!["pending"]!["sessionId"]!.GetValue<string>();
        var cancelled = await fixture.InvokeAsync("target.cancel", $"{{\"sessionId\":\"{sessionId}\"}}");

        // 取消是合法结局：ok=true + code=cancelled（R5）
        Assert.True(cancelled.Ok, cancelled.Message);
        Assert.Equal(McpCodes.Cancelled, cancelled.Code);
    }
}
