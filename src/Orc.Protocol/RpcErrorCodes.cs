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
}
