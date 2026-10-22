using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// R7/R11：待决选靶**超时**必须收束动作并推送 <c>notify/target.expired</c>，
/// 不得让执行器永久停在「忙」。
/// <para>
/// 触发方式取真实链路：样例卡包的单位卡打出需要选部署槽位 ⇒ <c>play.unit_pre</c> 返回
/// <c>targeting</c> 并发布待决请求；本用例**故意不应答**，等超时。
/// </para>
/// </summary>
public sealed class TargetingTimeoutTests
{
    [Fact]
    public async Task PendingTargeting_TimesOut_PushesExpired_AndExecutorLeavesBusy()
    {
        await using var fixture = await HostFixture.StartAsync(
            targetSelectionTimeout: TimeSpan.FromSeconds(2));

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");
        await using var notifications = new NotificationCollector(first);

        // 真实玩家的形态是**兼任**：controller（推输入）＋ viewer（收视角）。
        // 纯 controller 按 R7 收不到任何输出，连 observe.view 都会因角色不足被拒。
        await first.AttachAsync(new SessionAttachParams
        {
            Roles = [SessionRoles.Controller, SessionRoles.Viewer],
            Seat = 0,
            DisplayName = "seat0",
            Deck = TestSupport.DeckList(39),
            Packages = [TestSupport.SamplePackage()],
        });
        await HostFixture.AttachControllerAsync(second, seat: 1);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        await WaitForPlayPhaseAsync(fixture, first);

        var sessionId = await EnterTargetingAsync(first);
        Assert.NotNull(sessionId);
        Assert.True(fixture.Host.Service.Executor.IsBusy, "待决期间执行器应处于忙态。");

        var expired = await notifications.WaitAsync(
            ProtocolMethods.NotifyTargetExpired,
            timeout: TimeSpan.FromSeconds(20));
        Assert.Equal(sessionId, expired.Params!["sessionId"]!.GetValue<string>());

        // 超时＝取消：动作收束、执行器脱离忙态（这是「不会永久挂起」的可观测判据）。
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.Service.Executor.IsBusy),
            busy => !busy,
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private static async Task WaitForPlayPhaseAsync(HostFixture fixture, Orc.Client.OrcClient client)
    {
        var probe = await client.CallAsync("observe.view");
        Assert.True(probe.Ok, $"observe.view 失败：{probe.Code}: {probe.Message}");

        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await client.CallAsync("observe.view");
                return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
            },
            phase => string.Equals(phase, "play", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);
    }

    /// <summary>遍历手牌，打出一张需要选靶的牌；返回待决会话标识（无则 null）。</summary>
    private static async Task<string?> EnterTargetingAsync(Orc.Client.OrcClient client)
    {
        var view = await client.CallAsync("observe.view");
        var hand = view.Details!["view"]!["selfHand"]?.AsArray();
        if (hand is null)
        {
            return null;
        }

        foreach (var card in hand)
        {
            var reference = card!["uid"]?.GetValue<string>()
                ?? card["ref"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var pre = await client.CallAsync("play.unit_pre", new JsonObject { ["ref"] = reference });
            if (pre.Ok && string.Equals(pre.Code, ResultCodes.Targeting, StringComparison.Ordinal))
            {
                return pre.Details!["pending"]!["sessionId"]!.GetValue<string>();
            }
        }

        return null;
    }
}
