using Orc.Cards;
using Orc.Output;

namespace Orc.Core;

/// <summary>
/// 逻辑引擎（S5 形态）。公开面按用途组织：
/// ①事件流：<see cref="RootStream"/>（总流；树根，引擎域一切因果记录最终可达）与 <see cref="EventStreamJson"/>（树形 JSON 导出）；
/// ②信号：<see cref="Emit"/>（＝对 <c>engine#&lt;信号&gt;</c> 成员的调用）与 <see cref="InvokeMemberAsync(string, Context, CancellationToken)"/>
/// （＝执行一个引擎成员）；总线门面已在 b5 删除，调用点一律走本类；
/// ③流程：<see cref="OrderFlow"/>（S5；承载引擎级共享的施放检查位）。攻击／伤害流程类已删除（P5/S17），
/// 动作级实现归游戏层动作实体；
/// ④卡牌：<see cref="Cards"/>（S5 登记读面：创建即注册、销毁清理响应时移除）与 <see cref="DestroyCard"/>（一步式销毁辅助）；
/// ⑤宿主集成（S5 骨架）：<see cref="Bridge"/>（编译期强类型桥）与 <see cref="Subscribe"/>（运行期回调）——两通道均在更新广播（Emit）时通知；
/// ⑥快照：<see cref="SnapshotJson"/>（单实体/全量 JSON）；
/// 允许并主张多实例（每实例独立信号端点、总流与卡牌登记）；无全局单例；链上执行恒随「调用时显式传入的引擎引用」。
/// 发射面唯一：<see cref="Emit"/> → <see cref="SignalBridge"/> → 引擎成员。
/// </summary>
public sealed class LogicEngine
{
    private readonly List<Card> _cards = new();
    private readonly List<CallbackSubscription> _subscriptions = new();
    private readonly List<ImmediateSubscription> _immediateSubscriptions = new();
    private readonly List<SegmentCompletedSubscription> _segmentCompletedSubscriptions = new();
    private readonly Queue<EventSegment> _segmentQueue = new();
    private static readonly AsyncLocal<ActionScope?> CurrentActionScope = new();
    private long _segmentSequence;
    private CardLoadoutProcessor? _cardLoadout;
    private Orc.Cards.SnapshotStore? _prefabs;
    private readonly SignalBridge _signals;

    public LogicEngine()
    {
        RootStream = new EventStream();
        Hooks = new HookRegistry();
        Orchestration = new OrchestrationManager(this);
        OrderFlow = new OrderFlow(this);
        _signals = new SignalBridge(this);

        // S-C1：内置更新字符串预登记（定义级词汇表基线；自由字符串仍可随时登记）。
        Hooks.Register(Updates.CardPlaced);
        Hooks.Register(Updates.CardDestroyed);
        Hooks.Register(Updates.CardData);
        Hooks.Register(Updates.EffectRemoved);
    }

    // ---------- ①事件流 ----------

    /// <summary>总事件流（树根；顶层执行流自动挂载于其下，引擎域一切因果记录最终可达）。</summary>
    public EventStream RootStream { get; }

    // ---------- ②信号 ----------

    /// <summary>
    /// hook 标识注册表（S-C1；引擎级、引擎内维护）：hook 名 ↔ <see cref="HookId"/> 双向互转与登记；
    /// 多引擎实例各自独立；开放集合（自由字符串保留，无白名单）。
    /// </summary>
    public HookRegistry Hooks { get; }

    /// <summary>
    /// 编排管理器（S-C3；引擎级）：集中登记触发器 ↔ hook ↔ 事件关系，提供反向依赖与声明期可达链导出。
    /// 由内核自动采样（总线挂载）与装配方显式登记共同填充；多引擎实例各自独立。
    /// </summary>
    public OrchestrationManager Orchestration { get; }

    /// <summary>
    /// 脚本求值器（S-C6；接口倒置，可空）：由 satellite 工程（如 Orc.Script，含 Roslyn）实现并装配于此；
    /// 未装配（null）＝动态 csx 处理器不可用（内核零依赖、照常运转）。
    /// </summary>
    public IScriptEvaluator? ScriptEvaluator { get; set; }

