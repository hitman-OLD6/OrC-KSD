using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 服务面工具：能力自述与限制（agent 不必猜测模式与门控）。
/// </summary>
internal static class ServiceTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "service.info",
            "service",
            "返回服务层的运行模式、契约版本、沙盒根、门控开关与已声明的引擎限制。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(ServiceInfo(service)),
            readOnly: true);

        service.AddTool(
            "service.tools",
            "service",
            "枚举当前模式下可见的全部工具（名称/分组/说明）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(ToolList(service)),
            readOnly: true);
    }

    private static McpToolResult ServiceInfo(McpService service)
    {
        var options = service.Options;

        var limitations = new JsonArray
        {
            "插件不可热重启（插件资产只参与冷加载；变更后须人工重启进程）。",
            "沙盒局为唯一活跃对局；同一进程不允许第二个对局。",
            "沙盒产物的进程级副作用（词条注册面、卡包目录）不随沙盒局结束回滚。",
            "写入只允许落在沙盒根内；正式资产目录对写工具只读。",
            "提升（promote）默认关闭；开启后仍要求无活跃沙盒局。",
            "csx 编辑能力默认关闭（引擎沙箱为'尽力而为的弱沙箱……非安全边界'）。",
        };

        var details = new JsonObject
        {
            ["mode"] = options.Mode.ToString(),
            ["contractSchemaVersion"] = options.ContractSchemaVersion,
            ["sandboxRoot"] = service.Sandbox.Guard.Root,
            ["packRoot"] = service.Sandbox.PackRoot,
            ["packsRootReadOnly"] = options.PacksRoot,
            ["promoteEnabled"] = options.PromoteEnabled,
            ["csxEnabled"] = options.CsxEnabled,
            ["targetSelectionTimeoutSeconds"] = (int)options.TargetSelectionTimeout.TotalSeconds,
            ["segmentCacheCapacity"] = service.Segments.Capacity,
            ["limitations"] = limitations,
        };

        return McpToolResult.Success($"服务层就绪（模式＝{options.Mode}，契约版本＝{options.ContractSchemaVersion}）。", details: details);
    }

    private static McpToolResult ToolList(McpService service)
    {
        var tools = new JsonArray();
        foreach (var descriptor in service.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = descriptor.Name,
                ["group"] = descriptor.Group,
                ["description"] = descriptor.Description,
            });
        }

        return McpToolResult.Success($"{service.Tools.Count} 个工具可用。", details: new JsonObject { ["tools"] = tools });
    }
}
