using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 效果销毁状态（E-D1）：与装载标记 <see cref="Effect.IsMounted"/> 独立——
/// 主动效果不装载（恒 <see cref="NotMounted"/>），销毁后统一进入 <see cref="Disposed"/>。
/// </summary>
public enum DisposalState
{
    /// <summary>未装载（被动效果装载失败/回滚；主动效果初始态）。</summary>
    NotMounted = 0,

    /// <summary>已装载（disposing 触发器已挂载、主触发器（若有）已挂载）。</summary>
    Mounted = 1,

    /// <summary>销毁中（重入保护；主触发器/注入已停用、销毁逻辑执行中）。</summary>
    Disposing = 2,

    /// <summary>已销毁（容器已移除、effect.removed 已发射；实例不可复用为运行态）。</summary>
    Disposed = 3,
}

/// <summary>销毁调用来源（可观测性分类；E-D1）。</summary>
public enum DisposalSource
{
    /// <summary>显式入口（RemoveEffect / 一次性施放 / 受控卸载 / OneShot 保留槽）。</summary>
    Early = 0,

    /// <summary>disposing 触发器的兜底 hook 到达（宿主销毁等）。</summary>
    Fallback = 1,

    /// <summary>effect.removed 更新驱动的幂等兜底（旧路径/外部直接发射）。</summary>
    Signal = 2,
}

/// <summary>
/// 效果基类（S4；逻辑组件）：一个效果 ＝ 一个业务主触发器（若有）＋一个**默认必选**的 disposing 触发器
/// ＋作者覆写的装载钩子。
/// 行为契约（框架装载链保证）：
/// ①装载（加载时点/放置驱动/Add 即时——三入口幂等、统一装载链）后效果生效（disposing 触发器挂载；被动：主触发器挂载至总线 ＋ OnMount 注入完成）；
/// ②销毁经**唯一入口**——disposing 触发器：进入即停用（卸下该效果全部总线挂载＋撤销注入）→ 执行销毁事件链（作者额外副作用＋默认 OnUnmount）
/// → 最终 teardown 层（核心句柄撤销，不可绕过）→ 容器移除 → 恰发一次 effect.removed；
/// ③顺序：单位初始化逻辑先、效果注入后；④装载＝【挂 disposing → 挂主触发器 → OnMount】。
/// 主动效果（指令）**不参与装载/卸载钩子**（不挂主触发器、不执行 OnMount/OnUnmount；列表进出），但其 disposing 触发器照常挂载以接收兜底。
/// 兜底时机＝disposing 触发器的 hooks（与主触发器同构的更新名字符串）；默认强制包含 <see cref="Updates.CardDestroyed"/> 且不可移除，
/// <see cref="Updates.EffectRemoved"/> 被拒绝（显式销毁入口的完成通知，不能作为兜底自指）。
/// 幂等边界（框架保证）：未装载不触 OnUnmount（重复移除幂等）；已装载不重复 OnMount（重复放置不重复装载）；
/// 销毁后重复调用空转；同一效果实例可多次成对装载/销毁（移除后重新 Add 复装——状态复位为 Mounted）。
/// </summary>
public abstract class Effect
{
    private readonly List<Action> _rollbacks = new();
    private readonly List<EffectInjection> _injections = new();
    private readonly List<Action> _teardownRegistrations = new();
    private readonly Trigger<CardEventView> _disposingTrigger;
    private Card? _host;
    private bool _loading;
    private bool _invokingFromDispose;
    private DisposalState _disposalState = DisposalState.NotMounted;

    internal Effect(
        string name,
        TriggerKind kind,
        bool oneShot = false,
        IEnumerable<string>? disposalHooks = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("效果名不能为空或纯空白。", nameof(name));
        }

        Name = name;
        Kind = kind;
        OneShot = oneShot;
        DisposalHooks = NormalizeDisposalHooks(disposalHooks);

