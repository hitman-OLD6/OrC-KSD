using Orc.Mcp.Room;

namespace Orc.Mcp.Tests;

/// <summary>房间控制面的测试替身（不触碰引擎；仅用于工具面/白名单护栏）。</summary>
internal sealed class FakeRoomControl : IRoomControl
{
    public RoomSnapshot Snapshot(RoomViewRequest request) => new(
        "test-room",
        RoomStates.Lobby,
        2,
        [
            new RoomSeatSnapshot(0, true, SeatStates.Admitted, "房主", ControllerKinds.Remote,
                request.SeeAllDeclarations || request.Seat == 0 ? ["card-0"] : null,
                request.SeeAllDeclarations || request.Seat == 0
                    ? [new RoomPackageSnapshot("pack-a", "1.0.0", "guid-a")]
                    : null),
            new RoomSeatSnapshot(1, false, SeatStates.Idle, null),
        ],
        []);
}
