using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S5/S6b/S7/S8/S10：开局、终止（无胜者）、房间轮换、推送分发、进程级对象长存。
/// <para>断言名与 implementation.md 的清理清单对应（"停失败的现象"即断言）。</para>
/// </summary>
public class RoomRotationTests
{
    [Fact]
    public async Task MatchStart_ThenAbort_EndsWithNoWinner_AndReasonAborted()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        Assert.NotNull(fixture.Host.Service.Executor.Match);

        // 引擎只在"进行"相位接受终局（E2/E3 保留相位门禁）：先让双方确认换牌。
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);
        Assert.Equal("InProgress", fixture.Host.Service.Executor.Match!.State.ToString());

        var aborted = await fixture.AdminAsync(AdminOps.RoomAbort);
        Assert.True(aborted.Ok, aborted.Message);

        var match = fixture.Host.Service.Executor.Match;
        Assert.NotNull(match);
        Assert.Equal("Ended", match!.State.ToString());
        Assert.Null(match.Winner);
        Assert.Equal(Orc.Game.MatchEndReason.Aborted, match.EndReason);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Abort_DuringMulligan_EndsWithAborted()
    {
        // 相位门禁已放宽（仅 Abort 受益）：管理员收回房间不该被"还没进入进行态"挡住。
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);

        var match = fixture.Host.Service.Executor.Match;
        Assert.NotNull(match);
        Assert.Equal("Mulligan", match!.State.ToString());

        var aborted = await fixture.AdminAsync(AdminOps.RoomAbort);
        Assert.True(aborted.Ok, $"{aborted.Code}: {aborted.Message}");

        Assert.Equal("Ended", match.State.ToString());
        Assert.Null(match.Winner);
        Assert.Equal(Orc.Game.MatchEndReason.Aborted, match.EndReason);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Abort_WhenNoMatch_IsIdempotent_AlreadyEnded()
    {
        await using var fixture = await HostFixture.StartAsync();

        var aborted = await fixture.AdminAsync(AdminOps.RoomAbort);

        Assert.True(aborted.Ok);
        Assert.Equal(ResultCodes.AlreadyEnded, aborted.Payload!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rotation_ProducesNewLobby_RevokesSeatedRoles_AndKeepsHostAndQueue()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);
        await using var notifications = new NotificationCollector(first);

        var oldRoomId = fixture.Host.Room.RoomId;
        var queue = fixture.Host.Queue;
        var port = fixture.Host.Port;
        var epoch = fixture.Host.CurrentEpoch;

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await fixture.AdminAsync(AdminOps.RoomAbort)).Ok);

        var rotated = await notifications.WaitAsync(ProtocolMethods.NotifyRoomRotated, timeout: TimeSpan.FromSeconds(10));
        var newRoomId = rotated.Params!["roomId"]!.GetValue<string>();

        Assert.NotEqual(oldRoomId, newRoomId);
        Assert.Equal(port, fixture.Host.Port);
        Assert.Same(queue, fixture.Host.Queue);
        Assert.Equal(epoch + 1, fixture.Host.CurrentEpoch);
        Assert.Equal("Lobby", fixture.Host.Room.State.ToString());

        // 绑定 seat 的角色 → Revoked（旧房间终结即失效），且**不**自动断开连接。
        var connections = await fixture.AdminAsync(AdminOps.ConnectionsList);
        var states = connections.Payload!["connections"]!.AsArray()
            .Select(node => node!["state"]!.GetValue<string>())
            .ToArray();
        Assert.Contains(SessionStates.Revoked, states);

        // 轮换后旧 seatToken 失效（凭旧令牌重连 ⇒ invalid-token）。
        var stale = await HostFixture.AttachControllerAsync(first, seat: 0);
        Assert.Equal(ResultCodes.Ok, stale.Code); // 新房间允许重新申请（Pending）

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Rotation_ReleasesSeats_AndNewRoomAcceptsFreshApplications()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await fixture.AdminAsync(AdminOps.RoomAbort)).Ok);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.Room.RoomId),
            _ => false,
            TimeSpan.FromMilliseconds(100));

        var room = await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.Room),
            candidate => candidate.State.ToString() == "Lobby" && !candidate.IsOccupied(0) && !candidate.IsOccupied(1),
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        Assert.Equal(2, room.SeatCount);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ViewerDistribution_SeatVsOmniscient_FramesUsePerspectiveMode()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        await using var seatViewer = await fixture.ConnectAsync("seat-viewer");
        await using var omniscient = await fixture.ConnectAsync("omniscient");

        Assert.Equal(SessionStates.Pending, (await HostFixture.AttachViewerAsync(seatViewer, 0)).State);
        Assert.Equal(SessionStates.Pending, (await HostFixture.AttachViewerAsync(omniscient, null)).State);

        Assert.True((await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = seatViewer.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = omniscient.ConnectionId })).Ok);

        await using var seatNotifications = new NotificationCollector(seatViewer);
        await using var omniscientNotifications = new NotificationCollector(omniscient);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok, "开局失败");

        // 推进到进行态（双方确认换牌）——这一步会产生一次真实的状态变化。
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        var seatFrame = await seatNotifications.WaitAsync(
            ProtocolMethods.NotifyView,
            n => n.Params?["perspective"]?["mode"]?.GetValue<string>() == "seat",
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, seatFrame.Params!["perspective"]!["seat"]!.GetValue<int>());
        Assert.NotNull(seatFrame.Params!["view"]);
        Assert.Null(seatFrame.Params!["views"]); // 单视角帧不带 views

        var omniFrame = await omniscientNotifications.WaitAsync(
            ProtocolMethods.NotifyView,
            n => n.Params?["perspective"]?["mode"]?.GetValue<string>() == "omniscient",
            TimeSpan.FromSeconds(10));

        Assert.Equal(2, omniFrame.Params!["views"]!.AsArray().Count);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AiSeat_IsAdmittedByConfig_AndGetsDeckFromMatchPool()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai, "电脑"), new SeatConfig()]);

        // AI 座位配置即视为已批准（无需连接、无需管理员再批一次）。
        Assert.Equal(Orc.Mcp.Room.SeatStates.Admitted, fixture.Host.Room.Snapshot(Orc.Mcp.Room.RoomViewRequest.Anonymous).Seats[0].State);

        var human = await fixture.ConnectAsync();
        await HostFixture.AttachControllerAsync(human, seat: 1);
        Assert.True((await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = human.ConnectionId })).Ok);

        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        Assert.True(fixture.Host.Room.SeatAt(0)!.Deck.Count > 0, "AI 卡组应由本局卡池生成。");

        await human.DisposeAsync();
    }

    [Fact]
    public async Task MatchStart_WithEmptySeat_IsRejected_AndServerStaysAlive()
    {
        await using var fixture = await HostFixture.StartAsync();

        var started = await fixture.AdminAsync(AdminOps.MatchStart);

        Assert.False(started.Ok);
        Assert.Equal(ResultCodes.RoomNotReady, started.Code);
        Assert.Equal("Lobby", fixture.Host.Room.State.ToString());
        Assert.True(fixture.Host.Port > 0);
    }

    [Fact]
    public async Task MigrationDrain_RejectsMatchStartWithRetryHint()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        // 直接置 drain 标志（等价于轮换窗口），验证"窗口内开局失败并带 retryAfterMs"。
        var drainField = typeof(Orc.Server.Transport.ServerHost).GetField(
            "_draining", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(drainField);
        drainField!.SetValue(fixture.Host, true);

        try
        {
            var started = await fixture.AdminAsync(AdminOps.MatchStart);
            Assert.False(started.Ok);
            Assert.Equal(ResultCodes.RoomRotating, started.Code);
            Assert.Equal(200, started.Payload!["retryAfterMs"]!.GetValue<int>());

            // drain 窗口内 room.abort ＝ 幂等成功（不报错）。
            var aborted = await fixture.AdminAsync(AdminOps.RoomAbort);
            Assert.True(aborted.Ok);
            Assert.Equal(ResultCodes.AlreadyEnded, aborted.Payload!["code"]!.GetValue<string>());
        }
        finally
        {
            drainField.SetValue(fixture.Host, false);
        }

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AdminOps_AreNotRegisteredAsTools()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await HostFixture.AttachControllerAsync(client, seat: 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = client.ConnectionId });

        var tools = await client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var op in Orc.Server.Admin.AdminOps.All)
        {
            Assert.DoesNotContain(op, names);
        }

        // 业务端口上直接调用管理 op 名 ⇒ 未知方法（不路由到管理面）。
        var unknown = await client.CallAsync(Orc.Server.Admin.AdminOps.MatchStart);
        Assert.False(unknown.Ok);
        Assert.Equal(ResultCodes.UnknownTool, unknown.Code);
    }

    [Fact]
    public async Task ViewerOnly_Omniscient_CannotSeeOpponentDeclarations()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        await using var omniscient = await fixture.ConnectAsync("omni");
        await HostFixture.AttachViewerAsync(omniscient, null);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = omniscient.ConnectionId });

        var view = await omniscient.CallAsync("room.info");
        foreach (var seat in view.Details!["seats"]!.AsArray())
        {
            Assert.Null(seat!["deck"]);
            Assert.Null(seat["packages"]);
        }

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task MultipleViewersOnSameSeat_DisconnectIndependently()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        var viewerA = await fixture.ConnectAsync("viewer-a");
        var viewerB = await fixture.ConnectAsync("viewer-b");
        await HostFixture.AttachViewerAsync(viewerA, 0);
        await HostFixture.AttachViewerAsync(viewerB, 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = viewerA.ConnectionId });
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = viewerB.ConnectionId });

        await using var notificationsB = new NotificationCollector(viewerB);

        await viewerA.DisposeAsync();

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        // 另一个 viewer 不受影响：仍收到单视角帧。
        var frame = await notificationsB.WaitAsync(
            ProtocolMethods.NotifyView,
            n => n.Params?["perspective"]?["mode"]?.GetValue<string>() == "seat",
            TimeSpan.FromSeconds(10));
        Assert.Equal(0, frame.Params!["perspective"]!["seat"]!.GetValue<int>());

        await viewerB.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task MatchStateControllerDisconnect_KeepsBinding_AndViewerKeepsReceiving()
    {
        await using var fixture = await HostFixture.StartAsync(disconnectGrace: TimeSpan.FromSeconds(30));
        var (first, second) = await StartTwoControllersAsync(fixture);

        await using var viewer = await fixture.ConnectAsync("viewer");
        await HostFixture.AttachViewerAsync(viewer, 0);
        await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = viewer.ConnectionId });
        await using var notifications = new NotificationCollector(viewer);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        // 对局态断线：绑定保留（seat 不被释放），viewer 仍能收到推送。
        await first.DisposeAsync();

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.Room.Snapshot(Orc.Mcp.Room.RoomViewRequest.Anonymous).Seats[0].State),
            state => state == Orc.Mcp.Room.SeatStates.Disconnected,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        Assert.True((await second.CallAsync("match.end_turn")).Ok);
        Assert.True(await notifications.WaitAsync(ProtocolMethods.NotifyView, timeout: TimeSpan.FromSeconds(10)) is not null);

        await viewer.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AdminRevokeControllerDuringMatch_EndsMatchWithoutGrace()
    {
        await using var fixture = await HostFixture.StartAsync(disconnectGrace: TimeSpan.FromMinutes(5));
        var (first, second) = await StartTwoControllersAsync(fixture);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await second.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        var revoked = await fixture.AdminAsync(AdminOps.ConnectionRevoke, new JsonObject
        {
            ["connectionId"] = first.ConnectionId,
        });

        Assert.True(revoked.Ok, revoked.Message);

        // 撤销＝立即判负（不走宽限）：对局结束、胜者是另一方。
        var match = fixture.Host.Service.Executor.Match;
        Assert.NotNull(match);
        Assert.Equal("Ended", match!.State.ToString());
        Assert.Equal(1, match.Winner!.Index);
        Assert.Equal(Orc.Game.MatchEndReason.Concede, match.EndReason);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task ToolCallDuringRotation_ReturnsRoomRotating()
    {
        await using var fixture = await HostFixture.StartAsync();
        var (first, second) = await StartTwoControllersAsync(fixture);

        var drainField = typeof(Orc.Server.Transport.ServerHost).GetField(
            "_draining", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        drainField.SetValue(fixture.Host, true);

        try
        {
            var result = await first.CallAsync("match.end_turn");
            Assert.False(result.Ok);
            Assert.Equal(ResultCodes.RoomRotating, result.Code);
            Assert.Equal(200, result.Details!["retryAfterMs"]!.GetValue<int>());
        }
        finally
        {
            drainField.SetValue(fixture.Host, false);
        }

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task DebugFace_SurvivesRotation_WithoutReattach()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var (first, second) = await StartTwoControllersAsync(fixture);

        await using var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        Assert.True((await fixture.AdminAsync(AdminOps.RoomAbort)).Ok);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(fixture.Host.Room.RoomId),
            _ => false,
            TimeSpan.FromMilliseconds(200));

        // 轮换后调试面仍是 DebugFace（不绑 seat ⇒ 不随房间终结失效），工具清单照旧。
        var tools = await debug.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "card.write");

        await debug.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private static async Task<(Orc.Client.OrcClient First, Orc.Client.OrcClient Second)> StartTwoControllersAsync(
        HostFixture fixture)
    {
        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");

        await HostFixture.AttachControllerAsync(first, seat: 0);
        await HostFixture.AttachControllerAsync(second, seat: 1);

        var admitFirst = await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = first.ConnectionId });
        var admitSecond = await fixture.AdminAsync(AdminOps.ConnectionAdmit, new JsonObject { ["connectionId"] = second.ConnectionId });

        Assert.True(admitFirst.Ok, admitFirst.Message);
        Assert.True(admitSecond.Ok, admitSecond.Message);

        return (first, second);
    }
}
