using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 效果无头选靶判定器（效果运行期选择面）：把 <see cref="EffectSelector"/> 求值为一组单位——**无头**（不排队、不等 UI 桥）。
/// <para>形态＝「转发」：候选集合经目标候选名单判定器（<see cref="JudicatorNames.TargetCandidateEligibility"/>）产出
/// （单真源——不复制过滤逻辑）；本判定器只负责 <c>sel/count/random</c> 的取样语义。</para>
/// <para>无状态：只读枚举；不持有对局状态。名称＝<see cref="JudicatorNames.EffectTargetResolve"/>。</para>
/// </summary>
public sealed class EffectTargetResolveJudicator : Judicator<EffectTargetResolveJudicator.ResolveRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（视角卡，选择器）→ 候选单位。</summary>
    /// <param name="viewer">视角卡（决定 friendly/enemy 相对性）。</param>
    /// <param name="selector">选择器。</param>
    /// <returns>候选单位（顺序＝战场槽位序）。</returns>
    public delegate IReadOnlyList<Card> ResolveRule(Card viewer, EffectSelector selector);

    private readonly ResolveRule _rule;

    /// <summary>创建判定器（候选来源＝目标候选名单判定器）。</summary>
    /// <param name="candidateList">候选名单提供面（名单判定器条目调用）。</param>
    /// <param name="randomServiceProvider">对局随机服务解析器（random 取样；缺省＝确定性取首个）。</param>
    /// <exception cref="ArgumentNullException">candidateList 为 null。</exception>
    public EffectTargetResolveJudicator(
        Func<Card, EffectSelector, IReadOnlyList<Ref<Entity>>> candidateList,
        Func<Card, MatchRandomService?>? randomServiceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(candidateList);
        _rule = (viewer, selector) => Resolve(candidateList, randomServiceProvider, viewer, selector);
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(ResolveRule handler)
        => args => new object[] { handler(Unpack<Card>(args, 0), Unpack<EffectSelector>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(_rule)(args);

    private static IReadOnlyList<Card> Resolve(
        Func<Card, EffectSelector, IReadOnlyList<Ref<Entity>>> candidateList,
        Func<Card, MatchRandomService?>? randomServiceProvider,
        Card viewer,
        EffectSelector selector)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);

        var references = candidateList(viewer, selector);
        var cards = new List<Card>(references.Count);
        foreach (var reference in references)
        {
            if (reference.IsAlive && reference.Value is Card card)
            {
                cards.Add(card);
            }
        }

        if (selector.Sel is "all" or "self")
        {
            return cards;
        }

        var take = selector.Count is { } explicitCount && explicitCount > 0 ? explicitCount : 1;
        take = Math.Min(take, cards.Count);
        if (take <= 0)
        {
            return Array.Empty<Card>();
        }

        if (selector.Sel == "random")
        {
            var random = randomServiceProvider?.Invoke(viewer);
            if (random is not null)
            {
                return random.PickN(cards, take);
            }
        }

        return cards.GetRange(0, take);
    }
}
