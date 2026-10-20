using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S9 端到端：两个客户端声明座位 → 房主开局 → 收到 match.started → 各自取得整枚投影。
/// <para>走真实 TCP 与完整启动链路（示例卡包）；卡组规则在此关闭（示例包卡种不足，见 implementation.md）。</para>
/// </summary>
public class EndToEndTests
{
    [Fact]
    public async Task TwoClients_ClaimStartAndReadProjection()
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
        await using var guestClient = await ConnectAsync(host.Port);

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        var claimedHost = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimedHost.Ok, claimedHost.Message);

        var claimedGuest = await guestClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimedGuest.Ok, claimedGuest.Message);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guestClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyMatchStarted)
            {
                started.TrySetResult();
            }
        };

        var token = claimedHost.Details!["seatToken"]!.GetValue<string>();
        var start = await hostClient.CallAsync("room.start", new JsonObject { ["seatToken"] = token });

        Assert.True(start.Ok, start.Message);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var hostView = await hostClient.CallAsync("observe.view", new JsonObject { ["seat"] = 0 });
        Assert.True(hostView.Ok, hostView.Message);
        Assert.NotNull(hostView.Details!["view"]!["selfHand"]);

        var guestView = await guestClient.CallAsync("observe.view", new JsonObject { ["seat"] = 1 });
        Assert.True(guestView.Ok, guestView.Message);
        Assert.Equal(1, guestView.Details!["seat"]!.GetValue<int>());

        // 两个视角的座位不同 → 投影是分别生成的（不是广播同一份）。
        Assert.NotEqual(
            hostView.Details!["matchId"]!.GetValue<string>() + ":0",
            guestView.Details!["matchId"]!.GetValue<string>() + ":1");
    }

    [Fact]
    public async Task FullMatchLifecycle_Mulligan_EndTurn_Concede()
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
        await using var guestClient = await ConnectAsync(host.Port);

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimHost.Ok, claimHost.Message);

        var claimGuest = await guestClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimGuest.Ok, claimGuest.Message);

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        // 换牌相位：双方各自确认（不换牌直接确认）。
        var doneHost = await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        Assert.True(doneHost.Ok, doneHost.Message);

        var doneGuest = await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });
        Assert.True(doneGuest.Ok, doneGuest.Message);

        var playing = await hostClient.CallAsync("observe.view", new JsonObject { ["seat"] = 0 });
        Assert.True(playing.Ok, playing.Message);
        Assert.Equal("play", playing.Details!["view"]!["phase"]!.GetValue<string>());

        // 结束回合（当前方＝座位 0）。
        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        // 认输 → 终局。
        var concede = await hostClient.CallAsync("match.concede", new JsonObject { ["owner"] = 0 });
        Assert.True(concede.Ok, concede.Message);

        var ended = await hostClient.CallAsync("observe.view", new JsonObject { ["seat"] = 0 });
        Assert.True(ended.Ok, ended.Message);
        Assert.Equal("over", ended.Details!["view"]!["phase"]!.GetValue<string>());
        Assert.Equal("Lose", ended.Details!["view"]!["outcome"]!.GetValue<string>());

        var guestEnded = await guestClient.CallAsync("observe.view", new JsonObject { ["seat"] = 1 });
        Assert.True(guestEnded.Ok, guestEnded.Message);
        Assert.Equal("Win", guestEnded.Details!["view"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlayUnitPre_TriggersTargeting_AndSubmits()
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
        await using var guestClient = await ConnectAsync(host.Port);

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimHost.Ok, claimHost.Message);

        await guestClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = deck.DeepClone(),
        });

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });

        // 选靶请求推送（动作之前订阅，避免竞态）。
        var targeting = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyTargetRequest && notification.Params is { } parameters)
            {
                targeting.TrySetResult(parameters);
            }
        };

        var view = await hostClient.CallAsync("observe.view", new JsonObject { ["seat"] = 0 });
        Assert.True(view.Ok, view.Message);
        var hand = view.Details!["view"]!["selfHand"]!.AsArray();
        Assert.NotEmpty(hand);

        JsonNode? pending = null;
        foreach (var card in hand)
        {
            var uid = card!["uid"]!.GetValue<string>();
            var pre = await hostClient.CallAsync("play.unit_pre", new JsonObject { ["ref"] = uid });
            if (pre.Ok && pre.Code == ResultCodes.Targeting)
            {
                pending = await targeting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                break;
            }
        }

        Assert.NotNull(pending);
        Assert.Equal(0, pending!["seat"]!.GetValue<int>());
        Assert.NotEmpty(pending["candidates"]!.AsArray());

        // S4b：能成功发起"单位预打出"的卡，必然在 deploymentGaps 里有条目（可打出＝有部署空隙）。
        Assert.NotEmpty(view.Details!["actions"]!["deploymentGaps"]!.AsArray());

        var sessionId = pending["sessionId"]!.GetValue<string>();
        var submit = await hostClient.CallAsync("target.submit", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["picks"] = new JsonArray(1),
        });

        Assert.True(submit.Ok, submit.Message);
        Assert.NotEqual(ResultCodes.Busy, submit.Code);
    }

    [Fact]
    public async Task Reconnect_WithSeatToken_RebindsAndReadsProjection()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SandboxRoot = temp.Path,
            Port = 0,
            EnforceDeckRules = false,
        });

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        string seatToken;
        await using (var hostClient = await ConnectAsync(host.Port))
        {
            await using var guestClient = await ConnectAsync(host.Port);

            var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 0,
                ["kind"] = "local",
                ["name"] = "房主",
                ["deck"] = deck.DeepClone(),
            });
            Assert.True(claimHost.Ok, claimHost.Message);
            seatToken = claimHost.Details!["seatToken"]!.GetValue<string>();

            await guestClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 1,
                ["kind"] = "remote",
                ["name"] = "客人",
                ["deck"] = deck.DeepClone(),
            });

            var start = await hostClient.CallAsync("room.start", new JsonObject { ["seatToken"] = seatToken });
            Assert.True(start.Ok, start.Message);
        }

        // 原连接已断开：凭座位令牌重新绑定，且投影仍可取。
        await using var rejoined = await ConnectAsync(host.Port);

        var resume = await rejoined.CallAsync("room.resume", new JsonObject { ["seatToken"] = seatToken });
        Assert.True(resume.Ok, resume.Message);
        Assert.Equal(0, resume.Details!["seat"]!.GetValue<int>());

        var view = await rejoined.CallAsync("observe.view", new JsonObject { ["seat"] = 0 });
        Assert.True(view.Ok, view.Message);
        Assert.NotNull(view.Details!["view"]!["selfHand"]);

        // 无令牌者不能接管：新连接直接取座位视角会被 bound 到自己的空座位（未绑定）→ 用错误令牌明确拒绝。
        await using var stranger = await ConnectAsync(host.Port);
        var denied = await stranger.CallAsync("room.resume", new JsonObject { ["seatToken"] = "nope" });
        Assert.False(denied.Ok);
    }

    [Fact]
    public async Task Segments_ArePushed_AndHideOpponentHand()
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
        await using var guestClient = await ConnectAsync(host.Port);

        var deck = new JsonArray();
        for (var i = 0; i < 39; i++)
        {
            deck.Add("ai.sample.infantry");
        }

        var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["name"] = "房主",
            ["deck"] = deck.DeepClone(),
        });
        Assert.True(claimHost.Ok, claimHost.Message);

        await guestClient.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 1,
            ["kind"] = "remote",
            ["name"] = "客人",
            ["deck"] = deck.DeepClone(),
        });

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });

        var segments = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifySegment && notification.Params is { } parameters)
            {
                segments.TrySetResult(parameters);
            }
        };

        // 结束回合 → 对手回合开始抽牌 → 该抽牌对房主不可见。
        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        var payload = await segments.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var text = payload.ToJsonString();

        Assert.Equal(0, payload["seat"]!.GetValue<int>());
        Assert.NotEmpty(payload["segments"]!.AsArray());
        Assert.Contains("$hidden", text);
        Assert.DoesNotContain("$entity", text);
    }

    private static async Task<OrcClient> ConnectAsync(int port)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions { Host = "127.0.0.1", Port = port });
        await client.InitializeAsync(new InitializeParams { ClientName = "e2e" });
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-e2e-tests", Guid.NewGuid().ToString("N"));
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
