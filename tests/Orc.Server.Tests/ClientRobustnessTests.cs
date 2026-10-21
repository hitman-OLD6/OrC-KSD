using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Orc.Server;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 客户端读循环健壮性（F9–F12）：订阅者异常隔离、数字 id 容忍、IsConnected 语义、pending 不泄漏。
/// </summary>
public class ClientRobustnessTests
{
    /// <summary>
    /// 调用方传入的 <c>arguments</c> 已挂在另一棵树上时不得抛“node already has a parent”。
    /// <para>
    /// 真实命中路径：stdio 桥把 stdin 帧里的 <c>params.arguments</c> 节点直接转给 <see cref="OrcClient.CallAsync"/>；
    /// 修复前该节点换父失败，导致**所有经桥转发的工具调用**都返回 <c>not-connected</c>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Call_WithArgumentsAlreadyParented_DoesNotThrow()
    {
        using var temp = new TestSupport.TempDirectory("orc-client");
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
            EnforceDeckRules = false,
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        await client.InitializeAsync(new InitializeParams { ClientName = "parented-args" });

        // 模拟桥：arguments 从请求树里取出，已带父节点。
        var frame = JsonNode.Parse("""{"params":{"arguments":{}}}""")!;
        var parented = (JsonObject)frame["params"]!["arguments"]!;

        var result = await client.CallAsync("service.info", parented);

        Assert.True(result.Ok, result.Message);
    }

    /// <summary>一个订阅者抛异常：连接仍可用，其余订阅者仍被调用（F9）。</summary>
    [Fact]
    public async Task NotificationHandlerThrows_DoesNotKillReadLoop()
    {
        using var temp = new TestSupport.TempDirectory("orc-client");
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = temp.Path,
            Port = 0,
            EnforceDeckRules = false,
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var received = 0;
        client.NotificationReceived += _ => throw new InvalidOperationException("订阅者故障（测试注入）");
        client.NotificationReceived += _ => Interlocked.Increment(ref received);

        // 任何房间变更都会广播 notify/room（先触发的订阅者会抛）。
        Assert.True((await client.CallAsync("room.claim", TestSupport.Claim(0))).Ok);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(Volatile.Read(ref received)),
            count => count > 0,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        // 读循环没死：后续请求照常。
        var info = await client.CallAsync("room.info");
        Assert.True(info.Ok, info.Message);
        Assert.True(client.IsConnected);
    }

    /// <summary>数字 id 的响应不得让读循环退出（F10）。</summary>
    [Fact]
    public async Task NumericResponseId_IsTolerated()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();

            await using var client = await OrcClient.ConnectAsync(new ClientOptions
            {
                Host = "127.0.0.1",
                Port = port,
                HeartbeatInterval = TimeSpan.Zero, // 本用例只验读循环
            });

            using var serverSide = await accept;
            await using var stream = serverSide.GetStream();

            // 1) 数字 id 的响应（旧实现会抛 InvalidOperationException 并杀死读循环）
            await WriteLineAsync(stream, """{"jsonrpc":"2.0","id":1,"result":{"pong":true}}""");

            // 2) 正常响应（字符串 id）
            var pending = Task.Run(async () => await ReadLineAsync(stream));
            var reply = client.CallRawAsync("ping", null);

            string line;
            try
            {
                line = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"读取请求行超时（reply: {reply.Status}, {reply.Exception}）。");
            }
            var id = JsonNode.Parse(line)!["id"]!.GetValue<string>();
            await WriteLineAsync(
                stream,
                """{"jsonrpc":"2.0","id":""" + JsonValue.Create(id)!.ToJsonString() + ""","result":{"pong":true}}""");

            var response = await Record.ExceptionAsync(() => reply.WaitAsync(TimeSpan.FromSeconds(5)));
            if (response is not null)
            {
                throw new InvalidOperationException(
                    $"应答未完成（reply: {reply.Status}, connected={client.IsConnected}）。请求行='{line}' 回写id='{id}' 异常={response.Message}");
            }

            var completed = await reply;
            Assert.NotNull(completed.Result);
            Assert.True(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>服务端关闭后 <c>IsConnected == false</c>（F11）。</summary>
    [Fact]
    public async Task IsConnected_IsFalse_AfterServerCloses()
    {
        using var temp = new TestSupport.TempDirectory("orc-client");
        var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
            HeartbeatInterval = TimeSpan.Zero,
        });

        Assert.True(client.IsConnected);

        await host.DisposeAsync();

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(client.IsConnected),
            connected => !connected,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);
    }

    /// <summary>连接断开后的失败请求不得在 pending 里留下条目（F12）。</summary>
    [Fact]
    public async Task FailedWrites_DoNotLeakPendingEntries()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();

            await using var client = await OrcClient.ConnectAsync(new ClientOptions
            {
                Host = "127.0.0.1",
                Port = port,
                HeartbeatInterval = TimeSpan.Zero,
            });

            using var serverSide = await accept;
            serverSide.Close(); // 对端立刻消失：后续写入必然失败

            for (var i = 0; i < 5; i++)
            {
                var error = await Record.ExceptionAsync(
                    () => client.CallRawAsync("ping", null).WaitAsync(TimeSpan.FromMilliseconds(500)));

                Assert.NotNull(error);
            }

            // 连接已死 → 残留条目必须在有限时间内清空（读循环的清理是异步的，
            // 故这里等"最终为空"而不是取瞬时快照；写入失败那条路径由 CallRawAsync 的
            // catch 立即拆除，见 OrcClient.CallRawAsync）。
            await TestSupport.WaitUntilAsync(
                () => Task.FromResult(PendingEntries(client).Count),
                count => count == 0,
                TimeSpan.FromSeconds(5),
                throwOnTimeout: true);

            Assert.Empty(PendingEntries(client));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static System.Collections.ICollection PendingEntries(OrcClient client)
    {
        var field = typeof(OrcClient).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 _pending 字段（客户端实现已变）。");
        return (System.Collections.ICollection)field.GetValue(client)!;
    }

    private static async Task WriteLineAsync(NetworkStream stream, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single);
            if (read == 0)
            {
                throw new IOException("对端已关闭。");
            }

            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            buffer.Add(single[0]);
        }
    }
}
