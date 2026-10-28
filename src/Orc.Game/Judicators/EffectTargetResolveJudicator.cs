using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 效果目标**取样**判定器（效果运行期选择面的一部分）：把 <see cref="EffectSelector"/> 的**取样语义**
/// 求值为一组单位——确定性（<c>all</c>／<c>self</c>）与引擎随机（<c>random</c>）。
/// <para>
/// 形态＝「转发＋取样」：候选集合经目标候选名单判定器（<see cref="JudicatorNames.TargetCandidateEligibility"/>）产出
/// （单真源——不复制过滤逻辑）；本判定器只负责取样，**不承担裁定语义**（S9／A-6）：
/// <c>choose</c>／<c>one</c>／<c>any</c> 一律明确拒绝——外部裁定必须经提交通道（<c>EffectRuntime.SelectAsync</c> →
/// targeter → 前端），引擎永不代选（R2）。
/// </para>
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

        // 确定性取样：候选集即答案（R2b：确定性归判定器，不产生 targeter）。
        if (selector.Sel is "all" or "self")
        {
            return cards;
        }

        // 引擎随机：归对局随机服务（种子可复现）；无随机服务＝明确失败，**不落"取首个"兜底**（R2c）。
        if (selector.Sel == "random")
        {
            var random = randomServiceProvider?.Invoke(viewer)
                ?? throw new InvalidOperationException(
                    "效果选择器 sel=random 未被允许：本对局没有随机服务（生产对局禁止随机取样，R2c）。"
                    + "测试／沙盒／简单 AI 场景请经对局配置显式开启随机。");

            var count = selector.Count is > 0 ? selector.Count.Value : 1;
            return cards.Count == 0 ? cards : random.PickN(cards, Math.Min(count, cards.Count));
        }

        // 裁定语义不在这里：sel=one／any／choose 需要外部意志，必须经提交通道（R2／A-6）。
        // 本判定器只做「取样」，代选会让"引擎永不代选"失效——明确拒绝，而不是按顺序取首个。
        throw new InvalidOperationException(
            $"效果选择器 sel={selector.Sel} 需要外部裁定（提交通道），判定器不代选（R2）："
            + "请经 EffectRuntime.SelectAsync 发起 targeter，或把选择器改为确定性（all/self）。");
    }
}
