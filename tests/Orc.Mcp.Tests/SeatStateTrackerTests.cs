using System.Text;
using System.Text.Json.Nodes;
using Orc.Mcp.Bridge;
using Orc.Protocol;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 桥状态模型回归（2026-10-21 实打暴露的三处缺陷）：
/// <list type="number">
/// <item>动作提交成功后 <c>pendingSessionId</c> 必须清空（服务端不发“待决已结束”通知）。</item>
/// <item><c>seat.state</c> 用权威投影刷新，不再落后推送帧一帧。</item>
/// <item><c>seat.board</c> 给出精简战场摘要（含空支援槽，替代 gap uid）。</item>
/// </list>
/// </summary>
public class SeatStateTrackerTests
{
    private static ToolResultPayload Ok(string tool = "play.unit")
        => new() { Ok = true, Code = "ok", Message = "已执行。", Details = new JsonObject { ["kind"] = tool } };

    private static ToolResultPayload Targeting(string sessionId)
        => new()
        {
            Ok = true,
            Code = "targeting",
            Message = "待决。",
            Details = new JsonObject { ["pending"] = new JsonObject { ["sessionId"] = sessionId } },
        };

    private static void RequestTarget(SeatStateTracker tracker, string sessionId)
        => tracker.OnNotification(new RpcNotification
        {
            Method = ProtocolMethods.NotifyTargetRequest,
            Params = new JsonObject { ["sessionId"] = sessionId },
        });

    [Fact]
    public void SuccessfulAction_ClearsPending()
    {
        var tracker = new SeatStateTracker();
        RequestTarget(tracker, "s-1");
        Assert.Equal("s-1", tracker.Snapshot()["pendingSessionId"]!.GetValue<string>());

        tracker.ApplyCallResult("target.submit", Ok("command"));

        Assert.Null(tracker.Snapshot()["pendingSessionId"]);
    }

    [Fact]
    public void ActionReturningTargeting_SetsPending()
    {
        var tracker = new SeatStateTracker();

        tracker.ApplyCallResult("command.attack", Targeting("s-2"));

        Assert.Equal("s-2", tracker.Snapshot()["pendingSessionId"]!.GetValue<string>());
    }

    [Fact]
    public void NonActionTool_DoesNotTouchPending()
    {
        var tracker = new SeatStateTracker();
        RequestTarget(tracker, "s-3");

        tracker.ApplyCallResult("observe.view", Ok("observe.view"));
        tracker.ApplyCallResult("seat.state", Ok("seat.state"));

        Assert.Equal("s-3", tracker.Snapshot()["pendingSessionId"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyView_RefreshesTurnAndHand()
    {
        var tracker = new SeatStateTracker();

        tracker.ApplyView(new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["turn"] = 7,
                ["phase"] = "play",
                ["activePlayerSide"] = "self",
                ["selfHandCount"] = 3,
                ["enemyHandCount"] = 2,
            },
        });

        var state = tracker.Snapshot();
        Assert.Equal(7, state["turn"]!.GetValue<int>());
        Assert.Equal(3, state["handCount"]!.GetValue<int>());
        Assert.True(state["yourTurn"]!.GetValue<bool>());
    }

    /// <summary><c>seat.board</c> 必须给出精简摘要与空支援槽（HQ 占用的槽要排除）。</summary>
    [Fact]
    public async Task SeatBoard_ReturnsCompactSummaryWithEmptySlots()
    {
        var transport = new StubTransport(BuildViewPayload());
        var (_, output) = await RunAsync(
            new BridgeServer(transport),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"seat.board","arguments":{}}}""");

        var line = ParseLines(output).Last();
        var text = line["result"]!["content"]![0]!["text"]!.GetValue<string>();
        var envelope = JsonNode.Parse(text)!.AsObject();
        var board = envelope["result"]!["details"]!.AsObject();

        Assert.Equal(5, board["turn"]!.GetValue<int>());
        Assert.Equal("play", board["phase"]!.GetValue<string>());
        Assert.True(board["yourTurn"]!.GetValue<bool>());
        Assert.Equal(3, board["kredits"]!["self"]!.GetValue<int>());
        Assert.Equal(19, board["hq"]!["self"]!.GetValue<int>());

        // 槽位 1（支援线单位）与 2（HQ）被占 → 空槽 0/3/4。
        Assert.Equal([0, 3, 4], board["emptySupportSlots"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());

        var hand = board["hand"]!.AsArray();
        Assert.Single(hand);
        Assert.Equal("步枪兵", hand[0]!["name"]!.GetValue<string>());
        Assert.Equal(2, hand[0]!["hp"]!.GetValue<int>());
    }

    private static JsonObject BuildViewPayload()
        => new()
        {
            ["view"] = new JsonObject
            {
                ["turn"] = 5,
                ["phase"] = "play",
                ["activePlayerSide"] = "self",
                ["selfKredits"] = 3,
                ["selfMaxKredits"] = 3,
                ["selfHandCount"] = 1,
                ["enemyHandCount"] = 2,
                ["supportLineSlotCount"] = 5,
                ["frontLineSlotCount"] = 5,
                ["selfHand"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uid"] = "h1",
                        ["definition"] = new JsonObject { ["name"] = "步枪兵" },
                        ["effectiveCost"] = 0,
                        ["effectiveAttack"] = 1,
                        ["health"] = 2,
                        ["slotIndex"] = 0,
                        ["zone"] = "hand",
                    },
                },
                ["selfLine"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uid"] = "u1",
                        ["definition"] = new JsonObject { ["name"] = "军需官" },
                        ["effectiveCost"] = 0,
                        ["effectiveAttack"] = 1,
                        ["health"] = 4,
                        ["slotIndex"] = 1,
                        ["zone"] = "support",
                    },
                },
                ["selfHq"] = new JsonObject { ["uid"] = "hq1", ["health"] = 19, ["slotIndex"] = 2, ["zone"] = "support" },
                ["enemyHq"] = new JsonObject { ["uid"] = "hq2", ["health"] = 20, ["slotIndex"] = 2 },
                ["enemyLine"] = new JsonArray(),
            },
            ["actions"] = new JsonObject
            {
                ["playableUids"] = new JsonArray("h1"),
                ["attackPreviews"] = new JsonArray(),
                ["moves"] = new JsonArray(),
            },
        };

    private static async Task<(BridgeServer Server, string Output)> RunAsync(BridgeServer server, params string[] frames)
    {
        var input = new StringReader(string.Join('\n', frames) + "\n");
        var output = new StringWriter();
        var code = await server.RunAsync(input, output);
        Assert.Equal(0, code);
        return (server, output.ToString());
    }

    private static List<JsonObject> ParseLines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();

    private sealed class StubTransport : IBridgeTransport
    {
        private readonly JsonObject _details;

        public StubTransport(JsonObject details) => _details = details;

        public bool IsConnected => true;

        public event Action<RpcNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolDescriptorPayload>>(Array.Empty<ToolDescriptorPayload>());

        public Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default)
            => Task.FromResult(new ToolResultPayload
            {
                Ok = true,
                Code = "ok",
                Message = "已读取。",
                Details = (JsonObject)_details.DeepClone(),
            });

        public Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(BridgeConnectionState.Ready);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
