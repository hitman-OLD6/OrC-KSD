using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Orc.Mcp.Room;
using Orc.Server.Rooms;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 无头沙盒进程内用例（S8 用例 A 层）：自动开局、预置座位语义、日志连续性与摘要字段。
/// <para>真进程/CLI 断言在 <see cref="ProcessHeadlessTests"/>；本文件只看引擎与编排状态。</para>
/// </summary>
public class HeadlessTests
{
    // ---------------------------------------------------------------- R2 座位编排

    /// <summary>默认 ai+agent：无外部连接时房间态可查询、AI 已就位、agent 未连接、**不提前开局**。</summary>
    [Fact]
    public async Task AutoStartNotTriggeredWithoutAgent()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);

        await using var probe = await TestSupport.ConnectAsync(host.Port);
        await Task.Delay(400);

        var info = await probe.CallAsync("room.info");
        Assert.True(info.Ok, info.Message);
        Assert.Equal("lobby", info.Details!["state"]!.GetValue<string>());

        var seats = info.Details!["seats"]!.AsArray();
        Assert.Equal("ai", seats[0]!["controllerKind"]!.GetValue<string>());
        Assert.True(seats[0]!["connected"]!.GetValue<bool>());
        Assert.Equal("remote", seats[1]!["controllerKind"]!.GetValue<string>());
        Assert.False(seats[1]!["connected"]!.GetValue<bool>());

        Assert.Null(host.Service.ActiveMatch);
        Assert.True(File.Exists(host.Headless!.AgentTokenPath));
    }

    /// <summary>agent 在未入座前调 <c>room.start</c> 一律 unauthorized（无需也不可得开局权限）。</summary>
    [Fact]
    public async Task AgentStartRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);
        await using var agent = await TestSupport.ConnectAsync(host.Port);

        var token = AgentToken(host);
        var start = await agent.CallAsync("room.start", new JsonObject { ["seatToken"] = token });

        Assert.False(start.Ok);
        Assert.Equal(ResultCodes.Unauthorized, start.Code);
        Assert.Equal("lobby", (await agent.CallAsync("room.info")).Details!["state"]!.GetValue<string>());
    }

    /// <summary>agent <c>room.resume</c> 后由实例侧自动开局；开局后 agent 的 <c>room.start</c> 仍被拒。</summary>
    [Fact]
    public async Task AutoStartOnResume()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);
        await using var agent = await TestSupport.ConnectAsync(host.Port);

        var resume = await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = AgentToken(host) });
        Assert.True(resume.Ok, resume.Message);

        await WaitForStateAsync(host, "in-match");
        Assert.NotNull(host.Service.ActiveMatch);

        var again = await agent.CallAsync("room.start", new JsonObject { ["seatToken"] = AgentToken(host) });
        Assert.False(again.Ok);
    }

    /// <summary>无头模式下房间态断线**不释放座位**（座位生命周期＝进程生命周期，令牌仍有效）。</summary>
    [Fact]
    public async Task LobbyDisconnectKeepsSeat()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);

        var seat = host.Headless!.AgentSeat!.Value;
        var token = AgentToken(host);

        // 无头语义：断线只 MarkDisconnected（不 DropSeat、不销毁 token）。
        host.Room!.MarkDisconnected(seat);

        Assert.NotNull(host.Room.SeatAt(seat));
        Assert.True(host.Room.Resume(token).Ok);

        // 房间仍停在 lobby（未因断线丢掉座位）。
        Assert.Equal(RoomState.Lobby, host.Room.State);
    }

    /// <summary>断线后凭令牌重连：不重开对局（同一 Match 实例、状态仍为 in-match）。</summary>
    [Fact]
    public async Task ReconnectWithToken()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);

        var token = AgentToken(host);
        var first = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await first.CallAsync("room.resume", new JsonObject { ["seatToken"] = token })).Ok);

        await WaitForStateAsync(host, "in-match");
        var match = host.Service.ActiveMatch;

        // 断线（无头语义：座位不释放、token 不销毁）→ 凭同一令牌重连。
        await first.DisposeAsync();

        await using var second = await TestSupport.ConnectAsync(host.Port);
        var resume = await second.CallAsync("room.resume", new JsonObject { ["seatToken"] = token });
        Assert.True(resume.Ok, resume.Message);

        Assert.Same(match, host.Service.ActiveMatch);
        Assert.Equal("in-match", (await second.CallAsync("room.info")).Details!["state"]!.GetValue<string>());
    }

    /// <summary><c>ai+ai</c> 无 agent 座位：预置完成即自动开局。</summary>
    [Fact]
    public async Task AiAiStarts()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path, SeatRole.Ai, SeatRole.Ai);

        await WaitForStateAsync(host, "in-match");
        Assert.NotNull(host.Service.ActiveMatch);
        Assert.Null(host.Headless!.AgentSeat);
    }

    /// <summary>
    /// <c>ai+ai</c> 跑到终局：摘要落盘、退出码 <c>0</c>、停止请求已发出（R2「ai+ai 可开局」＋ R7 终局自退的
    /// 无 agent 路径）。
    /// <para>
    /// 守卫点：终局发现点会顺手停掉 AI 驱动，触发终局的那个 AI work item 正持着该驱动令牌——
    /// 收尾若沿用该令牌写盘会抛取消异常，摘要永久缺失且进程挂住（本用例即该缺陷的回归守卫）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AiAiReachesEndAndWritesSummary()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path, SeatRole.Ai, SeatRole.Ai, seed: 7);

        await WaitForStateAsync(host, "in-match");

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(host.Headless!.SummaryPath)),
            exists => exists,
            TimeSpan.FromSeconds(120),
            throwOnTimeout: true);

        var summary = JsonNode.Parse(File.ReadAllText(host.Headless!.SummaryPath))!;
        Assert.False(summary["aborted"]!.GetValue<bool>());
        Assert.NotNull(summary["winner"]);
        Assert.False(string.IsNullOrWhiteSpace(summary["endReason"]!.GetValue<string>()));
        Assert.True(summary["turns"]!.GetValue<int>() >= 1);

        // 终局后默认 --exit-on-end：停止请求已发出且退出码为 0。
        await host.Headless.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, host.Headless.ExitCode);
        Assert.Equal(RoomState.Ended, host.Room!.State);
    }

    // ---------------------------------------------------------------- R4 日志

    /// <summary>用例 A：跑到终局 → <c>events.jsonl</c> 的 sequence 从 0 连续、逐行可解析。</summary>
    [Fact]
    public async Task EventsSequenceContiguous()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);
        await PlayAndConcedeAsync(host);

        var lines = TestSupport.ReadAllLinesShared(host.Headless!.EventsPath);
        Assert.NotEmpty(lines);

        // 引擎段序号自 1 起（`++_segmentSequence`），承诺是"单调且逐段不丢"，不是"从 0 起"。
        var expected = 1L;
        foreach (var line in lines)
        {
            var segment = JsonNode.Parse(line)!;
            Assert.Equal(expected, segment["sequence"]!.GetValue<long>());
            Assert.NotNull(segment["entries"]);
            expected++;
        }
    }

    /// <summary>脱敏流：对手手牌写 <c>$hidden</c>，且与全知流同序号对齐。</summary>
    [Fact]
    public async Task SeatViewHidden()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path, logSeatViews: true);
        await PlayAndConcedeAsync(host);

        var seat0Path = Path.Combine(host.Headless!.RunDir, "seat0.jsonl");
        var seat1Path = Path.Combine(host.Headless.RunDir, "seat1.jsonl");
        Assert.True(File.Exists(seat0Path));
        Assert.True(File.Exists(seat1Path));

        var seat0 = TestSupport.ReadAllLinesShared(seat0Path);
        var full = TestSupport.ReadAllLinesShared(host.Headless.EventsPath);
        Assert.Equal(full.Length, seat0.Length);
        Assert.Contains("$hidden", string.Join('\n', seat0), StringComparison.Ordinal);
    }

    /// <summary>日志路径可由 <c>--run-dir</c>（此处为选项）覆盖。</summary>
    [Fact]
    public async Task RunDirOverride()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        var runDir = Path.Combine(temp.Path, "custom-run");
        await using var host = await StartAsync(temp.Path, runDir: runDir);

        Assert.Equal(Path.GetFullPath(runDir), Path.GetFullPath(host.Headless!.RunDir));
        Assert.True(File.Exists(host.Headless.EventsPath));
        Assert.True(File.Exists(host.Headless.CallsPath));
    }

    /// <summary><c>calls.jsonl</c> 可还原"谁在何时调了什么、结果如何"，含门禁拒绝。</summary>
    [Fact]
    public async Task CallsJsonlIncludesGateDenials()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);
        await using var bystander = await TestSupport.ConnectAsync(host.Port);

        var denied = await bystander.CallAsync("match.end_turn");
        Assert.Equal(ResultCodes.Unauthorized, denied.Code);

        var calls = ReadJsonLines(host.Headless!.CallsPath);
        var gate = calls.Single(call => call["tool"]!.GetValue<string>() == "match.end_turn");
        Assert.Equal("gate", gate["phase"]!.GetValue<string>());
        Assert.False(gate["ok"]!.GetValue<bool>());
        Assert.Equal("unauthorized", gate["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(gate["sessionId"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(gate["remote"]!.GetValue<string>()));
        Assert.Null(gate["seat"]);
    }

    /// <summary>
    /// 整局 e2e（用例 B 的进程内对应）：假 agent 占座 → 自动开局 → AI 推进至少一个完整回合并结束回合 →
    /// agent 认输 → 事件流/调用流水/摘要齐备。
    /// </summary>
    [Fact]
    public async Task FullGameWithFakeAgent()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path);

        var seat = host.Headless!.AgentSeat!.Value;
        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = AgentToken(host) })).Ok);

        await WaitForStateAsync(host, "in-match");

        // 推进若干回合：等 AI 座位完成它那一轮（软等待：AI 卡住也不阻塞用例收尾）。
        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await agent.CallAsync("observe.view");
                if (!view.Ok)
                {
                    return false;
                }

                var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
                if (phase == "mulligan")
                {
                    await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                    return false;
                }

                return host.Service.ActiveMatch is { TurnNumber: >= 2 };
            },
            ready => ready,
            TimeSpan.FromSeconds(20));

        var concede = await agent.CallAsync("match.concede", new JsonObject { ["owner"] = seat });
        Assert.True(concede.Ok, concede.Message);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(host.Headless.SummaryPath)),
            exists => exists,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        Assert.True(TestSupport.ReadAllLinesShared(host.Headless.EventsPath).Length > 0);
        Assert.True(TestSupport.ReadAllLinesShared(host.Headless.CallsPath).Length > 0);
        Assert.True(JsonNode.Parse(File.ReadAllText(host.Headless.SummaryPath))!["turns"]!.GetValue<int>() >= 1);
    }

    // ---------------------------------------------------------------- R7 摘要

    /// <summary>终局摘要含：胜负、回合数、终局原因、种子、卡包指纹、日志路径、错误计数、卡组指纹。</summary>
    [Fact]
    public async Task SummaryFields()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await StartAsync(temp.Path, seed: 12345);
        await PlayAndConcedeAsync(host);

        var summary = JsonNode.Parse(File.ReadAllText(host.Headless!.SummaryPath))!;

        Assert.False(summary["aborted"]!.GetValue<bool>());
        Assert.Equal(0, summary["winner"]!.GetValue<int>());           // AI 座位（agent 认输）
        Assert.True(summary["turns"]!.GetValue<int>() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(summary["endReason"]!.GetValue<string>()));
        Assert.Equal(12345, summary["seed"]!.GetValue<int>());          // 引擎实际使用的种子
        Assert.False(string.IsNullOrWhiteSpace(summary["packFingerprint"]!.GetValue<string>()));
        Assert.Equal(host.Headless.EventsPath, summary["eventsPath"]!.GetValue<string>());
        Assert.NotNull(summary["errorCount"]);
        Assert.Equal(2, summary["decks"]!.AsArray().Count);
        Assert.True(summary["decks"]![0]!["count"]!.GetValue<int>() > 0);
        Assert.False(string.IsNullOrWhiteSpace(summary["decks"]![0]!["fingerprint"]!.GetValue<string>()));
    }

    // ---------------------------------------------------------------- helpers

    internal static async Task<ServerHost> StartAsync(
        string sandboxRoot,
        SeatRole seat0 = SeatRole.Ai,
        SeatRole seat1 = SeatRole.Agent,
        ServerMode mode = ServerMode.Play,
        bool allowDebugWrites = false,
        int? seed = 1,
        string? runDir = null,
        bool logSeatViews = false,
        TimeSpan? waitAgent = null)
        => await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            Headless = true,
            Mode = mode,
            RunDir = runDir ?? Path.Combine(sandboxRoot, "run"),
            SeatRoles = [seat0, seat1],
            Seed = seed,
            DeckRules = Orc.Game.Decks.DeckRuleTier.Lenient,
            WaitAgent = waitAgent ?? TimeSpan.FromSeconds(30),
            LogSeatViews = logSeatViews,
            AllowDebugWrites = allowDebugWrites,
        });

    internal static string AgentToken(ServerHost host)
        => File.ReadAllText(host.Headless!.AgentTokenPath!).Trim();

    /// <summary>agent 入座 → 双方换牌完成进入 play → agent 认输 → 等待摘要落盘。</summary>
    internal static async Task PlayAndConcedeAsync(ServerHost host)
    {
        var seat = host.Headless!.AgentSeat!.Value;
        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = AgentToken(host) })).Ok);

        await WaitForStateAsync(host, "in-match");

        // 双方换牌确认（AI 座由驱动循环自行确认；agent 座由本用例代确认）后进入 play。
        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await agent.CallAsync("observe.view");
                if (!view.Ok)
                {
                    return false;
                }

                var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
                if (phase == "mulligan")
                {
                    await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                    return false;
                }

                return phase == "play";
            },
            ready => ready,
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);

        var concede = await agent.CallAsync("match.concede", new JsonObject { ["owner"] = seat });
        Assert.True(concede.Ok, $"{concede.Code}: {concede.Message}");

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(host.Headless.SummaryPath)),
            exists => exists,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(host.Service.Executor.Match?.State),
            state => state == Orc.Game.MatchState.Ended,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);
    }

    internal static async Task WaitForStateAsync(ServerHost host, string expected)
        => await TestSupport.WaitUntilAsync(
            async () =>
            {
                await using var probe = await TestSupport.ConnectAsync(host.Port);
                return (await probe.CallAsync("room.info")).Details!["state"]!.GetValue<string>();
            },
            state => state == expected,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

    internal static async Task WaitForPhaseAsync(OrcClient client, string expected)
        => await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await client.CallAsync("observe.view");
                return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
            },
            phase => phase == expected,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

    internal static List<JsonNode> ReadJsonLines(string path)
    {
        var items = new List<JsonNode>();
        foreach (var line in TestSupport.ReadAllLinesShared(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                items.Add(JsonNode.Parse(line)!);
            }
        }

        return items;
    }
}