    /// <summary>
    /// 同步脚本求值器（判定器/资产服务包装器专用；可空）：未装配＝<c>judicatorWrappers</c> 的 csx 不可用（隔离记录）。
    /// </summary>
    public ISyncScriptEvaluator? SyncScriptEvaluator { get; set; }

    /// <summary>
    /// 对局级判定器注册表（可空）：对局装配期注入——供效果装载链落地 <c>judicatorWrappers</c>（判定器包装器自注册）。
    /// </summary>
    public JudicatorRegistry? Judicators { get; set; }

    /// <summary>
    /// 效果快照登记处（原「预制体管理器」；v4-only 后只剩注册面）：处理器与效果快照的登记与查询。
    /// </summary>
    public Orc.Cards.SnapshotStore Prefabs => _prefabs ??= new Orc.Cards.SnapshotStore();

    /// <summary>
    /// 装载门报告（S14；引擎级、每局一份）：记录每一次包装载的结局（成功／可选包被跳过／必需包失败）。
    /// <para>它是"哪些包没生效"的**唯一留痕载体**：审查导出（<c>packages</c> 段）与初始化报告都读这一份。</para>
    /// </summary>
    public LoadGateReport LoadGate { get; } = new();

    /// <summary>
    /// 本对局用到的**随机选择器**记录（R2c／R7 验收项；引擎级、每局一份）。
    /// <para>
    /// 只有显式开启随机（<c>MatchOptions.AllowRandomTargeting</c>）并真正执行了 <c>sel=random</c> 取样时才登记；
    /// 审查导出（<c>randomSelectors</c> 段）读这一份，用来回答"本对局哪里用了随机"。
    /// </para>
    /// </summary>
    public List<RandomSelectionRecord> RandomSelections { get; } = new();

    // ---------- ⑨审查链导出（S-C4） ----------

    /// <summary>导出全域审查链 JSON（结构化真源；节点＝触发器种类，含实例清单与声明的下游边）。</summary>
    public string ExportAuditChainJson(bool indented = false)
        => Orc.Output.AuditChainJson.Serialize(Orchestration.BuildAuditChain(), indented);

    /// <summary>导出全域审查链文本视图（人类可读；与 JSON 同源）。</summary>
    public string ExportAuditChainText()
        => Orc.Output.AuditChainText.Serialize(Orchestration.BuildAuditChain());

    /// <summary>
    /// 导出以指定触发器种类为起点的声明期可达链 JSON（未登记起点＝空链）。
    /// </summary>
    public string ExportChainJson(TriggerId root, bool indented = false)
        => Orc.Output.AuditChainJson.Serialize(Orchestration.QueryChain(root), indented);

