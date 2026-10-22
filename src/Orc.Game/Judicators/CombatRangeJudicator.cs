using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 范围矩阵判定器（K1；C2＝combat.range——交战合法性判定族之子规则之一，独立成器便于单点 moding）：
/// （攻击者, 目标槽位）→ bool；原 <c>CommandManager.IsLineInRange</c> 逐字迁移（规则外提）。
/// 规则（默认）：炮/战/轰（任意线组）＝任意线全组合允许（含 HQ、含同线）；
/// 其余（步/坦/零类型）＝仅相邻（跨线紧邻）：支援线→敌前线；前线→敌支援线（单位/HQ）；同线不允许；异常布局＝false（防御）。
/// 类型组经 <see cref="CombatTypeGroups"/>（与 C3/C4 共表——单源）。
/// 输入：攻击者单位＋目标槽位（布局语义基准）；组件经装配期注入对局级只读设施（战场布局＋敌我判定——仅只读使用）。
/// 改写＝moding（单点改写经条目栈顶替换）；组合器（combat.target.legal）编排经条目句柄消费本判定器——改写穿透编排。
/// </summary>
internal sealed class CombatRangeJudicator : Judicator<CombatRangeJudicator.RangeRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（攻击者, 目标位置）→ 目标是否在范围内。</summary>
    public delegate bool RangeRule(UnitCard attacker, BoardPosition target);

    private readonly Battlefield _battlefield;
    private readonly Func<Player, Player?> _enemyOf;

    /// <summary>创建范围矩阵判定器（装配期注入对局级只读设施引用）。</summary>
    /// <exception cref="ArgumentNullException">battlefield / enemyOf 为 null。</exception>
    internal CombatRangeJudicator(Battlefield battlefield, Func<Player, Player?> enemyOf)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(enemyOf);

        _battlefield = battlefield;
        _enemyOf = enemyOf;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(RangeRule handler)
        => args => new object[] { handler(Unpack<UnitCard>(args, 0), Unpack<BoardPosition>(args, 1)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>范围矩阵求值（原 <c>CommandManager.IsLineInRange</c> 逐字迁移——顺序/短路/防御分支保持）。</summary>
    private bool Evaluate(UnitCard attacker, BoardPosition target)
    {
        var attackerState = attacker.GetData<UnitStateData>();
        var owner = attacker.Owner;
        if (owner is null || attackerState.Line is not { } attackerLine)
        {
            return false;
        }

        var enemy = _enemyOf(owner);
        if (enemy is null)
        {
            return false;
        }

        if (CombatTypeGroups.HasAnyLineRange(attacker))
        {
            return true;
        }

        if (ReferenceEquals(attackerLine, _battlefield.GetSupportLine(owner)))
        {
            return ReferenceEquals(target.Line, _battlefield.FrontLine); // 支援线 → 仅敌方前线单位
        }

        if (ReferenceEquals(attackerLine, _battlefield.FrontLine))
        {
            return ReferenceEquals(target.Line, _battlefield.GetSupportLine(enemy)); // 前线 → 仅敌方支援线（单位/HQ）
        }

        return false; // 异常布局（防御）
    }
}
