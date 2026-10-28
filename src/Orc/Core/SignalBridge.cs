using Orc.Core.Model;

namespace Orc.Core;

/// <summary>
/// 信号端点预制体（A2）：一次**订阅**＝一个端点实体。
/// <para>
/// 实体本身没有成员——成员在每个实例上按订阅者名**运行期注册**（R1e），然后挂到
/// <c>engine#&lt;信号&gt;</c> 成员上。于是"谁订阅了什么"在审查图里是**一条条可反查的边**
/// （<see cref="AuditGraph.EdgesInto"/>），不存在"总线订阅表"这个第二读面。
/// </para>
/// </summary>
public static class SignalEndpointPrefab
{
    /// <summary>端点预制体 id（运行期层；宿主需求 None——它不对应任何卡牌／引擎类型）。</summary>
    public const string PrefabId = "legacy.signal.endpoint";

    /// <summary>端点实体的来源标注（仅用于诊断标注）。</summary>
    public const string Origin = "signal-bridge";

    /// <summary>创建端点预制体声明（无成员；成员按实例注册）。</summary>
    public static PrefabDefinition Create()
        => new(PrefabId, EntityKind.Component, layer: PrefabLayer.Runtime);
}

/// <summary>
/// 生产发射与生产订阅（A2）：<c>Emit</c>＝对 <c>engine#&lt;信号&gt;</c> 成员的调用，
/// <c>Subscribe</c>＝往该成员上挂一条边，<c>UnmountOwner</c>＝按所有者撤销边（句柄级精确撤销）。
/// <para>
/// **与旧总线的行为等价项**（逐条对齐，见 PROGRESS §十六）：
/// ①更新条目（<c>source="bus"</c>、keywords＝[信号名]）先写、后通知外部观察者、再执行订阅者；
/// ②订阅者**各自拿到一份载荷副本**（原载荷的浅拷贝，不共享数据面）；
/// ③订阅者异常（取消类除外）被隔离记录进发射者流并继续，取消类原样穿透；
/// ④挂载／卸载留痕与旧总线逐字一致（<c>source="bus"</c>、keywords＝[动作, 展示名, 各 hook…]）。
/// </para>
/// <para>
/// **位次映射**：旧挂载优先级（<see cref="UpdatePriorities"/>）在新模型里没有独立概念，本桥把它折进
/// **边的声明序**（<c>DeclarationOrder(priority, 挂载序, 0)</c>）——与旧总线的排序键同构，
/// 因此"优先级升序→先挂先执行"逐项不变，且执行序仍是"由边的位次推出"（R1g 的形态不被破坏）。
/// </para>
/// </summary>
internal sealed class SignalBridge
{
    /// <summary>一次发射的原始载荷快照（AsyncLocal；订阅者各自取副本，嵌套发射逐层恢复）。</summary>
    private static readonly AsyncLocal<IDictionary<string, object?>?> CapturedPayload = new();

    private readonly LogicEngine _engine;
    private readonly Dictionary<object, List<SubscriptionRecord>> _byOwner = new(ReferenceEqualityComparer.Instance);
    private readonly List<SubscriptionRecord> _records = new();
    private long _mountSequence;

    internal SignalBridge(LogicEngine engine) => _engine = engine;

    /// <summary>
    /// 发射一个信号：写更新条目 → 通知外部观察者 → 执行 <c>engine#&lt;信号&gt;</c> 成员的边集合。
    /// 未声明的信号名在本桥＝**运行期注册**（R1e）后立即发射——自由字符串不再有"绕过成员表"的通路。
    /// <para>
    /// **执行会话（b4 修）**：成员执行期间必须有一个 <see cref="ExecutionFrame"/> 在场，否则
    /// <see cref="Context.Engine"/>／<see cref="Context.Stopped"/>／<see cref="Context.Interrupted"/> 三个读面全部退化
    /// （<c>Engine＝null</c>、<c>Stopped</c> 恒 false）。本帧的父帧＝发射时所在会话
    /// （嵌套发射因此自动挂到调用方树下）。
    /// </para>
    /// </summary>
    internal async Task EmitAsync(
        string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updateType);
        if (string.IsNullOrWhiteSpace(updateType))
        {
            throw new ArgumentException("更新字符串不能为空或纯空白。", nameof(updateType));
        }

