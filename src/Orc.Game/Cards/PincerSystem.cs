using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Targeting;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 第 2 批·A2 钳击关系体系（Q&A-3 / Q&A-12 口径）：
// 关系对象（PincerPair）＝「钳击效果」的来源（按来源整组撤销的施加/撤销承载）；
// 对局级注册表（PincerRegistry）＝一对一占用约束的查询与登记；
// 规则服务（PincerRules）＝候选收集/形成（部署链收尾驱动）/离场失效通知（死亡路径接入）。
// 组件入口见 KeywordComponents2.cs 的 PincerKeywordComponent。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 钳击配对（关系对象——「钳击效果」的来源）：一对一（单一配对）；双方都在战场上时获得钳击效果、
/// 一方离场（「不再在场」）时另一方即时失去（关系终结不自动恢复——重建仅经新的部署形成）。
/// 通用承载面＝以本关系为来源的成组效果施加/撤销（复用既有：修饰器按来源整组撤销；效果授予/词条授予
/// 同机制可表达——其余形态＝卡池内容层，在施加/撤销两处扩展）。本批示例＝修饰类「钳击：+2 攻击力」。
/// </summary>
public sealed class PincerPair
{
    /// <summary>本批示例效果（修饰类）：「钳击：+2 攻击力」的加成值。</summary>
    public const int ExampleAttackBonus = 2;

    private readonly UnitCard[] _members;

    internal PincerPair(UnitCard first, UnitCard second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        First = first;
        Second = second;
        _members = new[] { first, second };
    }

    /// <summary>先手方（部署方——发起选择者）。</summary>
    public UnitCard First { get; }

    /// <summary>同伴方（被选择方）。</summary>
    public UnitCard Second { get; }

    /// <summary>关系是否已终结（一方离场后＝true；终结后不自动恢复）。</summary>
    public bool IsBroken { get; private set; }

    /// <summary>
    /// 施加钳击效果（通用承载面）：以本关系对象为来源的成组效果施加——本批示例＝修饰类「+2 攻击力」
    /// （双方各挂一条加法修饰器；来源＝本对象——撤销经同一来源整组取回）。
    /// </summary>
    internal async Task ApplyBenefitsAsync(CancellationToken ct)
    {
        foreach (var member in _members)
        {
            await member.Modifiers.AddModifierAsync(
                new AddModifier(CardStatFields.Attack, ExampleAttackBonus, this), ct);
        }
    }

    /// <summary>
    /// 关系失效（一方离场）：撤销双方的钳击效果（按来源整组撤销）＋终结标记（幂等）。
    /// 「词条登记不动」（被清的是加成/效果而非词条本身）；离场方自身状态不另行处理（走既有离场/死亡链）。
    /// </summary>
    internal async Task BreakAsync(CancellationToken ct)
    {
        if (IsBroken)
        {
            return; // 幂等：已终结
        }

        IsBroken = true;
        foreach (var member in _members)
        {
            await member.Modifiers.RemoveBySourceAsync(this, ct); // 按来源整组撤销（无命中＝幂等无操作）
        }
    }
}

/// <summary>
/// 钳击关系注册表（对局级服务；由对局装配创建——指挥管理器持有并经词条装载上下文供给组件面）：
/// 成员 → 配对 索引（一对一占用约束：候选筛选与形成复验的查询源）。
/// 登记/移除＝体系内部写入（形成/终结时）；查询面公开。
/// </summary>
public sealed class PincerRegistry
{
    private readonly Dictionary<UnitCard, PincerPair> _byMember = new(ReferenceEqualityComparer.Instance);

    /// <summary>该单位是否已被配对（一对一占用——先到先得；未被配对的其它钳击单位可为候选）。</summary>
    public bool IsPaired(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _byMember.ContainsKey(unit);
    }

    /// <summary>查找该单位所属配对（未配对＝null）。</summary>
    public PincerPair? Find(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _byMember.TryGetValue(unit, out var pair) ? pair : null;
    }

    /// <summary>配对总数（诊断/测试读面）。</summary>
    public int Count => _byMember.Count / 2;

    /// <summary>登记配对（体系内部——形成时调用）。</summary>
    internal void Register(PincerPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _byMember[pair.First] = pair;
        _byMember[pair.Second] = pair;
    }

    /// <summary>移除配对（体系内部——关系终结时调用）。</summary>
    internal void Remove(PincerPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _byMember.Remove(pair.First);
        _byMember.Remove(pair.Second);
    }
}

