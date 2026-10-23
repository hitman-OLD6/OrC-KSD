using System.Text.Json.Nodes;

namespace Orc.Mcp.Contracts;

/// <summary>
/// 工具调用统一返回外壳（R5 决策）：所有工具**成功与失败同构**。
/// <para>
/// 语义约定：
/// ①<see cref="Ok"/>＝false 表示**业务失败**（可判别、可重试），不表示工具自身故障；
/// ②引擎侧的"取消"（<c>SelectorStatus.Cancelled</c> 等）视为**成功结果**（<c>Ok</c>＝true ＋ <see cref="Code"/>＝<see cref="McpCodes.Cancelled"/>）；
/// ③引擎原生结果（三态对象、结构化报告）原样放入 <see cref="EngineResult"/>，不丢信息；
/// ④引擎的扁平字符串失败（<c>IReadOnlyList&lt;string&gt;</c>）原样透传并附加 <see cref="McpCodes.ValidationFailed"/>。
/// </para>
/// 协议适配层（stdio sample）负责把本对象序列化为 MCP 的 <c>content</c>；协议级 <c>isError</c> 仅用于工具自身故障
/// （由 <see cref="McpCodes.Internal"/> 表达）。
/// </summary>
/// <param name="Ok">业务是否成功。</param>
/// <param name="Code">受控结果码（见 <see cref="McpCodes"/>）。</param>
/// <param name="Message">人类可读说明（面向 agent，须可行动）。</param>
/// <param name="Details">附加结构化信息（失败明细、警告、提示等）。</param>
/// <param name="EngineResult">引擎原生结果（三态对象/结构化报告的 JSON 形态）。</param>
public sealed record McpToolResult(
    bool Ok,
    string Code,
    string Message,
    JsonNode? Details = null,
    JsonNode? EngineResult = null)
{
    /// <summary>成功（默认码 <see cref="McpCodes.Ok"/>）。</summary>
    public static McpToolResult Success(string message = "ok", JsonNode? engineResult = null, JsonNode? details = null)
        => new(true, McpCodes.Ok, message, details, engineResult);

    /// <summary>成功但带状态码（如 <see cref="McpCodes.Targeting"/>、<see cref="McpCodes.Cancelled"/>）。</summary>
    public static McpToolResult SuccessWith(string code, string message, JsonNode? engineResult = null, JsonNode? details = null)
        => new(true, code, message, details, engineResult);

    /// <summary>业务失败。</summary>
    public static McpToolResult Failure(string code, string message, JsonNode? details = null, JsonNode? engineResult = null)
        => new(false, code, message, details, engineResult);

    /// <summary>不可行动/不支持的请求（明确拒绝，非静默）。</summary>
    public static McpToolResult NotSupported(string message, JsonNode? details = null)
        => Failure(McpCodes.NotSupported, message, details);

    /// <summary>能力/模式不开放（沙盒专属能力在非沙盒模式下的统一拒绝）。</summary>
    public static McpToolResult ModeDenied(string message, JsonNode? details = null)
        => Failure(McpCodes.ModeDenied, message, details);
}

/// <summary>
/// 受控结果码集合（唯一称谓来源；MCP 契约的一部分，随契约版本演进）。
/// </summary>
public static class McpCodes
{
    /// <summary>成功。</summary>
    public const string Ok = "ok";

    /// <summary>对局尚未就绪（未建局/准备态/已终局）。</summary>
    public const string NotReady = "not-ready";

    /// <summary>当前模式不开放该能力（沙盒专属工具在 player 模式等）。</summary>
    public const string ModeDenied = "mode-denied";

    /// <summary>写路径越出沙盒根（防污染拒绝）。</summary>
    public const string PathDenied = "path-denied";

    /// <summary>存在未完成的选择请求（挂起期门禁）。</summary>
    public const string PendingTargeting = "pending-targeting";

    /// <summary>目标选择待应答（成功态：动作已挂起等待 <c>target.*</c>）。</summary>
    public const string Targeting = "targeting";

    /// <summary>调用被取消（引擎三态中的 Cancelled——**成功态**，不是错误）。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>选择/输入非法（引擎 InvalidSelection 等——可重试）。</summary>
    public const string InvalidSelection = "invalid-selection";

    /// <summary>重试超出上限（引擎 RetryLimitExceeded）。</summary>
    public const string RetryLimitExceeded = "retry-limit-exceeded";

    /// <summary>等待应答超时（服务层定时器触发，已按取消处理）。</summary>
    public const string Timeout = "timeout";

    /// <summary>校验失败（校验报告存在 Error；或引擎扁平字符串失败透传）。</summary>
    public const string ValidationFailed = "validation-failed";

    /// <summary>引擎/资源明确拒绝（引擎抛出的契约类异常，如未注册定义、门禁拒绝）。</summary>
    public const string EngineRejected = "engine-rejected";

    /// <summary>参数缺失/格式非法（工具入参校验；与引擎无关）。</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>工具不存在。</summary>
    public const string UnknownTool = "unknown-tool";

    /// <summary>能力不开放（未启用/不实现）。</summary>
    public const string NotSupported = "not-supported";

    /// <summary>工具自身故障（协议适配层据此设置 MCP <c>isError</c>）。</summary>
    public const string Internal = "internal";

    // ---- 房间/座位/会话（多人支持；与 Orc.Protocol.ResultCodes 同名同值） ----

    /// <summary>串行队列已满，稍后重试。</summary>
    public const string Busy = "busy";

    /// <summary>座位已被占用。</summary>
    public const string SeatTaken = "seat-taken";

    /// <summary>座位令牌无效或已过期。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>房间未就绪（座位未填满/仍有空座）。</summary>
    public const string RoomNotReady = "room-not-ready";

    /// <summary>房间已开局，房间态操作被拒绝。</summary>
    public const string RoomStarted = "room-started";

    /// <summary>卡组不合法（张数/卡池/同名上限/配国）。</summary>
    public const string DeckInvalid = "deck-invalid";

    /// <summary>协议或契约版本不匹配；卡包集合里也用于「注销时版本与当前快照不符」（与 Orc.Protocol.ResultCodes 同名同值）。</summary>
    public const string VersionMismatch = "version-mismatch";

    // ---- 多人模型（管理面/房间轮换；与 Orc.Protocol.ResultCodes 同名同值） ----

    /// <summary><c>Pending</c> 连接调任何动作/调试/观察工具（不按工具区分）。</summary>
    public const string NotAdmitted = "not-admitted";

    /// <summary>调用携带旧 <c>roomId</c>（轮换后）。</summary>
    public const string StaleRoom = "stale-room";

    /// <summary>用旧纪元 <c>SeatBinding</c> 解析设施。</summary>
    public const string StaleSeat = "stale-seat";

    /// <summary>轮换 drain 窗口内的管理 op（带 <c>retryAfterMs</c>）。</summary>
    public const string RoomRotating = "room-rotating";

    /// <summary>对已结束对局调 <c>room.abort</c>（幂等成功，不报错）。</summary>
    public const string AlreadyEnded = "already-ended";

    /// <summary>重连令牌无效或已随房间终结失效。</summary>
    public const string InvalidToken = "invalid-token";

    /// <summary>并集中同 id 版本冲突（指名双方与版本）。</summary>
    public const string LockConflict = "lock-conflict";

    /// <summary>缺口清单（点名哪一方缺哪些 <c>{kind,id,version}</c>）。</summary>
    public const string MissingAssets = "missing-assets";

    /// <summary>实例进程起不来（boot 前置不满足）。</summary>
    public const string InstanceBootFailed = "instance-boot-failed";
}
