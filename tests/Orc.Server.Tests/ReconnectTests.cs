using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// R11：对局中 controller 断线 / 重连。
/// <para>
/// 两条不变量：①对局态断线**保留**绑定，凭座位令牌重连可跳过 <c>pending</c> 直接恢复，
/// 不需要管理员再次介入；②无效令牌被拒并落回 <c>connected</c>（不是静默半成功）。
/// </para>
/// <para>
/// 令牌只经"该连接自己的批准通知"下发（不经第三方转达），所以这里从 <c>notify/admission</c> 取。
/// </para>
/// </summary>
public sealed class ReconnectTests
{
    [Fact]
    public async Task InMatch_Reconnect_WithToken_RestoresControllerWithoutAdmin()
    {
        await using var fixture = await HostFixture.StartAsync();

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");
        await using var firstNotifications = new NotificationCollector(first);

        await HostFixture.AttachControllerAsync(first, seat: 0);
        await HostFixture.AttachControllerAsync(second, seat: 1);

        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        var admission = await firstNotifications.WaitAsync(ProtocolMethods.NotifyAdmission);
        var token = admission.Params!["seatToken"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(token), "批准时必须经该连接下发座位令牌。");

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.NotNull(fixture.Host.Service.Executor.Match);

        // 对局中 controller 断线：绑定保留（R11 与 lobby 断线语义相反）。
        await first.DisposeAsync();
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.SessionCount),
            count => count == 1,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        var resumed = await fixture.ConnectAsync("seat0-again");
        var result = await HostFixture.AttachControllerAsync(resumed, seat: 0, token: token);

        Assert.Equal(SessionStates.Admitted, result.State);
        Assert.Equal(0, result.Seat);
        Assert.Contains(SessionRoles.Controller, result.Roles);

        // 恢复后仍是同一局：对局对象未重建、seat0 的 controller 已重新绑定。
        var connections = await fixture.AdminAsync(AdminOps.ConnectionsList);
        var seat0State = connections.Payload!["connections"]!.AsArray()
            .Where(node => node!["seat"]?.GetValue<int>() == 0)
            .Select(node => node!["state"]!.GetValue<string>())
            .ToArray();
        Assert.Contains(SessionStates.Admitted, seat0State);

        await resumed.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task InMatch_Reconnect_WithInvalidToken_IsRejected()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoAsync(fixture);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);

        var intruder = await fixture.ConnectAsync("intruder");
        var result = await HostFixture.AttachControllerAsync(intruder, seat: 0, token: "not-a-real-token");

        Assert.Equal(SessionStates.Connected, result.State);
        Assert.Equal(ResultCodes.InvalidToken, result.Code);

        await intruder.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private static async Task<(Orc.Client.OrcClient First, Orc.Client.OrcClient Second)> StartTwoAsync(
        HostFixture fixture)
    {
        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");

        await HostFixture.AttachControllerAsync(first, seat: 0);
        await HostFixture.AttachControllerAsync(second, seat: 1);

        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        return (first, second);
    }
}
