namespace Orc.Mcp.Tools;

/// <summary>
/// 工具注册总入口（集中式目录：每个工具组一个 <c>Register</c> 方法；顺序稳定）。
/// </summary>
public static class ToolRegistration
{
    public static void RegisterAll(McpService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        ServiceTools.Register(service);
        RoomTools.Register(service);
        PoolTools.Register(service);
        SandboxTools.Register(service);
        CardTools.Register(service);
        AssetTools.Register(service);
        ShapeTools.Register(service);
        SandboxPrimitiveTools.Register(service);
        MatchTools.Register(service);
        PlayerActionTools.Register(service);
        TargetTools.Register(service);
        ObserveTools.Register(service);
        DocsTools.Register(service);
        EditorTools.Register(service);
        DiagTools.Register(service);
    }
}
