using System.Reflection;
using Orc.Core;

namespace Orc.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// S-C8/S-C10 动态效果：由效果快照（预制体）**从文本重建**出携带完整逻辑的效果。
//   · 被动（主触发器 Passive）→ DynamicPassiveEffect（继承 PassiveEffect：保留框架生命周期清理），
//     并把动态主触发器与其它被动触发器一并挂载（owner=效果实例，卸载整批移除）。
//   · 主动（主触发器 Active）→ DynamicActiveEffect（纯动作型，无 hook 段），施放经 CastAsync。
//   实例化＝**执行装载计划**（BuildLoadPlan 的步骤序）：挂主触发器 → 注册事件 → 施加 moding；
//   交由既有装载链（Effect.ExecuteMount/ExecuteDispose）驱动生效与清理。
//   视图类型为泛型参数，故触发器经反射构造、注册/挂载/施放经泛型适配（调用侧无泛型耦合）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>动态效果实例化结果（S-C8；结构化——失败不抛）。</summary>
public sealed class DynamicEffectInstantiation
{
    private DynamicEffectInstantiation(bool success, Effect? effect, EffectLoadPlan? plan, string? errorCategory, string? error)
    {
        Success = success;
        Effect = effect;
        Plan = plan;
        ErrorCategory = errorCategory;
        Error = error;
    }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>实例化的动态效果（成功时非 null）。</summary>
    public Effect? Effect { get; }

    /// <summary>该效果对应的装载计划（成功时非 null）。</summary>
    public EffectLoadPlan? Plan { get; }

    /// <summary>失败分类。</summary>
    public string? ErrorCategory { get; }

    /// <summary>失败消息。</summary>
    public string? Error { get; }

    internal static DynamicEffectInstantiation Ok(Effect effect, EffectLoadPlan plan) => new(true, effect, plan, null, null);

    public static DynamicEffectInstantiation Fail(string category, string error) => new(false, null, null, category, error);
}

/// <summary>动态效果的构建状态（S-C8 内部）：主触发器 + 视图类型 + 其它触发器。</summary>
internal sealed class DynamicBuildState
{
    internal DynamicBuildState(Type mainViewType, object mainTrigger)
    {
        MainViewType = mainViewType;
        MainTrigger = mainTrigger;
    }

    internal Type MainViewType { get; }

    internal object MainTrigger { get; }

    internal List<(object Trigger, Type ViewType)> OtherTriggers { get; } = new();

