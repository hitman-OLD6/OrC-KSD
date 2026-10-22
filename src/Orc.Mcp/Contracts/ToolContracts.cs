using System.Text.Json;

namespace Orc.Mcp.Contracts;

/// <summary>
/// 工具的角色要求（**能力由角色决定，不由工具名散落判断**）：判定＝"连接角色集合 ⊇ 工具要求"。
/// </summary>
public static class ToolRole
{
    /// <summary>不要求任何角色（<c>service.*</c>/<c>docs.*</c>/<c>room.info</c>）。</summary>
    public const string None = "none";

    /// <summary>玩家面 controller（绑 seat 的输入端）。</summary>
    public const string Controller = "controller";

    /// <summary>viewer（绑 seat＝脱敏；不绑 seat＝全知）。</summary>
    public const string Viewer = "viewer";

    /// <summary>调试面 controller（不绑 seat；仍须满足回环判据）。</summary>
    public const string Debug = "debug";
}

/// <summary>
/// 工具描述符（工具目录条目；MCP <c>tools/list</c> 的数据来源）。
/// </summary>
/// <param name="Name">工具名（命名空间前缀 + 全小写下划线，如 <c>sandbox.create</c>）。</param>
/// <param name="Group">分组（<c>sandbox</c>/<c>card</c>/<c>pack</c>/<c>target</c>/<c>match</c>/<c>diag</c>/<c>docs</c>/<c>service</c>）。</param>
/// <param name="Description">面向 agent 的一句话说明（说明用途与主要约束）。</param>
/// <param name="InputSchema">输入 JSON Schema（手写常量；MCP 参数校验来源）。</param>
/// <param name="RequiredRole">角色要求（见 <see cref="ToolRole"/>；缺省＝不要求）。</param>
public sealed record McpToolDescriptor(
    string Name,
    string Group,
    string Description,
    string InputSchema,
    string RequiredRole = ToolRole.None);

/// <summary>一次工具调用（协议无关形态：工具名 ＋ 参数 JSON）。</summary>
public readonly record struct McpToolCall(string Name, JsonElement Arguments);

/// <summary>
/// 工具执行器（每个工具一个实现；由工具目录集中登记）。
/// </summary>
public interface IMcpToolHandler
{
    /// <summary>工具名（须与目录条目一致）。</summary>
    string Name { get; }

    /// <inheritdoc cref="IMcpToolHandler" />
    Task<McpToolResult> InvokeAsync(McpToolCall call, CancellationToken ct);
}

/// <summary>
/// 工具入参读取 helper（配合"每个工具一个强类型参数 record"的解析面；把样板集中到一处）。
/// </summary>
public static class ToolArgs
{
    public static bool TryGet(JsonElement args, string name, out JsonElement value)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value))
        {
            return value.ValueKind != JsonValueKind.Null;
        }

        value = default;
        return false;
    }

    public static string? OptionalString(JsonElement args, string name)
        => TryGet(args, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string RequireString(JsonElement args, string name)
        => OptionalString(args, name) is { Length: > 0 } s
            ? s
            : throw new ToolArgumentException(name, $"缺少必填字符串参数 '{name}'。");

    public static int? OptionalInt(JsonElement args, string name)
        => TryGet(args, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static int RequireInt(JsonElement args, string name)
        => OptionalInt(args, name) ?? throw new ToolArgumentException(name, $"缺少必填整数参数 '{name}'。");

    public static bool OptionalBool(JsonElement args, string name, bool fallback = false)
        => TryGet(args, name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    public static IReadOnlyList<string> OptionalStringList(JsonElement args, string name)
    {
        if (!TryGet(args, name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    public static IReadOnlyList<int> OptionalIntList(JsonElement args, string name)
    {
        if (!TryGet(args, name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var list = new List<int>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var i))
            {
                list.Add(i);
            }
        }

        return list;
    }

    public static JsonElement? OptionalObject(JsonElement args, string name)
        => TryGet(args, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static JsonElement? OptionalValue(JsonElement args, string name)
        => TryGet(args, name, out var v) ? v : null;
}

/// <summary>
/// 工具入参错误（服务层自身的参数校验失败；映射为 <see cref="McpCodes.InvalidArguments"/>）。
/// </summary>
public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string parameterName, string message)
        : base(message)
        => ParameterName = parameterName;

    /// <summary>出错的参数名。</summary>
    public string ParameterName { get; }
}
