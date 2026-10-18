using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Judicators;

/// <summary>
/// 判定器选择规则（J3 示范②「新增包装类型」——选择规则接入点承载）：把**目标候选名单判定器**包装为「选择规则」。
/// <para>语义（S2 改造后）＝**名单成员判定**：候选由判定器 handler 产出（D9）；本类型只做
/// 「取名单 / 判成员 / 按名单过滤超集」三类只读转发，不自行构造候选。</para>
/// <para>玩家域：经视角卡归属（<see cref="CardBase.Owner"/> / <see cref="Hq.Owner"/>）的 Index 选择判定域；
/// 无归属＝默认域。</para>
/// </summary>
public sealed class JudicatorSelectionRule
{
    private readonly JudicatorRegistration _judicator;
    private readonly Card _viewer;
    private readonly EffectSelector _selector;

    /// <summary>创建选择规则（包装名单判定器锚 + 视角/选择描述）。</summary>
    /// <param name="judicator">判定器条目等价句柄（注册所得或按名解析所得——同一可寻址锚）。</param>
    /// <param name="viewer">来源卡视角（不得为 null）。</param>
    /// <param name="selector">选择描述（不得为 null）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public JudicatorSelectionRule(JudicatorRegistration judicator, Card viewer, EffectSelector selector)
    {
        ArgumentNullException.ThrowIfNull(judicator);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);
        _judicator = judicator;
        _viewer = viewer;
        _selector = selector;
    }

    /// <summary>取可选名单（判定器 handler 产出的全量候选）。</summary>
    /// <exception cref="InvalidOperationException">判定器输出契约不符（调用链错误——fail-fast 族）。</exception>
    public IReadOnlyList<Ref<Entity>> Candidates() => Invoke();

    /// <summary>单候选是否在可选名单内（提交侧复核用）。</summary>
    /// <exception cref="ArgumentNullException">candidate 为 null。</exception>
    public bool IsEligible(Ref<Entity> candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Invoke().Contains(candidate);
    }

    /// <summary>包装为逐项谓词（组装选择器候选时使用；每次调用实时求名单）。</summary>
    public Func<Ref<Entity>, bool> AsPredicate() => IsEligible;

    /// <summary>
    /// 一次性快照谓词（单次请求内使用）：名单只求一次，后续逐候选判定不再重算。
    /// 适用于候选集与判定结果在请求期间固定的交互（如一次 targeter 请求）。
    /// </summary>
    public Func<Ref<Entity>, bool> Snapshot()
    {
        var allowed = Invoke();
        return candidate => allowed.Contains(candidate);
    }

    /// <summary>按名单过滤超集（保持输入顺序；输入候选不在名单内＝剔除）。</summary>
    /// <exception cref="ArgumentNullException">candidates 为 null。</exception>
    public IReadOnlyList<Ref<Entity>> Filter(IReadOnlyList<Ref<Entity>> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var allowed = Invoke();
        var list = new List<Ref<Entity>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (allowed.Contains(candidate))
            {
                list.Add(candidate);
            }
        }

        return list;
    }

    private IReadOnlyList<Ref<Entity>> Invoke()
    {
        var ownerIndex = _viewer switch
        {
            Hq hq => hq.Owner?.Index,
            CardBase card => card.Owner?.Index,
            _ => null,
        };

        var raw = _judicator.Invoke(new object[] { _viewer, _selector }, ownerIndex);
        if (raw is { Length: 1 } && raw[0] is IReadOnlyList<Ref<Entity>> list)
        {
            return list;
        }

        throw new InvalidOperationException(
            $"选择规则判定器 '{_judicator.Name}' 输出契约不符：期望 object[]{{IReadOnlyList<Ref<Entity>>}}（1 元素）——fail-fast。");
    }
}
