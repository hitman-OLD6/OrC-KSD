using System.Text.Json.Nodes;
using Orc.Cards;
using Orc.Game;
using Orc.Game.Sandbox;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp.Tools;

/// <summary>
/// 沙盒调试原语工具（R5：<c>sandbox.*</c> 写类薄封装——只做参数解析与结果搬运，语义全在引擎原语层）。
/// <para>
/// 与既有工具的显式差异（**不合并、不改写**）：
/// <c>sandbox.spawn</c>（仅 hand / support 首空槽）↔ <c>sandbox.instantiate</c>（四去向、精确槽位）；
/// <c>sandbox.set_points</c>（增量）↔ <c>sandbox.points</c>（绝对值，可减）。
/// </para>
/// <para>
/// 门禁：<c>sandboxOnly</c>（仅 Authoring 模式登记）＋ 写类默认不登记（需 <c>--allow-debug-writes</c>）——两重门禁均在
/// <see cref="McpService.AddTool"/> 内既有实现，本文件不新增门禁。
/// </para>
/// </summary>
internal static class SandboxPrimitiveTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "sandbox.instantiate",
            "sandbox",
            "按定义 id 造卡并落到指定去向：hand（满手＝爆牌，成功语义）/ deck_top（静默）/ support[slot] / frontline[slot]"
            + "（精确槽位，两条线各自 0–4）。等效效果侧「生成并放置」原子组合。",
            """
            {
              "type": "object",
              "properties": {
                "definitionId": { "type": "string", "description": "已注册的卡定义 id" },
                "owner": { "type": "integer", "description": "玩家索引（0/1）" },
                "zone": { "type": "string", "enum": ["hand", "deck_top", "support", "frontline"], "description": "去向" },
                "slot": { "type": "integer", "description": "线内槽位索引 0..4（zone 为 support/frontline 时必填）" }
              },
              "required": ["definitionId", "owner", "zone"],
              "additionalProperties": false
            }
            """,
            (call, ct) => InstantiateAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.unitize",
            "sandbox",
            "把单位卡单位化到指定精确槽（加入链唯一写点）：发 card.placed ＋ unit.joined；不扣费、不校验回合/手牌/相位。",
            """
            {
              "type": "object",
              "properties": {
                "ref": { "type": "string", "description": "单位卡实体引用键" },
                "line": { "type": "string", "enum": ["support", "frontline"], "description": "目标线" },
                "slot": { "type": "integer", "description": "线内槽位索引 0..4" }
              },
              "required": ["ref", "line", "slot"],
              "additionalProperties": false
            }
            """,
            (call, ct) => UnitizeAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.move",
            "sandbox",
            "把在场单位挪到另一空槽（移动唯一写点 LandMoveAsync）：发 unit.position.changed；不扣行动费、不消耗行动次数，跨线允许。",
            """
            {
              "type": "object",
              "properties": {
                "ref": { "type": "string", "description": "在场单位引用键" },
                "line": { "type": "string", "enum": ["support", "frontline"], "description": "目标线" },
                "slot": { "type": "integer", "description": "线内槽位索引 0..4" }
              },
              "required": ["ref", "line", "slot"],
              "additionalProperties": false
            }
            """,
            (call, ct) => MoveAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.remove",
            "sandbox",
            "移除实体（无 mode 参数，分支由当前区域自动决定）：手牌＝弃置链、在场单位＝死亡链、其余＝一步式销毁。",
            """
            {
              "type": "object",
              "properties": {
                "ref": { "type": "string", "description": "实体引用键" }
              },
              "required": ["ref"],
              "additionalProperties": false
            }
            """,
            (call, ct) => RemoveAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.points",
            "sandbox",
            "把指挥点数设为**绝对值**（可低于当前值）：升＝point.gained→point.changed；降＝point.lost→point.changed；同值＝零信号。"
            + "不受槽数限制（超出＝成功＋warning）；判定器改写增量时＝成功＋warning=points-adjusted。"
            + "（旧 sandbox.set_points 为增量语义，原样保留。）",
            """
            {
              "type": "object",
              "properties": {
                "owner": { "type": "integer", "description": "玩家索引（0/1）" },
                "points": { "type": "integer", "description": "目标点数（绝对值，≥0）" }
              },
              "required": ["owner", "points"],
              "additionalProperties": false
            }
            """,
            (call, ct) => PointsAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.draw",
            "sandbox",
            "抽 N 张到手牌（抽牌唯一写点 DrawCard；每张独立结局）：未满手＝card.drawn→card.hand.add；满手＝爆牌（成功语义）。",
            """
            {
              "type": "object",
              "properties": {
                "owner": { "type": "integer", "description": "玩家索引（0/1）" },
                "count": { "type": "integer", "description": "抽牌张数（缺省 1）" }
              },
              "required": ["owner"],
              "additionalProperties": false
            }
            """,
            (call, ct) => DrawAsync(service, call, ct),
            sandboxOnly: true);
    }

    // ---------- 工具实现 ----------

    private static async Task<McpToolResult> InstantiateAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var definitionId = ToolArgs.RequireString(call.Arguments, "definitionId");
        var owner = ToolArgs.RequireInt(call.Arguments, "owner");
        var zone = ToolArgs.RequireString(call.Arguments, "zone");
        var slot = ToolArgs.OptionalInt(call.Arguments, "slot");

        // 参数层拦截（不触引擎）：两条阵线的去向必须给出线内槽位。
        if (zone is "support" or "frontline" && slot is null)
        {
            throw new ToolArgumentException("slot", "zone 为 support/frontline 时 slot 必填（线内索引 0..4）。");
        }

        var outcome = await service.Executor.RunAsync(
            async match =>
            {
                if (owner is < 0 or > 1)
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, "owner 须为 0 或 1。");
                }

                var result = await match.Sandbox
                    .InstantiateAsync(definitionId, match.Players[owner], zone, slot, ct).ConfigureAwait(false);

                return Translate(result, successMessage: "卡已实例化并放置。");
            },
            ct).ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "卡已实例化并放置。");
    }

    private static async Task<McpToolResult> UnitizeAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var reference = ToolArgs.RequireString(call.Arguments, "ref");
        var line = ToolArgs.RequireString(call.Arguments, "line");
        var slot = ToolArgs.RequireInt(call.Arguments, "slot");

        return await RunOnCardAsync(
            service,
            reference,
            (match, card) => match.Sandbox.UnitizeAsync(card, line, slot, ct),
            "单位已单位化。",
            ct).ConfigureAwait(false);
    }

    private static async Task<McpToolResult> MoveAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var reference = ToolArgs.RequireString(call.Arguments, "ref");
        var line = ToolArgs.RequireString(call.Arguments, "line");
        var slot = ToolArgs.RequireInt(call.Arguments, "slot");

        return await RunOnCardAsync(
            service,
            reference,
            (match, card) => match.Sandbox.MoveAsync(card, line, slot, ct),
            "单位已移动。",
            ct).ConfigureAwait(false);
    }

    private static async Task<McpToolResult> RemoveAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var reference = ToolArgs.RequireString(call.Arguments, "ref");

        return await RunOnCardAsync(
            service,
            reference,
            (match, card) => match.Sandbox.RemoveAsync(card, ct),
            "实体已移除。",
            ct).ConfigureAwait(false);
    }

    private static async Task<McpToolResult> PointsAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var owner = ToolArgs.RequireInt(call.Arguments, "owner");
        var points = ToolArgs.RequireInt(call.Arguments, "points");

        var outcome = await service.Executor.RunAsync(
            async match =>
            {
                if (owner is < 0 or > 1)
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, "owner 须为 0 或 1。");
                }

                var player = match.Players[owner];
                var result = await match.Sandbox.SetPointsAsync(player, points, ct).ConfigureAwait(false);

                var payload = new JsonObject
                {
                    ["ok"] = result.Ok,
                    ["owner"] = owner,
                    ["before"] = result.Before is int before ? before : null,
                    ["after"] = result.After is int after ? after : null,
                    ["delta"] = result.Delta,
                    ["pointSlots"] = player.PointSlots,
                };

                if (result.Warning is not null)
                {
                    payload["warning"] = result.Warning;
                    if (result.Warning == SandboxFailure.WarningPointsAdjusted)
                    {
                        payload["requested"] = points;
                        payload["actual"] = result.Actual;
                    }
                }

                if (!result.Ok)
                {
                    payload["reason"] = result.Reason;
                    return ActionOutcome.Failed(McpCodes.EngineRejected, $"原语被拒绝：{result.Reason}。", payload);
                }

                return ActionOutcome.Completed(payload);
            },
            ct).ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "指挥点已设定。");
    }

    private static async Task<McpToolResult> DrawAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var owner = ToolArgs.RequireInt(call.Arguments, "owner");
        var count = ToolArgs.OptionalInt(call.Arguments, "count") ?? 1;

        var outcome = await service.Executor.RunAsync(
            async match =>
            {
                if (owner is < 0 or > 1)
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, "owner 须为 0 或 1。");
                }

                var result = await match.Sandbox.DrawAsync(match.Players[owner], count, ct).ConfigureAwait(false);
                var payload = new JsonObject
                {
                    ["ok"] = result.Ok,
                    ["owner"] = owner,
                    ["drawn"] = ToRefArray(result.Drawn),
                    ["burned"] = ToRefArray(result.BurnedRefs),
                };

                if (!result.Ok)
                {
                    payload["reason"] = result.Reason;
                    return ActionOutcome.Failed(McpCodes.EngineRejected, $"原语被拒绝：{result.Reason}。", payload);
                }

                return ActionOutcome.Completed(payload);
            },
            ct).ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "抽牌已结算。");
    }

    // ---------- 共用 ----------

    /// <summary>按引用键取卡 → 执行原语 → 翻译结果（未建局/引用不存在等由执行器统一映射）。</summary>
    private static async Task<McpToolResult> RunOnCardAsync(
        McpService service,
        string reference,
        Func<Orc.Game.Match, Card, Task<SandboxPrimitiveResult>> primitive,
        string successMessage,
        CancellationToken ct)
    {
        var outcome = await service.Executor.RunAsync(
            async match =>
            {
                if (!match.Engine.References.TryResolve(reference, out var value))
                {
                    return ActionOutcome.Failed(
                        McpCodes.EngineRejected,
                        $"引用键 '{reference}' 不在本对局的引擎引用表中。",
                        new JsonObject { ["reason"] = SandboxFailure.EntityNotFound });
                }

                if (value is not Card card)
                {
                    return ActionOutcome.Failed(
                        McpCodes.EngineRejected,
                        $"引用键 '{reference}' 指向的对象不是卡牌（{value?.GetType().Name}）。",
                        new JsonObject { ["reason"] = SandboxFailure.EntityNotFound });
                }

                var result = await primitive(match, card).ConfigureAwait(false);
                return Translate(result, successMessage);
            },
            ct).ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, successMessage);
    }

    /// <summary>原语结果 → 执行器结局：失败＝engine-rejected ＋ details.reason（不塌成笼统码）。</summary>
    private static ActionOutcome Translate(SandboxPrimitiveResult result, string successMessage)
    {
        var payload = new JsonObject
        {
            ["ok"] = result.Ok,
            ["ref"] = result.Ref,
            ["line"] = result.Line,
            ["slot"] = result.Slot,
            ["before"] = result.Before is null ? null : JsonValue.Create(result.Before.ToString()!),
            ["after"] = result.After is null ? null : JsonValue.Create(result.After.ToString()!),
            ["burned"] = result.Burned,
        };

        if (result.Warning is not null)
        {
            payload["warning"] = result.Warning;
        }

        if (!result.Ok)
        {
            payload["reason"] = result.Reason;
            return ActionOutcome.Failed(McpCodes.EngineRejected, $"原语被拒绝：{result.Reason}。", payload);
        }

        _ = successMessage;
        return ActionOutcome.Completed(payload);
    }

    private static JsonArray ToRefArray(IEnumerable<string> references)
    {
        var array = new JsonArray();
        foreach (var reference in references)
        {
            array.Add(new JsonObject { ["ref"] = reference });
        }

        return array;
    }
}
