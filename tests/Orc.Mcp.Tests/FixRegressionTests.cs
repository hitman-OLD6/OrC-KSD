using System.Text.Json.Nodes;
using Orc.Game.Ai;
using Orc.Mcp;
using Orc.Mcp.Bridge;
using Orc.Mcp.Contracts;
using Orc.Mcp.Execution;
using Orc.Mcp.Match;
using Orc.Protocol;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 无头对战问题修复的回归断言（R3/R4/R5/R8）：预览与引擎候选同源、动作返回的 state 不落后、
/// <c>seat.board</c> 暴露武装状态、待决请求声明动作类别。
/// </summary>
public class FixRegressionTests
{
    private const string InfantryJson = """
{
  "schemaVersion": 1,
  "id": "fix.infantry",
  "name": "回归步兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
    { "component": "tagData", "rarity": "standard" }
  ]
}
""";

    // ---------------------------------------------------------------- R3

    /// <summary>
    /// R3：<c>attackPreviews</c> 与 <c>command.attack</c> 的待决候选**逐一对应**（同一份引擎候选）。
    /// 场景＝己方前线单位 vs 敌方支援线单位：旧实现只给「敌前线单位 / 空前线才给 HQ」，会把这条合法目标漏掉。
    /// </summary>
    [Fact]
    public async Task AttackPreviews_MatchEngineCandidates()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: null, agentSeat: 0);
        await CreateMatchAsync(fixture, seatACard: "ai.sample.artillery", seatBCard: "ai.sample.infantry");

        // 回合 1（座位 0、1 点，不够火炮 2 费）→ 直接过。
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);

        // 回合 2（座位 1）：敌方支援线单位入场。
        var defender = await DeployFirstAsync(fixture, seat: 1, cardId: "ai.sample.infantry");

        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);

        // 回合 3（座位 0、2 点）：火炮入场（火炮可跨线攻击，故无需推进到前线）。
        var attacker = await DeployFirstAsync(fixture, seat: 0, cardId: "ai.sample.artillery");

        // 回合 4（座位 1）→ 回合 5（座位 0）：入场回合不可行动，须等到下个己方回合。
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);

        // 预览（观察面）——投影只在消费者上下文内可用。
        JsonArray previews;
        using (ConsumerContext.Enter())
        {
            var view = await fixture.InvokeAsync("observe.view");
            Assert.True(view.Ok, view.Message);
            previews = view.Details!["actions"]!["attackPreviews"]!.AsArray();
        }

        var previewDefenders = previews
            .Where(node => node!["attackerUid"]!.GetValue<string>() == attacker)
            .Select(node => node!["defenderUid"]!.GetValue<string>())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        // 敌支援线单位可被攻击时应出现在预览中（旧的「只列敌前线」规则会漏掉它）。
        Assert.Contains(defender, previewDefenders);

        // 同一状态下的引擎候选必须与预览逐一对应。
        var attack = await fixture.InvokeAsync("command.attack", $"{{\"ref\":\"{attacker}\"}}");
        var attackPending = attack.Details!["pending"]!;
        Assert.Equal("attack", attackPending["action"]!.GetValue<string>());

        var engineTargets = attackPending["candidates"]!.AsArray()
            .Select(node => node!["ref"]!.GetValue<string>())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(engineTargets, previewDefenders);

        // 收尾：取消待决，避免影响夹具释放。
        await fixture.InvokeAsync(
            "target.cancel", $"{{\"sessionId\":\"{attackPending["sessionId"]!.GetValue<string>()}\"}}");
    }

    // ---------------------------------------------------------------- R8 / R2

    /// <summary>
    /// R2 附带修复：待决期间 <c>room.resume</c> 必须放行。
    /// 端到端实测的死锁：桥在待决期间断开后重连，<c>room.resume</c> 被「挂起期门禁」拒成
    /// <c>pending-targeting</c>；而应答待决又必须先绑定座位 ⇒ 谁也进不来。
    /// 放行是安全的：绑定座位不改对局状态，绑定后会把该座位的待决请求重推一次。
    /// </summary>
    [Fact]
    public async Task RoomResume_Is_Not_Blocked_While_Targeting()
    {
        using var fixture = new SandboxFixture(
            McpServiceMode.Player, room: new FakeRoomControl(), agentSeat: 0);
        await CreateMatchAsync(fixture, seatACard: "ai.sample.artillery", seatBCard: "ai.sample.infantry");

        // 走到「座位 0 可攻击座位 1 单位」的局面（步骤与 R3 用例同源）。
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        await DeployFirstAsync(fixture, seat: 1, cardId: "ai.sample.infantry");
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        var attacker = await DeployFirstAsync(fixture, seat: 0, cardId: "ai.sample.artillery");
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);

        var attack = await fixture.InvokeAsync("command.attack", $"{{\"ref\":\"{attacker}\"}}");
        Assert.Equal(McpCodes.Targeting, attack.Code);
        Assert.True(fixture.Service.Executor.IsBusy); // 确实处于挂起期

        var resume = await fixture.InvokeAsync("room.resume", "{\"seatToken\":\"dummy\"}");
        Assert.True(resume.Ok, $"待决期间 room.resume 被拒：{resume.Code} / {resume.Message}");
        Assert.Equal("resume", resume.Details!["kind"]!.GetValue<string>());

        await fixture.InvokeAsync(
            "target.cancel", $"{{\"sessionId\":\"{attack.Details!["pending"]!["sessionId"]!.GetValue<string>()}\"}}");
    }

    /// <summary>
    /// R2 附带修复：AI 网关的归属校验方向。
    /// <c>AgentSeat</c> 是 **agent 占用的座位**，AI 驱动其余座位；判据必须是「待决属于 AgentSeat 才拒绝」。
    /// 反向判定（不属于 AgentSeat 就拒绝）会把 AI 自己发起的选靶一并拒掉 → 对局死锁在 AI 回合（端到端实测）。
    /// </summary>
    [Fact]
    public async Task AiGateway_AnswersRequestsOwnedByAiSeat()
    {
        using var fixture = new SandboxFixture(
            McpServiceMode.Player, room: null, agentSeat: 1); // agent 占 1 → AI 驱动 0

        await CreateMatchAsync(fixture, seatACard: "ai.sample.artillery", seatBCard: "ai.sample.infantry");
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        await DeployFirstAsync(fixture, seat: 1, cardId: "ai.sample.infantry");
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        var attacker = await DeployFirstAsync(fixture, seat: 0, cardId: "ai.sample.artillery");
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);
        Assert.True((await fixture.InvokeAsync("match.end_turn")).Ok);

        var gateway = (IAiMatchGateway)fixture.Service;
        var begin = await gateway.BeginAttackAsync(attacker, default);
        Assert.False(begin.Failed, begin.FailureMessage);
        Assert.NotNull(begin.Pending);

        // AI 应答自己座位（0）的选靶：必须放行（旧实现按「≠ AgentSeat」拒绝，正是死锁现场）。
        var answer = await gateway.SubmitAsync(
            begin.Pending!.SessionId, new[] { 1 }, cancel: false, default, claimant: "ai");
        Assert.False(answer.Failed, answer.FailureMessage);
    }

    /// <summary>
    /// R8/R2：部署选位请求声明 <c>action == deploy</c>，且归属座位＝发起方（旧实现 Seat 恒 null → 广播给所有人）。
    /// </summary>
    [Fact]
    public async Task DeployTargeting_ReportsActionAndSeat()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Authoring);
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        var received = new List<PendingSelection>();
        using var subscription = fixture.Service.SubscribeTargetingRequests(received.Add);

        var card = await SpawnAsync(fixture, 0, "hand");
        var prePlay = await fixture.InvokeAsync("play.unit_pre", $"{{\"ref\":\"{card}\"}}");

        Assert.Equal(McpCodes.Targeting, prePlay.Code);
        var pending = Assert.Single(received);
        Assert.Equal("deploy", pending.Action);
        Assert.Equal(0, pending.Seat);

        // 发布面同样带 action（agent 读的是 details.pending）。
        Assert.Equal("deploy", prePlay.Details!["pending"]!["action"]!.GetValue<string>());
    }

    /// <summary>R8：未声明类别的待决请求呈现为 <c>unknown</c>（不伪造类别）。</summary>
    [Fact]
    public void PendingJson_DefaultsActionToUnknown()
    {
        var json = McpService.PendingJson(new PendingSelection(
            "s-1", "fieldUnit", "Click", 1, 1, Array.Empty<PendingCandidate>(), Array.Empty<string>(), Seat: 0));

        Assert.NotNull(json);
        Assert.Equal("unknown", json!["action"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- R5

    /// <summary>R5：<c>seat.board</c> 暴露 <c>isCounterArmed</c>、<c>handCount</c>、<c>armedCounterCount</c>，手牌序号为 <c>handIndex</c>。</summary>
    [Fact]
    public async Task Board_ExposesArmedCounterState()
    {
        var transport = new CannedTransport
        {
            View = BoardView(armed: true, counterCount: 1, handCount: 2),
        };

        var (server, output) = await RunAsync(
            new BridgeServer(transport), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"seat.board","arguments":{}}}""");

        Assert.Equal(0, server.ExitCode);
        var board = Envelope(ParseLines(output)[0])["result"]!["details"]!;

        Assert.Equal(2, board["handCount"]!.GetValue<int>());
        Assert.Equal(1, board["armedCounterCount"]!.GetValue<int>());

        var hand = board["hand"]!.AsArray();
        Assert.True(hand[0]!["isCounterArmed"]!.GetValue<bool>());
        Assert.Equal(0, hand[0]!["handIndex"]!.GetValue<int>());
        Assert.Null(hand[0]!["slot"]); // 手牌不再复用战线的 slot 字段名

        // 战线仍用 slot（域不同、名字不同）。
        Assert.Equal(1, board["selfLine"]!.AsArray()[0]!["slot"]!.GetValue<int>());

        // 取消武装后同一字段转 false（与 observe.view 的 selfCounterCount 同口径）。
        transport.View = BoardView(armed: false, counterCount: 0, handCount: 2);
        var (_, second) = await RunAsync(
            new BridgeServer(transport), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"seat.board","arguments":{}}}""");
        var afterCancel = Envelope(ParseLines(second)[0])["result"]!["details"]!;

        Assert.False(afterCancel["hand"]!.AsArray()[0]!["isCounterArmed"]!.GetValue<bool>());
        Assert.Equal(0, afterCancel["armedCounterCount"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- R4

    /// <summary>
    /// R4：动作类调用返回前用权威视图刷新——推送帧还停在动作前（turn 3），
    /// 返回载荷的 <c>state.turn</c> 必须等于动作结果（turn 7），不能一载荷两个真相。
    /// </summary>
    [Fact]
    public async Task ActionCall_ReturnsRefreshedState()
    {
        var transport = new CannedTransport
        {
            View = new JsonObject
            {
                ["seat"] = 1,
                ["turn"] = 7,
                ["phase"] = "play",
                ["activePlayerSide"] = "enemy",
                ["selfHandCount"] = 3,
            },
            CallDetails = new JsonObject { ["turn"] = 7 },
        };

        var server = new BridgeServer(transport);
        transport.Push(new RpcNotification
        {
            Method = ProtocolMethods.NotifyView,
            Params = new JsonObject
            {
                ["seat"] = 1,
                ["turn"] = 3, // 推送帧滞后一帧（真实链路的常态）
                ["phase"] = "play",
                ["activePlayerSide"] = "self",
                ["selfHandCount"] = 4,
            },
        });

        var (started, output) = await RunAsync(
            server, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"match.end_turn","arguments":{}}}""");

        Assert.Equal(0, started.ExitCode);
        var envelope = Envelope(ParseLines(output)[0]);

        Assert.Equal(7, envelope["state"]!["turn"]!.GetValue<int>());
        Assert.False(envelope["state"]!["yourTurn"]!.GetValue<bool>());

        // 刷新确实发生在返回之前（同一次调用里 observe.view 已被访问）。
        Assert.Contains("observe.view", transport.Calls);
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject BoardView(bool armed, int counterCount, int handCount) => new()
    {
        ["seat"] = 1,
        ["turn"] = 4,
        ["phase"] = "play",
        ["activePlayerSide"] = "self",
        ["selfHandCount"] = handCount,
        ["selfCounterCount"] = counterCount,
        ["supportLineSlotCount"] = 5,
        ["frontLineSlotCount"] = 5,
        ["selfHand"] = new JsonArray
        {
            new JsonObject
            {
                ["uid"] = "hand-0",
                ["slotIndex"] = 0,
                ["zone"] = "hand",
                ["isCounterArmed"] = armed,
                ["definition"] = new JsonObject { ["name"] = "反制" },
            },
            new JsonObject
            {
                ["uid"] = "hand-1",
                ["slotIndex"] = 1,
                ["zone"] = "hand",
                ["isCounterArmed"] = false,
                ["definition"] = new JsonObject { ["name"] = "步兵" },
            },
        },
        ["selfLine"] = new JsonArray
        {
            new JsonObject
            {
                ["uid"] = "line-0",
                ["slotIndex"] = 1,
                ["zone"] = "support",
                ["isCounterArmed"] = false,
                ["definition"] = new JsonObject { ["name"] = "步兵" },
            },
        },
        ["enemyLine"] = new JsonArray(),
    };

    /// <summary>同质卡组对局（Player 模式＝投影可用）：各自整副同一张卡，起手必得。</summary>
    private static async Task CreateMatchAsync(SandboxFixture fixture, string seatACard, string seatBCard)
    {
        var created = await fixture.Service.CreateMatchAsync(new MatchLaunchOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SeatA = new MatchSeatConfig(0, Enumerable.Repeat(seatACard, 39).ToArray()),
            SeatB = new MatchSeatConfig(1, Enumerable.Repeat(seatBCard, 39).ToArray()),
            Seed = 42,
            SkipMulligan = true,
            EnforceDeckRules = false,
        });

        Assert.Equal(ActionOutcome.StatusCompleted, created.Status);
    }

    /// <summary>以指定座位视角取目标卡的手牌条目，打出到支援线首个空槽（返回该卡引用键）。</summary>
    private static async Task<string> DeployFirstAsync(SandboxFixture fixture, int seat, string cardId)
    {
        JsonObject entry;
        using (ConsumerContext.Enter())
        {
            var view = await fixture.InvokeAsync("observe.view", $"{{\"seat\":{seat}}}");
            Assert.True(view.Ok, view.Message);
            var hand = view.Details!["view"]!["selfHand"]!.AsArray();
            // 卡定义 id 是带命名空间的（pack/<pack>/<id>）——按后缀匹配，避免测试绑定包名。
            var match = hand.Select(node => (JsonObject)node!).FirstOrDefault(card =>
                (card["definition"]?["cardId"]?.GetValue<string>() ?? card["cardId"]?.GetValue<string>() ?? string.Empty)
                    .EndsWith(cardId, StringComparison.Ordinal));
            Assert.True(match is not null, $"手牌里没有 '{cardId}'：{view.Details!["view"]!["selfHand"]!.ToJsonString()}");
            entry = match!;
        }

        var reference = entry["uid"]!.GetValue<string>();
        var played = await fixture.InvokeAsync(
            "play.unit", $"{{\"ref\":\"{reference}\",\"owner\":{seat},\"slot\":0}}");
        Assert.True(played.Ok, $"{played.Code}: {played.Message}");
        return reference;
    }

    private static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 samples/card-pack-ai（测试需要样例卡包）。");
    }

    private static async Task<string> SpawnAsync(SandboxFixture fixture, int owner, string zone)
    {
        // 首次造卡前写入定义文件（沙盒卡的唯一入口；不绕过 card.load）。
        var directory = fixture.Service.Sandbox.CardsDirectory;
        var path = Path.Combine(directory, "fix.infantry.card.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, InfantryJson);
            Assert.True((await fixture.InvokeAsync("card.load")).Ok);
        }

        var spawned = await fixture.InvokeAsync(
            "sandbox.spawn", $"{{\"definitionId\":\"fix.infantry\",\"owner\":{owner},\"zone\":\"{zone}\"}}");
        Assert.True(spawned.Ok, spawned.Message);
        return spawned.EngineResult!["ref"]!.GetValue<string>();
    }

    private static int FirstIndex(JsonArray candidates, string kind)
    {
        foreach (var node in candidates)
        {
            if (node is JsonObject candidate
                && string.Equals(candidate["kind"]?.GetValue<string>(), kind, StringComparison.Ordinal))
            {
                return candidate["index"]!.GetValue<int>();
            }
        }

        return -1;
    }

    private static async Task<(BridgeServer Server, string Output)> RunAsync(BridgeServer server, params string[] frames)
    {
        var input = new StringReader(string.Join('\n', frames) + "\n");
        var output = new StringWriter();
        var exitCode = await server.RunAsync(input, output);
        Assert.Equal(0, exitCode);
        return (server, output.ToString());
    }

    private static List<JsonNode> ParseLines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();

    private static JsonNode Envelope(JsonNode response)
        => JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

    /// <summary>可编程假传输：<c>observe.view</c> 返回受控视图，其余工具返回受控 details。</summary>
    private sealed class CannedTransport : IBridgeTransport
    {
        public JsonObject View { get; set; } = new();

        public JsonObject CallDetails { get; set; } = new();

        public List<string> Calls { get; } = new();

        public BridgeConnectionState Connection { get; set; } = BridgeConnectionState.Ready;

        public bool IsConnected => Connection == BridgeConnectionState.Ready;

        public event Action<RpcNotification>? NotificationReceived;

        public void Push(RpcNotification notification) => NotificationReceived?.Invoke(notification);

        public Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolDescriptorPayload>>(Array.Empty<ToolDescriptorPayload>());

        public Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default)
        {
            Calls.Add(tool);
            return Task.FromResult(new ToolResultPayload
            {
                Ok = true,
                Code = "ok",
                Message = "已读取。",
                Details = string.Equals(tool, "observe.view", StringComparison.Ordinal)
                    ? new JsonObject { ["view"] = View.DeepClone(), ["actions"] = new JsonObject() }
                    : (JsonObject)CallDetails.DeepClone(),
            });
        }

        public Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(Connection);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