        _disposingTrigger = new Trigger<CardEventView>(
            $"{Name}/dispose",
            TriggerKind.Passive,
            events: new[]
            {
                // 守卫恒最先（priority 0、注册序 0）：载荷不指向本效果/宿主时静默停层。
                new TriggerEvent<CardEventView>("disposing 守卫", GuardDisposalAsync),
                // 兜底驱动（priority 0、注册序 1）：hook 到达时发起完整销毁；显式销毁入口调用时跳过（防递归）。
                new TriggerEvent<CardEventView>("disposing 兜底驱动", FallbackDriveAsync),
                // 默认销毁事件：作者既有 OnUnmount 逻辑（动态效果的作者事件以普通注册插入在驱动与默认事件之间）。
                new TriggerEvent<CardEventView>("效果销毁", DefaultDisposeAsync, priority: 1),
            },
            hooks: DisposalHooks,
            owner: this,
            stableKey: $"{Name}/dispose");
    }

    /// <summary>效果名（用于留痕/排查；不承担全局唯一键职责）。</summary>
    public string Name { get; }

    /// <summary>效果种类（触发触发器种类）：被动＝装载语境（挂载/钩子/更新接线）；主动＝施放语境（不装载）。</summary>
    public TriggerKind Kind { get; }

    /// <summary>是否处于已装载状态（被动：主触发器已挂载且 OnMount 已完成；主动恒为 false）。</summary>
    public bool IsMounted { get; private set; }

    /// <summary>销毁状态（公开只读面；E-D1）。</summary>
    public DisposalState DisposalState => _disposalState;

    /// <summary>是否已销毁（终态；重复销毁调用空转）。</summary>
    public bool IsDisposed => _disposalState == DisposalState.Disposed;

    /// <summary>
    /// 是否为一次性效果（触发一次后自动销毁）：仅在有业务主触发器时成立——
    /// 动态/预制体效果由框架在装载成功后向主触发器保留槽注册销毁调用；主动效果在构造期向施放触发器保留槽注册；
    /// 无业务主触发器的 C# 被动效果在装配期被明确拒绝（无"一次触发"的判定点）。
    /// </summary>
    public bool OneShot { get; }

    /// <summary>实际生效的兜底时机（hooks；恒含 <see cref="Updates.CardDestroyed"/>）。</summary>
    public IReadOnlyList<string> DisposalHooks { get; }

    /// <summary>宿主卡牌（添加后可用；销毁完成后引用被清除、访问抛 <see cref="InvalidOperationException"/>）。</summary>
    /// <exception cref="InvalidOperationException">尚未添加宿主或引用已被清理。</exception>
    public Card Host => _host ?? throw new InvalidOperationException(
        $"效果 '{Name}' 当前无宿主卡牌（尚未添加或引用已被清理）。");

    /// <summary>宿主卡牌引用（可为 null；内部防御用）。</summary>
    internal Card? HostOrNull => _host;

    /// <summary>
    /// 业务主触发器元数据（S-C3 加性只读面；审查链用）：被动效果＝业务触发器（无业务触发器时为 null——纯注入型）、
    /// 主动效果＝施放触发器；基类无主触发器＝null。
    /// </summary>
    internal virtual ITriggerMetadata? MainTrigger => null;

    /// <summary>disposing 触发器（销毁逻辑承载；与主触发器并列、默认必选）。</summary>
    protected Trigger<CardEventView> DisposingTrigger => _disposingTrigger;

    /// <summary>disposing 触发器（装配期内部访问面；动态效果注册销毁事件用）。</summary>
    internal Trigger<CardEventView> DisposingTriggerInternal => _disposingTrigger;

    /// <summary>disposing 触发器的元数据视角（审查链取节点用）。</summary>
    internal ITriggerMetadata? DisposalTriggerMetadata => _disposingTrigger;

    /// <summary>是否已登记宿主（未被清理）。</summary>
    internal bool HasHost => _host is not null;

    /// <summary>装载钩子（作者覆写；被动效果在「主触发器已挂载」后执行——注入逻辑写在此处，经 <see cref="Inject{TView}"/> 登记）。</summary>
    protected virtual void OnMount()
    {
    }

    /// <summary>
    /// 默认销毁事件（disposing 触发器内建；作者可覆写以替换既有清理钩子语义）。
    /// 默认＝保留既有 <see cref="OnUnmount"/> 行为：专属清理写在此处；核心句柄撤销由框架最终 teardown 层保证（不依赖本钩子）。
    /// </summary>
    protected virtual void OnUnmount()
    {
    }

    /// <summary>
    /// 注入辅助（框架装载登记；仅可在 OnMount 装载语境中调用）：
    /// 把 handler 注册进目标触发器（如流程触发器）的具名 band，并把撤销动作登记到本效果；
    /// 销毁时框架自动撤销全部登记项（作者不在 OnUnmount 手写撤销）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null（其余校验由目标触发器注册面执行）。</exception>
    /// <exception cref="InvalidOperationException">不在装载语境（OnMount）中调用。</exception>
    protected TriggerRegistration Inject<TView>(
        Trigger<TView> target,
        string name,
        Enum band,
        Func<TView, Context, CancellationToken, Task> handler,
        int priority = 0)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_loading)
        {
            throw new InvalidOperationException(
                $"Inject 仅可在装载语境（OnMount）中调用；效果 '{Name}' 当前不在装载语境。");
        }

        var registration = target.Register(name, handler, band, priority);
        var record = new EffectInjection(target, name, band, priority, registration);
        _injections.Add(record);
        _rollbacks.Add(() =>
        {
            target.Unregister(registration);
            _injections.Remove(record); // S-C2：注入读面随撤销同步收敛
        });
        return registration;
    }

    /// <summary>视图类型是否声明了可读 <c>Host</c> 数据面（决定是否需要包一层宿主注入；带缓存）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> HostPropertyCache = new();

    private static bool DeclaresHostProperty(Type viewType) =>
        HostPropertyCache.GetOrAdd(viewType, type => type.GetProperty("Host") is { CanRead: true });

    /// <summary>按视图类型包一层「调用前注入宿主到数据面」（反射泛型适配——同 <c>TriggerReflection</c> 手法）。</summary>
    private static Delegate WrapWithHost(Delegate handler, Type viewType, object host) =>
        (Delegate)WrapWithHostMethod.MakeGenericMethod(viewType).Invoke(null, new[] { handler, host })!;

    private static readonly System.Reflection.MethodInfo WrapWithHostMethod = typeof(Effect)
        .GetMethod(nameof(WrapWithHostCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static Func<TView, Context, CancellationToken, Task> WrapWithHostCore<TView>(Delegate handler, Effect effect)
        where TView : class
    {
        var inner = (Func<TView, Context, CancellationToken, Task>)handler;
        return (view, ctx, ct) =>
        {
            // 宿主注入走**数据面**（与内核「所有者是带宿主的效果」时的注入同手法）：
            // 视图是框架生成的**代理**，其访问器被烘焙成权限闸门（[Read] 属性禁止写入——经 PropertyInfo.SetValue
            // 会抛 PermissionDeniedException、并被反射包成 TargetInvocationException），故**不能**写属性 setter；
            // 而 getter 直读 `__orc_data`，与该数据面同一载体（Context 构造时已拷贝调用方字典，不污染载荷）。
            // 「调用时」取宿主（时机无关；未绑定＝null ⇒ 与键缺失同效，可选面回退 default、op 自然 no-op）。
            ctx.Data["Host"] = effect.HostOrNull;
            return inner(view, ctx, ct);
        };
    }

    /// <summary>
    /// 框架侧注入（S5；装载链调用）：把 handler 注册进目标触发器的**默认区段**并把撤销动作登记到本效果
    /// （随销毁自动撤销）。与 <see cref="Inject{TView}"/> 的区别：不经"装载语境"校验、目标以 object ＋ 视图类型给出
    /// （供数据体/预制体路径的 <c>injects</c> 声明落地）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target / viewType / handler 为 null。</exception>
    public TriggerRegistration InjectByFramework(
        object target, Type viewType, string name, Delegate handler, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(viewType);
        ArgumentNullException.ThrowIfNull(handler);

        // E1-36/E1-37：inject 型处理器的**宿主注入**——注入进的是**共享**触发器（所有者＝装载方而非本效果），
        // 故内核「所有者是带宿主的效果时注入视图数据」对它不生效；此处在调用前把本效果的宿主注入**数据面**
        // （与内核同手法；视图未声明 Host 数据面则原样注册）。这样 inject 型 csx handler 也能拿到 "self"（施动卡）。
        if (DeclaresHostProperty(viewType))
        {
            // 传**本效果**而非宿主快照：宿主在**调用时**解析（与装载/绑定时机无关）。
            handler = WrapWithHost(handler, viewType, this);
        }

        var registration = TriggerReflection.RegisterDefaultBand(target, viewType, name, handler, priority);
        var record = new EffectInjection(target, name, Orc.Core.DefaultBands.Default, priority, registration);
        _injections.Add(record);
        _rollbacks.Add(() =>
        {
            TriggerReflection.Unregister(target, registration);
            _injections.Remove(record); // S-C2：注入读面随撤销同步收敛
        });
        return registration;
    }

    /// <summary>
    /// 本效果已登记的注入项（S-C2 加性只读面；审查链用）：装载语境中经 <see cref="Inject{TView}"/> 登记，
    /// 销毁/回滚时框架撤销并同步移除；顺序＝登记序快照。空＝无注入。
    /// </summary>
    public IReadOnlyList<EffectInjection> Injections => _injections;

    /// <summary>
    /// 登记最终 teardown 动作（原 `AddUnmountCleanup`；E-D3 破坏性改名）：
    /// 宿主机制扩展面——如游戏层「效果修饰器按来源撤销／光环声明注销」托管。
    /// 销毁终态（disposing 事件链之后、容器移除之前）由框架执行（后进先出；各动作恰执行一次；执行后清空）。
    /// 这是**核心句柄撤销**的承载面：不依赖 disposing 作者逻辑是否完整（双层责任）。
    /// 登记约定＝装载完成后进行（装载管线「装载完成动作」在每次装载成功时自动重建；回滚不触发、并清除登记残留）。
    /// 动作异常＝隔离记录（不阻断销毁链其余步骤）；动作不得引入新的跨调用可变状态。
    /// </summary>
    /// <exception cref="ArgumentNullException">cleanup 为 null。</exception>
    public void AddTeardownRegistration(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _teardownRegistrations.Add(cleanup);
    }

    // ---------- 框架装载/销毁链原语（由 CardLoadout 调用；对作者不可见） ----------

    /// <summary>挂载业务主触发器（被动子类重写；主动无操作）。</summary>
    internal virtual void MountMainTrigger(Bus bus)
    {
    }

    /// <summary>挂载 disposing 触发器（幂等；所有效果统一——含主动效果）。</summary>
    internal void MountDisposal(Bus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (_disposalState == DisposalState.Disposing)
        {
            return;
        }

        if (_disposalState == DisposalState.Disposed)
        {
            _disposalState = DisposalState.NotMounted; // 复装＝新生命周期（主动效果装载态仍由 IsMounted 表达）
        }

        if (_disposingTrigger.MountedBus is null)
        {
            bus.Mount(_disposingTrigger);
        }
    }

    /// <summary>装载链：挂 disposing → 挂业务主触发器 → 执行 OnMount。异常不拦截（由调用方记录并回滚）。</summary>
    internal void ExecuteMount(Bus bus)
    {
        MountDisposal(bus);
        MountMainTrigger(bus);
        IsMounted = true;
        _disposalState = DisposalState.Mounted;
        _loading = true;
        try
        {
            OnMount();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 装载失败回滚：撤销已登记注入 → 卸载该效果全部总线挂载 → 复位装载状态 → 清除 teardown 登记残留
    /// （不触发、仅清除——「回滚不留登记」；该效果视为未生效）。**不执行 disposing**、不发射 effect.removed。
    /// </summary>
    internal void RollbackMount()
    {
        RollbackInjections();
        if (_host?.Engine is { } engine)
        {
            engine.Bus.UnmountOwner(this);
        }

        IsMounted = false;
        _disposalState = DisposalState.NotMounted;
        _teardownRegistrations.Clear();
    }

    /// <summary>
    /// 销毁（唯一入口；E-D1）：幂等/重入空转；进入即停用（卸下该效果全部总线挂载＋撤销注入）→ 执行 disposing 事件链
    /// → 最终 teardown 层（核心句柄撤销）→ 容器移除 → 恰发一次 effect.removed；异常仍完成终态。
    /// </summary>
    /// <returns>true＝本次执行了销毁；false＝重复/重入调用（空转）。</returns>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    internal bool ExecuteDispose(
        Card card,
        bool emitRemoved = true,
        DisposalSource source = DisposalSource.Early)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (_disposalState is DisposalState.Disposing or DisposalState.Disposed)
        {
            WriteDisposalLog(
                card.Engine,
                "dispose:noop",
                $"效果 '{Name}' 重复销毁调用（空转：已处于 {_disposalState}）。",
                LogLevel.Debug);
            return false;
        }

        // 被动未装载（装载失败/回滚后的残留）：仅容器面移除（不执行 disposing、无运行态清理动作）。
        if (Kind == TriggerKind.Passive && !IsMounted)
        {
            _disposalState = DisposalState.Disposed;
            card.RemoveEffectCore(this);
            ClearHost();
            WriteDisposalLog(
                card.Engine,
                "dispose:detach",
                $"效果 '{Name}' 已从容器移除（未装载，无运行态销毁）。",
                LogLevel.Debug,
                keywords: new[] { "detach" });
            if (emitRemoved)
            {
                EmitRemoved(card);
            }

            return true;
        }

        _disposalState = DisposalState.Disposing;
        WriteDisposalLog(
            card.Engine,
            source switch
            {
                DisposalSource.Fallback => "dispose:fallback",
                DisposalSource.Signal => "dispose:signal",
                _ => "dispose:early",
            },
            $"效果 '{Name}' 开始销毁（来源：{source}）。",
            LogLevel.Info);

        try
        {
            // 进入即停用：卸下该效果全部总线挂载（含 disposing 自身——直接 Invoke 仍可执行，挂载不是执行前提）。
            card.Engine.Bus.UnmountOwner(this);
            RollbackInjections();

            var data = new Dictionary<string, object?>
            {
                [PayloadKeys.Card] = card,
                [PayloadKeys.Effect] = this,
            };

            _invokingFromDispose = true;
            try
            {
                _disposingTrigger.InvokeAsync(card.Engine, data).GetAwaiter().GetResult();
            }
            finally
            {
                _invokingFromDispose = false;
            }

            // 最终 teardown 层：核心句柄撤销必须执行（幂等；disposing 已完成的项自然 no-op）。
            RunTeardownRegistrations(card.Engine);

            card.RemoveEffectCore(this);

            if (emitRemoved)
            {
                EmitRemoved(card);
            }
        }
        finally
        {
            _disposalState = DisposalState.Disposed;
            IsMounted = false;
            _host = null;
            WriteDisposalLog(
                card.Engine,
                "dispose:done",
                $"效果 '{Name}' 销毁完成。",
                LogLevel.Info,
                keywords: new[] { "cleanup" });
        }

        return true;
    }

    /// <summary>登记宿主引用（AddEffect 时由容器调用）。</summary>
    internal void SetHost(Card card) => _host = card;

    /// <summary>清除宿主引用（未装载效果的容器移除路径）。</summary>
    internal void ClearHost() => _host = null;

    /// <summary>
    /// 装配期校验（AddEffect 调用）：一次性效果必须存在可执行的业务主触发器
    /// （C# 被动效果通常无业务主触发器——纯注入型，不能表达"一次触发"）。
    /// </summary>
    /// <exception cref="InvalidOperationException">声明 OneShot 但无业务主触发器。</exception>
    internal void ValidateForUse()
    {
        if (OneShot && Kind == TriggerKind.Passive && MainTrigger is null)
        {
            throw new InvalidOperationException(
                $"效果 '{Name}' 声明了 OneShot，但没有业务主触发器（C# 注入型效果无法判定\"一次触发\"）；请改用 Manual，在业务 handler 内显式调用销毁。");
        }
    }

    /// <summary>撤销全部注入登记（后进先出；撤销动作自身幂等、不抛错）。</summary>
    private void RollbackInjections()
    {
        for (var i = _rollbacks.Count - 1; i >= 0; i--)
        {
            _rollbacks[i]();
        }

        _rollbacks.Clear();
        _injections.Clear(); // S-C2：读面与撤销收敛（防御：回滚动作自身异常时不留残项）
    }

    /// <summary>
    /// 执行最终 teardown 登记（后进先出；异常隔离记录、不阻断销毁链其余步骤；执行后清空——重新装载需重新登记）。
    /// 记录写入引擎总流（与装载链留痕同渠道）。
    /// </summary>
    private void RunTeardownRegistrations(LogicEngine engine)
    {
        if (_teardownRegistrations.Count == 0)
        {
            return;
        }

        for (var i = _teardownRegistrations.Count - 1; i >= 0; i--)
        {
            try
            {
                _teardownRegistrations[i]();
            }
            catch (Exception ex)
            {
                WriteDisposalLog(
                    engine,
                    "dispose:teardown-error",
                    $"效果 '{Name}' 最终 teardown 异常（隔离：继续完成销毁链）：{ex.Message}",
                    LogLevel.Error,
                    ex);
            }
        }

        _teardownRegistrations.Clear();
    }

    /// <summary>发射 effect.removed（载荷＝{ Card, Effect }；经引擎总线 Emit、P2 同步）。</summary>
    private void EmitRemoved(Card card)
    {
        card.Engine.Emit(
            Updates.EffectRemoved,
            new Dictionary<string, object?>
            {
                [PayloadKeys.Card] = card,
                [PayloadKeys.Effect] = this,
            }).GetAwaiter().GetResult();
    }

    /// <summary>disposing 守卫（内建、不可撤销）：载荷不指向本效果/宿主时静默停层（不执行作者销毁逻辑）。</summary>
    private Task GuardDisposalAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (!MatchesDisposalPayload(view))
        {
            ctx.StopSilent();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 兜底驱动（内建、不可撤销）：disposing 触发器经总线收到兜底 hook 时，发起完整销毁（容器移除＋发射）；
    /// 随后静默停层，避免作者事件在本次 hook 轮次里重复执行。显式销毁入口（<see cref="ExecuteDispose"/>）调用时跳过本事件，
    /// 由外层统一完成相同的销毁链（防递归）。
    /// </summary>
    private Task FallbackDriveAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (_invokingFromDispose)
        {
            return Task.CompletedTask;
        }

        if (_host is not { } host)
        {
            ctx.StopSilent();
            return Task.CompletedTask;
        }

        ctx.StopSilent();
        CardLoadout.DisposeEffect(host, this, DisposalSource.Fallback);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 兜底载荷匹配：能识别出"本效果/宿主"时按引用匹配；识别不出（如 turn.start 的 Player 载荷）
    /// 视为**全局兜底时机**——到达即匹配（作者声明该 hook 的意图）。
    /// </summary>
    private bool MatchesDisposalPayload(CardEventView view)
    {
        if (view.Effect is Effect effect)
        {
            return ReferenceEquals(effect, this);
        }

        if (view.Card is Card card)
        {
            return ReferenceEquals(card, _host);
        }

        if (view.Unit is Card unit)
        {
            return ReferenceEquals(unit, _host);
        }

        return true;
    }

    /// <summary>默认销毁事件：执行既有 OnUnmount（作者专属清理；异常由触发器的异常隔离记录并继续）。</summary>
    private Task DefaultDisposeAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        OnUnmount();
        return Task.CompletedTask;
    }

    /// <summary>兜底 hooks 归一化：强制包含 card.destroyed、拒绝 effect.removed、去重保序（默认项在前）。</summary>
    private static string[] NormalizeDisposalHooks(IEnumerable<string>? hooks)
    {
        var list = new List<string> { Updates.CardDestroyed };
        if (hooks is not null)
        {
            foreach (var hook in hooks)
            {
                if (string.IsNullOrWhiteSpace(hook))
                {
                    throw new ArgumentException("兜底时机不能包含 null/空白项。", nameof(hooks));
                }

                if (string.Equals(hook, Updates.EffectRemoved, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "effect.removed 是显式销毁入口的完成通知，不能作为兜底时机（会与销毁入口自指）。", nameof(hooks));
                }

                if (!list.Contains(hook))
                {
                    list.Add(hook);
                }
            }
        }

        return list.ToArray();
    }

    private void WriteDisposalLog(
        LogicEngine engine,
        string keyword,
        string message,
        LogLevel level,
        Exception? exception = null,
        IReadOnlyList<string>? keywords = null)
    {
        var list = new List<string> { "loadout", "dispose", keyword, Name };
        if (keywords is not null)
        {
            list.AddRange(keywords);
        }

        if (exception is not null)
        {
            list.Add($"exception:{exception.GetType().Name}");
        }

        // 与既有装载链留痕同渠道（source＝loadout）；dispose 系列关键词区分销毁阶段/来源。
        CardsLog.Write(engine, "loadout", message, level, list);
    }
}

/// <summary>
/// 被动效果（S4）：装载语境效果——业务主触发器由子类装配（无业务触发器时＝纯注入型）；
/// disposing 触发器默认必选（销毁逻辑＋兜底时机）。
/// 作者只需覆写 <see cref="Effect.OnMount"/>（注入）/ <see cref="Effect.OnUnmount"/>（专属清理）。
/// 装载由统一装载链完成（三入口幂等；放置处理器兜底：初始化 → 逐效果「挂 disposing → 挂主触发器 → OnMount」，Effects 列表序、幂等）。
/// </summary>
public abstract class PassiveEffect : Effect
{
    protected PassiveEffect(string name, bool oneShot = false, IEnumerable<string>? disposalHooks = null)
        : base(name, TriggerKind.Passive, oneShot, disposalHooks)
    {
    }
}

/// <summary>
/// 主动效果（S4；指令）：施放语境效果——主触发器为作者声明的主动触发器（不挂总线、不执行装载钩子；S3 约束下主动触发器不可挂载）；
/// disposing 触发器照常挂总线接收兜底（主动效果的额外副作用随卡销毁清理）。
/// 施放＝主触发器被调用（<see cref="CastAsync"/>）→ 施放事件链（作者注册；最小形态：对目标调用伤害结算流程）。
/// 实现 <see cref="ICastAction"/>（S5）：供引擎级共享指令流程（<see cref="OrderFlow"/>）在检查位通过后执行施放链。
/// 放置/加入时无装载动作（仅存在于 Effects 列表）；一次性语义经 <c>oneShot</c> 声明（施放完成即销毁）。
/// </summary>
/// <typeparam name="TView">施放载荷的视图类型（作者视图）。</typeparam>
public abstract class ActiveEffect<TView> : Effect, ICastAction
    where TView : class
{
    private readonly Trigger<TView> _castTrigger;

    protected ActiveEffect(
        string name,
        Type? bandType = null,
        IEnumerable<TriggerEvent<TView>>? castEvents = null,
        bool oneShot = false,
        IEnumerable<string>? disposalHooks = null)
        : base(name, TriggerKind.Active, oneShot, disposalHooks)
    {
        _castTrigger = new Trigger<TView>(
            name,
            TriggerKind.Active,
            bandType,
            events: castEvents,
            owner: this);

        if (oneShot)
        {
            // 主动效果的"一次触发"＝一次施放；保留槽在施放事件链之后执行销毁。
            _castTrigger.RegisterDisposal("一次性销毁", (view, ctx, ct) =>
            {
                if (HostOrNull is { } host)
                {
                    ExecuteDispose(host);
                }

                return Task.CompletedTask;
            });
        }
    }

    /// <summary>主触发器（作者视图；可在子类构造体内经 <see cref="Trigger{TView}.Register"/> 注册施放事件）。
    /// 构造期 castEvents 项的注册句柄经 <see cref="Trigger{TView}.InitialRegistrations"/> 供给（子类可 moding（逻辑替换）/撤销寻址）。</summary>
    protected Trigger<TView> CastTrigger => _castTrigger;

    internal override ITriggerMetadata? MainTrigger => _castTrigger;

    /// <summary>施放入口：调用主触发器（施放 ＝ 主触发器被调用；其施放事件链按注册序执行）。</summary>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    public Task<EventStream> CastAsync(
        LogicEngine engine, IDictionary<string, object?>? data = null, CancellationToken ct = default)
        => _castTrigger.InvokeAsync(engine, data, ct);
}

