using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Protocol;

namespace Orc.Server.Rooms;

/// <summary>房间内部状态（与协议字符串一一对应）。</summary>
public enum RoomState
{
    Lobby,
    Starting,
    InMatch,
    Ended,
}

/// <summary>座位内部状态（与 <see cref="SeatStates"/> 一一对应）。</summary>
public enum SeatState
{
    Idle,
    Pending,
    Admitted,
    Disconnected,
    Revoked,
}

/// <summary>
/// seat ↔ 对局设施的绑定（**纯数据**，S8）：seat 不持有任何引擎活对象，只持 <c>(纪元, playerIndex)</c>。
/// <para>
/// 失效判定＝**纪元不匹配 或 当前无对局**——不用"引用置空"、不用显式 Dispose、不用 GC 可达性。
/// </para>
/// <para>
/// <c>Seat.Index ↔ PlayerIndex</c> 是恒等式，在**构造处**断言（越早失败越好）。
/// </para>
/// </summary>
public readonly record struct SeatBinding
{
    /// <summary>创建绑定（<paramref name="seatIndex"/> 必须等于 <paramref name="playerIndex"/>）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">座位/玩家索引越界。</exception>
    /// <exception cref="ArgumentException">座位索引与玩家索引不等（恒等式被破坏）。</exception>
    public SeatBinding(int epoch, int seatIndex, int playerIndex)
    {
        if (seatIndex is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seatIndex), seatIndex, "座位索引须为 0 或 1。");
        }

        if (playerIndex != seatIndex)
        {
            throw new ArgumentException(
                $"座位索引 {seatIndex} 与玩家索引 {playerIndex} 不相等（seat ↔ playerIndex 恒等）。",
                nameof(playerIndex));
        }

        Epoch = epoch;
        PlayerIndex = playerIndex;
    }

    /// <summary>绑定创建时的房间栈纪元（轮换时递增）。</summary>
    public int Epoch { get; }

    /// <summary>引擎玩家索引（恒等于座位索引）。</summary>
    public int PlayerIndex { get; }
}

/// <summary>座位申请输入（来自 <c>session.attach</c>，已由宿主归一化）。</summary>
/// <param name="SessionId">申请连接标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="Deck">卡组（可空＝AI seat 或未声明）。</param>
/// <param name="DeckConfig">构筑配置。</param>
/// <param name="Packages">卡包声明（引用形态）。</param>
public sealed record SeatApplication(
    string SessionId,
    string? DisplayName,
    IReadOnlyList<string> Deck,
    DeckConfigPayload? DeckConfig,
    IReadOnlyList<PackageRef> Packages);

/// <summary>座位申请结局。</summary>
/// <param name="Ok">是否受理。</param>
/// <param name="Code">拒绝码（成功＝<c>ok</c>）。</param>
/// <param name="Message">人读说明。</param>
public sealed record SeatApplyOutcome(bool Ok, string Code, string? Message = null)
{
    /// <summary>成功。</summary>
    public static readonly SeatApplyOutcome Accepted = new(true, McpCodes.Ok);

    /// <summary>失败。</summary>
    public static SeatApplyOutcome Reject(string code, string message) => new(false, code, message);
}

/// <summary>开局座位输入（卡组与构筑配置已在批准处校验过）。</summary>
public sealed record RoomSeatStart(
    int Seat,
    string DisplayName,
    IReadOnlyList<string> Deck,
    RoomDeckConfig? DeckConfig,
    string ControllerKind,
    IReadOnlyList<PackageRef> Packages);
