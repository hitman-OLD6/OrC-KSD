namespace Orc.Protocol;

/// <summary>握手请求参数（<c>initialize</c>）。</summary>
public sealed record InitializeParams
{
    /// <summary>客户端自述名称（仅日志/诊断用，不参与鉴权）。</summary>
    public string? ClientName { get; init; }

    /// <summary>客户端支持的线协议版本（不匹配即拒绝）。</summary>
    public string? ProtocolVersion { get; init; }
}

/// <summary>握手响应结果。</summary>
public sealed record InitializeResult
{
    /// <summary>服务端线协议版本（<see cref="ProtocolInfo.ProtocolVersion"/>）。</summary>
    public required string ProtocolVersion { get; init; }

    /// <summary>对局契约版本（来自服务层 <c>ContractSchemaVersion</c>）。</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>服务端能力标记（如 <c>room</c>、<c>push</c>、<c>targeting</c>）。</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>协议方法名常量（唯一称谓来源）。</summary>
public static class ProtocolMethods
{
    public const string Initialize = "initialize";
    public const string Ping = "ping";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";

    /// <summary>服务端推送：房间状态变化。</summary>
    public const string NotifyRoom = "notify/room";

    /// <summary>服务端推送：对局开始。</summary>
    public const string NotifyMatchStarted = "notify/match.started";

    /// <summary>服务端推送：视图 ＋ 动作可用性（同一拍）。</summary>
    public const string NotifyView = "notify/view";

    /// <summary>服务端推送：事件段（按座位过滤）。</summary>
    public const string NotifySegment = "notify/segment";

    /// <summary>服务端推送：待决选择请求。</summary>
    public const string NotifyTargetRequest = "notify/target.request";

    /// <summary>服务端推送：待决选择已过期。</summary>
    public const string NotifyTargetExpired = "notify/target.expired";

    /// <summary>服务端推送：对局结束。</summary>
    public const string NotifyMatchEnded = "notify/match.ended";
}
