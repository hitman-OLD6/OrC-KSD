namespace Orc.Protocol;

/// <summary>JSON-RPC 2.0 标准错误码（仅协议层语义）。</summary>
public static class RpcErrorCodes
{
    /// <summary>报文不是合法 JSON。</summary>
    public const int ParseError = -32700;

    /// <summary>合法 JSON 但不是合法请求对象。</summary>
    public const int InvalidRequest = -32600;

    /// <summary>方法不存在。</summary>
    public const int MethodNotFound = -32601;

    /// <summary>参数类型/形状非法。</summary>
    public const int InvalidParams = -32602;

    /// <summary>服务端内部故障。</summary>
    public const int InternalError = -32603;
}

/// <summary>
/// 业务结果码（服务端裁决的结局，随 <see cref="ToolResultPayload.Code"/> 过线）。
/// <para>
/// 值与 <c>Orc.Mcp.Contracts.McpCodes</c> 保持一致——本层不能引用 `Orc.Mcp`（会让客户端拖入引擎），
/// 一致性由 <c>Orc.Server.Tests</c> 的映射测试守住。
/// </para>
/// </summary>
public static class ResultCodes
{
    public const string Ok = "ok";
    public const string NotReady = "not-ready";
    public const string ModeDenied = "mode-denied";
    public const string PathDenied = "path-denied";
    public const string PendingTargeting = "pending-targeting";
    public const string Targeting = "targeting";
    public const string Cancelled = "cancelled";
    public const string InvalidSelection = "invalid-selection";
    public const string RetryLimitExceeded = "retry-limit-exceeded";
    public const string Timeout = "timeout";
    public const string ValidationFailed = "validation-failed";
    public const string EngineRejected = "engine-rejected";
    public const string InvalidArguments = "invalid-arguments";
    public const string UnknownTool = "unknown-tool";
    public const string NotSupported = "not-supported";
    public const string Internal = "internal";

    // ---- 房间/座位/会话（协议层新增；引擎侧无对应码） ----

    /// <summary>串行队列已满，稍后重试。</summary>
    public const string Busy = "busy";

    /// <summary>座位已被占用。</summary>
    public const string SeatTaken = "seat-taken";

    /// <summary>座位令牌无效或已过期（重连时）。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>房间未就绪（座位未填满/仍有空座）。</summary>
    public const string RoomNotReady = "room-not-ready";

    /// <summary>房间已开局，房间态操作被拒绝。</summary>
    public const string RoomStarted = "room-started";

    /// <summary>卡组不合法（张数/卡池/同名上限/配国）。</summary>
    public const string DeckInvalid = "deck-invalid";

    /// <summary>协议或契约版本不匹配。</summary>
    public const string VersionMismatch = "version-mismatch";

    // ---- 多人模型（管理面/房间轮换）----

    /// <summary><c>Pending</c> 连接调任何动作/调试/观察工具（不按工具区分）。</summary>
    public const string NotAdmitted = "not-admitted";

    /// <summary>调用携带旧 <c>roomId</c>（轮换后）；不静默按新房间处理。</summary>
    public const string StaleRoom = "stale-room";

    /// <summary>用旧纪元 <c>SeatBinding</c> 解析设施。</summary>
    public const string StaleSeat = "stale-seat";

    /// <summary>轮换 drain 窗口内的管理 op（带 <c>retryAfterMs</c>）。</summary>
    public const string RoomRotating = "room-rotating";

    /// <summary>对已结束对局调 <c>room.abort</c>（幂等成功，不报错）。</summary>
    public const string AlreadyEnded = "already-ended";

    /// <summary>重连令牌无效或已随房间终结失效。</summary>
    public const string InvalidToken = "invalid-token";

    // ---- 管理器（资产判定）----

    /// <summary>并集中同 id 版本冲突（指名双方与版本）。</summary>
    public const string LockConflict = "lock-conflict";

    /// <summary>缺口清单（点名哪一方缺哪些 <c>{kind,id,version}</c>）。</summary>
    public const string MissingAssets = "missing-assets";

    /// <summary>实例进程起不来（boot 前置不满足）。</summary>
    public const string InstanceBootFailed = "instance-boot-failed";
}
