using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 目标候选名单判定器（J3 示范②；S2 加性）：targeter 的**可选名单**唯一产出面。
/// <para>形态＝「持有转发」：内部持有默认规则（<see cref="TargetCandidateListRule"/>）；包装器（Before/After/Replace）
/// 可对名单做参数改写/剔除/整体替换——"其他单位声明可选性变化"＝包装本判定器。</para>
/// <para>签名（强类型面）＝（来源卡视角，选择描述）→ 候选名单（引用集）。</para>
/// <para>默认规则：在场（存活）单位可选、隐蔽单位默认剔除（<see cref="EffectSelector.IncludeCovert"/>＝请求级放宽）；
/// <c>sel == "self"</c>＝视角卡自身（不受可选性/隐蔽限制）。</para>
/// <para>无状态：不持有对局状态，全部经视角卡与战场只读读取。</para>
/// </summary>
public sealed class TargetEligibilityJudicator : Judicator<TargetEligibilityJudicator.TargetCandidateListRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（来源卡视角，选择描述）→ 候选名单。</summary>
    /// <param name="viewer">来源卡视角（决定 friendly/enemy 相对性；不得为 null）。</param>
    /// <param name="selector">选择描述（不得为 null）。</param>
    /// <returns>候选引用（顺序＝战场槽位序）。</returns>
    public delegate IReadOnlyList<Ref<Entity>> TargetCandidateListRule(Card viewer, EffectSelector selector);

    private readonly TargetCandidateListRule _rule;

    /// <summary>创建判定器（持有默认规则——「持有转发」形态）。</summary>
    /// <param name="battlefield">战场（枚举来源）。</param>
    /// <exception cref="ArgumentNullException">battlefield 为 null。</exception>
    public TargetEligibilityJudicator(Battlefield battlefield)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        _rule = (viewer, selector) => TargetCandidateResolver.Resolve(battlefield, viewer, selector);
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(TargetCandidateListRule handler)
        => args => new object[] { handler(Unpack<Card>(args, 0), Unpack<EffectSelector>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);
}

/// <summary>
/// 候选名单默认规则（判定器内部逻辑单元；<c>effect.target.resolve</c> 经本判定器转发后复用同一规则）：
/// 战场枚举 ＋ side/zone/unitType/keyword/threshold 过滤 ＋ 隐蔽剔除（请求级可放宽）。
/// </summary>
internal static class TargetCandidateResolver
{
    internal static IReadOnlyList<Ref<Entity>> Resolve(Battlefield battlefield, Card viewer, EffectSelector selector)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);

        // `self` ＝ 视角卡自身（自指效果；不受可选性/隐蔽限制——与隐蔽剔除的既有语义一致）。
        if (string.Equals(selector.Sel, "self", StringComparison.Ordinal))
        {
            return new[] { viewer.Ref };
        }

        var viewerOwner = OwnerOf(viewer);
        var lines = SelectLines(battlefield, selector.Zone);

        var candidates = new List<Ref<Entity>>();
        foreach (var line in lines)
        {
            foreach (var member in line.Members)
            {
                if (member is Hq hq && selector.Zone == "hq"
                    && (selector.Side is null or "both" or "any"
                        || viewerOwner is null
                        || (selector.Side == "friendly" && hq.Owner.Index == viewerOwner.Index)
                        || (selector.Side == "enemy" && hq.Owner.Index != viewerOwner.Index))
                    && selector.UnitType is null && selector.Threshold is null
                    && (string.IsNullOrWhiteSpace(selector.Keyword) || hq.Keywords.Has(selector.Keyword)))
                {
                    candidates.Add(hq.Ref);
                    continue;
                }

                if (selector.Zone == "hq" || member is not UnitCard unit
                    || !MatchesSide(unit, selector.Side, viewerOwner)
                    || !MatchesUnitType(unit, selector.UnitType)
                    || !MatchesKeyword(unit, selector.Keyword)
                    || !MatchesThreshold(unit, selector.Threshold)
                    || (!selector.IncludeCovert && CovertRules.IsCovert(unit)))
                {
                    continue;
                }

                candidates.Add(unit.Ref);
            }
        }

        return candidates;
    }

    private static IEnumerable<BattleLine> SelectLines(Battlefield battlefield, string? zone)
    {
        if (zone is null or "support" or "hq")
        {
            yield return battlefield.PlayerASupportLine;
            yield return battlefield.PlayerBSupportLine;
        }

        if (zone is null or "frontline")
        {
            yield return battlefield.FrontLine;
        }
    }

    private static bool MatchesSide(UnitCard unit, string? side, Player? viewerOwner)
    {
        if (side is null or "both" or "any")
        {
            return true;
        }

        if (viewerOwner is null)
        {
            return true;
        }

        var unitIndex = unit.Owner?.Index;
        return side switch
        {
            "friendly" => unitIndex == viewerOwner.Index,
            "enemy" => unitIndex is { } index && index != viewerOwner.Index,
            _ => true,
        };
    }

    private static bool MatchesThreshold(UnitCard unit, EffectThreshold? threshold)
    {
        if (threshold is null)
        {
            return true;
        }

        var field = threshold.Field switch
        {
            "attack" => CardStatFields.Attack,
            "defense" => CardStatFields.Defense,
            "opCost" => CardStatFields.OperateCost,
            "deployCost" => CardStatFields.DeployCost,
            _ => null,
        };
        if (field is null)
        {
            return false;
        }

        var value = unit.Modifiers.GetEffectiveValue(field);
        if (string.Equals(field, CardStatFields.OperateCost, StringComparison.Ordinal))
        {
            value = Math.Max(0, value); // 行动费有效值下限 0（与 OperateCosts / 投影同口径）
        }

        return threshold.Op switch
        {
            "gte" => value >= threshold.Value,
            "lte" => value <= threshold.Value,
            "gt" => value > threshold.Value,
            "lt" => value < threshold.Value,
            "eq" => value == threshold.Value,
            _ => false,
        };
    }

    private static bool MatchesKeyword(UnitCard unit, string? keyword) =>
        string.IsNullOrWhiteSpace(keyword) || unit.Keywords.Has(keyword);

    private static bool MatchesUnitType(UnitCard unit, string? unitType)
    {
        if (string.IsNullOrWhiteSpace(unitType))
        {
            return true;
        }

        if (!UnitType.TryNormalize(unitType, out var parsed))
        {
            return false; // 未知兵种词：不匹配（保守——不静默放行）
        }

        return unit.GetData<UnitStateData>().UnitTypes.Contains(parsed);
    }

    private static Player? OwnerOf(Card card) => card switch
    {
        Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };
}
