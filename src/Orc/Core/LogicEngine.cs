using Orc.Cards;
using Orc.Output;

namespace Orc.Core;

/// <summary>
/// 逻辑引擎（S5 形态）。公开面按用途组织：
/// ①事件流：<see cref="RootStream"/>（总流；树根，引擎域一切因果记录最终可达）与 <see cref="EventStreamJson"/>（树形 JSON 导出）；
/// ②总线：<see cref="Bus"/>（S3；更新广播；一对一双向绑定）与 <see cref="Emit"/>（便捷转发）；
/// ③流程：<see cref="OrderFlow"/>（S5；承载引擎级共享的施放检查位）。攻击／伤害流程类已删除（P5/S17），
/// 动作级实现归游戏层动作实体；
/// ④卡牌：<see cref="Cards"/>（S5 登记读面：创建即注册、销毁清理响应时移除）与 <see cref="DestroyCard"/>（一步式销毁辅助）；
/// ⑤宿主集成（S5 骨架）：<see cref="Bridge"/>（编译期强类型桥）与 <see cref="Subscribe"/>（运行期回调）——两通道均在更新广播（Emit）时通知；
/// ⑥快照：<see cref="SnapshotJson"/>（单实体/全量 JSON）；
/// ⑦卡牌装载处理（S4 懒创建）：<see cref="CardPlacedTrigger"/> / <see cref="CardCleanupTrigger"/>（内置事件注册句柄供给面）。
/// 允许并主张多实例（每实例独立总线、总流与卡牌登记）；无全局单例；链上执行恒随「调用时显式传入的引擎引用」。
/// engine.Emit 为 <see cref="Bus.Emit"/> 的便捷转发（完全一致、纯转发：含入流目标流判定）。
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
    private readonly List<Action<Card, Effect>> _cardMountCompletedActions = new();
    private CardLoadoutProcessor? _cardLoadout;
    private Orc.Cards.PrefabManager? _prefabs;

    public LogicEngine()
    {
        RootStream = new EventStream();
        Hooks = new HookRegistry();
        Bus = new Bus(this);
        Orchestration = new OrchestrationManager(this);
        OrderFlow = new OrderFlow(this);

        // S-C1：内置更新字符串预登记（定义级词汇表基线；自由字符串仍可随时登记）。
        Hooks.Register(Updates.CardPlaced);
        Hooks.Register(Updates.CardDestroyed);
        Hooks.Register(Updates.CardData);
        Hooks.Register(Updates.EffectRemoved);
    }

    // ---------- ①事件流 ----------

    /// <summary>总事件流（树根；顶层执行流自动挂载于其下，引擎域一切因果记录最终可达）。</summary>
    public EventStream RootStream { get; }

    // ---------- ②总线 ----------

    /// <summary>总线（S3；与所属引擎一对一绑定；公开只读、不可替换；多实例互不相通）。</summary>
    public Bus Bus { get; }

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
    /// 预制体管理器（S-C7；引擎级、懒创建）：处理器与效果快照的注册、解析与本地明文目录加载。
    /// </summary>
    public Orc.Cards.PrefabManager Prefabs => _prefabs ??= new Orc.Cards.PrefabManager(this);

    // ---------- ⑨审查链导出（S-C4） ----------

    /// <summary>导出全域审查链 JSON（结构化真源；节点＝触发器种类，含实例清单与声明的下游边）。</summary>
    public string ExportAuditChainJson(bool indented = false)
        => Orc.Output.AuditChainJson.Serialize(Orchestration.BuildAuditChain(), indented);

    /// <summary>导出全域审查链文本视图（人类可读；与 JSON 同源）。</summary>
    public string ExportAuditChainText()
        => Orc.Output.AuditChainText.Serialize(Orchestration.BuildAuditChain());

    /// <summary>导出以指定触发器种类为起点的声明期可达链 JSON（未登记起点＝空链）。</summary>
    public string ExportChainJson(TriggerId root, bool indented = false)
        => Orc.Output.AuditChainJson.Serialize(Orchestration.QueryChain(root), indented);

    // ---------- ③流程 ----------

    /// <summary>指令流程（S5；引擎级共享：反制检查 → 施放结算（执行指令施放链）→ 收尾；承载反制类效果接入的具名检查位）。</summary>
    public OrderFlow OrderFlow { get; }

    /// <summary>更新广播便捷转发（与 <see cref="Bus.Emit"/> 行为完全一致）。</summary>
    public Task Emit(
        string updateType, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
        => Bus.Emit(updateType, payload, ct);

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
    /// 注册「卡牌效果装载完成动作」（S4 装载管线扩展点；加性面）：每次被动效果装载成功
    /// （装载链完成「挂主触发器 → OnMount」）后，按注册序自动执行（P2 同步）。内核不感知具体语义——
    /// 供宿主机制在装配期注册「装载后自动动作」（如游戏层「效果托管登记」：卸载时按来源撤销该效果施加的
    /// 修饰器/光环声明；任何装载入口〔加载时点/放置驱动/Add 即时/词条内嵌〕统一生效、复装自动重建）。
    /// 扩展点可缺省（未注册时装载照常）；动作异常＝归入装载失败口径（由装载链记录并回滚为未生效——本面不捕获）。
    /// </summary>
    /// <exception cref="ArgumentNullException">action 为 null。</exception>
    public void RegisterCardMountCompletedAction(Action<Card, Effect> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _cardMountCompletedActions.Add(action);
    }

    /// <summary>
    /// 「卡牌放置处理器」触发器（S4 装载链内置件；懒创建——首次创建卡牌时装配，null＝尚未创建）。
    /// 用途：内置事件（「放置处理」）的寻址面——注册项句柄经 <see cref="Trigger{TView}.InitialRegistrations"/> 供给（moding（逻辑替换）/撤销等场景）。
    /// </summary>
    public Trigger<CardEventView>? CardPlacedTrigger => _cardLoadout?.PlacedTrigger;

    /// <summary>
    /// 「卡牌清理处理器」触发器（S4 装载链内置件；懒创建——首次创建卡牌时装配，null＝尚未创建）。
    /// 用途：内置事件（「清理处理」）的寻址面——注册项句柄经 <see cref="Trigger{TView}.InitialRegistrations"/> 供给（moding（逻辑替换）/撤销等场景）。
    /// </summary>
    public Trigger<CardEventView>? CardCleanupTrigger => _cardLoadout?.CleanupTrigger;

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
    /// 执行「卡牌效果装载完成动作」（装载链内部调用；快照遍历——执行期间的新注册不影响本轮）。
    /// 异常不拦截（由装载链按装载失败口径记录并回滚为未生效）；未注册＝无操作。
    /// </summary>
    internal void RunCardMountCompletedActions(Card card, Effect effect)
    {
        foreach (var action in _cardMountCompletedActions.ToArray())
        {
            action(card, effect);
        }
    }

    /// <summary>
    /// 确保卡牌装载链处理器已挂载（S4；首次创建卡牌时登记；幂等——每引擎至多两个处理器触发器：
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