    /// <summary>事件 id → handler（S5 注入用：把同一 handler 注册进宿主具名触发器）。</summary>
    internal Dictionary<string, Delegate> Handlers { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 动态效果构建器（S-C8 内部）：按装载计划序解析处理器、构造触发器、注册事件并施加 moding。
/// 一切失败结构化返回（不抛）——分类：view-type / handler-missing / evaluator-missing / signature /
/// compile / runtime / entry-missing / sandbox / timeout / moding-target。
/// </summary>
internal static class DynamicEffectBuilder
{
    internal static DynamicEffectInstantiation Build(
        LogicEngine engine, PrefabManager manager, EffectSnapshot snapshot, string name)
    {
        var prefab = snapshot.Root;

        var mainViewType = ResolveViewType(prefab.MainTrigger.ViewTypeName);
        if (mainViewType is null)
        {
            return DynamicEffectInstantiation.Fail(
                "view-type", $"主触发器视图类型 '{prefab.MainTrigger.ViewTypeName}' 无法解析。");
        }

        var isPassive = prefab.MainTrigger.Kind == TriggerKind.Passive;

        // E-D1：OneShot 只对"有可挂载业务主触发器"的效果成立（纯注入型主触发器从不 Invoke，无法判定一次触发）。
        if (prefab.OneShot && isPassive && prefab.MountedHooks.Count == 0)
        {
            return DynamicEffectInstantiation.Fail(
                "one-shot-no-trigger",
                $"效果 '{name}' 声明了 OneShot，但主触发器无挂载 hooks（纯注入型无法判定\"一次触发\"）；请改用 Manual。");
        }

        Effect effect = isPassive
            ? new DynamicPassiveEffect(name, snapshot, mainViewType, prefab.OneShot, prefab.DisposingTrigger.Hooks)
            : new DynamicActiveEffect(name, snapshot, mainViewType, prefab.OneShot, prefab.DisposingTrigger.Hooks);

        object mainTrigger;
        try
        {
            mainTrigger = TriggerReflection.Create(
                mainViewType, prefab.MainTrigger, effect);
        }
        catch (Exception ex)
        {
            return DynamicEffectInstantiation.Fail("view-type", $"主触发器构造失败：{ex.Message}");
        }

        var state = new DynamicBuildState(mainViewType, mainTrigger);
        var eventsById = new Dictionary<string, TriggerRegistration>(StringComparer.Ordinal);
        var failure = RegisterEvents(engine, manager, mainTrigger, mainViewType, prefab.MainTrigger, eventsById, state);
        if (failure is not null)
        {
            return failure;
        }

        foreach (var otherPrefab in prefab.OtherTriggers)
        {
            var otherViewType = ResolveViewType(otherPrefab.ViewTypeName);
            if (otherViewType is null)
            {
                return DynamicEffectInstantiation.Fail(
                    "view-type", $"触发器 '{otherPrefab.Id}' 视图类型 '{otherPrefab.ViewTypeName}' 无法解析。");
            }

            object otherTrigger;
            try
            {
                otherTrigger = TriggerReflection.Create(otherViewType, otherPrefab, effect);
            }
            catch (Exception ex)
            {
                return DynamicEffectInstantiation.Fail("view-type", $"触发器 '{otherPrefab.Id}' 构造失败：{ex.Message}");
            }

            var otherEvents = new Dictionary<string, TriggerRegistration>(StringComparer.Ordinal);
            failure = RegisterEvents(engine, manager, otherTrigger, otherViewType, otherPrefab, otherEvents, state);
            if (failure is not null)
            {
                return failure;
            }

            state.OtherTriggers.Add((otherTrigger, otherViewType));
        }

        foreach (var wrapper in prefab.Wrappers)
        {
            if (!eventsById.TryGetValue(wrapper.TargetEventId, out var target))
            {
                return DynamicEffectInstantiation.Fail(
                    "wrapper-target", $"包装器目标事件 '{wrapper.TargetEventId}' 不在主触发器中。");
            }

            var resolution = manager.ResolveHandler(wrapper.Handler, mainViewType);
            if (!resolution.Success)
            {
                return DynamicEffectInstantiation.Fail(resolution.ErrorCategory!, resolution.Error!);
            }

            try
            {
                TriggerReflection.RegisterWrapper(
                    mainTrigger, mainViewType, target, wrapper.Phase, wrapper.SeamName, resolution.Handler!);
            }
            catch (ArgumentException ex)
            {
                return DynamicEffectInstantiation.Fail("wrapper-signature", ex.Message);
            }
        }

        // E-D1：disposing 触发器的事件（作者销毁逻辑；普通注册，执行在默认 OnUnmount 之前）。
        foreach (var ev in prefab.DisposingTrigger.Events)
        {
            var resolution = manager.ResolveHandler(ev, typeof(CardEventView));
            if (!resolution.Success)
            {
                return DynamicEffectInstantiation.Fail(resolution.ErrorCategory!, resolution.Error!);
            }

            effect.DisposingTriggerInternal.Register(
                ev.Id,
                (Func<CardEventView, Context, CancellationToken, Task>)resolution.Handler!);
        }

        // E-D1：一次性效果——向业务主触发器保留槽注册"调用销毁"（恒在全部普通事件之后执行）。
        if (prefab.OneShot)
        {
            TriggerReflection.RegisterDisposal(
                mainTrigger,
                mainViewType,
                "一次性销毁",
                () =>
                {
                    if (effect.HostOrNull is { } host)
                    {
                        effect.ExecuteDispose(host);
                    }
                });
        }

        Attach(effect, state);
        return DynamicEffectInstantiation.Ok(effect, snapshot.BuildLoadPlan());
    }

    private static DynamicEffectInstantiation? RegisterEvents(
        LogicEngine engine,
        PrefabManager manager,
        object trigger,
        Type viewType,
        TriggerPrefab prefab,
        Dictionary<string, TriggerRegistration> eventsById,
        DynamicBuildState state)
    {
        foreach (var ev in prefab.Events)
        {
            var resolution = manager.ResolveHandler(ev, viewType);
            if (!resolution.Success)
            {
                return DynamicEffectInstantiation.Fail(resolution.ErrorCategory!, resolution.Error!);
            }

            var registration = string.Equals(ev.Band, "disposing", StringComparison.Ordinal)
                ? TriggerReflection.RegisterDisposalHandler(
                    trigger, viewType, ev.Id, resolution.Handler!, ev.Downstream)
                : TriggerReflection.Register(
                    trigger, viewType, ev.Id, resolution.Handler!, ev.Downstream, ev.Seams);
            eventsById[ev.Id] = registration;
            state.Handlers[ev.Id] = resolution.Handler!; // S5：注入用的 handler 来源
        }

        return null;
    }

    private static void Attach(Effect effect, DynamicBuildState state)
    {
        switch (effect)
        {
            case DynamicPassiveEffect passive:
                passive.AttachState(state);
                break;
            case DynamicActiveEffect active:
                active.AttachState(state);
                break;
        }
    }

    private static Type? ResolveViewType(string typeName)
    {
        var type = Type.GetType(typeName);
        if (type is null || !type.IsClass)
        {
            return null;
        }

        return type;
    }
}

/// <summary>
/// 反射适配（S-C8 内部）：触发器为泛型 <c>Trigger&lt;TView&gt;</c>，动态装配需按运行期视图类型分派。
/// 每个方法体在泛型实例化后即为强类型调用（无 DynamicInvoke，无装箱调用开销）。
/// </summary>
internal static class TriggerReflection
{
    private static readonly MethodInfo RegisterMethod = Get(nameof(RegisterCore));
    private static readonly MethodInfo WrapperMethod = Get(nameof(WrapperCore));
    private static readonly MethodInfo MountMethod = Get(nameof(MountCore));
    private static readonly MethodInfo InvokeMethod = Get(nameof(InvokeCore));

    private static MethodInfo Get(string name) => typeof(TriggerReflection)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == name);

    internal static object Create(Type viewType, TriggerPrefab prefab, object owner)
    {
        var triggerType = typeof(Trigger<>).MakeGenericType(viewType);
        var ctor = triggerType.GetConstructors().Single();
        return ctor.Invoke(new object?[]
        {
            prefab.StableKey,
            prefab.Kind,
            null,
            null,
            prefab.Hooks.ToArray(),
            UpdatePriorities.Normal,
            owner,
            prefab.StableKey,
            owner, // R9：注册者＝效果实例（反射 Invoke 需补齐全部形参——可选参数不被反射识别）
        });
    }

    internal static TriggerRegistration Register(
        object trigger, Type viewType, string name, Delegate handler,
        IReadOnlyList<string> downstream, IReadOnlyList<string> seams, object? registrant = null)
        => (TriggerRegistration)RegisterMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { trigger, name, handler, downstream, seams, registrant })!;

