using System.Text.Json.Nodes;
using Xunit;

namespace Orc.Manager.Tests;

/// <summary>
/// 管理器的 MCP 面（stdio 前端 ＋ 回环 TCP 服务）。
/// <para>
/// 三条不变量：①前端能正确应答 MCP 握手与工具目录；②<see cref="ManagerClient"/> 与
/// <see cref="ManagerServer"/> 的往返能穿透到 <see cref="IManager"/>；③管理器不在线时，
/// 前端的失败必须是**可读、可操作**的提示，而不是崩溃或静默。
/// </para>
/// </summary>
public sealed class ManagerMcpTests
{
    [Fact]
    public async Task Front_AnswersInitializeAndListsManagerTools()
    {
        await using var manager = new StubManager();
        await using var server = ManagerServer.Start(manager, 0);
        await using var client = new ManagerClient("127.0.0.1", server.Port);

        var output = await RunFrontAsync(
            client,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            """);

        Assert.Equal(2, output.Count);

        var initialize = output[0];
        Assert.Equal(1, initialize["id"]!.GetValue<int>());
        Assert.Equal("orc-manager", initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        var tools = output[1]["result"]!["tools"]!.AsArray()
            .Select(node => node!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("manager.spawn", tools);
        Assert.Contains("manager.kill", tools);
        Assert.Contains("manager.reload", tools);
        Assert.Contains("manager.list", tools);
        Assert.Contains("manager.admin", tools);
        // 代持连接：让 agent 只连管理器一个 MCP 就能当玩家/调试面（不重启宿主）。
        Assert.Contains("manager.connect", tools);
        Assert.Contains("manager.action", tools);
        Assert.Contains("manager.sessions", tools);
        Assert.Contains("manager.disconnect", tools);
    }

    [Fact]
    public async Task Front_ManagerSessions_StartsEmpty()
    {
        await using var manager = new StubManager();
        await using var server = ManagerServer.Start(manager, 0);
        await using var client = new ManagerClient("127.0.0.1", server.Port);

        var output = await RunFrontAsync(
            client,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"manager.sessions","arguments":{}}}
            """);

        var text = output[1]["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var reply = JsonNode.Parse(text)!;
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(0, reply["payload"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Front_ForwardsToolCallToManager()
    {
        await using var manager = new StubManager();
        await using var server = ManagerServer.Start(manager, 0);
        await using var client = new ManagerClient("127.0.0.1", server.Port);

        var output = await RunFrontAsync(
            client,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"manager.list","arguments":{}}}
            """);

        var result = output[1]["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>());

        // 工具结果是把 ManagerReply 序列化进 content[0].text —— 断言它真的来自 IManager。
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var reply = JsonNode.Parse(text)!;
        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(1, reply["payload"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Front_UnknownTool_IsError()
    {
        await using var manager = new StubManager();
        await using var server = ManagerServer.Start(manager, 0);
        await using var client = new ManagerClient("127.0.0.1", server.Port);

        var output = await RunFrontAsync(
            client,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"manager.nope","arguments":{}}}
            """);

        Assert.True(output[1]["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Front_WhenManagerOffline_GivesActionableMessage()
    {
        // 找一个几乎不可能被占用的端口，且不启动服务。
        await using var client = new ManagerClient("127.0.0.1", 1);

        var output = await RunFrontAsync(
            client,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"manager.ping","arguments":{}}}
            """);

        var result = output[1]["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());

        var message = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("orc-manager serve", message, StringComparison.Ordinal);
    }

    private static async Task<List<JsonObject>> RunFrontAsync(ManagerClient client, string input, CancellationToken ct = default)
    {
        using var reader = new StringReader(input);
        var writer = new StringWriter();

        var front = new ManagerMcpFront(client);
        var exitCode = await front.RunAsync(reader, writer, ct);
        Assert.Equal(0, exitCode);

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException($"非法输出行：{line}"))
            .ToList();
    }

    /// <summary>不启动任何子进程的管理器替身：只验证传输层与工具路由。</summary>
    private sealed class StubManager : IManager
    {
        private readonly List<InstanceInfo> _instances =
        [
            new(
                "stub",
                ProcessId: 1,
                Port: 12345,
                RoomId: "room01",
                Mode: "sandbox",
                RunDir: "/tmp/stub",
                Label: "stub",
                PackDirectories: ["/packs"],
                StartedAt: DateTimeOffset.UnixEpoch,
                Running: true),
        ];

        public AssetLibrary Assets { get; } = new();

        public Task<ManagerReply> SpawnAsync(SpawnRequest request, CancellationToken ct = default)
            => Task.FromResult(ManagerReply.Success(new JsonObject { ["instanceId"] = request.InstanceId }));

        public Task<ManagerReply> KillAsync(string instanceId, CancellationToken ct = default)
            => Task.FromResult(ManagerReply.Success(new JsonObject { ["instanceId"] = instanceId }));

        public Task<ManagerReply> ReloadAsync(string instanceId, CancellationToken ct = default)
            => Task.FromResult(ManagerReply.Success(new JsonObject { ["instanceId"] = instanceId }));

        public IReadOnlyList<InstanceInfo> List() => _instances;

        public Task<ManagerReply> AdminAsync(string instanceId, ManagerAdminCall call, CancellationToken ct = default)
            => Task.FromResult(ManagerReply.Success(new JsonObject { ["op"] = call.Op }));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