        ct.ThrowIfCancellationRequested(); // 已取消：不写条目、不广播

        var capturedStream = ExecutionFrame.Current?.Stream ?? _engine.RootStream; // 发射时捕获一次、整轮固定

        // 更新条目：与旧总线同一形态（source="bus"；message＝信号名；keywords＝恰单元素 [信号名]；level=Info）。
        capturedStream.WriteUpdate("bus", updateType, LogLevel.Info, new[] { updateType }, payload);

        // S5：外部通道通知（桥 → 订阅回调；各通道异常隔离、取消类穿透；先于成员执行）
        await _engine.NotifyExternalObservers(updateType, payload, capturedStream, ct).ConfigureAwait(false);

        var runtime = _engine.SignalRuntime;
        var member = runtime.IsSignal(updateType) ? runtime.RequireSignal(updateType) : runtime.RegisterSignal(updateType);

        var previous = CapturedPayload.Value;
        CapturedPayload.Value = payload is null ? null : new Dictionary<string, object?>(payload);
        var parent = ExecutionFrame.Current;

        // 执行流**沿用发射者的流**（不另开子流）：信号是高频路径，每个信号一建流会让审查树按信号数膨胀，
        // 而"哪次发射产生哪些条目"已由帧链＋条目的 `Message`（信号名）表达。子流留给"另起执行面"的入口
        // （`RunMemberAsync` 的建流分支）——那里一次调用对应一棵真正的子树。
        var frame = new ExecutionFrame(parent, capturedStream, updateType, _engine)
        {
            // R9：**入口从载荷写入**（旧语义"由执行链在进入事件前写入数据面"）。帧间只做继承，
            // 首帧不写就会恒 null——这是 §28.5 第 1 条（`ctx.TriggerCard` 语义消失）的根因。
            TriggerCard = ResolveTriggerCard(payload) ?? parent?.TriggerCard,
        };
        ExecutionFrame.Current = frame;
        try
        {
            var ctx = new Context(CapturedPayload.Value);
            ctx.AttachFrame(frame);
            frame.AttachContext(ctx);
            await runtime.Engine
                .ExecuteMemberAsync(member, runtime.Engine.AddressOf(member.Name), ctx, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ExecutionFrame.Current = parent;
            CapturedPayload.Value = previous;
        }
    }

    /// <summary>
    /// 从信号载荷解析触发者卡牌（<see cref="TriggerPayloadKeys.TriggerCard"/>）：**只接受 Card／null**，
    /// 其它类型一律视为无触发者（结构性错误由业务侧判定，不在协议层猜）。
    /// </summary>
    private static Orc.Cards.Card? ResolveTriggerCard(IReadOnlyDictionary<string, object?>? payload)
    {
        if (payload is null
            || !payload.TryGetValue(TriggerPayloadKeys.TriggerCard, out var value)
            || value is not Orc.Cards.Card card)
        {
            return null;
        }

        return card;
    }

    /// <summary>
    /// 订阅：为一个订阅者建一个端点实体＋一条运行期成员，再把该成员挂到 <paramref name="signal"/> 上。
    /// <para>
    /// 订阅体是**成员 handler**（<see cref="KernelHandler"/>：视图＋ctx→Task），与组件边同一形态；
    /// 强类型 C# 处理器经 <see cref="MemberEdges.For{TView}(Func{TView, Context, CancellationToken, Task})"/> 包一层即可。
    /// </para>
    /// </summary>
    /// <param name="signal">信号名（<c>engine#&lt;信号&gt;</c> 的成员名段）。</param>
    /// <param name="displayName">订阅者名（＝端点成员名；审查面/留痕共用）。</param>
    /// <param name="priority">旧挂载优先级（折进边的声明序第一键，见类型注释）。</param>
    /// <param name="owner">所有者（引用相等卸载的依据；可为 null＝不可按所有者卸载）。</param>
    /// <param name="edge">订阅体。</param>
    internal void SubscribeSignal(
        string signal,
        string displayName,
        int priority,
        object? owner,
        OwnedEdge edge)
        => Attach(signal, displayName, priority, owner, edge, registerHook: true);

