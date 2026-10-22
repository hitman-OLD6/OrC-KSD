using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 目标合法性判定器（K1；C1＝combat.target.legal——交战合法性判定族之组合判定器）：
/// （攻击者, 目标引用）→ bool；编排六重＝归属→存活/在场→烟幕→守护资格→拦截→范围
/// （HQ 分支＝归属→占位槽→守护→拦截→范围）；原 <c>CommandManager.IsLegalUnitTarget</c>／<c>IsLegalHqTarget</c>／
/// <c>IsAttackTargetLegal</c> 规则编排迁入（规则外提）——顺序与短路行为与收编前一致。
/// 引用契约：接受任意引用——失效引用＝false、未知载体类型＝false（不异常）；载体分派＝Hq 分支／UnitCard 分支／其它→false。
/// 子规则消费（经条目句柄——运行时解析、每次调用动态取栈顶）：守护资格→<see cref="CombatGuardEligibilityJudicator"/>、
/// 拦截→<see cref="CombatInterceptionJudicator"/>、范围→<see cref="CombatRangeJudicator"/>；
/// 禁止内联副本/快照子规则行为——单点改写任一子规则＝本编排对应环节同步生效。
/// 组件经装配期注入（敌我判定／守护查询〔维护型状态读取——经既有查询面〕／子规则条目调用通道——仅只读使用）。
/// 改写＝moding（combat.target.legal——候选链与复验链全部调用点同步生效）；无状态：不持有跨调用可变状态。
/// </summary>
internal sealed class CombatTargetLegalJudicator : Judicator<CombatTargetLegalJudicator.TargetLegalRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（攻击者, 目标引用）→ 目标是否合法。</summary>
    public delegate bool TargetLegalRule(UnitCard attacker, Ref<Entity> targetRef);

    private readonly Func<Player, Player?> _enemyOf;
    private readonly Func<UnitCard, bool> _isUnitGuarded;
    private readonly Func<Hq, bool> _isHqGuarded;
    private readonly Func<UnitCard, BoardPosition, bool> _rangeRule;
    private readonly Func<UnitCard, bool> _guardEligibilityRule;
    private readonly Func<UnitCard, BoardPosition, bool, bool> _interceptionRule;

    /// <summary>创建目标合法性判定器（装配期注入对局级只读设施引用与子规则条目调用通道）。</summary>
    /// <param name="enemyOf">敌我判定（归属环节；返回该玩家的对手，无对手＝null）。</param>
    /// <param name="isUnitGuarded">单位被守护查询（维护型状态读取——经既有查询面转发）。</param>
    /// <param name="isHqGuarded">HQ 被守护查询（维护型状态读取——经既有查询面转发）。</param>
    /// <param name="rangeRule">范围矩阵子规则调用通道（C2 条目句柄／内置实例——每次调用经统一解析点）。</param>
    /// <param name="guardEligibilityRule">资格子规则调用通道（C3 条目句柄／内置实例——每次调用经统一解析点）。</param>
    /// <param name="interceptionRule">拦截子规则调用通道（C4 条目句柄／内置实例——每次调用经统一解析点）。</param>
    /// <exception cref="ArgumentNullException">任一注入项为 null。</exception>
    internal CombatTargetLegalJudicator(
        Func<Player, Player?> enemyOf,
        Func<UnitCard, bool> isUnitGuarded,
        Func<Hq, bool> isHqGuarded,
        Func<UnitCard, BoardPosition, bool> rangeRule,
        Func<UnitCard, bool> guardEligibilityRule,
        Func<UnitCard, BoardPosition, bool, bool> interceptionRule)
    {
        ArgumentNullException.ThrowIfNull(enemyOf);
        ArgumentNullException.ThrowIfNull(isUnitGuarded);
        ArgumentNullException.ThrowIfNull(isHqGuarded);
        ArgumentNullException.ThrowIfNull(rangeRule);
        ArgumentNullException.ThrowIfNull(guardEligibilityRule);
        ArgumentNullException.ThrowIfNull(interceptionRule);

        _enemyOf = enemyOf;
        _isUnitGuarded = isUnitGuarded;
        _isHqGuarded = isHqGuarded;
        _rangeRule = rangeRule;
        _guardEligibilityRule = guardEligibilityRule;
        _interceptionRule = interceptionRule;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(TargetLegalRule handler)
        => args => new object[] { handler(Unpack<UnitCard>(args, 0), Unpack<Ref<Entity>>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>目标合法性求值（原 <c>IsAttackTargetLegal</c> 三段语义逐字迁移：失效引用＝false／Hq 分支／单位分支／其它＝false）。</summary>
    private bool Evaluate(UnitCard attacker, Ref<Entity> targetRef)
    {
        if (!targetRef.IsAlive)
        {
            return false;
        }

        var targetEntity = targetRef.Value;
        if (targetEntity is Hq hq)
        {
            return IsLegalHqTarget(attacker, hq);
        }

        if (targetEntity is UnitCard target)
        {
            return IsLegalUnitTarget(attacker, target);
        }

        return false;
    }

    /// <summary>单位为目标的合法性（原 <c>CommandManager.IsLegalUnitTarget</c> 逐字迁移：归属 ∧ 存活/在场 ∧ 不被烟幕 ∧
    /// 被守护仅炮/轰可攻〔经 C3 条目〕∧ 拦截〔经 C4 条目〕∧ 范围矩阵〔经 C2 条目〕——顺序与短路行为保持）。</summary>
    private bool IsLegalUnitTarget(UnitCard attacker, UnitCard target)
    {
        var enemy = attacker.Owner is { } owner ? _enemyOf(owner) : null;
        if (enemy is null || !ReferenceEquals(target.Owner, enemy))
        {
            return false;
        }

        if (!target.TryGetData<UnitStateData>(out var targetState)
            || targetState.IsDestroyed
            || targetState.Line is null)
        {
            return false;
        }

        if (KeywordRules.HasKeyword(target, KeywordIds.SmokeScreen))
        {
            return false; // 烟幕：不可被攻击（对一切攻击者生效）
        }

        if (_isUnitGuarded(target) && !_guardEligibilityRule(attacker))
        {
            return false; // 被守护：仅能被炮/轰攻击（资格经 C3 条目——单点改写穿透）
        }

        // 轰炸机拦截（后置项 C；经 C4 条目）：目标所在战线存在存活敌方战斗机时，该战线的非战斗机目标置黑
        var targetLine = targetState.Line!;
        var targetIndex = targetLine.IndexOf(target);
        if (targetIndex < 0)
        {
            return false;
        }

        var targetPosition = new BoardPosition(targetLine, targetIndex);
        if (_interceptionRule(attacker, targetPosition, CombatTypeGroups.IsFighter(target)))
        {
            return false;
        }

        return _rangeRule(attacker, targetPosition); // 范围矩阵（经 C2 条目）
    }

    /// <summary>HQ 为目标的合法性（原 <c>CommandManager.IsLegalHqTarget</c> 逐字迁移：归属 ∧ 占位槽布局 ∧
    /// 被守护仅炮/轰可攻〔经 C3 条目〕∧ 拦截〔经 C4 条目〕∧ 范围矩阵〔经 C2 条目〕；HQ 无存活/烟幕环节——保持现状差异）。</summary>
    private bool IsLegalHqTarget(UnitCard attacker, Hq hq)
    {
        var enemy = attacker.Owner is { } owner ? _enemyOf(owner) : null;
        if (enemy is null || !ReferenceEquals(hq.Owner, enemy))
        {
            return false;
        }

        if (hq.Line is not { } hqLine)
        {
            return false; // 异常布局（防御：HQ 未入线）
        }

        var hqSlot = new BoardPosition(hqLine, hqLine.IndexOf(hq));

        if (_isHqGuarded(hq) && !_guardEligibilityRule(attacker))
        {
            return false; // HQ 被守护（相邻守护者）：仅能被炮/轰攻击（资格经 C3 条目——单点改写穿透）
        }

        // 轰炸机拦截（后置项 C；经 C4 条目）：HQ 位于敌方支援线——该战线存在存活敌方战斗机时不可选（targetIsFighter＝false）
        if (_interceptionRule(attacker, hqSlot, false))
        {
            return false;
        }

        return _rangeRule(attacker, hqSlot); // 范围矩阵（经 C2 条目）
    }
}
