using Orc.Core;

namespace Orc.Game.Actions;

/// <summary>
/// 动作目录的**运行时查询面**（P5b）：按对局引擎取当前装配的动作实例，只暴露 <c>I*Action</c> 契约。
/// <para>
/// 目录在构造时把八个实例登记到引擎引用表（每个 <see cref="ActionId"/> 一个稳定键）；卡牌/管理器等
/// 宿主内部调用方经本类取同一实例，**不引用任何实现类型**（实现可整体迁到插件程序集）。
/// </para>
/// <para>
/// 独立构造（无动作目录）时的兜底：经 <see cref="ActionImplementationRegistry.Resolve"/> 的默认实现集
/// 按需创建一个最小实例并登记；不存在第二条实现路径。
/// </para>
/// </summary>
public static class ActionRuntime
{
    private const string HandAddKey = "orc.game.action.hand.add";
    private const string DrawKey = "orc.game.action.draw";
    private const string DamageKey = "orc.game.action.damage";
    private const string DeathKey = "orc.game.action.death";
    private const string AttackKey = "orc.game.action.attack";
    private const string MoveKey = "orc.game.action.move";
    private const string JoinBattlefieldKey = "orc.game.action.join.battlefield";
    private const string OrderKey = "orc.game.action.order.cast";

    /// <summary>按引擎解析「加入战场」动作（未装配＝<c>null</c>：独立构造的卡无入场动作可用）。</summary>
    public static IJoinBattlefieldAction? ResolveJoinBattlefield(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return Lookup<IJoinBattlefieldAction>(engine, JoinBattlefieldKey);
    }

    /// <summary>加到手牌动作（对局路径＝目录实例；独立构造＝按默认实现集创建并登记）。</summary>
    internal static IAddToHandAction ResolveAddToHand(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Lookup<IAddToHandAction>(engine, HandAddKey) is { } existing)
        {
            return existing;
        }

        var set = ActionImplementationRegistry.Resolve();
        var created = set.CreateAddToHand!(engine, new MatchActionServices(engine));
        Register(engine, HandAddKey, created);
        return created;
    }

    /// <summary>抽牌动作（对局路径＝目录实例；独立构造＝按默认实现集创建并登记）。</summary>
    internal static IDrawAction ResolveDraw(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Lookup<IDrawAction>(engine, DrawKey) is { } existing)
        {
            return existing;
        }

        var set = ActionImplementationRegistry.Resolve();
        var created = set.CreateDraw!(engine, new MatchActionServices(engine), ResolveAddToHand(engine));
        Register(engine, DrawKey, created);
        return created;
    }

    /// <summary>指令施放动作（对局路径＝目录实例；独立构造＝按默认实现集创建并登记）。</summary>
    internal static IOrderAction ResolveOrder(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Lookup<IOrderAction>(engine, OrderKey) is { } existing)
        {
            return existing;
        }

        var set = ActionImplementationRegistry.Resolve();
        var created = set.CreateOrder!(engine.OrderFlow);
        Register(engine, OrderKey, created);
        return created;
    }

    /// <summary>把目录实例登记到引擎引用表（同键替换：以最新实例为准）。</summary>
    internal static void Register(LogicEngine engine, ActionId id, object instance)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(instance);
        if (KeyOf(id) is { } key)
        {
            Register(engine, key, instance);
        }
    }

    private static void Register(LogicEngine engine, string key, object instance)
    {
        if (engine.References.TryResolve(key, out _))
        {
            engine.References.Unregister(key);
        }

        engine.References.Register(key, instance);
    }

    private static T? Lookup<T>(LogicEngine engine, string key)
        where T : class
        => engine.References.TryResolve<T>(key, out var found) ? found : null;

    private static string? KeyOf(ActionId id)
        => id == ActionId.HandAdd ? HandAddKey
            : id == ActionId.Draw ? DrawKey
            : id == ActionId.Damage ? DamageKey
            : id == ActionId.Death ? DeathKey
            : id == ActionId.Attack ? AttackKey
            : id == ActionId.Move ? MoveKey
            : id == ActionId.JoinBattlefield ? JoinBattlefieldKey
            : id == ActionId.Order ? OrderKey
            : null;
}
