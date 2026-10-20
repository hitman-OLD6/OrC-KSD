namespace Orc.Protocol;

/// <summary>
/// 工具描述（<c>tools/list</c> 的元素）。
/// <para>协议侧 DTO：客户端不引用 `Orc.Mcp`，故工具目录以本形状过线。</para>
/// </summary>
public sealed record ToolDescriptorPayload(
    string Name,
    string Group,
    string Description,
    string InputSchema);