    /// <summary>
    /// 往**任意引擎成员**上挂一条外来边（信号＝订阅；流程成员＝检查位／守卫）。
    /// 返回的句柄 Dispose＝撤销该边并销毁端点（幂等）。
    /// </summary>
    /// <param name="memberName">目标成员名（<c>engine#&lt;成员名&gt;</c> 的成员名段）。</param>
    /// <param name="displayName">来源名（＝端点成员名；留痕/审查共用）。</param>
    /// <param name="priority">位次优先级（折进边的声明序第一键）。</param>
    /// <param name="owner">所有者（引用相等卸载的依据；可为 null＝只经返回句柄撤销）。</param>
    /// <param name="edge">边体。</param>
    /// <param name="registerHook">是否把成员名登记进 hook 词汇表（信号＝true；流程成员＝false）。</param>
    internal IDisposable Attach(
        string memberName,
        string displayName,
        int priority,
        object? owner,
        OwnedEdge edge,
        bool registerHook = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(edge);

        var runtime = _engine.SignalRuntime;
        var endpoint = runtime.CreateEntity(SignalEndpointPrefab.PrefabId, DescribeOwner(owner, displayName));
        endpoint.Bound = owner;

        var member = endpoint.RegisterMember(new MemberDefinition(
            displayName,
            ViewSpec.Empty,
            MemberVisibility.Callable | MemberVisibility.Mountable,
            new[] { edge },
            layer: PrefabLayer.Runtime,
            declaredBy: SignalEndpointPrefab.PrefabId));

        var sequence = _mountSequence++;
        var record = new SubscriptionRecord(displayName, owner, memberName, priority, sequence, endpoint);

        if (registerHook)
        {
            _engine.Hooks.Register(memberName); // S-C1/S-C2：hook 首次出现即登记进引擎词汇表（对局内固化）
        }

        // 未声明的信号**不在订阅时注册**：保留"未对应信号成员＝图缺口"的可见性（审查面据此暴露"这个名字没人声明过"）。
        // 注册发生在首次发射（R1e）——发射前它只是一个"边挂在尚不存在的成员上"的悬空引用。
        var source = displayName.EndsWith("/dispose", StringComparison.Ordinal)
            ? EdgeSource.FrameworkDisposal
            : EdgeSource.Explicit;

        // 位次＝（旧挂载优先级，挂载序）：与旧总线的排序键**同构**（数值小者先→先挂先执行）。
        // 之所以落在"声明序"位次上：这是唯一支持有序插入的位次；追加序只能追加、无法复现优先级。
        var table = runtime.Engine.EdgesFor(memberName);
        var handle = table.Attach(
            endpoint,
            member,
            source,
            EdgeOrderSource.Declared,
            new DeclarationOrder(priority, (int)sequence, 0),
            $"engine#{memberName}");

        endpoint.Track(handle);
        record.Handles.Add(handle);

        if (owner is not null)
        {
            if (!_byOwner.TryGetValue(owner, out var list))
            {
                list = new List<SubscriptionRecord>();
                _byOwner[owner] = list;
            }

            list.Add(record);
        }

        _records.Add(record);

        WriteTrace("mount", $"已挂载订阅 '{displayName}'（更新：{memberName}）。", displayName, new[] { memberName });

        return new SubscriptionLease(this, record);
    }

