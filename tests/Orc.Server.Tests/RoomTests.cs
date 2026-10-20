using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Server.Rooms;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S3 房间状态机：座位填充/令牌、AI、开局前置、释放/踢出、重连。
/// </summary>
public class RoomTests
{
    private static readonly string[] Deck = Enumerable.Range(0, 39).Select(i => $"card-{i}").ToArray();

    [Fact]
    public void Claim_FillsSeat_AndReturnsSeatToken()
    {
        var room = CreateRoom();

        var result = room.Claim(Claim(0, ControllerKinds.Local));

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Details?["seatToken"]?.GetValue<string>()));

        var snapshot = room.Snapshot();
        Assert.Equal(RoomStates.Lobby, snapshot.State);
        Assert.Equal(ControllerKinds.Local, snapshot.Seats[0].ControllerKind);
        Assert.True(snapshot.Seats[0].Ready);
        Assert.Equal(string.Empty, snapshot.Seats[1].ControllerKind);
    }

    [Fact]
    public void Claim_SameSeatTwice_IsRejectedUnlessTokenMatches()
    {
        var room = CreateRoom();
        var first = room.Claim(Claim(0, ControllerKinds.Local));
        var token = first.Details!["seatToken"]!.GetValue<string>();

        var conflict = room.Claim(Claim(0, ControllerKinds.Remote));
        Assert.False(conflict.Ok);
        Assert.Equal(McpCodes.SeatTaken, conflict.Code);

        var reconnect = room.Claim(Claim(0, ControllerKinds.Local, token));
        Assert.True(reconnect.Ok);
    }

    [Fact]
    public async Task Start_RequiresAllSeatsFilled()
    {
        var room = CreateRoom();
        room.Claim(Claim(0, ControllerKinds.Local));

        var result = await room.StartAsync(null);

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.RoomNotReady, result.Code);
    }

    [Fact]
    public async Task Start_WithFullSeats_InvokesLauncher_AndEntersInMatch()
    {
        var launcher = new RecordingLauncher();
        var room = CreateRoom(launcher: launcher);
        var host = room.Claim(Claim(0, ControllerKinds.Local));
        room.Claim(Claim(1, ControllerKinds.Remote));

        var result = await room.StartAsync(host.Details!["seatToken"]!.GetValue<string>());

        Assert.True(result.Ok);
        Assert.Single(launcher.Requests);
        Assert.Equal(2, launcher.Requests[0].Seats.Count);
        Assert.Equal(RoomStates.InMatch, room.Snapshot().State);
    }

    [Fact]
    public async Task Start_ByNonHost_IsRejected()
    {
        var room = CreateRoom();
        room.Claim(Claim(0, ControllerKinds.Local));
        var guest = room.Claim(Claim(1, ControllerKinds.Remote));

        var result = await room.StartAsync(guest.Details!["seatToken"]!.GetValue<string>());

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.Unauthorized, result.Code);
    }

    [Fact]
    public async Task Start_Failure_ReturnsToLobby()
    {
        var launcher = new RecordingLauncher { Fail = true };
        var room = CreateRoom(launcher: launcher);
        var host = room.Claim(Claim(0, ControllerKinds.Local));
        room.Claim(Claim(1, ControllerKinds.Remote));

        var result = await room.StartAsync(host.Details!["seatToken"]!.GetValue<string>());

        Assert.False(result.Ok);
        Assert.Equal(RoomStates.Lobby, room.Snapshot().State);
    }

    [Fact]
    public async Task Start_Twice_IsRejected()
    {
        var room = CreateRoom(launcher: new RecordingLauncher());
        var host = room.Claim(Claim(0, ControllerKinds.Local));
        room.Claim(Claim(1, ControllerKinds.Remote));
        var token = host.Details!["seatToken"]!.GetValue<string>();

        var first = await room.StartAsync(token);
        Assert.True(first.Ok);

        var second = await room.StartAsync(token);
        Assert.False(second.Ok);
        Assert.Equal(McpCodes.RoomStarted, second.Code);
    }

    [Fact]
    public void AddAi_FillsEmptySeat_WithGeneratedDeckAllowed()
    {
        var room = CreateRoom();

        var result = room.AddAi(new RoomAiSeat(1, "电脑"));

        Assert.True(result.Ok);
        Assert.Equal(ControllerKinds.Ai, room.Snapshot().Seats[1].ControllerKind);
    }

    [Fact]
    public void Release_ByHostToken_ClearsSeat()
    {
        var room = CreateRoom();
        var host = room.Claim(Claim(0, ControllerKinds.Local));
        room.Claim(Claim(1, ControllerKinds.Remote));

        var result = room.Release(new RoomSeatRelease(1, host.Details!["seatToken"]!.GetValue<string>()));

        Assert.True(result.Ok);
        Assert.Equal(string.Empty, room.Snapshot().Seats[1].ControllerKind);
    }

    [Fact]
    public void Kick_ByHostToken_ClearsSeat()
    {
        var room = CreateRoom();
        var host = room.Claim(Claim(0, ControllerKinds.Local));
        room.Claim(Claim(1, ControllerKinds.Remote));

        var result = room.Kick(1, host.Details!["seatToken"]!.GetValue<string>());

        Assert.True(result.Ok);
        Assert.Equal(string.Empty, room.Snapshot().Seats[1].ControllerKind);
    }

    [Fact]
    public void Resume_WithUnknownToken_IsUnauthorized()
    {
        var room = CreateRoom();

        var result = room.Resume("nope");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.Unauthorized, result.Code);
    }

    [Fact]
    public void Claim_OutOfRangeSeat_IsRejected()
    {
        var room = CreateRoom();

        var result = room.Claim(Claim(2, ControllerKinds.Local));

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.InvalidArguments, result.Code);
    }

    [Fact]
    public void Claim_WithInvalidDeck_IsRejected_ByValidator()
    {
        var room = CreateRoom();

        var result = room.Claim(new RoomSeatClaim(0, "房主", ["card-0"], ControllerKind: ControllerKinds.Local));

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.DeckInvalid, result.Code);
    }

    private static Room CreateRoom(IMatchLauncher? launcher = null)
        => new("room-1", 2, new LengthOnlyDeckValidator(), launcher);

    private static RoomSeatClaim Claim(int seat, string kind, string? token = null)
        => new(seat, $"seat-{seat}", Deck, ControllerKind: kind, SeatToken: token);

    private sealed class LengthOnlyDeckValidator : IDeckValidator
    {
        public McpToolResult? Validate(IReadOnlyList<string> deck, RoomDeckConfig? config)
            => deck.Count == 39
                ? null
                : McpToolResult.Failure(McpCodes.DeckInvalid, $"卡组为 {deck.Count} 张，要求 39 张。");
    }

    private sealed class RecordingLauncher : IMatchLauncher
    {
        public List<RoomStartRequest> Requests { get; } = [];

        public bool Fail { get; init; }

        public Task<McpToolResult> StartAsync(RoomStartRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Fail
                ? McpToolResult.Failure(McpCodes.Internal, "装配失败。")
                : McpToolResult.Success("已开局。"));
        }
    }
}
