using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;

namespace Orc.Game.Actions;

/// <summary>
/// 动作**实现集**（S10 第 2 条 / S15 / R17）：每个工厂只声明自己需要的子动作接口，不用万能依赖容器。
/// <para>
/// 用途：整包替换（<see cref="ActionImplementationRegistry.SetDefault"/>）或按 <see cref="ActionId"/> 单动作覆盖
/// （<see cref="ActionImplementationRegistry.Override"/>）。已被替换的动作由工厂返回**宿主可见的契约类型**
/// （<c>I*Action</c> 或动作实体基类），因此替换不触及调用方（入口、服务面、信号、结果对象）。
/// </para>
/// <para>
/// 空值语义：属性为 <c>null</c>＝该动作不由本集合提供；合并时由 <see cref="ActionImplementationRegistry.Resolve"/>
/// 按「覆盖优先 → 默认兜底」补齐，仍缺失即 fail-fast。
/// </para>
/// </summary>
public sealed class ActionImplementationSet
{
    /// <summary>加入手牌（<c>hand.add</c>）工厂。</summary>
    public Func<LogicEngine, IHandActionServices, IAddToHandAction>? CreateAddToHand { get; init; }

    /// <summary>抽牌（<c>draw</c>）工厂；依赖加入手牌。</summary>
    public Func<LogicEngine, IDrawActionServices, IAddToHandAction, IDrawAction>? CreateDraw { get; init; }

    /// <summary>伤害结算（<c>damage</c>）工厂。</summary>
    public Func<LogicEngine, IDamageActionServices, IDamageAction>? CreateDamage { get; init; }

    /// <summary>死亡（<c>death</c>）工厂；依赖加入手牌（<c>Disposition=ToHand</c> 的子动作）。</summary>
    public Func<LogicEngine, IDeathActionServices, IAddToHandAction, IDeathAction>? CreateDeath { get; init; }

    /// <summary>攻击（<c>attack</c>）工厂；依赖伤害。</summary>
    public Func<LogicEngine, Trigger<UnitAttackTriggerView>, Trigger<AttackDamageTriggerView>, IDamageAction,
        Func<UnitCard, Ref<Entity>, bool>, Func<UnitCard, UnitCard, bool>, IAttackAction>? CreateAttack { get; init; }

    /// <summary>卡牌移动（<c>move</c>）工厂；返回宿主侧契约 <see cref="IMoveAction"/>（实现可来自插件程序集）。</summary>
    public Func<LogicEngine, IMoveActionServices, IMoveAction>? CreateMove { get; init; }

    /// <summary>加入战场（<c>join.battlefield</c>）工厂。</summary>
    public Func<LogicEngine, IJoinActionServices, IJoinBattlefieldAction>? CreateJoinBattlefield { get; init; }

    /// <summary>指令施放（<c>order.cast</c>）工厂。</summary>
    public Func<OrderFlow, IOrderAction>? CreateOrder { get; init; }

    /// <summary>本集合实际提供的工厂数量（单动作覆盖校验用）。</summary>
    internal int ImplementedCount
        => (CreateAddToHand is null ? 0 : 1)
           + (CreateDraw is null ? 0 : 1)
           + (CreateDamage is null ? 0 : 1)
           + (CreateDeath is null ? 0 : 1)
           + (CreateAttack is null ? 0 : 1)
           + (CreateMove is null ? 0 : 1)
           + (CreateJoinBattlefield is null ? 0 : 1)
           + (CreateOrder is null ? 0 : 1);

    /// <summary>该动作是否由本集合提供实现。</summary>
    internal bool HasImplementation(ActionId id)
        => id == ActionId.HandAdd ? CreateAddToHand is not null
            : id == ActionId.Draw ? CreateDraw is not null
            : id == ActionId.Damage ? CreateDamage is not null
            : id == ActionId.Death ? CreateDeath is not null
            : id == ActionId.Attack ? CreateAttack is not null
            : id == ActionId.Move ? CreateMove is not null
            : id == ActionId.JoinBattlefield ? CreateJoinBattlefield is not null
            : id == ActionId.Order ? CreateOrder is not null
            : false;

    /// <summary>以 <paramref name="overlay"/> 的非空工厂覆盖本集合的同名工厂（覆盖优先）。</summary>
    internal ActionImplementationSet Overlay(ActionImplementationSet overlay)
        => new()
        {
            CreateAddToHand = overlay.CreateAddToHand ?? CreateAddToHand,
            CreateDraw = overlay.CreateDraw ?? CreateDraw,
            CreateDamage = overlay.CreateDamage ?? CreateDamage,
            CreateDeath = overlay.CreateDeath ?? CreateDeath,
            CreateAttack = overlay.CreateAttack ?? CreateAttack,
            CreateMove = overlay.CreateMove ?? CreateMove,
            CreateJoinBattlefield = overlay.CreateJoinBattlefield ?? CreateJoinBattlefield,
            CreateOrder = overlay.CreateOrder ?? CreateOrder,
        };
}
