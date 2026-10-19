using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 底层逻辑集入口（<c>orc.foundation.actions</c>；R6/R17）。
/// <para>
/// P5b 完成态：八个动作的实现全部位于本程序集，<see cref="Set"/> 是完整实现集，
/// <see cref="Register"/> 只做整包默认注册（不再有过渡期合并）。
/// </para>
/// </summary>
public static class FoundationActions
{
    /// <summary>本程序集提供的**完整**动作实现集（八个工厂全部非空）。</summary>
    public static ActionImplementationSet Set { get; } = new()
    {
        CreateAddToHand = (engine, services) => new AddToHandAction(engine, services),
        CreateDraw = (engine, services, addToHand) => new DrawAction(engine, services, addToHand),
        CreateDamage = (engine, services) => new DamageAction(engine, services),
        CreateDeath = (engine, services, addToHand) => new DeathAction(engine, services, addToHand),
        CreateAttack = (engine, trigger, handle, damage, targetLegal, counterEligibility)
            => new AttackAction(engine, trigger, handle, damage, targetLegal, counterEligibility),
        CreateMove = (engine, services) => new MoveAction(engine, services),
        CreateJoinBattlefield = (engine, services) => new JoinBattlefieldAction(engine, services),
        CreateOrder = flow => new OrderAction(flow),
    };

    /// <summary>注册默认实现集（进程级、幂等）。</summary>
    public static void Register() => ActionImplementationRegistry.SetDefault(Set);
}
