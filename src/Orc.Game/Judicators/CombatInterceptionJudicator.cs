using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 轰炸机拦截判定器（K1；C4＝combat.interception——交战合法性判定族之子规则之一，独立成器便于单点 moding）：
/// （攻击者, 目标槽位, 目标是否战斗机）→ bool；原 <c>CommandManager.IsBlockedByEnemyFighter</c> 逐字迁移（规则外提，含槽位→战线解析）。
/// 规则（默认）：攻击者含轰炸机 ∧ 目标非战斗机 ∧ 目标所在战线存在存活敌方战斗机 → 拦截（置黑）；
/// 跨战线其他目标不受影响；以存活为限；多条战斗机共存＝无额外优先级（存在性判定）；异常布局→不拦截（防御）。
/// 输入：攻击者＋目标槽位（布局语义基准）＋targetIsFighter（组合器提供——单位分支＝目标含战斗机类型、HQ 分支＝false）。
/// 类型判定经 <see cref="CombatTypeGroups"/>（与 C2/C3 共表——单源）；组件经装配期注入对局级只读设施（战场布局＋敌我判定——仅只读使用）。
/// 改写＝moding（单点改写经条目栈顶替换）；组合器（combat.target.legal）编排经条目句柄消费本判定器——改写穿透编排。
/// </summary>
internal sealed class CombatInterceptionJudicator : Judicator<CombatInterceptionJudicator.InterceptionRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（攻击者, 目标槽位, 目标是否战斗机）→ 是否拦截。</summary>
    public delegate bool InterceptionRule(UnitCard attacker, BoardPosition target, bool targetIsFighter);

    private readonly Battlefield _battlefield;
    private readonly Func<Player, Player?> _enemyOf;

    /// <summary>创建拦截判定器（装配期注入对局级只读设施引用）。</summary>
    /// <exception cref="ArgumentNullException">battlefield / enemyOf 为 null。</exception>
    internal CombatInterceptionJudicator(Battlefield battlefield, Func<Player, Player?> enemyOf)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(enemyOf);

        _battlefield = battlefield;
        _enemyOf = enemyOf;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(InterceptionRule handler)
        => args => new object[]
        {
            handler(Unpack<UnitCard>(args, 0), Unpack<BoardPosition>(args, 1), Unpack<bool>(args, 2)),
        };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>拦截判定（原 <c>CommandManager.IsBlockedByEnemyFighter</c> 逐字迁移——顺序/短路/防御分支保持）。</summary>
    private bool Evaluate(UnitCard attacker, BoardPosition target, bool targetIsFighter)
    {
        if (!CombatTypeGroups.IsBomber(attacker) || targetIsFighter)
        {
            return false;
        }

        var enemy = attacker.Owner is { } owner ? _enemyOf(owner) : null;
        if (enemy is null)
        {
            return false;
        }

        if (ResolveLineOf(target) is not { } line)
        {
            return false; // 异常布局（防御）
        }

        foreach (var member in line.Members)
        {
            if (member is UnitCard unit && !IsDead(unit)
                && ReferenceEquals(unit.Owner, enemy) && CombatTypeGroups.IsFighter(unit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>位置所属战线（三线判定；异常布局＝null——防御）。</summary>
    private BattleLine? ResolveLineOf(BoardPosition target)
    {
        if (ReferenceEquals(target.Line, _battlefield.FrontLine))
        {
            return _battlefield.FrontLine;
        }

        if (ReferenceEquals(target.Line, _battlefield.PlayerASupportLine))
        {
            return _battlefield.PlayerASupportLine;
        }

        if (ReferenceEquals(target.Line, _battlefield.PlayerBSupportLine))
        {
            return _battlefield.PlayerBSupportLine;
        }

        return null;
    }

    private static bool IsDead(UnitCard unit)
        => !unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed;
}
