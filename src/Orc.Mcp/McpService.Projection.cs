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
    /// 产出「视图 ＋ 动作可用性」载荷（按座位过滤）。
    /// <para>载荷字段：<c>seat</c>、<c>view</c>（整枚 UiMatchView）、<c>actions</c>、以及若干扁平标量（兼容旧 <c>observe.view</c>）。</para>
    /// </summary>
    public ActionOutcome ProjectView(int seat)
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
                var payload = new JsonObject
                {
                    ["kind"] = "view.updated",
                    ["seat"] = seat,
                    ["matchId"] = view.MatchId,
                    ["view"] = JsonSerializer.SerializeToNode(view, ViewJson),
                    ["actions"] = ComputeActions(m, seat),

                    // 兼容既有 observe.view 的扁平标量
                    ["turn"] = view.Turn,
                    ["phase"] = view.Phase,
                    ["activePlayerSide"] = view.ActivePlayerSide,
                    ["selfHandCount"] = view.SelfHandCount,
                    ["selfDeckCount"] = view.SelfDeckCount,
                    ["enemyHandCount"] = view.EnemyHandCount,
                    ["enemyDeckCount"] = view.EnemyDeckCount,
                    ["selfKredits"] = view.SelfKredits,
                    ["selfMaxKredits"] = view.SelfMaxKredits,
                    ["selfCounterCount"] = view.SelfCounterCount,
                    ["resultTitle"] = view.ResultTitle,
                };

                return ActionOutcome.Completed(payload);
            });
        }
        catch (InvalidOperationException ex)
        {
            return ActionOutcome.Failed(McpCodes.EngineRejected, ex.Message);
        }
        catch (Exception ex)
        {
            return ActionOutcome.Failed(McpCodes.Internal, $"投影失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 动作可用性（S4b 候选集版本）：
    /// 可打出集合、部署空隙、可移动目标槽、可攻击目标、能否结束回合、换牌开关。
    /// <para>
    /// 不预测伤害与死亡（`attackPreviews` 只给攻击者/目标，伤害字段为 null，死亡标记恒 false）——
    /// 伤害预演需要独立模拟，与真实结算存在漂移风险。
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
                    var line = battlefield.FrontLine;
                    for (var i = 0; i < line.Count; i++)
                    {
                        if (line[i].Occupant is null)
                        {
                            moves.Add(new JsonObject
                            {
                                ["uid"] = unit.Id.ToString("N"),
                                ["toZone"] = "front",
                                ["slotIndex"] = i,
                            });
                        }
                    }
                }

                if (availability.Attack?.CanUse == true)
                {
                    var attackerAttack = EffectiveAttack(unit);

                    foreach (var target in AttackTargets(player, enemy, battlefield))
                    {
                        var defenderAttack = target is Orc.Game.Cards.UnitCard defender
                            ? EffectiveAttack(defender)
                            : 0;

                        attackPreviews.Add(new JsonObject
                        {
                            ["attackerUid"] = unit.Id.ToString("N"),
                            ["defenderUid"] = target.Id.ToString("N"),

                            // 基础伤害（有效攻击力）；**不含**伤害管线上的词条/效果改写与死亡判定。
                            ["damageToDefender"] = attackerAttack,
                            ["damageToAttacker"] = defenderAttack,
                            ["defenderDies"] = false,
                            ["attackerDies"] = false,
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

    /// <summary>己方在场单位（前线＋己方支援线）。</summary>
    private static IEnumerable<Orc.Game.Cards.UnitCard> OwnUnits(
        Orc.Game.Players.Player player,
        Orc.Game.Board.Battlefield battlefield)
    {
        foreach (var slot in battlefield.FrontLine)
        {
            if (slot.Occupant is Orc.Game.Cards.UnitCard unit && ReferenceEquals(unit.Owner, player))
            {
                yield return unit;
            }
        }

        foreach (var slot in battlefield.GetSupportLine(player))
        {
            if (slot.Occupant is Orc.Game.Cards.UnitCard unit)
            {
                yield return unit;
            }
        }
    }

    /// <summary>
    /// 可攻击目标（候选集，不含伤害预测）：敌方前线单位；敌前线为空时可指向敌方 HQ。
    /// </summary>
    private static IEnumerable<Orc.Core.Entity> AttackTargets(
        Orc.Game.Players.Player player,
        Orc.Game.Players.Player enemy,
        Orc.Game.Board.Battlefield battlefield)
    {
        var found = false;
        foreach (var slot in battlefield.FrontLine)
        {
            if (slot.Occupant is Orc.Game.Cards.UnitCard unit && !ReferenceEquals(unit.Owner, player))
            {
                found = true;
                yield return unit;
            }
        }

        if (!found && enemy.Hq is { } hq)
        {
            yield return hq;
        }
    }
}
