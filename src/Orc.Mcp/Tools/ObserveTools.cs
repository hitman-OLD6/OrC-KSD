using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 观察面工具（沙盒：全知面——实体快照含组件、事件流段、对局摘要）。
/// </summary>
internal static class ObserveTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "observe.snapshot",
            "observe",
            "全量实体快照（引擎 SnapshotJson：卡片/单位/HQ 的 entityId、名称、存活与组件字段）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Snapshot(service),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "observe.segments",
            "observe",
            "拉取新的事件流段（引擎侧「取走即移除」）并追加到服务层缓存；返回服务层缓存中最近的段（可重复读取）。",
            """
            {
              "type": "object",
              "properties": {
                "includeCached": { "type": "boolean", "description": "返回缓存中的全部段（缺省 true）；false＝仅返回本次新拉取的段" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => SegmentsAsync(service, call, ct),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "observe.view",
            "observe",
            "读取当前对局视图（与推送**同形**）：绑 seat 的 viewer＝单座位视角帧；不绑 seat 的 viewer＝全知帧；"
            + "调试面＝对局摘要（含双方手牌，供诊断）。视角由角色决定，不由参数决定。",
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(View(service)),
            readOnly: true,
            requiredRole: ToolRole.Viewer);
    }

    private static Task<McpToolResult> Snapshot(McpService service)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return Task.FromResult(McpToolResult.Failure(McpCodes.NotReady, "尚无活跃沙盒对局。"));
        }

        var json = Orc.Output.SnapshotJson.SerializeAll(match.Engine, indented: false);
        return Task.FromResult(McpToolResult.Success("快照已导出。", JsonNode.Parse(json)));
    }

    private static Task<McpToolResult> SegmentsAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return Task.FromResult(McpToolResult.Failure(McpCodes.NotReady, "尚无活跃沙盒对局。"));
        }

        // 段取用者归属：宿主担任时（UI 形态）服务层不取段，只读宿主喂入的缓存；
        // 默认（无头/MCP 形态）由服务层取段并缓存。
        IReadOnlyList<Orc.Output.EventSegment> taken = service.ShouldServicePullSegments
            ? match.Engine.TakeSegments()
            : Array.Empty<Orc.Output.EventSegment>();

        if (taken.Count > 0)
        {
            service.Segments.AddRange(taken);
        }

        var includeCached = ToolArgs.OptionalBool(call.Arguments, "includeCached", true);
        var segments = includeCached ? service.Segments.Snapshot() : taken;

        var array = new JsonArray();
        foreach (var segment in segments)
        {
            array.Add(SegmentJson(segment));
        }

        var details = new JsonObject
        {
            ["takenNow"] = taken.Count,
            ["cachedTotal"] = service.Segments.Count,
            ["cacheCapacity"] = service.Segments.Capacity,
            ["lastSequence"] = service.Segments.LastSequence,
            ["segments"] = array,
        };

        return Task.FromResult(McpToolResult.Success($"本次拉取 {taken.Count} 段（缓存共 {service.Segments.Count} 段）。", details: details));
    }

    private static JsonObject SegmentJson(Orc.Output.EventSegment segment)
    {
        var entries = new JsonArray();
        foreach (var entry in segment.Entries)
        {
            entries.Add(new JsonObject
            {
                ["kind"] = entry.Kind.ToString(),
                ["level"] = entry.Level.ToString(),
                ["source"] = entry.Source,
                ["message"] = entry.Message,
            });
        }

        // 完整段 JSON（引擎侧 EventSegmentJson：含条目载荷与因果子树）——供 agent 精确复盘。
        var full = Orc.Output.EventSegmentJson.Serialize(segment, indented: false);

        return new JsonObject
        {
            ["sequence"] = segment.Sequence,
            ["timestamp"] = segment.Timestamp.ToString("O"),
            ["entryCount"] = segment.Entries.Count,
            ["children"] = segment.Children.Count,
            ["entries"] = entries,
            ["json"] = full,
        };
    }

    /// <summary>
    /// 视图拉取面（R7）：与推送同形，**视角由角色决定**（退役 <c>panel</c> 参数旁路）。
    /// <list type="bullet">
    ///   <item>调试面 → 对局摘要（含双方手牌，供诊断中间态）；</item>
    ///   <item>绑 seat → 单座位视角帧；</item>
    ///   <item>不绑 seat 的 viewer → 全知帧（两份座位视角打包）。</item>
    /// </list>
    /// </summary>
    private static McpToolResult View(McpService service)
    {
        if (service.Executor.Match is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "尚无活跃对局。");
        }

        var outcome = service.Caller.DebugFace
            ? service.ProjectDebugSummary()
            : service.Caller.Seat is { } seat
                ? service.ProjectSeatFrame(seat)
                : service.ProjectOmniscientFrame();

        if (outcome.Payload is not JsonObject details)
        {
            return McpToolResult.Failure(
                outcome.FailureCode ?? McpCodes.Internal,
                outcome.FailureMessage ?? "视图读取失败。");
        }

        return McpToolResult.Success("视图已读取。", details: details);
    }
}
