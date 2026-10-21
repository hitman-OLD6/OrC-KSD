using Orc.Mcp.Contracts;

namespace Orc.Mcp.Room;

/// <summary>房间状态标识（协议可见的受控字符串）。</summary>
public static class RoomStates
{
    /// <summary>等人阶段：只有房间与座位，没有活跃对局。</summary>
    public const string Lobby = "lobby";

    /// <summary>对局进行中。</summary>
    public const string InMatch = "in-match";

    /// <summary>对局已结束（不可回到房间态）。</summary>
    public const string Ended = "ended";
}

/// <summary>控制器类型标识（三种来源）。</summary>
public static class ControllerKinds
{
    /// <summary>本地玩家（房主）。</summary>
    public const string Local = "local";

    /// <summary>远程客户端（MCP/远程连接声明座位）。</summary>
    public const string Remote = "remote";

    /// <summary>服务端生成的 AI 玩家。</summary>
    public const string Ai = "ai";
}

/// <summary>
/// 座位快照（纯 DTO；不含任何引擎对象）。
/// <para>
/// **没有 <c>ready</c> 字段**：本协议不存在"座位就绪"语义（开局只要求座位被填满），
/// 残留该字段会让客户端误以为有就绪流程。
/// </para>
/// </summary>
public sealed record RoomSeatSnapshot(
    int Seat,
    string ControllerKind,
    string? DisplayName,
    bool Connected);

/// <summary>可连接地址（房主 UI 展示/复制；非回环 IPv4 ＋ 实际端口）。</summary>
public sealed record RoomListenAddress(string Ip, int Port);

/// <summary>房间快照（<c>room.info</c> 的载荷）。</summary>
public sealed record RoomSnapshot(
    string RoomId,
    string State,
    IReadOnlyList<RoomSeatSnapshot> Seats,
    string PackPolicy,
    string? PackFingerprint,
    IReadOnlyList<RoomListenAddress> Addresses);

/// <summary>构筑配置（主国/盟国；可空＝不校验配对）。</summary>
public sealed record RoomDeckConfig(string? MainFaction, string? AllyFaction);

/// <summary>声明座位（远程客户端或本地玩家）。</summary>
public sealed record RoomSeatClaim(
    int Seat,
    string DisplayName,
    IReadOnlyList<string> Deck,
    RoomDeckConfig? DeckConfig = null,
    string? SeatToken = null,
    string ControllerKind = ControllerKinds.Remote);

/// <summary>释放座位。</summary>
public sealed record RoomSeatRelease(int Seat, string? SeatToken = null);

/// <summary>生成 AI 控制器（缺省卡组＝服务端生成）。</summary>
public sealed record RoomAiSeat(
    int Seat,
    string DisplayName,
    IReadOnlyList<string>? Deck = null,
    RoomDeckConfig? DeckConfig = null);

/// <summary>
/// 房间控制面（服务层向宿主暴露的**唯一**房间入口）。
/// <para>
/// 约束：入参与出参一律纯 DTO/JSON，**不得出现 `Match`/`Player`/`Card`/`Ref&lt;T&gt;`**——
/// 本接口是 `Orc.Mcp` 与房间实现（`Orc.Server`）之间的活对象隔离点。
/// </para>
/// <para>实现由宿主注入；未注入＝房间工具不登记（无房间能力）。</para>
/// </summary>
public interface IRoomControl
{
    /// <summary>房间快照。</summary>
    RoomSnapshot Snapshot();

    /// <summary>声明座位（填充控制器）；成功时结果 details 携带 <c>seatToken</c>。</summary>
    McpToolResult Claim(RoomSeatClaim claim);

    /// <summary>释放座位（房主或持有令牌者）。</summary>
    McpToolResult Release(RoomSeatRelease release);

    /// <summary>在空座生成 AI 控制器。</summary>
    McpToolResult AddAi(RoomAiSeat ai);

    /// <summary>开局（房主/持有令牌者）；成功后房间进入对局态。</summary>
    Task<McpToolResult> StartAsync(string? seatToken, CancellationToken ct = default);

    /// <summary>强制释放座位（房主）。</summary>
    McpToolResult Kick(int seat, string? seatToken);

    /// <summary>凭座位令牌重连并重新绑定控制器。</summary>
    McpToolResult Resume(string seatToken);
}
