namespace Orc.Protocol;

/// <summary>握手请求参数（<c>initialize</c>）。</summary>
public sealed record InitializeParams
{
    /// <summary>客户端自述名称（仅日志/诊断用，不参与鉴权）。</summary>
    public string? ClientName { get; init; }

    /// <summary>客户端支持的线协议版本（不匹配即拒绝）。</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>自由文本标签（不参与鉴权；管理员据此指代连接）。</summary>
    public string? Label { get; init; }
}

/// <summary>
/// 握手响应结果。
/// <para>
/// <c>connectionId</c> **只在这里返回**——管理员必须能指代"尚未申请角色的连接"，
/// 因此连接标识不能等到 <c>session.attach</c> 才出现。客户端不可自选。
/// </para>
/// </summary>
public sealed record InitializeResult
{
    /// <summary>服务端线协议版本（<see cref="ProtocolInfo.ProtocolVersion"/>）。</summary>
    public required string ProtocolVersion { get; init; }

    /// <summary>对局契约版本（来自服务层 <c>ContractSchemaVersion</c>）。</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>服务端能力标记（如 <c>push</c>、<c>targeting</c>、<c>admin-channel</c>）。</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>连接标识（服务端分配；管理员与 <c>session.attach</c> 均以此指代本连接）。</summary>
    public required string ConnectionId { get; init; }

    /// <summary>标签（回显 <see cref="InitializeParams.Label"/>；不参与鉴权）。</summary>
    public string? Label { get; init; }

    /// <summary>当前房间标识（轮换后变化；客户端据此发现"换了房间"）。</summary>
    public string? RoomId { get; init; }
}

/// <summary>
/// 协议方法名常量（唯一称谓来源）。
/// <para>请求方法 6 个；<c>hello</c> 不是方法名，新协议里不出现。</para>
/// </summary>
public static class ProtocolMethods
{
    public const string Initialize = "initialize";
    public const string Ping = "ping";

    /// <summary>申请角色（角色 ＋ 座位 ＋ 卡组 ＋ 卡包声明，一条报文带齐）。立即返回 <c>pending</c>（调试面立即生效）。</summary>
    public const string SessionAttach = "session.attach";

    /// <summary>撤回申请／释放座位（房间态）。</summary>
    public const string SessionDetach = "session.detach";

    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";

    /// <summary>服务端推送：视图 ＋ 动作可用性（同一拍；形状由 <c>perspective</c> 区分）。</summary>
    public const string NotifyView = "notify/view";

    /// <summary>服务端推送：事件段（绑座＝脱敏；全知＝全量）。</summary>
    public const string NotifySegment = "notify/segment";

    /// <summary>服务端推送：待决选择请求。</summary>
    public const string NotifyTargetRequest = "notify/target.request";

    /// <summary>服务端推送：待决选择已过期。</summary>
    public const string NotifyTargetExpired = "notify/target.expired";

    /// <summary>服务端推送：接入许可决定（<c>admitted</c>/<c>rejected</c>/<c>revoked</c>）。</summary>
    public const string NotifyAdmission = "notify/admission";

    /// <summary>服务端推送：房间已轮换（旧房间终结，新 lobby 已就绪）。</summary>
    public const string NotifyRoomRotated = "notify/room.rotated";

    /// <summary>服务端推送：座位状态 delta（**不含**卡组/卡包）。</summary>
    public const string NotifyRoomSeats = "notify/room.seats";
}
