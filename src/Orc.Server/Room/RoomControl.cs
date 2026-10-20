using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// <see cref="IRoomControl"/> 的服务端实现：薄适配到 <see cref="Room"/> 状态机。
/// <para>由 <c>McpService</c> 注入后，<c>room.*</c> 工具即可用；本类型不持有引擎对象。</para>
/// </summary>
public sealed class RoomControl : IRoomControl
{
    private readonly Room _room;

    public RoomControl(Room room)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
    }

    /// <inheritdoc />
    public RoomSnapshot Snapshot() => _room.Snapshot();

    /// <inheritdoc />
    public McpToolResult Claim(RoomSeatClaim claim) => _room.Claim(claim);

    /// <inheritdoc />
    public McpToolResult Release(RoomSeatRelease release) => _room.Release(release);

    /// <inheritdoc />
    public McpToolResult AddAi(RoomAiSeat ai) => _room.AddAi(ai);

    /// <inheritdoc />
    public Task<McpToolResult> StartAsync(string? seatToken, CancellationToken ct = default)
        => _room.StartAsync(seatToken, ct);

    /// <inheritdoc />
    public McpToolResult Kick(int seat, string? seatToken) => _room.Kick(seat, seatToken);

    /// <inheritdoc />
    public McpToolResult Resume(string seatToken) => _room.Resume(seatToken);
}
