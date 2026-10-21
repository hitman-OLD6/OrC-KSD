using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp.Tools;

/// <summary>
/// 对局塑造工具（Q15 B 档）：直接构造验证场景——绕过输入/费用/相位门禁，但**不改变引擎不变量**
/// （伤害走引擎伤害管线、销毁走引擎销毁入口、事件走总线）。
/// </summary>
internal static class ShapeTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "sandbox.spawn",
            "sandbox",
            "按定义 id 造卡并放置：zone=hand（手牌）或 zone=support（支援线首个空槽，仅单位卡）。（逐步由原语替代）",
            """
            {
              "type": "object",
              "properties": {
                "definitionId": { "type": "string", "description": "已注册的卡定义 id" },
                "owner": { "type": "integer", "description": "玩家索引（0/1）" },
                "zone": { "type": "string", "enum": ["hand", "support"], "description": "落点（缺省 hand）" }
              },
              "required": ["definitionId", "owner"],
              "additionalProperties": false
            }
            """,
            (call, ct) => SpawnAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.kill",
            "sandbox",
            "销毁指定实体（走引擎一步式销毁入口：杀 + card.destroyed + 装载链清理）。（逐步由原语替代）",
            """
            {
              "type": "object",
              "properties": {
                "ref": { "type": "string", "description": "实体引用键（N 格式 GUID，取自观察面/候选）" }
              },
              "required": ["ref"],
              "additionalProperties": false
            }
            """,
            (call, ct) => KillAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.damage",
            "sandbox",
            "对目标造成伤害（走引擎伤害管线：单位扣防御、HQ 扣血；致死时返回 lethal=true）。（逐步由原语替代）",
            """
            {
              "type": "object",
              "properties": {
                "ref": { "type": "string", "description": "目标实体引用键" },
                "amount": { "type": "integer", "description": "伤害点数" },
                "sourceRef": { "type": "string", "description": "可选：施动方引用键（提供时归属伤害来源）" }
              },
              "required": ["ref", "amount"],
              "additionalProperties": false
            }
            """,
            (call, ct) => DamageAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.set_points",
            "sandbox",
            "调整玩家指挥点（delta 可正可负；经资源管理器入口，受指挥点上限约束）。（逐步由原语替代：绝对值设定见 sandbox.points）",
            """
            {
              "type": "object",
              "properties": {
                "owner": { "type": "integer", "description": "玩家索引（0/1）" },
                "delta": { "type": "integer", "description": "增量（可负）" }
              },
              "required": ["owner", "delta"],
              "additionalProperties": false
            }
            """,
            (call, ct) => SetPointsAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.emit",
            "sandbox",
            "向对局总线投递事件（任意事件名；载荷中的字符串若可解析为引擎引用键则替换为对象引用）。"
            + "返回订阅者数量——为 0 表示无人接收（事件名可能写错或该事件无订阅者）。（逐步由原语替代）",
            """
            {
              "type": "object",
              "properties": {
                "event": { "type": "string", "description": "事件名（如 card.placed；任意非空白字符串）" },
                "payload": { "type": "object", "description": "可选载荷（键名与引擎载荷键常量一致）" }
              },
              "required": ["event"],
              "additionalProperties": false
            }
            """,
            (call, ct) => EmitAsync(service, call, ct),
            sandboxOnly: true);
    }

    private static async Task<McpToolResult> SpawnAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var definitionId = ToolArgs.RequireString(call.Arguments, "definitionId");
        var owner = ToolArgs.RequireInt(call.Arguments, "owner");
        var zone = ToolArgs.OptionalString(call.Arguments, "zone") ?? "hand";

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    if (owner is < 0 or > 1)
                    {
                        return ActionOutcome.Failed(McpCodes.InvalidArguments, "owner 须为 0 或 1。");
                    }

                    var player = m.Players[owner];
                    var card = await m.CardService.CreateAsync(definitionId, player, ct).ConfigureAwait(false);

                    var payload = new JsonObject
                    {
                        ["ref"] = card.Id.ToString("N"),
                        ["definitionId"] = definitionId,
                        ["owner"] = owner,
                        ["cardName"] = card.Name,
                    };

                    switch (zone)
                    {
                        case "hand":
                            var handResult = await m.CardService.PlaceToHandAsync(card, player, ct).ConfigureAwait(false);
                            payload["zone"] = "hand";
                            payload["placeStatus"] = handResult.Status.ToString();
                            break;

                        case "support":
                            if (card is not UnitCard unit)
                            {
                                return ActionOutcome.Failed(McpCodes.InvalidArguments, $"卡 '{definitionId}' 不是单位卡，不能放置到支援线。");
                            }

                            var line = m.Battlefield.GetSupportLine(player);
                            Slot? empty = null;
                            for (var i = 0; i < line.Count; i++)
                            {
                                if (line[i].Occupant is null)
                                {
                                    empty = line[i];
                                    break;
                                }
                            }

                            if (empty is null)
                            {
                                return ActionOutcome.Failed(McpCodes.EngineRejected, "目标玩家支援线已无空槽。");
                            }

                            var placeResult = await m.CardService.PlaceToSupportLineAsync(unit, player, empty, ct).ConfigureAwait(false);
                            payload["zone"] = "support";
                            payload["slotIndex"] = empty.Index;
                            payload["placeStatus"] = placeResult.Status.ToString();
                            break;

                        default:
                            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"未知 zone '{zone}'（支持 hand/support）。");
                    }

                    return ActionOutcome.Completed(payload);
                },
                ct)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "卡已创建并放置。");
    }

    private static async Task<McpToolResult> KillAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var reference = ToolArgs.RequireString(call.Arguments, "ref");

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    if (!TryResolveCard(m, reference, out var card, out var error))
                    {
                        return ActionOutcome.Failed(McpCodes.EngineRejected, error);
                    }

                    await m.Engine.DestroyCard(card!).ConfigureAwait(false);
                    return ActionOutcome.Completed(new JsonObject
                    {
                        ["ref"] = reference,
                        ["destroyed"] = true,
                        ["alive"] = card!.Life.IsAlive,
                    });
                },
                ct)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "实体已销毁。");
    }

    private static async Task<McpToolResult> DamageAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var reference = ToolArgs.RequireString(call.Arguments, "ref");
        var amount = ToolArgs.RequireInt(call.Arguments, "amount");
        var sourceReference = ToolArgs.OptionalString(call.Arguments, "sourceRef");

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    if (!TryResolveCard(m, reference, out var target, out var error))
                    {
                        return ActionOutcome.Failed(McpCodes.EngineRejected, error);
                    }

                    Card? source = null;
                    if (!string.IsNullOrWhiteSpace(sourceReference))
                    {
                        if (!TryResolveCard(m, sourceReference!, out source, out var sourceError))
                        {
                            return ActionOutcome.Failed(McpCodes.EngineRejected, sourceError);
                        }
                    }

                    var runtime = EffectRuntime.ResolveFor(target!);
                    if (runtime is null)
                    {
                        return ActionOutcome.Failed(McpCodes.EngineRejected, $"无法解析目标 '{reference}' 的效果运行时（返回空）。");
                    }

                    var lethal = await runtime.DamageAsync(target!, amount, source, ct).ConfigureAwait(false);

                    return ActionOutcome.Completed(new JsonObject
                    {
                        ["ref"] = reference,
                        ["amount"] = amount,
                        ["lethal"] = lethal,
                        ["alive"] = target!.Life.IsAlive,
                    });
                },
                ct)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "伤害已结算。");
    }

    private static async Task<McpToolResult> SetPointsAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var owner = ToolArgs.RequireInt(call.Arguments, "owner");
        var delta = ToolArgs.RequireInt(call.Arguments, "delta");

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    if (owner is < 0 or > 1)
                    {
                        return ActionOutcome.Failed(McpCodes.InvalidArguments, "owner 须为 0 或 1。");
                    }

                    var player = m.Players[owner];
                    await m.ResourceManager.AddPointsAsync(player, delta, ct).ConfigureAwait(false);

                    return ActionOutcome.Completed(new JsonObject
                    {
                        ["owner"] = owner,
                        ["delta"] = delta,
                        ["points"] = player.Points,
                        ["pointSlots"] = player.PointSlots,
                    });
                },
                ct)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, "指挥点已调整。");
    }

    private static async Task<McpToolResult> EmitAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var eventName = ToolArgs.RequireString(call.Arguments, "event");
        var payloadElement = ToolArgs.OptionalObject(call.Arguments, "payload");

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (payloadElement is { } obj)
                    {
                        foreach (var property in obj.EnumerateObject())
                        {
                            payload[property.Name] = ResolvePayloadValue(m, property.Value);
                        }
                    }

                    await m.Engine.Emit(eventName, payload, ct).ConfigureAwait(false);

                    var subscribers = m.Engine.Bus.GetSubscribers(eventName);
                    return ActionOutcome.Completed(new JsonObject
                    {
                        ["event"] = eventName,
                        ["subscriberCount"] = subscribers.Count,
                        ["hasSubscriber"] = subscribers.Count > 0,
                    });
                },
                ct)
            .ConfigureAwait(false);

        var result = ToolResults.FromOutcome(outcome, "事件已投递。");
        if (result.Ok && result.EngineResult is JsonObject node && node["subscriberCount"]?.GetValue<int>() == 0)
        {
            node["warning"] = "no-subscriber：该事件名当前无订阅者（事件名可能写错，或该事件在本对局确实无人监听）。";
        }

        return result;
    }

    private static bool TryResolveCard(Orc.Game.Match match, string reference, out Card? card, out string error)
    {
        card = null;
        error = string.Empty;
        if (!match.Engine.References.TryResolve(reference, out var value))
        {
            error = $"引用键 '{reference}' 不在本对局的引擎引用表中。";
            return false;
        }

        if (value is not Card resolved)
        {
            error = $"引用键 '{reference}' 指向的对象不是卡牌（{value?.GetType().Name}）。";
            return false;
        }

        card = resolved;
        return true;
    }

    private static object? ResolvePayloadValue(Orc.Game.Match match, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                return match.Engine.References.TryResolve(text, out var resolved) ? resolved : text;

            case JsonValueKind.Number:
                return value.TryGetInt32(out var i) ? i : value.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in value.EnumerateArray())
                {
                    list.Add(ResolvePayloadValue(match, item));
                }

                return list;

            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    map[property.Name] = ResolvePayloadValue(match, property.Value);
                }

                return map;

            default:
                return null;
        }
    }
}