    /// <summary>默认区段注册（S5 注入用：无 band、可带优先级）。</summary>
    internal static TriggerRegistration RegisterDefaultBand(
        object trigger, Type viewType, string name, Delegate handler, int priority, object? registrant = null)
        => (TriggerRegistration)RegisterBandMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { trigger, name, handler, priority, registrant })!;

    /// <summary>按 band 注册（S11：预制体注入的 band 名落地；band 成员须属目标触发器声明的方案）。</summary>
    internal static TriggerRegistration RegisterBand(
        object trigger, Type viewType, string name, Delegate handler, Enum band, int priority,
        object? registrant = null)
        => (TriggerRegistration)RegisterWithBandMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { trigger, name, handler, band, priority, registrant })!;

    /// <summary>目标触发器声明的 band 方案（S11；null＝默认方案）。</summary>
    internal static Type? GetBandType(object trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.GetType().GetProperty(nameof(Trigger<object>.BandType))?.GetValue(trigger) as Type;
    }

    /// <summary>保留槽注册（E-D1：销毁 handler；与视图泛型无关，动作型委托即可）。</summary>
    internal static TriggerRegistration RegisterDisposal(
        object trigger, Type viewType, string name, Action action, int priority = 0)
        => (TriggerRegistration)RegisterDisposalMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object[] { trigger, name, action, priority })!;

    /// <summary>保留槽注册（E-D1：带视图的完整 handler；供预制体 band=disposing 的事件落地）。</summary>
    internal static TriggerRegistration RegisterDisposalHandler(
        object trigger, Type viewType, string name, Delegate handler, IEnumerable<string>? downstream = null)
        => (TriggerRegistration)RegisterDisposalHandlerMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { trigger, name, handler, downstream })!;

    /// <summary>撤销注册（S5 注入撤销用；目标触发器 <c>Unregister</c> 面）。</summary>
    /// <remarks>
    /// E1-36 修正：原实现从**开放式泛型** <c>typeof(Trigger&lt;&gt;)</c> 取 <c>MethodInfo</c>——该 <c>MethodInfo</c>
    /// 的 <c>ContainsGenericParameters</c> 为 true，<c>Invoke</c> 必抛
    /// 「Late bound operations cannot be performed on types or methods for which ContainsGenericParameters is true」；
    /// 且即便封闭，取到的方法也属 <c>Trigger&lt;object&gt;</c>、无法作用于 <c>Trigger&lt;X&gt;</c> 实例。
    /// 现改为**按实例运行时类型**反射（闭型 ⇒ Invoke 合法；按类型缓存）。
    /// </remarks>
    /// <exception cref="ArgumentNullException">trigger / registration 为 null。</exception>
    internal static void Unregister(object trigger, TriggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(registration);

        var method = UnregisterMethods.GetOrAdd(
            trigger.GetType(),
            type => type.GetMethod(nameof(Trigger<object>.Unregister), new[] { typeof(TriggerRegistration) })!);
        method.Invoke(trigger, new object[] { registration });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo> UnregisterMethods = new();

    private static readonly MethodInfo RegisterBandMethod = Get(nameof(RegisterBandCore));

    private static readonly MethodInfo RegisterWithBandMethod = Get(nameof(RegisterWithBandCore));

    private static readonly MethodInfo RegisterDisposalMethod = Get(nameof(RegisterDisposalCore));

    private static readonly MethodInfo RegisterDisposalHandlerMethod = Get(nameof(RegisterDisposalHandlerCore));

    private static TriggerRegistration RegisterBandCore<TView>(
        object trigger, string name, Delegate handler, int priority, object? registrant)
        where TView : class
        => ((Trigger<TView>)trigger).Register(
            name, (Func<TView, Context, CancellationToken, Task>)handler, priority, registrant: registrant);

    private static TriggerRegistration RegisterWithBandCore<TView>(
        object trigger, string name, Delegate handler, Enum band, int priority, object? registrant)
        where TView : class
        => ((Trigger<TView>)trigger).Register(
            name, (Func<TView, Context, CancellationToken, Task>)handler, band, priority, registrant: registrant);

    private static TriggerRegistration RegisterDisposalHandlerCore<TView>(
        object trigger, string name, Delegate handler, IEnumerable<string>? downstream) where TView : class
        => ((Trigger<TView>)trigger).RegisterDisposal(
            name,
            (Func<TView, Context, CancellationToken, Task>)handler,
            0,
            downstream);

    private static TriggerRegistration RegisterDisposalCore<TView>(
        object trigger, string name, Action action, int priority) where TView : class
        => ((Trigger<TView>)trigger).RegisterDisposal(
            name,
            (view, ctx, ct) =>
            {
                action();
                return Task.CompletedTask;
            },
            priority);

    internal static void RegisterWrapper(
        object trigger, Type viewType, TriggerRegistration target,
        string phase, string? seam, Delegate wrapper)
        => WrapperMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { trigger, target, phase, seam, wrapper });

    internal static void Mount(Bus bus, Type viewType, object trigger)
        => MountMethod.MakeGenericMethod(viewType).Invoke(null, new object[] { bus, trigger });

    internal static Task<EventStream> Invoke(
        LogicEngine engine, Type viewType, object trigger, IDictionary<string, object?>? data, CancellationToken ct)
        => (Task<EventStream>)InvokeMethod.MakeGenericMethod(viewType)
            .Invoke(null, new object?[] { engine, trigger, data, ct })!;

    private static TriggerRegistration RegisterCore<TView>(
        object trigger, string name, Delegate handler,
        IReadOnlyList<string> downstream, IReadOnlyList<string> seams, object? registrant) where TView : class
        => ((Trigger<TView>)trigger).Register(
            name, (Func<TView, Context, CancellationToken, Task>)handler, 0, downstream, seams, registrant: registrant);

    private static void WrapperCore<TView>(
        object trigger, TriggerRegistration target, string phase, string? seam, Delegate wrapper)
        where TView : class
    {
        var typed = (Func<TView, Context, CancellationToken, Task>)wrapper;
        var typedTrigger = (Trigger<TView>)trigger;

        switch ((phase ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "before":
                if (seam is null)
                {
                    typedTrigger.RegisterBeforeWrapper(target, typed);
                }
                else
                {
                    typedTrigger.RegisterBeforeWrapper(target, seam, typed);
                }

                break;

            case "after":
                typedTrigger.RegisterAfterWrapper(target, typed);
                break;

            case "replace":
                typedTrigger.RegisterReplaceWrapper(target, typed);
                break;

            default:
                throw new ArgumentException(
                    $"包装器 phase '{phase}' 非法（须为 before／after／replace）。", nameof(phase));
        }
    }

    private static void MountCore<TView>(Bus bus, object trigger) where TView : class
        => bus.Mount((Trigger<TView>)trigger);

    private static Task<EventStream> InvokeCore<TView>(
        LogicEngine engine, object trigger, IDictionary<string, object?>? data, CancellationToken ct) where TView : class
        => ((Trigger<TView>)trigger).InvokeAsync(engine, data, ct);
}

