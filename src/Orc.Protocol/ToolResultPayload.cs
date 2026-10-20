using System.Text.Json.Nodes;

namespace Orc.Protocol;

/// <summary>
/// 业务结果壳（放在 <c>result</c> 内）。
/// <para>
/// 客户端判定只需看 <see cref="Ok"/> 与 <see cref="Code"/>；<see cref="Details"/> 承载结构化载荷
/// （如非法卡 id 列表、房间快照、投影），<see cref="Message"/> 只作人读。
/// </para>
/// </summary>
public sealed record ToolResultPayload
{
    /// <summary>业务是否成功（与 JSON-RPC <c>error</c> 无关）。</summary>
    public required bool Ok { get; init; }

    /// <summary>结果码（见 <see cref="ResultCodes"/>）。</summary>
    public required string Code { get; init; }

    /// <summary>人读说明（失败时可空）。</summary>
    public string? Message { get; init; }

    /// <summary>结构化载荷（投影、快照、违规清单等）。</summary>
    public JsonNode? Details { get; init; }

    public static ToolResultPayload Success(string message = "", JsonNode? details = null)
        => new() { Ok = true, Code = ResultCodes.Ok, Message = message, Details = details };

    public static ToolResultPayload Failure(string code, string message, JsonNode? details = null)
        => new() { Ok = false, Code = code, Message = message, Details = details };
}
