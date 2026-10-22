using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 沙盒状态工具（**沙盒专属，只读**）。
/// <para>
/// 建局/毁局只走管理面（<c>match.start</c> / <c>room.abort</c>）：<c>sandbox.create</c> / <c>sandbox.dispose</c>
/// 已**退役且不留别名**（刻意变化第 11 条）。它们的四项附带职责分别由 boot 的脚手架生成、
/// boot 段的进程级注册、开局装配（沙盒包强制入并集）与管理面承接。
/// </para>
/// </summary>
internal static class SandboxTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "sandbox.state",
            "sandbox",
            "读取沙盒状态：是否已建局、执行器是否忙（存在未完成的选择请求）、本进程已发生的进程级变更留痕。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(State(service)),
            sandboxOnly: true,
            readOnly: true);
    }

    private static McpToolResult State(McpService service)
    {
        var match = service.Executor.Match;

        var details = new JsonObject
        {
            ["mode"] = service.Options.Mode.ToString(),
            ["hasMatch"] = match is not null,
            ["busy"] = service.Executor.IsBusy,
            ["pending"] = McpService.PendingJson(service.Bridge.CurrentPending),
            ["sandboxRoot"] = service.Sandbox.Guard.Root,
            ["scaffolded"] = service.Sandbox.IsScaffolded,
            ["processStateNotes"] = ToJsonArray(service.ProcessStateNotes),
            ["promoteEnabled"] = service.Options.PromoteEnabled,
            ["csxEnabled"] = service.Options.CsxEnabled,
            ["pool"] = service.PoolJson(),
        };

        if (match is not null)
        {
            details["state"] = match.State.ToString();
            details["phase"] = match.Phase.ToString();
            details["turn"] = match.TurnNumber;
        }

        return McpToolResult.Success("沙盒状态已读取。", details: details);
    }

    internal static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
