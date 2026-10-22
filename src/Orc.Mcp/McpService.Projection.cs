using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp;

/// <summary>
/// 投影面（多人支持 S4）：整枚 <c>UiMatchView</c> ＋ 动作可用性同拍产出。
/// <para>
/// 只读执行面（<see cref="Match.MatchExecutor.ReadOnly"/>）不取门禁、不判忙，故在选靶挂起期间仍可产出。
/// </para>
/// </summary>
public sealed partial class McpService
{
    private static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 产出**单座位视角帧**（绑 seat 的 viewer / 兼任连接的 viewer 面）。
    /// <para>帧形状：<c>{kind:"view.updated", perspective:{mode:"seat",seat:N}, view, actions}</c>。</para>
    /// <para>
    /// 刻意变化第 2 条：不再输出扁平标量兼容字段（<c>turn</c>/<c>selfHandCount</c>/…）——
    /// 帧形状只保留 <c>perspective</c> ＋ <c>view</c>/<c>views</c> ＋ <c>actions</c> 一套真源。
    /// </para>
    /// </summary>
    public ActionOutcome ProjectSeatFrame(int seat)
    {
        if (seat is not (0 or 1))
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"座位号 {seat} 越界（0/1）。");
        }

        if (!TryGetActiveMatch(out var match))
        {
            return ActionOutcome.Failed(McpCodes.NotReady, "无活跃对局。");
        }

        if (!match.UiProjections.HasProvider)
        {
            return ActionOutcome.Failed(McpCodes.NotSupported, "该对局未装配 UI 投影提供者（无法产出按座位过滤的视图）。");
        }

        try
        {
            return Executor.ReadOnly(m =>
            {
                // artLookup / definitionLookup 传 null：美术与卡面文本属客户端自持资源（R6）。
                var provider = m.UiProjections.CreateProvider(null, Options.MatchId, null);
                if (provider is null)
                {
                    return ActionOutcome.Failed(McpCodes.NotSupported, "投影提供者不可用（CreateProvider 返回空）。");
                }

                var view = provider.Project(m, m.Players[seat]);
                return ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "view.updated",
                    ["perspective"] = new JsonObject
                    {
                        ["mode"] = "seat",
                        ["seat"] = seat,
                    },
                    ["view"] = JsonSerializer.SerializeToNode(view, ViewJson),
                    ["actions"] = ComputeActions(m, seat),
                });
            });
        }
        catch (Exception ex)
        {
            return ProjectionFailure(match, ex);
        }
    }

    /// <summary>
    /// 产出**全知帧**（不绑 seat 的 viewer）：**同一次投影**内产出两份座位视角并打包下发，
    /// 每份仍是既有 <see cref="Orc.UI.Contracts.UiMatchView"/>（不新增"与视角无关的视图类型"）。
    /// <para>帧形状：<c>{kind:"view.updated", perspective:{mode:"omniscient"}, views:[{seat,view,actions},…]}</c>。</para>
    /// </summary>
    public ActionOutcome ProjectOmniscientFrame()
    {
        if (!TryGetActiveMatch(out var match))
        {
            return ActionOutcome.Failed(McpCodes.NotReady, "无活跃对局。");
        }

        if (!match.UiProjections.HasProvider)
        {
            return ActionOutcome.Failed(McpCodes.NotSupported, "该对局未装配 UI 投影提供者（无法产出全知视图）。");
        }

        try
        {
            return Executor.ReadOnly(m =>
            {
                var provider = m.UiProjections.CreateProvider(null, Options.MatchId, null);
                if (provider is null)
                {
                    return ActionOutcome.Failed(McpCodes.NotSupported, "投影提供者不可用（CreateProvider 返回空）。");
                }

                var views = new JsonArray();
                for (var seat = 0; seat < 2; seat++)
                {
                    var view = provider.Project(m, m.Players[seat]);
                    views.Add(new JsonObject
                    {
                        ["seat"] = seat,
                        ["view"] = JsonSerializer.SerializeToNode(view, ViewJson),
                        ["actions"] = ComputeActions(m, seat),
                    });
                }

                return ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "view.updated",
                    ["perspective"] = new JsonObject { ["mode"] = "omniscient" },
                    ["views"] = views,
                });
            });
        }
        catch (Exception ex)
        {
            return ProjectionFailure(match, ex);
        }
    }

    /// <summary>
    /// 调试面的对局摘要（含双方手牌明细）：诊断中间态用，**不是**"全知 viewer 的输出"。
    /// <para>同样走只读执行面（消费者上下文守卫），避免绕过单写者语义直接读引擎。</para>
    /// </summary>
    public ActionOutcome ProjectDebugSummary()
    {
        if (!TryGetActiveMatch(out var match))
        {
            return ActionOutcome.Failed(McpCodes.NotReady, "无活跃对局。");
        }

        try
        {
            return Executor.ReadOnly(m =>
            {
                var players = new JsonArray();
                foreach (var player in m.Players)
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
                        ["hand"] = hand,
                    });
                }

                return ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "observe.summary",
                    ["matchId"] = Options.MatchId,
                    ["state"] = m.State.ToString(),
                    ["phase"] = m.Phase.ToString(),
                    ["turn"] = m.TurnNumber,
                    ["currentPlayerIndex"] = m.CurrentPlayer?.Index,
                    ["winnerIndex"] = m.Winner?.Index,
                    ["endReason"] = m.EndReason?.ToString(),
                    ["players"] = players,
                });
            });
        }
        catch (Exception ex)
        {
            return ProjectionFailure(match, ex);
        }
    }

    /// <summary>
    /// 投影失败分类：换牌／准备期没有"当前玩家"不是故障（按 not-ready），其余按引擎拒绝或内部故障。
    /// </summary>
    private static ActionOutcome ProjectionFailure(Orc.Game.Match match, Exception ex)
    {
        // 注：`Orc.Game.Match` 命名空间与 `Match` 类型同名，此处无法书写枚举全名，故按相位名比较。
        var notStarted = string.Equals(match.Phase.ToString(), "Mulligan", StringComparison.Ordinal)
            || string.Equals(match.State.ToString(), "Preparing", StringComparison.Ordinal);
        if (notStarted)
        {
            return ActionOutcome.Failed(
                McpCodes.NotReady,
                $"对局尚未进入进行态（相位 {match.Phase} / 状态 {match.State}）——投影暂不可用。");
        }

        return ex is InvalidOperationException
            ? ActionOutcome.Failed(McpCodes.EngineRejected, ex.Message)
            : ActionOutcome.Failed(McpCodes.Internal, $"投影失败：{ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// 动作可用性（S4b 候选集版本）：
    /// 可打出集合、部署空隙、可移动目标槽、可攻击目标、能否结束回合、换牌开关。
    /// <para>
    /// <b>候选来源＝引擎</b>（R3）：<c>attackPreviews</c> 与 <c>moves</c> 一律读
    /// <see cref="Orc.Game.Commanding.CommandManager.GetCommandAvailability"/> 给出的候选——
    /// 与 <c>command.attack</c> / <c>command.move</c> 同一来源、同一过滤链，投影层**不**自建第二套目标规则。
    /// </para>
    /// <para>
    /// 含**基础**伤害与死亡判定（<c>damageToDefender</c> / <c>damageToAttacker</c> / <c>defenderDies</c> /
    /// <c>attackerDies</c>：按有效攻防计算、不经伤害管线），**不含**词条与效果改写——
    /// 那是结算期才有的事实，预演不做模拟以免与真实结算漂移。HQ 条目不做死亡判定（血量远高于单次攻击）。
    /// </para>
    /// <para>`orderTargets` 仍为空：指令目标在打出后由选靶请求提供。</para>
    /// </summary>
    private static JsonObject ComputeActions(Orc.Game.Match match, int seat)
    {
        var player = match.Players[seat];
        var enemy = match.Players[1 - seat];
        var battlefield = match.Battlefield;
        var inPlay = match.State == Orc.Game.MatchState.InProgress;

        var playable = new JsonArray();
        var deploymentGaps = new JsonArray();

        foreach (var card in player.Hand)
        {
            if (card is not Orc.Game.Cards.CardBase cardBase || !match.PlayManager.CanPlay(cardBase))
            {
                continue;
            }

            playable.Add(card.Id.ToString("N"));

            if (card is Orc.Game.Cards.UnitCard unit)
            {
                var gaps = match.PlayManager.GetDeploymentGaps(unit);
                if (gaps.Count > 0)
                {
                    var refs = new JsonArray();
                    foreach (var gap in gaps)
                    {
                        refs.Add(gap.Id.ToString("N"));
                    }

                    deploymentGaps.Add(new JsonObject
                    {
                        ["uid"] = unit.Id.ToString("N"),
                        ["gaps"] = refs,
                    });
                }
            }
        }

        var moves = new JsonArray();
        var attackPreviews = new JsonArray();

        if (inPlay)
        {
            foreach (var unit in OwnUnits(player, battlefield))
            {
                var availability = match.CommandManager.GetCommandAvailability(unit);

                if (availability.Move?.CanUse == true)
                {
                    // 候选＝引擎目的地（前沿空槽；后撤语义下为己方支援线空槽）——不再自行枚举前线空槽。
                    foreach (var candidate in availability.Move.Candidates)
                    {
                        var destination = candidate.IsAlive ? candidate.Value as Orc.Game.Board.FormationGap : null;
                        if (destination is null)
                        {
                            continue;
                        }

                        // toZone/slotIndex 由槽所属线给出：slotIndex 的语义随 toZone 解释（support 时指己方支援线槽位）。
                        if (!TryLocateSlot(battlefield.FrontLine, destination, out var index))
                        {
                            if (!TryLocateSlot(battlefield.GetSupportLine(player), destination, out index))
                            {
                                continue; // 未知线：不猜测，直接略过
                            }

                            moves.Add(new JsonObject
                            {
                                ["uid"] = unit.Id.ToString("N"),
                                ["toZone"] = "support",
                                ["slotIndex"] = index,
                            });
                            continue;
                        }

                        moves.Add(new JsonObject
                        {
                            ["uid"] = unit.Id.ToString("N"),
                            ["toZone"] = "front",
                            ["slotIndex"] = index,
                        });
                    }
                }

                if (availability.Attack?.CanUse == true)
                {
                    var attackerAttack = EffectiveAttack(unit);

                    foreach (var candidate in availability.Attack.Candidates)
                    {
                        // 候选＝引擎合法目标（敌支援线 → 前线 → 敌 HQ，逐项经 combat.target.legal）。
                        if (!candidate.IsAlive)
                        {
                            continue;
                        }

                        var target = candidate.Value;
                        var defenderAttack = target is Orc.Game.Cards.UnitCard defender
                            ? EffectiveAttack(defender)
                            : 0;

                        // 死亡判定＝基础伤害是否达到当前有效血量（不含伤害管线上的词条/效果改写）。
                        // HQ 不走此判定（血量远高于单次攻击；真打死由终局检测负责）。
                        var defenderHealth = target is Orc.Game.Cards.UnitCard defendingUnit
                            ? EffectiveHealth(defendingUnit)
                            : int.MaxValue;
                        var attackerHealth = EffectiveHealth(unit);

                        attackPreviews.Add(new JsonObject
                        {
                            ["attackerUid"] = unit.Id.ToString("N"),
                            ["defenderUid"] = target.Id.ToString("N"),

                            // 基础伤害（有效攻击力）；**不含**伤害管线上的词条/效果改写。
                            ["damageToDefender"] = attackerAttack,
                            ["damageToAttacker"] = defenderAttack,
                            ["defenderDies"] = attackerAttack >= defenderHealth,
                            ["attackerDies"] = defenderAttack > 0 && defenderAttack >= attackerHealth,
                        });
                    }
                }
            }
        }

        return new JsonObject
        {
            ["playableUids"] = playable,
            ["handPlayabilityKnown"] = true,
            ["moves"] = moves,
            ["attackPreviews"] = attackPreviews,
            ["orderTargets"] = new JsonArray(),
            ["deploymentGaps"] = deploymentGaps,
            ["canEndTurn"] = inPlay,
            ["attacksEnabled"] = inPlay,
            ["mulliganOpen"] = match.Phase == Orc.Game.MatchPhase.Mulligan,
        };
    }

    /// <summary>有效攻击力（含修饰器链，不含伤害管线改写）。</summary>
    private static int EffectiveAttack(Orc.Game.Cards.UnitCard unit)
        => unit.Modifiers.GetEffectiveValue(Orc.Game.Cards.CardStatFields.Attack);

    /// <summary>单位当前有效血量（含防御修正；用于攻击预览的死亡判定）。</summary>
    private static int EffectiveHealth(Orc.Game.Cards.UnitCard unit)
        => unit.Modifiers.GetEffectiveValue(Orc.Game.Cards.CardStatFields.Defense);

    /// <summary>己方在场单位（前线＋己方支援线）。</summary>
    private static IEnumerable<Orc.Game.Cards.UnitCard> OwnUnits(
        Orc.Game.Players.Player player,
        Orc.Game.Board.Battlefield battlefield)
    {
        foreach (var slot in battlefield.FrontLine.Members)
        {
            if (slot is Orc.Game.Cards.UnitCard unit && ReferenceEquals(unit.Owner, player))
            {
                yield return unit;
            }
        }

        foreach (var slot in battlefield.GetSupportLine(player).Members)
        {
            if (slot is Orc.Game.Cards.UnitCard unit)
            {
                yield return unit;
            }
        }
    }

    /// <summary>插入空隙在该线中的序号（线身份 ＋ 序号；不属于该线＝false）。</summary>
    private static bool TryLocateSlot(
        Orc.Game.Board.BattleLine line, Orc.Game.Board.FormationGap slot, out int index)
    {
        if (ReferenceEquals(slot.Line, line))
        {
            index = slot.Ordinal;
            return true;
        }

        index = -1;
        return false;
    }
}
