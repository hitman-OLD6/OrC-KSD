using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 双运行模式与调试能力边界（S3）：注册面（默认最小）＋ 可见面（按会话）＋ 调用侧第二道拒绝。
/// <para>全部走真 socket 与完整启动链路；断言只看工具清单与 <c>ok/code</c>。</para>
/// </summary>
public class ModeTests
{
    /// <summary>play 下调试类工具不在清单里，调用被拒（未登记＝默认拒绝）。</summary>
    [Fact]
    public async Task PlayHidesDebugTools()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Play);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var tools = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("observe.snapshot", tools);
        Assert.DoesNotContain("observe.segments", tools);
        Assert.DoesNotContain("diag.report", tools);
        Assert.DoesNotContain("card.write", tools);
        Assert.DoesNotContain("sandbox.emit", tools);
        Assert.Contains("observe.view", tools);

        // 未注册＝服务层 unknown-tool（门禁之前就不存在），同样是"被拒"。
        var snapshot = await client.CallAsync("observe.snapshot");
        Assert.False(snapshot.Ok);
        Assert.Equal(ResultCodes.UnknownTool, snapshot.Code);
    }

    /// <summary>play 模式不存在任何沙盒原语工具（既有语义不变：工具面零新增）。</summary>
    [Fact]
    public async Task PlayMode_ToolsList_ContainsNoPrimitiveTools()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Play);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var tools = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var primitive in new[]
                 {
                     "sandbox.instantiate", "sandbox.unitize", "sandbox.move",
                     "sandbox.remove", "sandbox.points", "sandbox.draw",
                 })
        {
            Assert.DoesNotContain(primitive, tools);
        }

        // 未注册＝服务层 unknown-tool（门禁之前就不存在）。
        var rejected = await client.CallAsync("sandbox.points", new JsonObject { ["owner"] = 0, ["points"] = 3 });
        Assert.False(rejected.Ok);
        Assert.Equal(ResultCodes.UnknownTool, rejected.Code);
    }

    /// <summary>sandbox 默认：读类对未绑定/已绑定会话都可见；写类根本不注册。</summary>
    [Fact]
    public async Task SandboxReadOnlyByDefault()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Sandbox);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var unbound = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("observe.snapshot", unbound);
        Assert.Contains("observe.segments", unbound);
        Assert.Contains("diag.report", unbound);
        Assert.Contains("sandbox.state", unbound);
        Assert.DoesNotContain("card.write", unbound);
        Assert.DoesNotContain("sandbox.emit", unbound);
        Assert.DoesNotContain("editor.session_begin", unbound);

        // 已绑定座位的会话仍能看到读类（读类对所有连接可见）。
        var token = TestSupport.SeatToken(await client.CallAsync("room.claim", TestSupport.Claim(0, "remote")));
        Assert.False(string.IsNullOrWhiteSpace(token));
        var bound = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("observe.snapshot", bound);
        Assert.DoesNotContain("card.write", bound);
    }

    /// <summary>
    /// <c>--allow-debug-writes</c>：未绑定编排连接可见可用；已绑定座位的会话**不可见**且调用被拒。
    /// </summary>
    [Fact]
    public async Task DebugWritesOrchestratorOnly()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Sandbox, allowDebugWrites: true);

        await using var orchestrator = await TestSupport.ConnectAsync(host.Port);
        var unbound = (await orchestrator.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("card.write", unbound);
        Assert.Contains("sandbox.emit", unbound);

        // 未绑定编排连接：通过门禁（业务失败不等于 unauthorized）。
        var write = await orchestrator.CallAsync("card.write", new JsonObject
        {
            ["path"] = "cards/none.card.json",
            ["content"] = "{}",
        });
        Assert.NotEqual(ResultCodes.Unauthorized, write.Code);

        await using var agent = await TestSupport.ConnectAsync(host.Port);
        TestSupport.SeatToken(await agent.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var bound = (await agent.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("card.write", bound);
        Assert.DoesNotContain("sandbox.emit", bound);
        Assert.Contains("observe.snapshot", bound);

        var denied = await agent.CallAsync("card.write", new JsonObject
        {
            ["path"] = "cards/none.card.json",
            ["content"] = "{}",
        });
        Assert.False(denied.Ok);
        Assert.Equal(ResultCodes.Unauthorized, denied.Code);
    }

    /// <summary>未绑定 <c>observe.view</c>：play 拒绝（隐藏信息）；sandbox 放行且全知。</summary>
    [Fact]
    public async Task ObserveViewUnboundPlayRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Play);
        await StartMatchAsync(host);

        await using var bystander = await TestSupport.ConnectAsync(host.Port);
        var view = await bystander.CallAsync("observe.view", new JsonObject { ["seat"] = 1 });

        Assert.False(view.Ok);
        Assert.Equal(ResultCodes.Unauthorized, view.Code);
    }

    /// <summary>sandbox 下未绑定会话读视图放行且全知（可读任意座位摘要）。</summary>
    [Fact]
    public async Task ObserveViewUnboundSandboxOmniscient()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await StartAsync(temp.Path, ServerMode.Sandbox);
        await StartMatchAsync(host);

        await using var bystander = await TestSupport.ConnectAsync(host.Port);
        var view = await bystander.CallAsync("observe.view");

        Assert.True(view.Ok, view.Message);
        Assert.Equal(2, view.Details!["players"]!.AsArray().Count);
        Assert.NotNull(view.Details!["players"]![0]!["hand"]);
    }

    /// <summary>sandbox ＋ 写面开启：对局进行中注册新卡，并可从 <c>card.list</c> 观察到。</summary>
    [Fact]
    public async Task SandboxHotCardRegistration()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        var cardsDir = Path.Combine(temp.Path, "pack", "cards");
        Directory.CreateDirectory(cardsDir);
        File.WriteAllText(Path.Combine(cardsDir, "hot.card.json"), HotCardJson);

        await using var host = await StartAsync(temp.Path, ServerMode.Sandbox, allowDebugWrites: true);
        await StartMatchAsync(host);

        await using var orchestrator = await TestSupport.ConnectAsync(host.Port);
        var load = await orchestrator.CallAsync("card.load");
        Assert.True(load.Ok, $"{load.Code}: {load.Message}");
        Assert.Contains(
            "orc.hotcard",
            load.Details!["registered"]!.AsArray().Select(node => node!.GetValue<string>()));

        var list = await orchestrator.CallAsync("card.list");
        Assert.True(list.Ok, list.Message);
        Assert.Contains(
            "orc.hotcard",
            list.Details!["registeredInMatch"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    /// <summary>两种模式共用同一套房间/座位/日志机制（无头 sandbox 同样产出 logs 与房间态）。</summary>
    [Fact]
    public async Task SharedRoomAndLogPath()
    {
        using var temp = new TestSupport.TempDirectory("orc-mode");
        await using var host = await HeadlessTests.StartAsync(temp.Path, mode: ServerMode.Sandbox);

        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = HeadlessTests.AgentToken(host) })).Ok);
        await HeadlessTests.WaitForStateAsync(host, "in-match");

        Assert.True(File.Exists(host.Headless!.EventsPath));
        Assert.True(File.Exists(host.Headless.CallsPath));
        Assert.Equal("in-match", (await agent.CallAsync("room.info")).Details!["state"]!.GetValue<string>());
        Assert.Contains("observe.snapshot", (await agent.ListToolsAsync()).Select(t => t.Name));
    }

    // ---------------------------------------------------------------- helpers

    private const string HotCardJson = """
        {
          "schemaVersion": 1,
          "id": "orc.hotcard",
          "name": "运行期注册卡",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """;

    private static Task<ServerHost> StartAsync(
        string sandboxRoot,
        ServerMode mode,
        bool allowDebugWrites = false)
        => ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            Mode = mode,
            EnforceDeckRules = false,
            AllowDebugWrites = allowDebugWrites,
        });

    /// <summary>房间态填满两座位并开局（座位 0＝回环房主，座位 1＝AI）。</summary>
    private static async Task StartMatchAsync(ServerHost host)
    {
        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        var token = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));

        var ai = await hostClient.CallAsync("room.add_ai", new JsonObject
        {
            ["seat"] = 1,
            ["deck"] = TestSupport.Deck(),
        });
        Assert.True(ai.Ok, ai.Message);

        var start = await hostClient.CallAsync("room.start", new JsonObject { ["seatToken"] = token });
        Assert.True(start.Ok, start.Message);

        // 换牌确认后进入 play（卡注册/全知摘要都要求"进行"态）。
        // 注意：Authoring 的 `observe.view` 在换牌相位读取"当前行动方"会抛（既有缺陷），
        // 因此这里先盲确认换牌，再以"视图可读且 phase=play"作为完成判据。
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
            var view = await hostClient.CallAsync("observe.view");
            if (view.Ok)
            {
                var phase = view.Details!["view"]?["phase"]?.GetValue<string>()
                    ?? view.Details!["phase"]?.GetValue<string>();
                // Authoring 摘要的 phase 是枚举名（"Play"），Player 视图是小写（"play"）。
                if (string.Equals(phase, "play", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(200);
        }

        Assert.Fail("开局后未在 15 秒内进入 play 相位。");
    }
}