/// <summary>
/// 可序列化效果标记（S-C12）：以预制体（动态）实现的效果实现本接口＝其快照可导出；
/// 既有 C# 效果不实现本接口——审查链中标记 <c>serializable=false</c>（仅作节点、不导出快照）。
/// </summary>
public interface ISerializableEffect
{
}

/// <summary>
/// 效果注入项（S-C2；审查链用只读面）：记录一次 <c>Inject</c> 落地的目标触发器、事件名、band、优先级与注册句柄。
/// 修复既有缺口（原实现只留撤销闭包，无法反查"效果注入了什么"）。
/// </summary>
public sealed class EffectInjection
{
    internal EffectInjection(object target, string name, Enum band, int priority, TriggerRegistration registration)
    {
        Target = target;
        Name = name;
        Band = band;
        Priority = priority;
        Registration = registration;
    }

    /// <summary>目标触发器实例（<c>Trigger&lt;TView&gt;</c> 的 object 视角；泛型参数不可静态表达）。</summary>
    public object Target { get; }

    /// <summary>注入时登记的事件名。</summary>
    public string Name { get; }

    /// <summary>注入的 band 成员。</summary>
    public Enum Band { get; }

    /// <summary>注入的 band 内优先级。</summary>
    public int Priority { get; }

    /// <summary>注册句柄（可经目标触发器 <c>Unregister</c> 撤销）。</summary>
    public TriggerRegistration Registration { get; }
}
