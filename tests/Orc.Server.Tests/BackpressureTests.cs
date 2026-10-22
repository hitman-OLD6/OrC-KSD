using System.Diagnostics;
using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 慢查看者（背压的**可观测后果**）：一个"读不动"的 viewer 不得拖住引擎，也不得让其他参与者卡住。
/// <para>
/// 构造方式是真行为而非桩：<see cref="Orc.Client.OrcClient.NotificationReceived"/> 在**读循环内同步**分发，
/// 因此让 handler 阻塞就等于客户端停止读 socket——服务端推送随之积压在其每会话有界队列里。
/// </para>
/// <para>
/// 本用例断言两件**用户可感知**的事：①积压期间其他连接的调用仍能完成；②恢复读取后能追到最新状态。
/// 它**不**断言队列的 <c>DropOldest</c> 细节（那是每会话 <c>Channel.CreateBounded(256)</c> 的 BCL 行为，
/// 要复现需产生 256+ 条推送，端到端成本不成比例）。
/// </para>
/// </summary>
public sealed class BackpressureTests
{
    [Fact]
    public async Task SlowViewer_DoesNotStallEngine_AndCatchesUpAfterRecovery()
    {
        await using var fixture = await HostFixture.StartAsync();

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");
        var slow = await fixture.ConnectAsync("slow-viewer");

        await first.AttachAsync(new SessionAttachParams
        {
            Roles = [SessionRoles.Controller, SessionRoles.Viewer],
            Seat = 0,
            DisplayName = "seat0",
            Deck = TestSupport.DeckList(39),
            Packages = [TestSupport.SamplePackage()],
        });
        await HostFixture.AttachControllerAsync(second, seat: 1);
        await HostFixture.AttachViewerAsync(slow, seat: 1);

        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = slow.ConnectionId })).Ok);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);

        // 慢查看者：读循环被阻塞 ⇒ 不再从 socket 取帧（服务端推送积压）。
        using var release = new ManualResetEventSlim(false);
        slow.NotificationReceived += _ => release.Wait(TimeSpan.FromSeconds(30));

        // 积压期间：其他参与者仍能推进对局（引擎不被慢消费者拖住）。
        var stopwatch = Stopwatch.StartNew();
        var firstDone = await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        Assert.True(firstDone.Ok, $"{firstDone.Code}: {firstDone.Message}");
        var secondDone = await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });
        Assert.True(secondDone.Ok, $"{secondDone.Code}: {secondDone.Message}");
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"慢查看者拖住了引擎：换牌确认耗时 {stopwatch.Elapsed.TotalSeconds:0.0}s。");

        var match = fixture.Host.Service.Executor.Match;
        Assert.NotNull(match);
        Assert.Equal("InProgress", match!.State.ToString());

        // 恢复读取：慢查看者必须能追到**当前**回合（不是停在积压时的旧帧）。
        release.Set();

        var currentTurn = match.TurnNumber;
        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await slow.CallAsync("observe.view");
                return view.Ok ? view.Details!["view"]!["turn"]!.GetValue<int>() : -1;
            },
            turn => turn == currentTurn,
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);

        await slow.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }
}
