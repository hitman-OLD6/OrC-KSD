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

/// <summary>座位状态（协议可见的受控字符串；R12 的 <c>state</c> 字段）。</summary>
public static class SeatStates
{
    /// <summary>空闲。</summary>
    public const string Idle = "idle";

    /// <summary>已被申请、尚未批准。</summary>
    public const string Pending = "pending";

    /// <summary>已批准（或 AI 配置态）。</summary>
    public const string Admitted = "admitted";

    /// <summary>已批准但当前断线（对局态保留绑定）。</summary>
    public const string Disconnected = "disconnected";

    /// <summary>许可被撤销。</summary>
    public const string Revoked = "revoked";
}

/// <summary>控制器类型标识（三种来源；仅用于展示，不参与鉴权）。</summary>
public static class ControllerKinds
{
    /// <summary>本地玩家（历史值；新模型中不再参与门禁）。</summary>
    public const string Local = "local";

    /// <summary>远程客户端（MCP/远程连接声明座位）。</summary>
    public const string Remote = "remote";

    /// <summary>服务端生成的 AI 玩家。</summary>
    public const string Ai = "ai";
}

/// <summary>卡包声明（引用形态；房间快照里只回显**自己的**）。</summary>
public sealed record RoomPackageSnapshot(string PackageId, string? Version = null, string? Guid = null);

/// <summary>
/// 房间座位快照（纯 DTO；不含任何引擎对象）。
/// <para>
/// **按调用者角色构造**（不是"构造全量再裁剪"）：<see cref="Deck"/>/<see cref="Packages"/> 只在自己那座、
/// 或调试面/管理员读取时出现——对手的构筑与卡包声明永远不可见（全知 viewer 亦不可）。
/// </para>
/// </summary>
public sealed record RoomSeatSnapshot(
    int Seat,
    bool Occupied,
    string State,
    string? DisplayName,
    string? Kind = null,
    IReadOnlyList<string>? Deck = null,
    IReadOnlyList<RoomPackageSnapshot>? Packages = null)
{
    /// <summary>
    /// 座位声明的**卡组标识**（D28：快照回显「作者提交的原始形态」，不展开成卡 id 列表）。
    /// 给了它时 <see cref="Deck"/> 恒为 null——声明形态对对手视图天然安全。
    /// </summary>
    public string? DeckId { get; init; }
}

/// <summary>可连接地址（房主 UI 展示/复制；非回环 IPv4 ＋ 实际端口）。</summary>
public sealed record RoomListenAddress(string Ip, int Port);

/// <summary>房间快照（<c>room.info</c> 的载荷）。</summary>
public sealed record RoomSnapshot(
    string RoomId,
    string State,
    int SeatCount,
    IReadOnlyList<RoomSeatSnapshot> Seats,
    IReadOnlyList<RoomListenAddress> Addresses);

/// <summary>构筑配置（主国/盟国；可空＝不校验配对）。</summary>
public sealed record RoomDeckConfig(string? MainFaction, string? AllyFaction);

/// <summary>
/// 读取房间快照时的调用者视角（由宿主按连接角色构造；服务层不感知会话模型）。
/// </summary>
/// <param name="Seat">调用者绑定的座位（未绑＝null）。</param>
/// <param name="SeeAllDeclarations">是否可见全部卡组/卡包声明（调试面与管理员＝true）。</param>
public sealed record RoomViewRequest(int? Seat = null, bool SeeAllDeclarations = false)
{
    /// <summary>默认视角（未绑定、不可见全量）。</summary>
    public static readonly RoomViewRequest Anonymous = new();
}

/// <summary>
/// 房间控制面（服务层向宿主暴露的**唯一**房间入口）。
/// <para>
/// 约束：入参与出参一律纯 DTO/JSON，**不得出现 `Match`/`Player`/`Card`/`Ref&lt;T&gt;`**——
/// 本接口是 `Orc.Mcp` 与房间实现（`Orc.Server`）之间的活对象隔离点。
/// </para>
/// <para>
/// 管理动作（开局/终止/绑定/撤销）**不在这里**——它们属于管理面（管理 API 不进工具表），
/// 因此本接口只有"读一间房间的当前态"这一项能力。
/// </para>
/// </summary>
public interface IRoomControl
{
    /// <summary>按调用者视角构造房间快照。</summary>
    RoomSnapshot Snapshot(RoomViewRequest request);
}