    /// <summary>
    /// 新模型的编排运行时（S10／批次 3；可空）：对局装配期挂上后，审查图可同时导出实体—成员—挂载边。
    /// 未挂载（纯内核/独立构造场景）＝审查图只投影旧路径的边（能力不退化）。
    /// <para>
    /// 挂上时**接线发射通道**（<see cref="Orc.Core.Model.OrchestrationRuntime.SignalEmitter"/>）：组件卸载的
    /// <c>effect.removed</c> 完成通知经本引擎的 <see cref="Emit"/> 发出（b4 泛化——组件卸载对订阅方可见）。
    /// </para>
    /// <para>
    /// **身份不可互换（R6b 的引擎级推论）**：一个 <see cref="LogicEngine"/> 只有一个引擎实体。若内核兜底运行时
    /// （<see cref="SignalRuntime"/> 的懒建分支）已经承载了边／运行期成员，再赋一个**不同**的运行时会让那些边
    /// 挂在旧实体上——<c>Emit</c> 照常写更新条目，订阅者却一次都不执行（静默失效，最坏的一类退化）。
    /// 因此此处**拒绝**这种替换并指出原因；装配方必须在首次发射／挂载之前完成赋值。无状态时允许替换
    /// （纯折叠，不丢任何东西）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">内核兜底运行时已承载状态却要用另一个运行时替换。</exception>
    public Orc.Core.Model.OrchestrationRuntime? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value))
            {
                return;
            }

            // 当前**生效**的运行时（可能来自外部赋值，也可能是内核兜底）——两者都可能是"已承载边"的那一个。
            var current = _model ?? _kernelModel;
            if (current is not null
                && !ReferenceEquals(current, value)
                && (current.Engine.HasRuntimeState || current.Components.Count > 0))
            {
                throw new InvalidOperationException(
                    "编排运行时不可替换：本引擎当前生效的运行时已经承载了运行期状态"
                    + $"（engine#{current.Engine.Name} 上的边／运行期成员／组件实例）。"
                    + "换一个运行时（或解除）会让这些边挂在旧引擎实体上——Emit 照常写更新条目，订阅者却不再执行"
                    + "（静默失效，R6b：一个 LogicEngine 只有一个引擎实体）。"
                    + "请在首次 Emit／挂载之前完成 Model 装配；若当前实例只是探针，请先撤销它的边（句柄 Dispose）。");
            }

            _model = value;
            if (value is not null)
            {
                value.SignalEmitter = Emit;
                _kernelModel = null; // 身份已确定：兜底槽位不再有存在意义（无可丢状态才可能走到这里）
            }
        }
    }

    private Orc.Core.Model.OrchestrationRuntime? _model;

    /// <summary>
    /// 构建审查图（S10／R7）：实体—成员—挂载边。替代旧订阅查询读面（`GetSubscribers`／
    /// <c>EnumerateHooks</c>）作为"谁订阅了什么"的唯一读面。
    /// <para>只读、无副作用、可反复调用：每次调用取一次快照。</para>
    /// <para>
    /// 未挂载新模型运行时（独立构造的纯内核引擎）时，用**内核基础预制体**的运行时兜底——
    /// 否则内核 4 个信号会被误判成"无对应信号的 hook"（图缺口），旧订阅读面的等价性就不成立了。
    /// </para>
    /// </summary>
    public Orc.Core.Model.AuditGraph BuildAuditGraph()
        => Orc.Core.Model.AuditGraphBuilder.Build(this, SignalRuntime);

    private Orc.Core.Model.OrchestrationRuntime? _kernelModel;

    /// <summary>
    /// **实际生效的编排运行时**（A2 起是生产执行的唯一通路）：对局装配期挂上的 <see cref="Model"/>；
    /// 未挂载（纯内核／独立构造场景）＝内核基础预制体的运行时（内核 4 个信号）。
    /// <para>懒创建：只有真的发射／挂载／导出审查图时才物化（不影响零使用场景）。</para>
    /// <para>
    /// **物化即定身份**：懒建出的兜底运行时一旦承载了边／运行期成员，就不再允许被 <see cref="Model"/> 替换
    /// （见该属性的异常说明）——否则 <c>Emit</c> 与 <c>Subscribe</c> 会落在两个不同的引擎实体上。
    /// </para>
    /// </summary>
    internal Orc.Core.Model.OrchestrationRuntime SignalRuntime
        => Model ?? (_kernelModel ??= new Orc.Core.Model.OrchestrationRuntime(
            Orc.Core.Model.KernelSignals.CreateCatalog(), new Orc.Core.Model.HandlerRegistry()));

    // ---------- ③流程 ----------

    /// <summary>指令流程（S5；引擎级共享：反制检查 → 施放结算（执行指令施放链）→ 收尾；承载反制类效果接入的具名检查位）。</summary>
    public OrderFlow OrderFlow { get; }

    /// <summary>
    /// 更新广播（A2：**经引擎实体的信号成员**发射——<c>engine#&lt;updateType&gt;</c>）。
    /// 行为与旧总线逐项等价（更新条目→外部观察者→订阅者顺序、载荷副本、异常隔离、取消穿透）：
    /// 差异只在于"订阅者"现在是该信号成员上的**边**，不再是总线订阅表。
    /// 未声明的名字＝运行期注册（R1e）后立即发射（旧 API 的自由字符串由此收敛进成员表，不存在绕过成员表的通路）。
    /// </summary>
    public Task Emit(
        string updateType, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
        => _signals.EmitAsync(updateType, payload, ct);

    /// <summary>
    /// 引擎成员的一次执行入口（<c>engine#&lt;成员名&gt;</c>）：沿用调用方的 <see cref="Context"/>
    /// （数据面共享——组件对 <c>view.&lt;可写字段&gt;</c> 的写回能被调用方读回），并补建执行帧。
    /// <para>
    /// 这是**唯一**的成员调用面：动作端、卡牌成员、订阅体都经它执行；未声明的成员＝
    /// <see cref="KeyNotFoundException"/>（fail-fast——成员名与预制体同源，不该在运行期才发现拼错）。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">memberName／ctx 为 null。</exception>
    /// <exception cref="KeyNotFoundException">引擎实体上没有该成员。</exception>
    public Task<Orc.Core.Model.MemberExecutionResult> InvokeMemberAsync(
        string memberName, Context ctx, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(ctx);

        var runtime = SignalRuntime;
        return runtime.RunMemberAsync(runtime.Engine, memberName, ctx, this, ct);
    }

    /// <summary>
    /// 引擎成员的一次执行入口（数据面形态）：以 <paramref name="data"/> 复制一份新 <see cref="Context"/>。
    /// </summary>
    /// <exception cref="ArgumentNullException">memberName 为 null。</exception>
    /// <exception cref="KeyNotFoundException">引擎实体上没有该成员。</exception>
    public Task<Orc.Core.Model.MemberExecutionResult> InvokeMemberAsync(
        string memberName, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        return InvokeMemberAsync(memberName, new Context(data), ct);
    }

    /// <summary>
    /// 运行期注册信号（R1e）：自定义信号（沙盒／脚本的 <c>emit</c> 操作等）必须先经此进入 <c>engine</c> 命名空间，
    /// 之后的挂载与发射都按正常信号走（不存在"任意字符串直接发射"的旁路）。同名幂等。
    /// </summary>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    /// <exception cref="Orc.Core.Model.SignalNotRegisteredException">同名成员已存在但不是信号。</exception>
    public Orc.Core.Model.MemberDefinition RegisterSignal(string name)
        => SignalRuntime.RegisterSignal(name);

    /// <summary>信号端点（订阅序；审查面投影用：订阅者身份 → <c>engine#&lt;信号&gt;</c> 边）。</summary>
    internal IReadOnlyList<SignalEndpointInfo> SignalEndpoints => _signals.Endpoints;

    /// <summary>
    /// 往**任意引擎成员**上挂一条外来边（信号＝订阅、流程成员＝检查位／守卫），返回撤销句柄。
    /// <para>
    /// 反制的使用检查＝<c>engine#order.cast</c> 上的一条外来边（<c>Counter</c> 段＝早于结算的
    /// <c>Resolve</c> 段）；句柄 Dispose 即撤销——与旧实现"注册一个 Counter 段事件再 Unregister"逐项等价。
    /// </para>
    /// </summary>
    /// <param name="memberName">目标成员名（<c>engine#&lt;成员名&gt;</c> 的成员名段）。</param>
    /// <param name="displayName">来源名（留痕/审查用）。</param>
    /// <param name="owner">所有者（引用相等卸载的依据；可为 null）。</param>
    /// <param name="handler">边体。</param>
    /// <param name="priority">位次优先级（同一位次内先小后大）。</param>
    /// <param name="band">外来边在目标成员段序方案里的段名（null＝与目标成员自有边同段）。</param>
    /// <exception cref="ArgumentException">memberName／displayName 为空。</exception>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    /// <summary>
    /// 订阅一个**信号成员**（<c>engine#&lt;信号&gt;</c>）：建端点实体＋成员＋边，并把信号名登记进 hook 词汇表。
    /// 与 <see cref="Subscribe(string, EngineUpdateCallback)"/>（宿主回调）不是一件事：那条只通知宿主，本方法进模型边图。
    /// </summary>
    /// <param name="signal">信号名（<c>engine#&lt;信号&gt;</c> 的成员名段）。</param>
    /// <param name="displayName">订阅者名（留痕/审查用）。</param>
    /// <param name="owner">所有者（引用相等卸载依据）。</param>
    /// <param name="edge">订阅体（一条边）。</param>
    /// <param name="priority">位次优先级（旧挂载优先级）。</param>
    /// <param name="band">外来边在目标成员段序方案里的段名（null＝与目标成员自有边同段）。</param>
    public void SubscribeMember(
        string signal,
        string displayName,
        object? owner,
        Orc.Core.Model.OwnedEdge edge,
        int priority = UpdatePriorities.Normal,
        string? band = null)
        => _signals.SubscribeSignal(signal, displayName, priority, owner, edge, band);

    /// <inheritdoc cref="SubscribeMember"/>
    public IDisposable AttachMemberEdge(
        string memberName,
        string displayName,
        object? owner,
        Orc.Core.Model.OwnedEdge edge,
        int priority = 0,
        string? band = null)
        => _signals.Attach(memberName, displayName, priority, owner, edge, band: band);

    // ---------- ④卡牌 ----------

    /// <summary>
    /// 卡牌登记读面（S5 加性）：全部已创建（注册）卡牌的只读列表，顺序＝创建序（登记序）。
    /// 登记时机＝卡牌创建（<see cref="Card"/> 构造）即注册；范围＝全部已创建卡牌（含未放置）；
    /// 移除时机＝销毁清理响应（card.destroyed 更新处理；一步式与两步式（杀＋发更新）路径收敛一致）。
    /// 多引擎实例各自独立；快照导出经 <see cref="SnapshotJson.SerializeAll"/>。
    /// </summary>
    public IReadOnlyList<Card> Cards => _cards;

    /// <summary>
    /// 引擎托管引用表（引擎实例级）：以全 GUID 字符串（N 格式）为 key 的对象引用登记面。
    /// 卡牌（含 HQ）**创建即登记、销毁即注销**（与 <see cref="Cards"/> 同生命周期）；
    /// 无 GUID 对象（游戏层 Player、效果对象等）经 <see cref="EngineReferences.Assign"/> 分配键。
    /// 用途＝外部（宿主/服务层）以稳定标识表达与解析对象引用（事件载荷、工具参数、观察输出）。
    /// 多引擎实例各自独立；随引擎实例丢弃而清空。
    /// </summary>
    public EngineReferences References { get; } = new();

    /// <summary>
    /// 引擎共享变量表（引擎实例级＝对局级）：跨效果/跨卡牌的 key→object 数据袋。
    /// 与 <see cref="References"/> 并列且互不干扰——key 为自由字符串，值为任意对象（引用共享），
    /// 不进快照、不发事件流、不参与回滚。供效果保存自身生命周期之外的数据
    /// （如"所有友方已摧毁单位"的累积列表）；多引擎实例各自独立，随引擎实例丢弃而清空。
    /// </summary>
    public EngineVariables Variables { get; } = new();

    /// <summary>登记卡牌（S5；卡牌构造时调用；内部使用）。</summary>
    internal void RegisterCard(Card card)
    {
        _cards.Add(card);
        References.Register(card.Id, card);
    }

    /// <summary>移除卡牌登记（S5；销毁清理响应调用；幂等——重复移除＝无操作；内部使用）。</summary>
    internal void UnregisterCard(Card card)
    {
        _cards.Remove(card);
        References.Unregister(card.Id);
    }

    /// <summary>
    // ---------- ⑤宿主集成（S5 骨架） ----------

    /// <summary>
    /// 引擎桥（S5 骨架；可空）：宿主实现 <see cref="IEngineBridge"/>（编译期强类型契约）并装配于此；
    /// 引擎在更新广播（Emit）时点调用其更新通知；未装配（null）＝跳过、引擎照常运转；桥异常被隔离（记录、不破坏广播）。
    /// </summary>
    public IEngineBridge? Bridge { get; set; }

    /// <summary>
    /// 注册更新回调（S5 骨架）：订阅单位＝更新（Emit 时回调）；多订阅者＝注册序回调；回调异常被隔离（记录、不破坏更新广播主流程）。
    /// 返回取消句柄：Dispose 后该订阅不再触发（幂等）；句柄释放后订阅引用被移除（不残留）。
    /// </summary>
    /// <exception cref="ArgumentNullException">callback 为 null。</exception>
    public IDisposable Subscribe(EngineUpdateCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new CallbackSubscription(this, callback);
        _subscriptions.Add(subscription);
        return subscription;
    }

    private void RemoveSubscription(CallbackSubscription subscription) => _subscriptions.Remove(subscription);

    /// <summary>
    /// 更新时点的外部通知分发（S5 内部；由总线 Emit 调用）：更新条目写入后、订阅者广播前——先桥（单点，可空）、后回调（注册序快照）。
    /// 各通道异常彼此隔离并记入发射者流（Error 级；source＝通道标识）；取消类异常穿透上抛（与订阅者广播口径一致）。
    /// </summary>
    internal async Task NotifyExternalObservers(
        string updateType, IReadOnlyDictionary<string, object?>? payload, EventStream targetStream, CancellationToken ct)
    {
        var bridge = Bridge;
        if (bridge is not null)
        {
            try
            {
                await bridge.OnUpdate(updateType, payload, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消类异常：与订阅者广播口径一致，穿透上抛
            }
            catch (Exception ex)
            {
                WriteObserverError(targetStream, bridge.GetType().Name, ex);
            }
        }

        foreach (var subscription in _subscriptions.ToArray())
        {
            try
            {
                await subscription.Callback(updateType, payload, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteObserverError(targetStream, "update-callback", ex);
            }
        }

        // ⑧UI 消费桥接：非阻塞即时更新口——不 await、异常隔离（void handler，无取消令牌参与）
        foreach (var subscription in _immediateSubscriptions.ToArray())
        {
            try
            {
                subscription.Callback(updateType, payload);
            }
            catch (Exception ex)
            {
                WriteObserverError(targetStream, "immediate-update", ex);
            }
        }
    }

    /// <summary>写外部通道（桥/回调）的隔离错误记录（形态与订阅者传播错误记录同构）。</summary>
    private static void WriteObserverError(EventStream stream, string source, Exception ex)
    {
        stream.WriteLog(
            source,
            ex.Message,
            LogLevel.Error,
            new[] { $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
    }

    // ---------- ⑧UI 消费桥接（段轮询 + 即时更新监听） ----------

    /// <summary>
    /// 开始一次动作作用域（UI 消费桥接；S1）：显式标注"一次动作"的起止；
    /// 由驱动方（游戏层动作入口）以 <c>await using var _ = engine.BeginAction();</c> 使用；
    /// 作用域释放时（最外层）把该动作产生的引擎内部信号聚合为一段并放入待取队列。
    /// 嵌套：内层合并入外层（仅最外层产出段）。不改变既有 Emit/总线语义。
    /// </summary>
    public ActionScope BeginAction()
    {
        var parent = CurrentActionScope.Value;
        var scope = new ActionScope(this, parent, RootStream.EntryCount, RootStream.ChildCount);
        CurrentActionScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// 释放动作作用域（S1；由 <see cref="ActionScope.Dispose"/> 调用）：还原作用域栈；
    /// 仅最外层产出段——取总流自进入以来的新增条目（含报错）与新增子树，封段入待取队列
    /// （异常路径亦产出"已发生部分"）。嵌套内层＝合并入外层、不单独产出。
    /// </summary>
    internal void EndAction(ActionScope scope)
    {
        if (ReferenceEquals(CurrentActionScope.Value, scope))
        {
            CurrentActionScope.Value = scope.Parent;
        }

        if (scope.Parent is not null)
        {
            return; // 内层：合并入外层，不单独产出
        }

        var entries = RootStream.EntriesInRange(scope.EntryStart, RootStream.EntryCount);
        var children = RootStream.ChildrenInRange(scope.ChildStart, RootStream.ChildCount);
        if (entries.Count == 0 && children.Count == 0)
        {
            return; // 零发射：本段无任何信号（如动作开头即被参数校验拒绝）时不产段
        }

        var segment = new EventSegment(++_segmentSequence, entries, children);
        _segmentQueue.Enqueue(segment);
        NotifySegmentCompleted(segment);
    }

    /// <summary>
    /// 段完成同步通知（S9）：订阅者可在段产出点立即冻结历史值（前后态）；
    /// 同步、非阻塞、异常隔离——回调异常被记录为错误日志，不影响动作作用域释放与段入队。
    /// </summary>
    private void NotifySegmentCompleted(EventSegment segment)
    {
        foreach (var subscription in _segmentCompletedSubscriptions.ToArray())
        {
            try
            {
                subscription.Callback(segment);
            }
            catch (Exception ex)
            {
                WriteObserverError(RootStream, "segment.completed", ex);
            }
        }
    }

    /// <summary>
    /// 取走全部待取段（UI 消费桥接；S4）：FIFO、取走即移除；无内容返回空列表。
    /// 供 UI 轮询消费（不依赖精确通知信号）。
    /// </summary>
    public IReadOnlyList<EventSegment> TakeSegments()
    {
        if (_segmentQueue.Count == 0)
        {
            return Array.Empty<EventSegment>();
        }

        var result = _segmentQueue.ToArray();
        _segmentQueue.Clear();
        return result;
    }

    /// <summary>尝试取走一段（S4；FIFO）：取走即移除；无内容返回 false。</summary>
    public bool TryTakeSegment(out EventSegment segment)
    {
        if (_segmentQueue.Count > 0)
        {
            segment = _segmentQueue.Dequeue();
            return true;
        }

        segment = null!;
        return false;
    }

    /// <summary>
    /// 注册即时更新监听（S8）：<b>非阻塞</b> handler——引擎在更新广播（Emit）时点调用，但不等待其完成；
    /// handler 不得阻塞（仅通知/轻量）；监听全部更新（过滤由 UI 自持）；异常被隔离（记录、不破坏广播）。
    /// 返回取消句柄：Dispose 后不再被调用（幂等）。与 <see cref="Subscribe"/> 及段轮询通道并存、互不影响。
    /// </summary>
    /// <exception cref="ArgumentNullException">callback 为 null。</exception>
    public IDisposable OnImmediateUpdate(ImmediateUpdateCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new ImmediateSubscription(this, callback);
        _immediateSubscriptions.Add(subscription);
        return subscription;
    }

    private void RemoveImmediateSubscription(ImmediateSubscription subscription)
        => _immediateSubscriptions.Remove(subscription);

    /// <summary>
    /// 注册段完成监听（S9）：<b>同步</b> handler——段入队后立即调用，供消费方在段产出点冻结历史值；
    /// handler 须轻量、只读引擎（投影查询允许、写入口禁止）；异常被隔离（记录、不破坏动作作用域）。
    /// 返回取消句柄：Dispose 后不再被调用（幂等）。
    /// </summary>
    /// <exception cref="ArgumentNullException">callback 为 null。</exception>
    public IDisposable OnSegmentCompleted(Action<EventSegment> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new SegmentCompletedSubscription(this, callback);
        _segmentCompletedSubscriptions.Add(subscription);
        return subscription;
    }

    private void RemoveSegmentCompletedSubscription(SegmentCompletedSubscription subscription)
        => _segmentCompletedSubscriptions.Remove(subscription);

    /// <summary>段完成订阅记录（S9；Dispose＝取消注册（幂等））。</summary>
    private sealed class SegmentCompletedSubscription : IDisposable
    {
        private readonly LogicEngine _engine;
        private bool _disposed;

        internal SegmentCompletedSubscription(LogicEngine engine, Action<EventSegment> callback)
        {
            _engine = engine;
            Callback = callback;
        }

        /// <summary>回调（注册序调用；同步——引擎调用栈内执行；异常隔离由引擎分发方处理）。</summary>
        internal Action<EventSegment> Callback { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return; // 幂等：重复取消＝无操作
            }

            _disposed = true;
            _engine.RemoveSegmentCompletedSubscription(this);
        }
    }

    // ---------- 内部机制 ----------

    /// <summary>
    /// 确保卡牌装载链处理器已挂载（S4；首次创建卡牌时登记；幂等——每引擎至多两个订阅：
    /// 「卡牌放置处理器」「卡牌清理处理器」）。
    /// S3 域行为不受影响：不使用卡牌的引擎不会挂载任何处理器。
    /// </summary>
    internal void EnsureCardLoadout() => _cardLoadout ??= new CardLoadoutProcessor(this);

    /// <summary>
    /// 一步式销毁辅助（S4）：「杀（<see cref="Entity.Destroy"/> 语义）＋发 card.destroyed 更新」同一步完成，驱动装载链清理该卡全部效果。
    /// <see cref="Entity.Destroy"/> 本身仍「只杀不管卸载」（S3 约束不变）；清理响应归装载链处理器/效果主触发器。
    /// 幂等：重复销毁＝卡牌已失效（Lifetime 幂等）＋清理模板幂等（无操作）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">card 不属于本引擎。</exception>
    public async Task DestroyCard(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (!ReferenceEquals(card.Engine, this))
        {
            throw new InvalidOperationException($"卡牌 '{card.Name}' 不属于本引擎，不能经本引擎销毁。");
        }

        card.Destroy();
        await Emit(Updates.CardDestroyed, new Dictionary<string, object?> { [PayloadKeys.Card] = card });
    }

    /// <summary>回调订阅记录（S5；Dispose＝取消注册（幂等）；取消后从引擎订阅列表移除、引用释放）。</summary>
    private sealed class CallbackSubscription : IDisposable
    {
        private readonly LogicEngine _engine;
        private bool _disposed;

        internal CallbackSubscription(LogicEngine engine, EngineUpdateCallback callback)
        {
            _engine = engine;
            Callback = callback;
        }

        /// <summary>回调（注册序调用；异常隔离由引擎分发方处理）。</summary>
        internal EngineUpdateCallback Callback { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return; // 幂等：重复取消＝无操作
            }

            _disposed = true;
            _engine.RemoveSubscription(this);
        }
    }

    /// <summary>即时更新订阅记录（S8；Dispose＝取消注册（幂等）；取消后从列表移除、引用释放）。</summary>
    private sealed class ImmediateSubscription : IDisposable
    {
        private readonly LogicEngine _engine;
        private bool _disposed;

        internal ImmediateSubscription(LogicEngine engine, ImmediateUpdateCallback callback)
        {
            _engine = engine;
            Callback = callback;
        }

        /// <summary>回调（注册序调用；非阻塞——引擎不等待；异常隔离由引擎分发方处理）。</summary>
        internal ImmediateUpdateCallback Callback { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return; // 幂等：重复取消＝无操作
            }

            _disposed = true;
            _engine.RemoveImmediateSubscription(this);
        }
    }
}

/// <summary>
/// 一次随机选择器取样（R2c 审查面）：谁用了随机、取样条件是什么。
/// </summary>
/// <param name="Viewer">发起取样的卡牌名（诊断定位用）。</param>
/// <param name="Selector">选择器描述（sel/side/zone/type/keyword/count 的可读拼接）。</param>
public sealed record RandomSelectionRecord(string Viewer, string Selector);
