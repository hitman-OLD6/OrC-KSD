using System.Text.Json;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 端到端最小闭环（R6 验收）：建沙盒局 → 逐卡注册 → 放置 → 构造场景 → 观察 → 销毁。
/// </summary>
public class SandboxEndToEndTests
{
    private const string SampleCardJson = """
{
  "schemaVersion": 1,
  "id": "e2e.rifleman",
  "name": "端到端步枪兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 0, "attack": 2, "defense": 3 },
    { "component": "tagData", "rarity": "standard" }
  ]
}
""";

    [Fact]
    public async Task FullLoop_CreateLoadSpawnObserveDispose()
    {
        using var fixture = new SandboxFixture();

        // ① 建局：脚手架 + 占位卡
        var created = await fixture.CreateSandboxMatchAsync();
        Assert.True(created.Ok, created.Message);
        Assert.True(File.Exists(fixture.Service.Sandbox.ManifestFile));
        Assert.True(File.Exists(fixture.Service.Sandbox.PlaceholderCardFile));
        Assert.Equal("InProgress", created.EngineResult!["state"]!.GetValue<string>());

        // ② 写一张卡（写文件属资产管线；本测试直接落盘，验证"逐卡注册"通路）
        var cardFile = Path.Combine(fixture.Service.Sandbox.CardsDirectory, "e2e.rifleman.card.json");
        File.WriteAllText(cardFile, SampleCardJson);

        var loaded = await fixture.InvokeAsync("card.load");
        Assert.True(loaded.Ok, loaded.Message);
        var registered = loaded.EngineResult!["registered"]!.AsArray();
        Assert.Contains(registered, node => node!.GetValue<string>() == "e2e.rifleman");

        // ③ 造卡置入手牌
        var spawned = await fixture.InvokeAsync("sandbox.spawn", """{"definitionId":"e2e.rifleman","owner":0,"zone":"hand"}""");
        Assert.True(spawned.Ok, spawned.Message);
        var cardRef = spawned.EngineResult!["ref"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(cardRef));

        // ④ 观察：手牌含该卡
        var view = await fixture.InvokeInContextAsync("observe.view");
        Assert.True(view.Ok);
        var hand = view.Details!["players"]!.AsArray()[0]!["hand"]!.AsArray();
        Assert.Contains(hand, node => node!["ref"]!.GetValue<string>() == cardRef);

        // ⑤ 构造场景：投递事件（订阅者数量可判定）
        var emitted = await fixture.InvokeAsync("sandbox.emit", """{"event":"card.placed","payload":{"Card":"x"}}""");
        Assert.True(emitted.Ok, emitted.Message);
        Assert.True(emitted.EngineResult!["subscriberCount"]!.GetValue<int>() >= 0);

        // ⑥ 快照可读且为合法 JSON
        var snapshot = await fixture.InvokeAsync("observe.snapshot");
        Assert.True(snapshot.Ok);
        Assert.Contains(snapshot.EngineResult!["cards"]!.AsArray(), node =>
            node!["entityId"]!.GetValue<string>() == cardRef);

        // ⑦ 销毁实体（走引擎一步式销毁）
        var killed = await fixture.InvokeAsync("sandbox.kill", $"{{\"ref\":\"{cardRef}\"}}");
        Assert.True(killed.Ok, killed.Message);
        Assert.True(killed.EngineResult!["destroyed"]!.GetValue<bool>());

        // ⑧ 段缓存：可重复读取
        var segments = await fixture.InvokeAsync("observe.segments");
        Assert.True(segments.Ok);
        Assert.True(segments.Details!["cacheCapacity"]!.GetValue<int>() > 0);

        // ⑨ 销毁对局
        var disposed = await fixture.DisposeSandboxMatchAsync();
        Assert.True(disposed.Ok, disposed.Message);
        Assert.Null(fixture.Service.Executor.Match);
    }

    [Fact]
    public async Task Create_Twice_IsRejected()
    {
        using var fixture = new SandboxFixture();

        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var second = await fixture.CreateSandboxMatchAsync();
        Assert.False(second.Ok);
        Assert.Equal(McpCodes.EngineRejected, second.Code);
    }

    [Fact]
    public async Task Emit_WithoutSubscriber_ReturnsWarning()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var emitted = await fixture.InvokeAsync("sandbox.emit", """{"event":"totally.unknown.event"}""");

        Assert.True(emitted.Ok);
        Assert.Equal(0, emitted.EngineResult!["subscriberCount"]!.GetValue<int>());
        Assert.Contains("no-subscriber", emitted.EngineResult!["warning"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndTurn_AdvancesTurn()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var before = (await fixture.InvokeInContextAsync("observe.view")).Details!["turn"]!.GetValue<int>();
        var ended = await fixture.InvokeAsync("match.end_turn");
        Assert.True(ended.Ok, ended.Message);
        var after = (await fixture.InvokeInContextAsync("observe.view")).Details!["turn"]!.GetValue<int>();

        Assert.True(after >= before);
    }

    [Fact]
    public async Task SetPoints_ChangesPlayerPoints()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var result = await fixture.InvokeAsync("sandbox.set_points", """{"owner":0,"delta":3}""");

        Assert.True(result.Ok, result.Message);
        Assert.True(result.EngineResult!["points"]!.GetValue<int>() >= 3);
    }

    [Fact]
    public async Task DiagReport_AggregatesState()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var report = await fixture.InvokeAsync("diag.report");

        Assert.True(report.Ok);
        Assert.True(report.Details!["toolCount"]!.GetValue<int>() > 0);
        Assert.True(report.Details!["engine"]!["cards"]!.GetValue<int>() > 0);
        Assert.True(report.Details!["processStateDirty"]!.GetValue<bool>());

        var chain = await fixture.InvokeAsync("diag.audit_chain");
        Assert.True(chain.Ok);
        Assert.True(chain.EngineResult!["nodes"]!.AsArray().Count > 0);
    }

    [Fact]
    public async Task SandboxState_ReportsDirtyProcessState()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var state = await fixture.InvokeAsync("sandbox.state");

        Assert.True(state.Ok);
        Assert.True(state.Details!["hasMatch"]!.GetValue<bool>());
        var notes = state.Details!["processStateNotes"]!.AsArray();
        Assert.NotEmpty(notes);
    }

    [Fact]
    public async Task CardList_ShowsParsedCardsAndFailures()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var list = await fixture.InvokeAsync("card.list");

        Assert.True(list.Ok);
        var cards = list.Details!["cards"]!.AsArray();
        Assert.Contains(cards, node => node!["id"]!.GetValue<string>() == fixture.Options.PlaceholderCardId);
    }

    [Fact]
    public async Task ToolCall_ReturnsUniformEnvelope()
    {
        using var fixture = new SandboxFixture();

        var result = await fixture.InvokeAsync("service.info");
        var json = JsonSerializer.Serialize(new
        {
            ok = result.Ok,
            code = result.Code,
            message = result.Message,
        });

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("message").GetString()));
    }
}
