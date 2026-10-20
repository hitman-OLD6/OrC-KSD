using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 韧性：服务端关闭/心跳超时后客户端请求必须**失败而不是挂起**；销毁对局发 match.ended；
/// 多客户端并发请求全部被服务。
/// </summary>
public class ResilienceTests
{
    [Fact]
    public async Task ClientCall_Fails_WhenServerCloses_InsteadOfHanging()
    {
        using var temp = new TempDirectory();
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
        });

        await host.DisposeAsync();

        // 关键断言：抛的是真实异常，而不是 WaitAsync 的 TimeoutException（那说明挂起了）。
        var exception = await Record.ExceptionAsync(
            () => client.CallAsync("room.info").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact]
    public async Task IdleConnection_IsClosed_ByHeartbeatTimeout()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
            HeartbeatTimeout = TimeSpan.FromSeconds(1),
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        // 不发送任何请求：等服务端心跳判定（1s 超时 + 0.5s 检查间隔）后关闭连接。
        await Task.Delay(TimeSpan.FromSeconds(3));

        var exception = await Record.ExceptionAsync(
            () => client.CallAsync("room.info").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact]
    public async Task DisposingMatch_PushesMatchEnded()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SandboxRoot = temp.Path,
            Port = 0,
            EnforceDeckRules = false,
        });

        await using var hostClient = await ConnectAsync(host.Port);

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        var claim = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = deck,
        });
        Assert.True(claim.Ok, claim.Message);

        // 单人座（另一个座位留空无法开局）——直接用服务层建局，验证生命周期事件。
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyMatchEnded)
            {
                ended.TrySetResult();
            }
        };

        var created = await host.Service.CreateMatchAsync(new Orc.Mcp.Match.MatchLaunchOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SeatA = new Orc.Mcp.Match.MatchSeatConfig(0, Enumerable.Repeat("ai.sample.infantry", 39).ToArray()),
            SeatB = new Orc.Mcp.Match.MatchSeatConfig(1, Enumerable.Repeat("ai.sample.infantry", 39).ToArray()),
            Seed = 11,
            EnforceDeckRules = false,
        });
        Assert.Equal(Orc.Mcp.Match.ActionOutcome.StatusCompleted, created.Status);

        host.Service.DisposeMatch();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentRequests_FromTwoClients_AreAllServed()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var first = await ConnectAsync(host.Port);
        await using var second = await ConnectAsync(host.Port);

        var firstCalls = Enumerable.Range(0, 50).Select(_ => first.CallAsync("room.info")).ToArray();
        var secondCalls = Enumerable.Range(0, 50).Select(_ => second.CallAsync("room.info")).ToArray();

        var results = await Task.WhenAll(firstCalls.Concat(secondCalls));

        Assert.Equal(100, results.Length);
        Assert.All(results, result => Assert.True(result.Ok, result.Message));
    }

    private static async Task<OrcClient> ConnectAsync(int port)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions { Host = "127.0.0.1", Port = port });
        await client.InitializeAsync(new InitializeParams { ClientName = "resilience" });
        return client;
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

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-resilience-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }
}
