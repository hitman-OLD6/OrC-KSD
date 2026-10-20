using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// R3（服务端托管 AI 会行动）＋ R9（断线宽限判负、房间态释放座位）＋ R12（卡包指纹）。
/// </summary>
public class AiAndDisconnectTests
{
    [Fact]
    public async Task AiSeat_TakesItsTurn_WithoutAnyRemoteClient()
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

        var claim = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = Deck(),
        });
        Assert.True(claim.Ok, claim.Message);

        var ai = await hostClient.CallAsync("room.add_ai", new JsonObject
        {
            ["seat"] = 1,
            ["name"] = "AI",
            ["deck"] = Deck(),
        });
        Assert.True(ai.Ok, ai.Message);

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claim.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        // 房主确认换牌；AI 由自己确认。
        await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        await WaitUntilAsync(
            () => PhaseAsync(hostClient, 0),
            phase => phase == "play",
            TimeSpan.FromSeconds(15));

        // 房主结束回合 → 轮到 AI → AI 应自行行动并结束回合（turn 至少 +2）。
        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        var turn = await WaitUntilAsync(
            () => TurnAsync(hostClient, 0),
            value => value >= 2,
            TimeSpan.FromSeconds(30));

        Assert.True(turn >= 2, $"AI 未推进回合（turn={turn}）。");
    }

    [Fact]
    public async Task DisconnectedSeat_LosesAfterGrace_WhenItsTurnComes()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SandboxRoot = temp.Path,
            Port = 0,
            EnforceDeckRules = false,
            DisconnectGrace = TimeSpan.FromSeconds(1),
        });

        await using var hostClient = await ConnectAsync(host.Port);
        var guest = await ConnectAsync(host.Port);

        var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = Deck(),
        });
        Assert.True(claimHost.Ok, claimHost.Message);

        var claimGuest = await guest.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = Deck(),
        });
        Assert.True(claimGuest.Ok, claimGuest.Message);

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        await guest.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });
        await WaitUntilAsync(
            () => PhaseAsync(hostClient, 0),
            phase => phase == "play",
            TimeSpan.FromSeconds(10));

        // 客人断线 → 房主结束回合 → 轮到客人 → 宽限 1s 后服务端代其认输。
        await guest.DisposeAsync();

        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        var phase = await WaitUntilAsync(
            () => PhaseAsync(hostClient, 0),
            value => value == "over",
            TimeSpan.FromSeconds(20),
            throwOnTimeout: true);

        Assert.Equal("over", phase);
    }

    [Fact]
    public async Task LobbyDisconnect_ReleasesSeatImmediately()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
            // 本用例只验座位释放，不验卡组规则（空卡池下任何卡组都会 deck-invalid）。
            EnforceDeckRules = false,
        });

        await using var hostClient = await ConnectAsync(host.Port);
        var guest = await ConnectAsync(host.Port);

        var claim = await guest.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = Deck(),
        });
        Assert.True(claim.Ok, claim.Message);

        await guest.DisposeAsync();

        var seat = await WaitUntilAsync(
            async () =>
            {
                var info = await hostClient.CallAsync("room.info");
                return info.Details!["seats"]!.AsArray()[1]!["controllerKind"]!.GetValue<string>();
            },
            value => value.Length == 0,
            TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, seat);
    }

    [Fact]
    public void PackFingerprint_IsStableAndSixteenChars()
    {
        var pack = SamplePackDirectory();

        var first = ServerBootstrap.ComputePackFingerprint([pack]);
        var second = ServerBootstrap.ComputePackFingerprint([pack]);

        Assert.NotNull(first);
        Assert.Equal(16, first!.Length);
        Assert.Equal(first, second);
        Assert.Null(ServerBootstrap.ComputePackFingerprint([]));
    }

    // ---------------------------------------------------------------- helpers

    private static JsonArray Deck()
    {
        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        return deck;
    }

    private static async Task<string?> PhaseAsync(OrcClient client, int seat)
    {
        var view = await client.CallAsync("observe.view", new JsonObject { ["seat"] = seat });
        return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
    }

    private static async Task<int> TurnAsync(OrcClient client, int seat)
    {
        var view = await client.CallAsync("observe.view", new JsonObject { ["seat"] = seat });
        return view.Ok ? view.Details!["view"]!["turn"]!.GetValue<int>() : 0;
    }

    private static async Task<T> WaitUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        bool throwOnTimeout = false)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var last = default(T)!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"等待条件超时（最后值：{last}）。");
        }

        return last;
    }

    private static async Task<OrcClient> ConnectAsync(int port)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions { Host = "127.0.0.1", Port = port });
        await client.InitializeAsync(new InitializeParams { ClientName = "ai-disconnect" });
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-ai-tests", Guid.NewGuid().ToString("N"));
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
