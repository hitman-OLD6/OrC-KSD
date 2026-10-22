using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// <see cref="IRoomControl"/> 的服务端实现：薄适配到 <see cref="Room"/> 状态机（**只读**）。
/// <para>
/// 管理动作（开局/终止/绑定/撤销）不在这里——它们属于管理面（<c>AdminApi</c>），不进工具表。
/// </para>
/// </summary>
public sealed class RoomControl : IRoomControl
{
    private readonly Room _room;

    public RoomControl(Room room)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
    }

    /// <inheritdoc />
    public RoomSnapshot Snapshot(RoomViewRequest request) => _room.Snapshot(request);
}