    /// <summary>按所有者卸载（引用相等；幂等——未命中＝无操作、不写留痕）。</summary>
    internal void UnmountOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_byOwner.TryGetValue(owner, out var list) || list.Count == 0)
        {
            return;
        }

        foreach (var record in list.ToArray())
        {
            Detach(record);
            WriteTrace(
                "unmount",
                $"已卸载订阅 '{record.DisplayName}'（更新：{record.Signal}）。",
                record.DisplayName,
                new[] { record.Signal });
        }
    }

    /// <summary>当前全部订阅端点（订阅序；审查面投影用）。</summary>
    internal IReadOnlyList<SignalEndpointInfo> Endpoints
    {
        get
        {
            var result = new SignalEndpointInfo[_records.Count];
            for (var i = 0; i < _records.Count; i++)
            {
                result[i] = new SignalEndpointInfo(
                    _records[i].DisplayName, _records[i].Owner, new[] { _records[i].Signal },
                    _records[i].Priority, _records[i].Sequence);
            }

            return result;
        }
    }

    /// <summary>订阅体内使用的载荷副本（与旧总线的订阅者载荷隔离一致；无发射在途＝null）。</summary>
    internal static IDictionary<string, object?>? CapturedPayloadCopy()
        => CapturedPayload.Value is { } captured ? new Dictionary<string, object?>(captured) : null;

    /// <summary>撤销一条订阅（句柄级；端点实体一并销毁）。</summary>
    private void Detach(SubscriptionRecord record)
    {
        foreach (var handle in record.Handles)
        {
            handle.Revoke();
        }

        record.Handles.Clear();
        _records.Remove(record);

        if (record.Owner is not null
            && _byOwner.TryGetValue(record.Owner, out var list))
        {
            list.Remove(record);
            if (list.Count == 0)
            {
                _byOwner.Remove(record.Owner);
            }
        }

        record.Endpoint.Destroy();
    }

    /// <summary>写一条订阅动作留痕（与旧总线逐字一致：source="bus"、Info 级、目标流＝当前执行者流（无则总流））。</summary>
    private void WriteTrace(string actionKeyword, string message, string displayName, IReadOnlyList<string> hooks)
    {
        var keywords = new List<string>(hooks.Count + 2) { actionKeyword, displayName };
        keywords.AddRange(hooks);

        var stream = ExecutionFrame.Current?.Stream ?? _engine.RootStream;
        stream.WriteLog("bus", message, LogLevel.Info, keywords);
    }

    /// <summary>端点实体名（与旧审查面投影的"来源实体名"一致：效果名／卡名／订阅者名／类型名）。</summary>
    private static string DescribeOwner(object? owner, string displayName)
        => owner switch
        {
            Orc.Cards.Card card => card.Name,
            null => displayName,
            _ => owner.GetType().Name,
        };

    /// <summary>一次订阅（端点实体＋边句柄）。</summary>
    private sealed class SubscriptionRecord
    {
        internal SubscriptionRecord(
            string displayName, object? owner, string signal, int priority, long sequence, OrchestrationEntity endpoint)
        {
            DisplayName = displayName;
            Owner = owner;
            Signal = signal;
            Priority = priority;
            Sequence = sequence;
            Endpoint = endpoint;
        }

        internal string DisplayName { get; }

        internal object? Owner { get; }

        internal string Signal { get; }

        /// <summary>旧挂载优先级（执行序的第一排序键）。</summary>
        internal int Priority { get; }

        /// <summary>挂载序（同优先级内的次序）。</summary>
        internal long Sequence { get; }

        internal OrchestrationEntity Endpoint { get; }

        internal List<MountHandle> Handles { get; } = new();
    }

    /// <summary>一条外来边的撤销句柄（Dispose＝撤销；幂等）。</summary>
    private sealed class SubscriptionLease : IDisposable
    {
        private readonly SignalBridge _bridge;
        private SubscriptionRecord? _record;

        internal SubscriptionLease(SignalBridge bridge, SubscriptionRecord record)
        {
            _bridge = bridge;
            _record = record;
        }

        public void Dispose()
        {
            if (_record is { } record)
            {
                _record = null;
                _bridge.Detach(record);
            }
        }
    }
}

/// <summary>一条订阅在审查面的来源身份（成员名＝订阅者名、来源对象＝所有者）。</summary>
/// <param name="DisplayName">订阅者名（端点成员名）。</param>
/// <param name="Owner">所有者对象（审查面按引用比对）。</param>
/// <param name="Hooks">目标信号（单元素；保留复数形态供审查投影沿用）。</param>
/// <param name="Priority">旧挂载优先级（执行序的第一排序键）。</param>
/// <param name="Sequence">订阅序（同优先级内的次序）。</param>
internal sealed record SignalEndpointInfo(
    string DisplayName, object? Owner, IReadOnlyList<string> Hooks, int Priority, long Sequence);