/// <summary>
/// 动态被动效果（S-C8）：由效果快照重建；继承 <see cref="PassiveEffect"/> 以保留框架生命周期清理
/// （effect.removed / card.destroyed → 卸载），并把动态触发器（主 + 其它）一并挂载（owner=效果实例）。
/// </summary>
public sealed class DynamicPassiveEffect : PassiveEffect, ISerializableEffect
{
    private readonly EffectSnapshot _snapshot;
    private readonly Type _viewType;
    private DynamicBuildState? _state;

    internal DynamicPassiveEffect(
        string name,
        EffectSnapshot snapshot,
        Type viewType,
        bool oneShot,
        IEnumerable<string> disposalHooks)
        : base(name, oneShot, disposalHooks)
    {
        _snapshot = snapshot;
        _viewType = viewType;
    }

    /// <summary>业务主触发器（动态触发器；纯注入型仍存在但不挂载——OneShot 由装载期校验拒绝）。</summary>
    internal override ITriggerMetadata? MainTrigger => _state?.MainTrigger as ITriggerMetadata;

    /// <summary>本动态效果对应的效果快照（可再次序列化——往返）。</summary>
    public EffectSnapshot Snapshot => _snapshot;

    internal void AttachState(DynamicBuildState state) => _state = state;

    /// <summary>取本效果内某事件的 handler（S5 注入用；未命中＝false）。</summary>
    public bool TryGetEventHandler(string eventId, out Delegate? handler)
    {
        handler = null;
        return _state is not null
            && !string.IsNullOrWhiteSpace(eventId)
            && _state.Handlers.TryGetValue(eventId, out handler);
    }

