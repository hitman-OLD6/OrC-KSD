using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Orc.Protocol;

/// <summary>
/// JSON-RPC 2.0 请求（客户端 → 服务端）。
/// <para>
/// 业务失败**不占用** <see cref="RpcResponse.Error"/>：它放在 <c>result</c> 里（<see cref="ToolResultPayload"/>），
/// <c>error</c> 只承载解析/方法不存在/参数类型错误。
/// </para>
/// </summary>
public sealed record RpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolInfo.JsonRpcVersion;

    /// <summary>请求标识（客户端生成；Notification 为 null）。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>方法名（工具调用＝<c>tools/call</c>，其余见协议方法表）。</summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonNode? Params { get; init; }
}

/// <summary>JSON-RPC 2.0 响应（服务端 → 客户端）。<see cref="Result"/> 与 <see cref="Error"/> 互斥。</summary>
public sealed record RpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolInfo.JsonRpcVersion;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; init; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; init; }

    /// <summary>成功响应（业务失败也走这里，由 <see cref="ToolResultPayload.Ok"/> 表达）。</summary>
    public static RpcResponse Success(string? id, JsonNode? result)
        => new() { Id = id, Result = result };

    /// <summary>协议层失败（解析错误、未知方法、参数类型错误）。</summary>
    public static RpcResponse Failure(string? id, int code, string message, JsonNode? data = null)
        => new() { Id = id, Error = new RpcError(code, message, data) };
}

/// <summary>JSON-RPC 2.0 错误对象（仅协议层）。</summary>
public sealed record RpcError(int Code, string Message, JsonNode? Data = null);

/// <summary>服务端主动推送（无 <c>id</c> 的 JSON-RPC 消息）。</summary>
public sealed record RpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolInfo.JsonRpcVersion;

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonNode? Params { get; init; }
}
