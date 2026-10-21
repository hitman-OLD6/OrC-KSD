using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 调试读取工具（R3）：细粒度面（审计链/信号清单/入口清单）＋ <c>diag.report</c> 聚合入口（引用式摘要）。
/// 全部只读；不提供写入型诊断（不清空/裁剪引擎事件流）。
/// </summary>
internal static class DiagTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "diag.report",
            "diag",
            "聚合诊断摘要（引用式）：引擎规模计数、段缓存水位、进程级变更留痕、服务层门控状态；不内联大结构。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Report(service)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "diag.audit_chain",
            "diag",
            "导出全域审查链 JSON（触发器种类节点 + 实例清单 + 声明的下游边；图缺口以 resolved=null 表示）。",
            """
            {
              "type": "object",
              "properties": {
                "indented": { "type": "boolean", "description": "是否缩进输出（缺省 false）" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(AuditChain(service, call)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "diag.hooks",
            "diag",
            "导出信号/判定器/触发器分层清单（代码生成的权威清单——事件名与判定器名的唯一称谓来源）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(JsonText("信号/判定器/触发器清单已导出。", Orc.Game.Output.GameHooksJson.Serialize(false))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "diag.entry_points",
            "diag",
            "导出引擎公开输入入口全集（名称/类别/宿主/签名/门禁/结果形态/交付状态）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(JsonText("入口清单已导出。", Orc.Game.Output.GameEntryPointsJson.Serialize(false))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "diag.card_load",
            "diag",
            "读取卡牌加载报告（两部分）：本批报告（冻结 schema：items/slots/summary）与本局累积视图"
            + "（按资产 GUID 合并、refCount 累加；不含 slots——卡位序号只对单批有意义）。报告不落盘。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(CardLoad(service)),
            sandboxOnly: true,
            readOnly: true);
    }

    private static McpToolResult Report(McpService service)
    {
        var match = service.Executor.Match;

        var details = new JsonObject
        {
            ["mode"] = service.Options.Mode.ToString(),
            ["contractSchemaVersion"] = service.Options.ContractSchemaVersion,
            ["toolCount"] = service.Tools.Count,
            ["hasMatch"] = match is not null,
            ["busy"] = service.Executor.IsBusy,
            ["segmentCache"] = new JsonObject
            {
                ["count"] = service.Segments.Count,
                ["capacity"] = service.Segments.Capacity,
                ["lastSequence"] = service.Segments.LastSequence,
            },
            ["processStateNotes"] = SandboxTools.ToJsonArray(service.ProcessStateNotes),
            ["processStateDirty"] = service.ProcessStateNotes.Count > 0,
            ["referenceTable"] = new JsonObject
            {
                ["count"] = match?.Engine.References.Count ?? 0,
            },
            ["entryPoints"] = new JsonObject
            {
                ["read"] = new JsonArray("diag.audit_chain", "diag.hooks", "diag.entry_points", "diag.card_load"),
            },
        };

        if (match is not null)
        {
            details["engine"] = new JsonObject
            {
                ["cards"] = match.Engine.Cards.Count,
                ["hooks"] = match.Engine.Hooks.Names.Count,
                ["triggers"] = match.Engine.Orchestration.Count,
                ["prefabs"] = match.Engine.Prefabs.PrefabIds.Count,
                ["handlers"] = match.Engine.Prefabs.HandlerKeys.Count,
            };
            details["match"] = new JsonObject
            {
                ["state"] = match.State.ToString(),
                ["phase"] = match.Phase.ToString(),
                ["turn"] = match.TurnNumber,
            };
        }

        return McpToolResult.Success("诊断摘要已生成。", details: details);
    }

    private static McpToolResult AuditChain(McpService service, McpToolCall call)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "尚无活跃沙盒对局（审查链需要引擎实例）。");
        }

        var indented = ToolArgs.OptionalBool(call.Arguments, "indented");
        var exported = match.Engine.ExportAuditChainJson(indented);

        // 保留触发器（只读增节）：解析引擎导出后挂 `reservedTriggers` 节点——不重写导出器、不改既有字段。
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(exported);
        }
        catch (Exception ex)
        {
            return McpToolResult.Failure(McpCodes.Internal, $"审查链导出结果不是合法 JSON：{ex.Message}");
        }

        if (root is JsonObject audit)
        {
            audit["reservedTriggers"] = JsonNode.Parse(match.ExportReservedTriggersJson());
        }

        return McpToolResult.Success("审查链已导出。", root);
    }

    /// <summary>
    /// `diag.card_load`：两部分——**本批报告**（I10 冻结 schema，原样嵌入）与本局**累积视图**
    /// （<see cref="Orc.Packages.Loading.CardLoadLedger"/> 按 GUID 合并、<c>refCount</c> 累加）。
    /// <para>
    /// 报告**不落盘**（R5）：唯一来源是对局侧持有的台账；工具只读、不改台账。
    /// 走「代码内定义集直注册」路径的对局（沙盒局）没有卡牌加载批次 ⇒ <c>batch</c> 为 null，
    /// 累积视图为空表（不是错误：该路径本就不经加载器）。
    /// </para>
    /// </summary>
    private static McpToolResult CardLoad(McpService service)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "尚无活跃对局（卡牌加载报告属于某一局）。");
        }

        var assembly = service.CardAssembly;
        var ledger = assembly?.Ledger;
        var latest = ledger?.Latest;
        var cumulative = ledger?.Snapshot() ?? Orc.Packages.Loading.CardLoadLedgerSnapshot.Empty;

        var details = new JsonObject
        {
            ["hasMatch"] = true,
            ["source"] = assembly is null ? "none" : "card-assembly",
            ["batchCount"] = ledger?.BatchCount ?? 0,
            ["batch"] = latest is null ? null : JsonNode.Parse(latest.ToJson()),
            ["cumulative"] = JsonNode.Parse(cumulative.ToJson()),
        };

        if (assembly is null)
        {
            details["note"] = "当前对局走代码内定义集直注册路径（沙盒局）：未经卡牌加载器批次装载，故本批报告为空。";
            return McpToolResult.Success("当前对局没有卡牌加载批次（直注册路径）。", details: details);
        }

        return McpToolResult.Success(
            latest is null
                ? "本局尚无卡牌加载批次。"
                : $"本批 {latest.Summary.Total} 条资产／{latest.Slots.Count} 个卡位；累积 {cumulative.Summary.Total} 条。",
            details: details);
    }

    private static McpToolResult JsonText(string message, string json)
    {
        try
        {
            return McpToolResult.Success(message, JsonNode.Parse(json));
        }
        catch (Exception ex)
        {
            return McpToolResult.Failure(McpCodes.Internal, $"导出结果不是合法 JSON：{ex.Message}");
        }
    }
}
