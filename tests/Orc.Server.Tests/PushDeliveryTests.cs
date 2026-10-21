using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 推送闭环（F4/F5）：AI 每步、断线判负、选靶超时三处都必须把状态变更推给客户端；
/// AI 空转不得刷推送；宽限判负必须等到"轮到断线方"才发生。
/// </summary>
public class PushDeliveryTests
{
    [Fact]
    public async Task AiTurn_PushesViewPerPerformedStep()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(temp.Path, disconnectGrace: TimeSpan.FromSeconds(30));
        await using var human = await TestSupport.ConnectAsync(host.Port);

        await SeatHumanAgainstAiAsync(human);

        // 等换牌结束（AI 自己确认）。
        await TestSupport.WaitUntilAsync(
            () => PhaseAsync(human),
            phase => phase == "play",
            TimeSpan.FromSeconds(20),
            throwOnTimeout: true);

        var views = new List<int>();
        human.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView && notification.Params is { } payload)
            {
                lock (views)
                {
                    views.Add(payload["seat"]!.GetValue<int>());
                }
            }
        };

        var endTurn = await human.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        var turn = await TestSupport.WaitUntilAsync(
            () => TurnAndSideAsync(human),
            state => state.Turn >= 2 && state.Active == "self",
            TimeSpan.FromSeconds(30),
            throwOnTimeout: true);

        Assert.True(turn.Turn >= 2, $"AI 未推进回合（turn={turn.Turn}）。");

        int pushed;
        lock (views)
        {
            pushed = views.Count;
        }

        // 旧实现只有"我这次 end_turn"的 1 次推送（AI 步不推）；修复后 AI 每一步都推。
        Assert.True(pushed >= 2, $"AI 回合期间推送不足（{pushed}；旧实现为 1）。");
        Assert.All(views, seat => Assert.Equal(0, seat));
    }

    [Fact]
    public async Task AiIdle_DoesNotPush()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(temp.Path, disconnectGrace: TimeSpan.FromSeconds(30));
        await using var human = await TestSupport.ConnectAsync(host.Port);

        await SeatHumanAgainstAiAsync(human);
        await TestSupport.WaitUntilAsync(
            () => PhaseAsync(human),
            phase => phase == "play",
            TimeSpan.FromSeconds(20),
            throwOnTimeout: true);

        await Task.Delay(500); // 排空换牌/开局阶段的推送

        var views = 0;
        human.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView)
            {
                Interlocked.Increment(ref views);
            }
        };

        // AI 不是当前方：它的驱动循环仍在跑（每 300ms 一次），但 Performed=false → 不得推送。
        await Task.Delay(1200);

        Assert.Equal(0, Volatile.Read(ref views));
    }

    [Fact]
    public async Task DisconnectConcede_PushesGameOver()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(
            temp.Path,
            disconnectGrace: TimeSpan.FromSeconds(1),
            heartbeatTimeout: TimeSpan.FromSeconds(2));

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        var guest = await TestSupport.ConnectAsync(host.Port);

        await SeatTwoHumansAsync(hostClient, guest);

        var over = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView
                && notification.Params is { } payload
                && payload["view"]?["phase"]?.GetValue<string>() == "over")
            {
                over.TrySetResult(payload);
            }
        };

        await guest.DisposeAsync();

        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        var payload = await over.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, payload["seat"]!.GetValue<int>());
        Assert.Equal("over", payload["view"]!["phase"]!.GetValue<string>());
    }

    /// <summary>断线发生在对手回合：宽限到期也**不能**丢记录，必须在轮到断线方时判负（F5）。</summary>
    [Fact]
    public async Task DisconnectDuringOpponentTurn_StillLosesAfterGrace()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(
            temp.Path,
            disconnectGrace: TimeSpan.FromSeconds(1),
            heartbeatTimeout: TimeSpan.FromSeconds(2));

        // 服务端心跳超时很短：留守会话必须自己 ping（否则连接先被心跳判死）。
        await using var hostClient = await TestSupport.ConnectAsync(
            host.Port,
            heartbeatInterval: TimeSpan.FromMilliseconds(400),
            heartbeatTimeout: TimeSpan.FromSeconds(2));
        var guest = await TestSupport.ConnectAsync(host.Port);

        await SeatTwoHumansAsync(hostClient, guest);

        await guest.DisposeAsync();

        // 断线方不是当前方：让心跳检查跑过至少两轮（grace 1s 早已过期）——记录必须保留。
        await Task.Delay(TimeSpan.FromSeconds(3));

        var phase = await PhaseAsync(hostClient);
        Assert.Equal("play", phase);

        var over = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView
                && notification.Params is { } payload
                && payload["view"]?["phase"]?.GetValue<string>() == "over")
            {
                over.TrySetResult();
            }
        };

        // 把回合交给断线方 → 下一次检查即应判负。
        var endTurn = await hostClient.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        await over.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>选靶超时：先 <c>notify/target.expired</c>，后 <c>notify/view</c>（反映取消后的状态）。</summary>
    [Fact]
    public async Task TargetingTimeout_PushesExpiredThenView()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(
            temp.Path,
            targetSelectionTimeout: TimeSpan.FromSeconds(1));
        await using var hostClient = await TestSupport.ConnectAsync(host.Port);

        await SeatHumanAgainstAiAsync(hostClient);
        await TestSupport.WaitUntilAsync(
            () => PhaseAsync(hostClient),
            phase => phase == "play",
            TimeSpan.FromSeconds(20),
            throwOnTimeout: true);

        var sequence = new List<string>();
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewAfterExpired = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiredFlag = 0;

        hostClient.NotificationReceived += notification =>
        {
            lock (sequence)
            {
                sequence.Add(notification.Method);
            }

            if (notification.Method == ProtocolMethods.NotifyTargetExpired)
            {
                Interlocked.Exchange(ref expiredFlag, 1);
                expired.TrySetResult();
                return;
            }

            if (notification.Method == ProtocolMethods.NotifyView
                && Volatile.Read(ref expiredFlag) == 1
                && notification.Params is { } payload)
            {
                viewAfterExpired.TrySetResult(payload);
            }
        };

        var sessionId = await EnterTargetingAsync(hostClient, expectPending: true);
        Assert.NotNull(sessionId);

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var view = await viewAfterExpired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("play", view["view"]!["phase"]!.GetValue<string>());

        // 动作已按取消推进：没有待决请求残留。
        Assert.False(host.Service.Bridge.HasPending);

        lock (sequence)
        {
            var expiredIndex = sequence.IndexOf(ProtocolMethods.NotifyTargetExpired);
            var viewIndex = sequence.LastIndexOf(ProtocolMethods.NotifyView);
            Assert.True(expiredIndex >= 0);
            Assert.True(viewIndex > expiredIndex, "notify/view 必须晚于 notify/target.expired。");
        }
    }

    /// <summary><c>Settled</c> 订阅者抛异常不得污染动作结果（超时动作仍按取消推进）。</summary>
    [Fact]
    public async Task Settled_SubscriberThrows_DoesNotBreakAction()
    {
        using var temp = new TestSupport.TempDirectory("orc-push");
        await using var host = await StartHostAsync(
            temp.Path,
            targetSelectionTimeout: TimeSpan.FromSeconds(1));
        await using var hostClient = await TestSupport.ConnectAsync(host.Port);

        await SeatHumanAgainstAiAsync(hostClient);
        await TestSupport.WaitUntilAsync(
            () => PhaseAsync(hostClient),
            phase => phase == "play",
            TimeSpan.FromSeconds(20),
            throwOnTimeout: true);

        host.Service.Executor.Settled += _ => throw new InvalidOperationException("订阅者故障（测试注入）");

        var sessionId = await EnterTargetingAsync(hostClient, expectPending: true);
        Assert.NotNull(sessionId);

        // 超时按取消推进 → 动作完结；抛异常的订阅者不得让执行器卡在 busy。
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(host.Service.Bridge.HasPending),
            pending => !pending,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(host.Service.Executor.IsBusy),
            busy => !busy,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        var next = await hostClient.CallAsync("match.end_turn");
        Assert.True(next.Ok, next.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<ServerHost> StartHostAsync(
        string sandboxRoot,
        TimeSpan? disconnectGrace = null,
        TimeSpan? heartbeatTimeout = null,
        TimeSpan? targetSelectionTimeout = null)
        => await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            EnforceDeckRules = false,
            DisconnectGrace = disconnectGrace ?? TimeSpan.FromSeconds(60),
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(45),
            TargetSelectionTimeout = targetSelectionTimeout ?? TimeSpan.FromMinutes(10),
        });

    /// <summary>房主占 0、AI 占 1 并开局（房主确认换牌；AI 自行确认）。</summary>
    private static async Task SeatHumanAgainstAiAsync(OrcClient human)
    {
        var claim = await human.CallAsync("room.claim", TestSupport.Claim(0));
        Assert.True(claim.Ok, claim.Message);

        var ai = await human.CallAsync("room.add_ai", new JsonObject { ["seat"] = 1, ["deck"] = TestSupport.Deck() });
        Assert.True(ai.Ok, ai.Message);

        var start = await human.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claim.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        var done = await human.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        Assert.True(done.Ok, done.Message);
    }

    private static async Task SeatTwoHumansAsync(OrcClient hostClient, OrcClient guest)
    {
        var claim = await hostClient.CallAsync("room.claim", TestSupport.Claim(0));
        Assert.True(claim.Ok, claim.Message);
        Assert.True((await guest.CallAsync("room.claim", TestSupport.Claim(1, "remote"))).Ok);

        var start = await hostClient.CallAsync("room.start", new JsonObject
        {
            ["seatToken"] = claim.Details!["seatToken"]!.GetValue<string>(),
        });
        Assert.True(start.Ok, start.Message);

        Assert.True((await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await guest.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        await TestSupport.WaitUntilAsync(
            () => PhaseAsync(hostClient),
            phase => phase == "play",
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);
    }

    private static async Task<string?> PhaseAsync(OrcClient client)
    {
        var view = await client.CallAsync("observe.view");
        return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
    }

    /// <summary>（回合数, 当前方相对本视角）——用于等"AI 把回合交回给我"。</summary>
    private static async Task<(int Turn, string? Active)> TurnAndSideAsync(OrcClient client)
    {
        var view = await client.CallAsync("observe.view");
        return view.Ok
            ? (view.Details!["view"]!["turn"]!.GetValue<int>(), view.Details!["view"]!["activePlayerSide"]?.GetValue<string>())
            : (0, null);
    }

    /// <summary>用自己手牌里能进入选靶的卡发起动作；返回 pending.sessionId。</summary>
    private static async Task<string?> EnterTargetingAsync(OrcClient client, bool expectPending)
    {
        var view = await client.CallAsync("observe.view");
        Assert.True(view.Ok, view.Message);

        foreach (var card in view.Details!["view"]!["selfHand"]!.AsArray())
        {
            var uid = card!["uid"]!.GetValue<string>();
            var pre = await client.CallAsync("play.unit_pre", new JsonObject { ["ref"] = uid });
            if (pre.Ok && pre.Code == ResultCodes.Targeting && pre.Details?["pending"] is { } pending)
            {
                return pending["sessionId"]!.GetValue<string>();
            }
        }

        if (expectPending)
        {
            throw new InvalidOperationException("手牌里没有能进入选靶的单位卡（夹具失效）。");
        }

        return null;
    }
}