    internal override void MountMainTrigger(Bus bus)
    {
        if (_state is null)
        {
            return;
        }

        // E1-36（纯注入型效果支持）：主触发器**无 hooks** 时无总线订阅可挂——此时它是"注入载体"，
        // 不参与挂载（挂上去会被总线拒绝）；注入由装载链（effect.injects）另行施加。
        if (_snapshot.Root.MountedHooks.Count == 0)
        {
            return;
        }

        TriggerReflection.Mount(bus, _state.MainViewType, _state.MainTrigger);
        foreach (var (trigger, viewType) in _state.OtherTriggers)
        {
            TriggerReflection.Mount(bus, viewType, trigger);
        }
    }
}

/// <summary>
/// 动态主动效果（S-C10；纯动作型）：无 hook 段、不参与装载/卸载；施放经 <see cref="CastAsync"/>
/// 调用动态主触发器。其"下游边"可由其它被动触发器 declare 指向（Q5 补充语义）。
/// </summary>
public sealed class DynamicActiveEffect : Effect, ISerializableEffect
{
    private readonly EffectSnapshot _snapshot;
    private readonly Type _viewType;
    private DynamicBuildState? _state;

    internal DynamicActiveEffect(
        string name,
        EffectSnapshot snapshot,
        Type viewType,
        bool oneShot,
        IEnumerable<string> disposalHooks)
        : base(name, TriggerKind.Active, oneShot, disposalHooks)
    {
        _snapshot = snapshot;
        _viewType = viewType;
    }