/// <summary>
/// 钳击规则服务（Q&A-3/Q&A-12）：候选收集 / 形成（部署链收尾驱动——可选交互）/ 离场失效通知。
/// </summary>
public static class PincerRules
{
    /// <summary>
    /// 尝试形成钳击（部署链收尾调用；仅部署路径）：候选＝己方战场上单位（不含自身/HQ/已配对）；
    /// 候选为空＝不发起选择（无可选项）、不形成、部署正常完成；单选交互（既有形态）；
    /// 放弃/取消/失败＝不形成（该单位仍具「钳击」词条——计数与是否形成无关）；形成＝登记＋双方获钳击效果。
    /// </summary>
    internal static async Task TryFormPairAsync(UnitCard self, KeywordLoadContext context, CancellationToken ct)
    {
        var registry = context.PincerRegistry;
        var battlefield = context.Battlefield;
        var targeterManager = context.TargeterManager;
        if (registry is null || battlefield is null || targeterManager is null)
        {
            return; // 无对局服务（独立构造场景）：不形成（防御、不抛错）
        }

        if (registry.IsPaired(self))
        {
            return; // 防御：自身已配对（不应发生——部署单位首次形成）
        }

        var candidates = CollectCandidates(self, battlefield, registry);
        if (candidates.Count == 0)
        {
            return; // 无合法候选：不发起选择、不形成、部署正常完成
        }

        UnitCard? resultPartner = null;
        var result = await targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.TargetPoint,
                // R2/R8：归属＝发起钳击的部署单位归属（此前未填 → 请求被广播给所有会话）；类别＝effect（卡效果选靶）。
                new ReferenceSetParameter(candidates.Select(unit => unit.Ref), 1, 1)
                {
                    Owner = self.Owner,
                    Action = SelectorActions.Effect,
                });

            // 非法选择＝同一选择器重入（Q8a）。
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<Ref<Entity>>();
            }

            if (!step.IsOk)
            {
                return TargeterResult.Cancelled(); // 取消/失败：不形成
            }

            resultPartner = step.Value?.Value as UnitCard;
            return TargeterResult.Ok();
        });

        if (!result.IsOk || resultPartner is null)
        {
            return; // 取消（放弃选择）/ 失败：不形成、部署照常完成
        }

        var partner = resultPartner;

        // 一对一占用复验（先到先得——形成时再查：交互期间另一方可能已被占用）＋自身/归属复验
        if (registry.IsPaired(self) || registry.IsPaired(partner) || ReferenceEquals(partner, self))
        {
            return;
        }

        if (self.Owner is null || !ReferenceEquals(partner.Owner, self.Owner))
        {
            return; // 防御：非友方（不应发生——候选面已收敛）
        }

        if (partner.TryGetData<UnitStateData>(out var partnerState) && partnerState.IsDestroyed)
        {
            return; // 防御：交互期间同伴死亡（不应发生——先到先得复验之外的双保险）
        }

        var pair = new PincerPair(self, partner);
        registry.Register(pair);
        await pair.ApplyBenefitsAsync(ct); // 双方获得钳击效果（示例＝修饰类 +2 攻击力）
    }

    /// <summary>
    /// 离场通知（一方不再在场——本批接入死亡路径；其余离场路径实现接入时按同一判据「不再在场」调用）：
    /// 配对失效——撤销双方钳击效果（另一方即时失去；同事务内落定）、移除登记（关系终结不自动恢复）。
    /// 离场方自身状态不另行处理（走既有离场/死亡链）。
    /// </summary>
    internal static async Task OnUnitLeftBattlefieldAsync(UnitCard unit, PincerRegistry registry, CancellationToken ct)
    {
        var pair = registry.Find(unit);
        if (pair is null)
        {
            return; // 未配对：无操作
        }

        await pair.BreakAsync(ct);
        registry.Remove(pair);
    }

    /// <summary>候选收集（己方战场上单位：己方支援线→前线、线内索引序稳定；不含自身/HQ/已配对/隐蔽〔S2：
    /// 隐蔽单位不可被选为同伴——「被外部效果选择/收集」侧剔除；隐蔽单位自己部署钳击时作为来源不受限〕）。</summary>
    private static List<UnitCard> CollectCandidates(UnitCard self, Battlefield battlefield, PincerRegistry registry)
    {
        var result = new List<UnitCard>();
        var owner = self.Owner;
        if (owner is null)
        {
            return result;
        }

        foreach (var slot in battlefield.GetSupportLine(owner))
        {
            if (slot.Occupant is UnitCard unit && IsEligibleCandidate(self, unit, registry))
            {
                result.Add(unit);
            }
        }

        foreach (var slot in battlefield.FrontLine)
        {
            if (slot.Occupant is UnitCard unit && IsEligibleCandidate(self, unit, registry))
            {
                result.Add(unit);
            }
        }

        return result;
    }

    private static bool IsEligibleCandidate(UnitCard self, UnitCard candidate, PincerRegistry registry)
        => !ReferenceEquals(candidate, self)
            && ReferenceEquals(candidate.Owner, self.Owner)
            && !CovertRules.IsCovert(candidate) // S2：隐蔽单位不可被选为同伴（被外部效果选择/收集侧剔除；来源侧〔self〕不受限）
            && candidate.TryGetData<UnitStateData>(out var state)
            && !state.IsDestroyed
            && !registry.IsPaired(candidate);
}
