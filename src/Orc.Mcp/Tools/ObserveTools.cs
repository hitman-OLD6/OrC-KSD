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
            "对局摘要视图：沙盒模式＝全知摘要（含双方手牌明细）；player 模式＝按座位过滤的投影视图（不含对手手牌明细）。"
            + "panel=true 时无视模式强制返回投影面板（整枚 view ＋ actions），供 MCP 桥构造 seat.board。",
            """
            {
              "type": "object",
              "properties": {
                "seat": { "type": "integer", "minimum": 0, "maximum": 1, "description": "视角座位（缺省＝进程级 AgentSeat）" },
                "panel": { "type": "boolean", "description": "true＝返回投影面板（view+actions）；沙盒摘要不含战线/槽位/攻击预览，桥需要面板形态" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => View(service, call.Arguments),
            readOnly: true);
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

    private static Task<McpToolResult> View(McpService service, JsonElement args)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return Task.FromResult(McpToolResult.Failure(McpCodes.NotReady, "尚无活跃对局。"));
        }

        // R9d：player 模式＝按座位过滤的投影视图（不含对手手牌明细）；沙盒模式＝全知摘要。
        // panel=true：沙盒模式下也可显式索取投影面板——seat.board 需要 view/actions，
        // 而沙盒摘要只有 players[]（无战线、槽位、攻击预览），桥侧无法自行还原。
        if (ToolArgs.OptionalBool(args, "panel") || service.Options.Mode != McpServiceMode.Authoring)
        {
            return Task.FromResult(ProjectedView(service, match, args));
        }

        // 摘要面读 CurrentPlayer（换牌期尚为 null，引擎抛"回合尚未开始（当前行动方未定）"）。
        // 这是阶段未就绪而非故障：按 not-ready 报告，别让桥的每次刷新把它记成 internal。
        if (string.Equals(match.Phase.ToString(), "Mulligan", StringComparison.Ordinal))
        {
            return Task.FromResult(McpToolResult.Failure(
                McpCodes.NotReady,
                "对局尚未进入进行态（换牌阶段）——摘要视图暂不可用；需要面板请用 panel=true。"));
        }

        var players = new JsonArray();
        foreach (var player in match.Players)
        {
            players.Add(new JsonObject
            {
                ["index"] = player.Index,
                ["ref"] = player.ReferenceKey,
                ["handCount"] = player.Hand.Count,
                ["deckCount"] = player.Deck.Count,
                ["points"] = player.Points,
                ["pointSlots"] = player.PointSlots,
                ["hqRef"] = player.Hq.Id.ToString("N"),
                ["hqHealth"] = player.HqHealth,
                ["hand"] = HandJson(player),
            });
        }

        var details = new JsonObject
        {
            ["matchId"] = service.Options.MatchId,
            ["state"] = match.State.ToString(),
            ["phase"] = match.Phase.ToString(),
            ["turn"] = match.TurnNumber,
            ["currentPlayerIndex"] = match.CurrentPlayer?.Index,
            ["winnerIndex"] = match.Winner?.Index,
            ["endReason"] = match.EndReason?.ToString(),
            ["players"] = players,
        };

        return Task.FromResult(McpToolResult.Success("对局摘要已读取。", details: details));
    }

    /// <summary>player 模式：按座位过滤的投影视图（不含对手手牌明细）。</summary>
    private static McpToolResult ProjectedView(McpService service, Orc.Game.Match match, JsonElement args)
    {
        // 多客户端：视角优先取本次调用的 seat 参数（每个会话一个座位），缺省回落进程级 AgentSeat。
        var seat = ToolArgs.OptionalInt(args, "seat") ?? service.Options.AgentSeat;
        if (seat is null)
        {
            return McpToolResult.ModeDenied(
                "player 模式的观察面需要指定座位（调用参数 seat 或选项 AgentSeat＝0/1）——否则无法产出按座位过滤的视图。");
        }

        if (seat is < 0 or > 1)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, "AgentSeat 须为 0 或 1。");
        }

        // 统一走服务层投影面（整枚 view ＋ actions；只读执行面，选靶挂起期也可用）。
        _ = match;
        var projected = service.ProjectView(seat.Value);
        if (projected.Payload is not JsonObject details)
        {
            return McpToolResult.Failure(
                projected.FailureCode ?? McpCodes.Internal,
                projected.FailureMessage ?? "投影失败。");
        }

        details["kind"] = "observe.view.player";
        details["hiddenInfoNote"] = "player 模式只提供按座位过滤的信息（对手手牌明细不可见）。";
        return McpToolResult.Success("按座位过滤的视图已读取。", details: details);
    }

    private static JsonArray HandJson(Orc.Game.Players.Player player)
    {
        var hand = new JsonArray();
        foreach (var card in player.Hand)
        {
            hand.Add(new JsonObject
            {
                ["ref"] = card.Id.ToString("N"),
                ["name"] = card.Name,
                ["alive"] = card.Life.IsAlive,
            });
        }

        return hand;
    }
}