    /// <summary>本动态效果对应的效果快照。</summary>
    public EffectSnapshot Snapshot => _snapshot;

    internal void AttachState(DynamicBuildState state) => _state = state;

    /// <summary>施放入口（调用动态主触发器；施放事件链按注册序执行）。</summary>
    /// <exception cref="InvalidOperationException">尚未构建（未实例化）。</exception>
    public Task<EventStream> CastAsync(
        LogicEngine engine, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (_state is null)
        {
            throw new InvalidOperationException($"动态效果 '{Name}' 尚未实例化（无构建状态）。");
        }

        return TriggerReflection.Invoke(engine, _state.MainViewType, _state.MainTrigger, data, ct);
    }

}

/// <summary>动态效果工厂（S-C8；入口）：由效果快照在引擎上实例化动态效果。</summary>
public static class DynamicEffectFactory
{
    /// <summary>
    /// 实例化动态效果（解析视图类型 → 构造触发器 → 解析处理器 → 注册事件 → 施加 moding）。
    /// 失败结构化返回（不抛，除参数为 null）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine 或 snapshot 为 null。</exception>
    public static DynamicEffectInstantiation Instantiate(
        LogicEngine engine, EffectSnapshot snapshot, string? name = null, PrefabManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(snapshot);

        var effectiveManager = manager ?? engine.Prefabs;
        var effectiveName = string.IsNullOrWhiteSpace(name) ? snapshot.Root.Id : name!;
        return DynamicEffectBuilder.Build(engine, effectiveManager, snapshot, effectiveName);
    }

    /// <summary>按预制体 id 从管理器实例化（未注册＝结构化失败）。</summary>
    /// <exception cref="ArgumentNullException">engine 或 prefabId 为 null。</exception>
    public static DynamicEffectInstantiation InstantiateRegistered(
        LogicEngine engine, string prefabId, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prefabId);

        return engine.Prefabs.TryGetPrefab(prefabId, out var snapshot)
            ? Instantiate(engine, snapshot, name)
            : DynamicEffectInstantiation.Fail("prefab-missing", $"未注册效果预制体 '{prefabId}'。");
    }
}
