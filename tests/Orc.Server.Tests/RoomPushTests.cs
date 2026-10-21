using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Mcp.Room;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 房间态推送（F7）：claim/release/kick/add_ai 与房间态断线释放都会广播 <c>notify/room</c>；
/// 载荷与 <c>room.info</c> 的 details 同形，且**不含** <c>ready</c>。
/// </summary>
public class RoomPushTests
{
    [Fact]
    public async Task GuestClaim_BroadcastsRoomSnapshot_WithoutReady()
    {
        using var temp = new TestSupport.TempDirectory("orc-room-push");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);

        var claims = await hostClient.CallAsync("room.claim", TestSupport.Claim(0));
        Assert.True(claims.Ok, claims.Message);

        var pushes = new List<JsonNode>();
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyRoom && notification.Params is { } payload)
            {
                lock (pushes)
                {
                    pushes.Add(payload);
                }
            }
        };

        Assert.True((await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote"))).Ok);

        var payload = await WaitForRoomPushAsync(pushes, room => SeatKind(room, 1) == "remote");

        Assert.Equal("remote", SeatKind(payload, 1));
        Assert.Null(payload["seats"]!.AsArray()[0]!["ready"]);
        Assert.Null(payload["seats"]!.AsArray()[1]!["ready"]);
    }

    /// <summary><c>room.info</c> 与 <c>notify/room</c> 必须同形（同一个序列化来源）。</summary>
    [Fact]
    public async Task RoomInfo_And_NotifyRoom_HaveSameShape()
    {
        using var temp = new TestSupport.TempDirectory("orc-room-push");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);

        Assert.True((await hostClient.CallAsync("room.claim", TestSupport.Claim(0))).Ok);

        var pushes = new List<JsonNode>();
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyRoom && notification.Params is { } payload)
            {
                lock (pushes)
                {
                    pushes.Add(payload);
                }
            }
        };

        Assert.True((await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote"))).Ok);
        var pushed = await WaitForRoomPushAsync(pushes, room => SeatKind(room, 1) == "remote");

        var info = await hostClient.CallAsync("room.info");
        Assert.True(info.Ok, info.Message);
        var details = info.Details!;

        var pushedKeys = ((JsonObject)pushed).Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var infoKeys = ((JsonObject)details).Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.Equal(infoKeys, pushedKeys);

        // 座位条目形状一致（字段名集合相同）。
        var pushedSeatKeys = ((JsonObject)pushed["seats"]!.AsArray()[1]!).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var infoSeatKeys = ((JsonObject)details["seats"]!.AsArray()[1]!).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(infoSeatKeys, pushedSeatKeys);
    }

    [Fact]
    public async Task ReleaseAndKick_And_AddAi_BroadcastRoom()
    {
        using var temp = new TestSupport.TempDirectory("orc-room-push");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var pushes = new List<JsonNode>();
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyRoom && notification.Params is { } payload)
            {
                lock (pushes)
                {
                    pushes.Add(payload);
                }
            }
        };

        Assert.True((await hostClient.CallAsync("room.release", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        })).Ok);

        await WaitRoomKindAsync(hostClient, seat: 1, expected: string.Empty);

        Assert.True((await hostClient.CallAsync("room.add_ai", new JsonObject { ["seat"] = 1 })).Ok);
        await WaitRoomKindAsync(hostClient, seat: 1, expected: ControllerKinds.Ai);

        Assert.True((await hostClient.CallAsync("room.kick", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        })).Ok);
        await WaitRoomKindAsync(hostClient, seat: 1, expected: string.Empty);

        lock (pushes)
        {
            Assert.True(pushes.Count >= 3, $"notify/room 次数不足（{pushes.Count}）。");
        }
    }

    /// <summary>房间态断线释放座位后也广播（房主能看到"座位空了"）。</summary>
    [Fact]
    public async Task LobbyDisconnect_BroadcastsRoom()
    {
        using var temp = new TestSupport.TempDirectory("orc-room-push");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        var guest = await TestSupport.ConnectAsync(host.Port);

        Assert.True((await hostClient.CallAsync("room.claim", TestSupport.Claim(0))).Ok);
        Assert.True((await guest.CallAsync("room.claim", TestSupport.Claim(1, "remote"))).Ok);

        var emptied = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostClient.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyRoom
                && notification.Params is { } payload
                && payload["seats"]!.AsArray()[1]!["controllerKind"]!.GetValue<string>().Length == 0)
            {
                emptied.TrySetResult(payload);
            }
        };

        await guest.DisposeAsync();

        var payload = await emptied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(string.Empty, payload["seats"]!.AsArray()[1]!["controllerKind"]!.GetValue<string>());
    }

    /// <summary>等一条满足条件的 <c>notify/room</c>（房间态可能连推多条，不能假设"第一条就是想要的"）。</summary>
    private static async Task<JsonNode> WaitForRoomPushAsync(List<JsonNode> pushes, Func<JsonNode, bool> predicate)
        => await TestSupport.WaitUntilAsync(
            () =>
            {
                lock (pushes)
                {
                    return Task.FromResult(pushes.FirstOrDefault(predicate));
                }
            },
            match => match is not null,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true) ?? throw new TimeoutException("未收到匹配的 notify/room。");

    private static string SeatKind(JsonNode room, int seat)
        => room["seats"]!.AsArray()[seat]!["controllerKind"]!.GetValue<string>();

    private static async Task WaitRoomKindAsync(OrcClient client, int seat, string expected)        => await TestSupport.WaitUntilAsync(
            async () =>
            {
                var info = await client.CallAsync("room.info");
                return info.Details!["seats"]!.AsArray()[seat]!["controllerKind"]!.GetValue<string>();
            },
            kind => kind == expected,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

    private static Task<ServerHost> StartHostAsync(string sandboxRoot)
        => ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            EnforceDeckRules = false,
        });
}
