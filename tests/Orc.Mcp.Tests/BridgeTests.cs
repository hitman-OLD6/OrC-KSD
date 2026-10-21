using System.Text.Json.Nodes;
using Orc.Mcp.Bridge;
using Orc.Protocol;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// stdio MCP 桥用例（S5/S8）：工具清单契约、可拉取状态、段缓冲与 stdout 纯净性。
/// <para>
/// 桥的"打完整局"能力由服务端直连用例间接保证（同一服务端语义）；本文件只做最小断言，
/// 用假传输注入推送，不重复覆盖服务端行为。
/// </para>
/// </summary>
public class BridgeTests
{
    /// <summary>工具清单：桥专属工具单独可辨、服务端工具原样透传、写类不出现。</summary>
    [Fact]
    public async Task ToolsListContract()
    {
        var transport = new FakeTransport();
        transport.ServerTools.Add(new ToolDescriptorPayload("room.info", "room", "房间快照", """{"type":"object"}"""));
        transport.ServerTools.Add(new ToolDescriptorPayload("observe.view", "observe", "视图", """{"type":"object"}"""));

        var (server, output) = await RunAsync(new BridgeServer(transport),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(0, server.ExitCode);
        var lines = ParseLines(output);
        Assert.Equal(2, lines.Count);

        var tools = lines[1]["result"]!["tools"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("seat.state", tools);
        Assert.Contains("seat.segments", tools);
        Assert.Contains("room.info", tools);
        Assert.Contains("observe.view", tools);
        Assert.DoesNotContain("card.write", tools);
    }

    /// <summary>静置后 <c>seat.state</c> 仍给出"轮到你/无待决"，不挂死。</summary>
    [Fact]
    public async Task SeatStateAfterIdle()
    {
        var transport = new FakeTransport();
        var server = new BridgeServer(transport);
        transport.Push(new RpcNotification
        {
            Method = ProtocolMethods.NotifyView,
            Params = new JsonObject
            {
                ["seat"] = 1,
                ["turn"] = 5,
                ["phase"] = "play",
                ["activePlayerSide"] = "self",
                ["selfHandCount"] = 3,
                ["enemyHandCount"] = 4,
            },
        });

        var (started, output) = await RunAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"seat.state","arguments":{}}}""");

        var lines = ParseLines(output);
        Assert.Equal(0, started.ExitCode);

        var envelope = JsonNode.Parse(lines[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        var state = envelope["state"]!;
        Assert.True(state["yourTurn"]!.GetValue<bool>());
        Assert.Equal(5, state["turn"]!.GetValue<int>());
        Assert.Equal("play", state["phase"]!.GetValue<string>());
        Assert.Equal(3, state["handCount"]!.GetValue<int>());
        Assert.Null(state["pendingSessionId"]);
        Assert.Equal(1, state["stateVersion"]!.GetValue<long>());
    }

    /// <summary>段超 200：<c>dropped</c> 递增；<c>seat.segments</c> 按 sinceSequence 截断并置 <c>truncated</c>。</summary>
    [Fact]
    public async Task SeatSegmentsTruncatedAndDropped()
    {
        var transport = new FakeTransport();
        var server = new BridgeServer(transport);
        for (var i = 0; i < SeatStateTracker.SegmentCapacity + 5; i++)
        {
            transport.Push(new RpcNotification
            {
                Method = ProtocolMethods.NotifySegment,
                Params = new JsonObject
                {
                    ["seat"] = 1,
                    ["segments"] = new JsonArray(new JsonObject { ["sequence"] = i }),
                },
            });
        }

        var (_, output) = await RunAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"seat.state","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"seat.segments","arguments":{"sinceSequence":0}}}""");

        var lines = ParseLines(output);

