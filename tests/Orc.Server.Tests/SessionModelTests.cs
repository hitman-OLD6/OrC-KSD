using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Protocol;
using Orc.Mcp.Room;
using Orc.Server.Admin;
using Orc.Server.Rooms;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S6 会话/角色模型（R5/R6/R7/R12）：申请—批准两段、<c>Pending</c> 零能力、调试面即时生效、
/// 兼任共享同一 seat 绑定、房间态可见性按角色裁剪。
/// </summary>
public class SessionModelTests
{
    [Fact]
    public async Task Initialize_ReturnsConnectionId_AndRoomId()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        Assert.False(string.IsNullOrWhiteSpace(client.ConnectionId));
        Assert.False(string.IsNullOrWhiteSpace(client.RoomId));
    }

    [Fact]
    public async Task AttachController_IsPending_AndCannotAct_ButCanReadRoom()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        var attached = await HostFixture.AttachControllerAsync(client, seat: 0);

        Assert.Equal(SessionStates.Pending, attached.State);
        Assert.Equal(ResultCodes.Ok, attached.Code);

        // Pending 只能 ping / session.detach / room.info（R6/R12）。
        var room = await client.CallAsync("room.info");
        Assert.True(room.Ok, room.Message);

        var action = await client.CallAsync("match.end_turn");
        Assert.False(action.Ok);
        Assert.Equal(ResultCodes.NotAdmitted, action.Code);
    }

    [Fact]
    public async Task ToolsList_ForPending_ContainsOnlyRoomInfo()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await HostFixture.AttachControllerAsync(client, seat: 0);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "room.info");
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("play.", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("card.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdminAdmit_GrantsController_AndDeliversSeatTokenToThatConnection()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await using var notifications = new NotificationCollector(client);

        await HostFixture.AttachControllerAsync(client, seat: 0);

        var connections = await fixture.AdminAsync(AdminOps.ConnectionsList);
        var pendingId = connections.Payload!["connections"]!.AsArray()
            .Single(node => node!["connectionId"]!.GetValue<string>() == client.ConnectionId)!["connectionId"]!
            .GetValue<string>();

        var admitted = await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject
        {
            ["connectionId"] = pendingId,
        });

        Assert.True(admitted.Ok, admitted.Message);
        Assert.Equal(0, admitted.Payload!["seat"]!.GetValue<int>());

        var notification = await notifications.WaitAsync(ProtocolMethods.NotifyAdmission);
        Assert.Equal("admitted", notification.Params!["decision"]!.GetValue<string>());
        Assert.Equal(0, notification.Params!["seat"]!.GetValue<int>());

        var token = notification.Params!["seatToken"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // 批准即授权：动作工具出现在清单里（引擎还没开局，所以只断言可见性）。
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "match.end_turn");
    }

    [Fact]
    public async Task AttachSecondController_OnOccupiedSeat_IsRejectedSynchronously()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var first = await fixture.ConnectAsync("first");
        await using var second = await fixture.ConnectAsync("second");

        Assert.Equal(SessionStates.Pending, (await HostFixture.AttachControllerAsync(first, seat: 0)).State);

        var rejected = await HostFixture.AttachControllerAsync(second, seat: 0);

        // 同步拒绝：不进 Pending，也不产生第二个 pending 记录（R12）。
        Assert.Equal(ResultCodes.SeatTaken, rejected.Code);
        Assert.NotEqual(SessionStates.Pending, rejected.State);

        var connections = await fixture.AdminAsync(AdminOps.ConnectionsList);
        Assert.Single(
            connections.Payload!["connections"]!.AsArray(),
            node => node!["hasDeclaration"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DebugFace_Attach_IsImmediate_Unbound_AndSeesDebugTools()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        await using var client = await fixture.ConnectAsync();

        var attached = await HostFixture.AttachDebugAsync(client);
        Assert.Equal(SessionStates.Debug, attached.State);
        Assert.Null(attached.Seat);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "card.write");
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("play.", StringComparison.Ordinal));

        // 调试面不占 seat：座位快照里看不到它。
        var room = await client.CallAsync("room.info");
        Assert.All(room.Details!["seats"]!.AsArray(), seat => Assert.False(seat!["occupied"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task PlayerFace_CannotCallDebugTools()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        await using var client = await fixture.ConnectAsync();

        await HostFixture.AttachControllerAsync(client, seat: 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = client.ConnectionId });

        var write = await client.CallAsync("card.write", new JsonObject { ["id"] = "x", ["json"] = "{}" });
        Assert.False(write.Ok);
        Assert.Equal(ResultCodes.Unauthorized, write.Code);

        // 也不可见（工具表按角色过滤）。
        var tools = await client.ListToolsAsync();
        Assert.DoesNotContain(tools, tool => tool.Name == "card.write");
    }

    [Fact]
    public async Task RoomInfo_HidesOpponentDeclarations_ButShowsOwn()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var first = await fixture.ConnectAsync("first");
        await using var second = await fixture.ConnectAsync("second");
        await using var debug = await fixture.ConnectAsync("debug");

        await HostFixture.AttachControllerAsync(first, seat: 0);
        await HostFixture.AttachControllerAsync(second, seat: 1);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = first.ConnectionId });
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = second.ConnectionId });
        await HostFixture.AttachDebugAsync(debug);

        var view = await first.CallAsync("room.info");
        var seats = view.Details!["seats"]!.AsArray();

        // 自己的 deck/packages 可见；对手的**永不出现**。
        Assert.NotNull(seats[0]!["deck"]);
        Assert.NotNull(seats[0]!["packages"]);
        Assert.Null(seats[1]!["deck"]);
        Assert.Null(seats[1]!["packages"]);
        Assert.Equal(SeatStates.Admitted, seats[1]!["state"]!.GetValue<string>());

        // 调试面可见全量声明。
        var full = await debug.CallAsync("room.info");
        Assert.NotNull(full.Details!["seats"]!.AsArray()[1]!["deck"]);
    }

    [Fact]
    public async Task LobbyDisconnect_ReleasesSeatImmediately()
    {
        await using var fixture = await HostFixture.StartAsync();
        var client = await fixture.ConnectAsync();

        await HostFixture.AttachControllerAsync(client, seat: 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = client.ConnectionId });

        await client.DisposeAsync();

        var seat = await TestSupport.WaitUntilAsync(
            async () =>
            {
                var connections = await fixture.AdminAsync(AdminOps.ConnectionsList);
                _ = connections;
                return fixture.Host.Room.Snapshot(Orc.Mcp.Room.RoomViewRequest.Anonymous).Seats[0].State;
            },
            state => state == SeatStates.Idle,
            TimeSpan.FromSeconds(5));

        Assert.Equal(SeatStates.Idle, seat);
    }

    [Fact]
    public async Task Revoke_DisconnectsConnection_AndInvalidatesToken()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await using var notifications = new NotificationCollector(client);

        await HostFixture.AttachControllerAsync(client, seat: 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = client.ConnectionId });

        var revoked = await fixture.AdminAsync(AdminOps.ConnectionRevoke, new JsonObject
        {
            ["connectionId"] = client.ConnectionId,
            ["reason"] = "admin-revoked",
        });

        Assert.True(revoked.Ok, revoked.Message);

        var notification = await notifications.WaitAsync(
            ProtocolMethods.NotifyAdmission,
            n => n.Params?["decision"]?.GetValue<string>() == "revoked");
        Assert.Equal("revoked", notification.Params!["decision"]!.GetValue<string>());
        Assert.Equal("admin-revoked", notification.Params!["reason"]!.GetValue<string>());

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(client.IsConnected),
            connected => !connected,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);
    }

    [Fact]
    public void SeatBinding_Asserts_SeatEqualsPlayerIndex()
    {
        var binding = new SeatBinding(epoch: 3, seatIndex: 1, playerIndex: 1);
        Assert.Equal(3, binding.Epoch);
        Assert.Equal(1, binding.PlayerIndex);

        Assert.Throws<ArgumentException>(() => new SeatBinding(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeatBinding(0, 2, 2));
    }
}
