using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 推进前置判定器（K3；C8＝move.frontline-enemy——原 <c>CommandManager.HasLivingEnemyOnFrontLine</c> 逐字迁移）：
/// （所有者）→ bool；前线是否存在存活敌方单位（空前线或己方已占＝false；敌方清空后实时恢复）。
/// 调用点＝移动可用性（ComputeMoveAvailability）与移动复验（MoveRevalidationJudicator）——两处共用同一条目
/// （单源、无第二真源；旧装配期注入转发路径退役）。
/// 组件经装配期注入（战场布局／敌我判定——仅只读使用）。改写＝moding（两调用点每次调用经统一解析点同步生效）。
/// 无状态：不持有跨调用可变状态。
/// </summary>
internal sealed class MoveFrontlineEnemyJudicator : Judicator<MoveFrontlineEnemyJudicator.FrontlineEnemyRule>
{
    /// <summary>强类型 delegate（该判定器签名）：（所有者）→ 前线是否存在存活敌方单位。</summary>
    public delegate bool FrontlineEnemyRule(Player owner);

    private readonly Battlefield _battlefield;
    private readonly Func<Player, Player?> _enemyOf;

    /// <summary>创建推进前置判定器（装配期注入对局级只读设施引用）。</summary>
    /// <param name="battlefield">战场（布局读取——前线槽位遍历）。</param>
    /// <param name="enemyOf">敌我判定（返回该玩家的对手，无对手＝null）。</param>
    /// <exception cref="ArgumentNullException">任一注入项为 null。</exception>
    internal MoveFrontlineEnemyJudicator(Battlefield battlefield, Func<Player, Player?> enemyOf)
    {
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(enemyOf);

        _battlefield = battlefield;
        _enemyOf = enemyOf;
    }

    /// <inheritdoc />
    protected override Func<object[]?, object[]?> Adapt(FrontlineEnemyRule handler)
        => args => new object[] { handler(Unpack<Player>(args, 0)) };

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);

    /// <summary>推进前置求值（原 <c>HasLivingEnemyOnFrontLine</c> 逐字迁移）。</summary>
    private bool Evaluate(Player owner)
    {
        var enemy = _enemyOf(owner);
        if (enemy is null)
        {
            return false;
        }

        foreach (var member in _battlefield.FrontLine.Members)
        {
            if (member is UnitCard unit && !IsDead(unit) && ReferenceEquals(unit.Owner, enemy))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDead(UnitCard unit)
        => !unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed;
}