        var stateEnvelope = JsonNode.Parse(lines[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(5, stateEnvelope["state"]!["segments"]!["dropped"]!.GetValue<long>());
        Assert.Equal(SeatStateTracker.SegmentCapacity, stateEnvelope["state"]!["segments"]!["buffered"]!.GetValue<int>());

        var segmentsEnvelope = JsonNode.Parse(lines[1]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        var payload = segmentsEnvelope["result"]!["details"]!;
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Equal(SeatStateTracker.SegmentCapacity, payload["segments"]!.AsArray().Count);
        Assert.Equal(SeatStateTracker.SegmentCapacity + 4, payload["lastSequence"]!.GetValue<long>());
    }

    /// <summary>未连接：<c>seat.state</c> 返回 <c>not-connected</c>，不返回空状态；桥退出 <c>0</c>。</summary>
    [Fact]
    public async Task NotConnectedSeatState()
    {
        var transport = new FakeTransport { Connection = BridgeConnectionState.NotConnected };

        var (server, output) = await RunAsync(new BridgeServer(transport),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"seat.state","arguments":{}}}""");

        Assert.Equal(0, server.ExitCode);

        var lines = ParseLines(output);
        Assert.True(lines[0]["result"]!["isError"]!.GetValue<bool>());
        var envelope = JsonNode.Parse(lines[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.False(envelope["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("not-connected", envelope["result"]!["code"]!.GetValue<string>());
    }

    /// <summary>令牌失效：桥以退出码 <c>2</c> 收尾，结果码 <c>unauthorized</c>。</summary>
    [Fact]
    public async Task TokenRejectedExitCode()
    {
        var transport = new FakeTransport { Connection = BridgeConnectionState.TokenRejected };

        var (server, output) = await RunAsync(new BridgeServer(transport),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"room.info","arguments":{}}}""");

        Assert.Equal(2, server.ExitCode);
        var envelope = JsonNode.Parse(ParseLines(output)[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("unauthorized", envelope["result"]!["code"]!.GetValue<string>());
    }

    /// <summary>服务端工具结果附带精简 state（不是"仅变化时附"）。</summary>
    [Fact]
    public async Task ServerToolResultCarriesState()
    {
        var transport = new FakeTransport();
        var server = new BridgeServer(transport);
        transport.Push(new RpcNotification
        {
            Method = ProtocolMethods.NotifyView,
            Params = new JsonObject { ["turn"] = 2, ["phase"] = "mulligan", ["activePlayerSide"] = "opponent" },
        });

        var (_, output) = await RunAsync(server,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"room.info","arguments":{}}}""");

        var lines = ParseLines(output);
        var envelope = JsonNode.Parse(lines[0]["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

        Assert.True(envelope["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal("mulligan", envelope["state"]!["phase"]!.GetValue<string>());
        Assert.False(envelope["state"]!["yourTurn"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(BridgeServer Server, string Output)> RunAsync(
        BridgeServer server,
        params string[] frames)
    {
        using var input = new StringReader(string.Join('\n', frames) + "\n");
        using var output = new StringWriter();

        await server.RunAsync(input, output);
        return (server, output.ToString());
    }

    /// <summary>每一行都必须是可解析的 JSON-RPC 帧（stdout 纯净性）。</summary>
    private static List<JsonObject> ParseLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);

        var parsed = new List<JsonObject>(lines.Length);
        foreach (var line in lines)
        {
            parsed.Add(Assert.IsType<JsonObject>(JsonNode.Parse(line)));
        }

        return parsed;
    }

    /// <summary>假传输：注入推送与工具结果，不连服务端。</summary>
    private sealed class FakeTransport : IBridgeTransport
    {
        public List<ToolDescriptorPayload> ServerTools { get; } = new();

        public BridgeConnectionState Connection { get; set; } = BridgeConnectionState.Ready;

        public bool IsConnected => Connection == BridgeConnectionState.Ready;

        public event Action<RpcNotification>? NotificationReceived;

        public void Push(RpcNotification notification) => NotificationReceived?.Invoke(notification);

        public Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolDescriptorPayload>>(ServerTools);

        public Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default)
            => Task.FromResult(new ToolResultPayload
            {
                Ok = true,
                Code = "ok",
                Message = "已读取。",
                Details = new JsonObject { ["tool"] = tool },
            });

        public Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(Connection);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
