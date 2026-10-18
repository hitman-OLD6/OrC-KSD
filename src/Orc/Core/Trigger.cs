namespace Orc.Core;

/// <summary>触发器种类：主动（默认）／被动（S3 起：声明 hooks 后经总线挂载、响应更新激活；主动不可挂载）。</summary>
public enum TriggerKind
{
    /// <summary>主动触发器（默认；由编排/动作直接调用；不参与挂载）。</summary>
    Active,

    /// <summary>被动触发器（需显式声明；S3 起声明 hooks 后经总线挂载，被更新激活）。</summary>
    Passive,
}

/// <summary>
/// 触发器（S3 形态）：一个触发器固定绑定一个视图类型（泛型编译期承载「一个触发器一个 ContextView」约束）。
/// 内部维护 band 方案（构造期声明：缺省＝内置默认枚举；显式＝专门枚举）与事件注册表；
/// 事件排序键＝band 值×1000＋band 内优先级＋注册序兜底（仅排列同一触发器内的事件）。
/// S3 追加：hook 声明（构造期；非空即被动语义）、挂载优先级与所有者（均构造期声明；三者仅内部使用、公共面不暴露）。
/// 注册表与挂载状态（MountedBus）为可变面——除事件注册与总线挂载（Mount/UnmountOwner）外无跨调用可变状态。
/// 开放继承（额外注入 handler 途径的基座）：执行与流产出只经公共执行入口；注册/校验只经公共注册面；
/// 子类不得引入新的跨调用可变状态面。
/// 包装器（管道式；加性扩展）：注册项可经 RegisterBeforeWrapper / RegisterAfterWrapper / RegisterReplaceWrapper 挂载包装逻辑
/// （执行时动态解析「最后一个」、栈语义、纯替换）；构造期装配项的注册句柄经 <see cref="InitialRegistrations"/> 供给（使全部注册项可寻址）。
/// 合法性验证（J2：判定器承载）：可选按名绑定「验证判定器」（<see cref="BindValidation"/>；基类默认未绑定＝恒合法）；每次执行固定先调用（空转豁免）。
/// 不合法＝仅本次取消（不绑视图、不执行事件、留痕、不传染嵌套链）；验证通过后的结构性错误（含绑定失败）＝契约兜底（记录＋失败标记＋安全结束）。
/// 触发入口三形态（同一 InvokeAsync 重载族）：①统一入口（本类提供）；②具名重载（作者在具体触发器/子类/调用侧书写，
/// 经手写 Translate 汇入①）；③字典透传（调用方就绪字典直接调用①，零加工）。框架不隐式调用 Translate。
/// </summary>
/// <typeparam name="TView">该触发器唯一的视图类型（作者视图类）。</typeparam>
public class Trigger<TView> : ITriggerMetadata where TView : class
{
    private const int MaxBandValue = 1_000_000;
    private const int PriorityUpperBound = 1000;

    private readonly List<EventEntry> _events = new();
    private readonly List<TriggerRegistration> _initialRegistrations = new();
    private readonly Type? _bandType;
    private readonly string[] _hooks;
    private readonly int _mountPriority;
    private readonly object? _owner;
    private long _seq;
    private long _wrapperSeq;
    private JudicatorBinding? _validationBinding;
    private Func<IReadOnlyList<Ref<Entity>>, Ref<Entity>?>? _validationSubjectProvider;
    private Func<IReadOnlyList<Ref<Entity>>, int?>? _validationPlayerProvider;

    /// <summary>
    /// 以初始化形态构造触发器（名称〔可选〕、Kind〔默认主动〕、band 方案〔默认缺省〕、初始事件集合〔可选〕、
    /// hooks 声明〔S3；可选〕、挂载优先级〔S3；默认 Normal〕、所有者〔S3；可选〕、
    /// 稳定键〔S-C1；可选，定义级身份来源，未声明回退派生〕）。
    /// 初始事件集合与注册 API 为等价通道（逐项走相同装配校验）。
    /// hooks 声明时机仅构造期（无运行期注册 API）：非空即被动语义；每项须非空、非纯空白、不重复（声明顺序保留）。
    /// </summary>
    /// <exception cref="ArgumentNullException">events 集合包含 null 元素。</exception>
    /// <exception cref="ArgumentException">band 方案非法（非枚举）；初始事件与 band 方案不匹配；
    /// hooks 非法（含 null/空白/重复项），或主动触发器（Kind=Active）声明非空 hooks。</exception>
    /// <exception cref="ArgumentOutOfRangeException">初始事件的 band 值/优先级越界。</exception>
    public Trigger(
        string? name = null,
        TriggerKind kind = TriggerKind.Active,
        Type? bandType = null,
        IEnumerable<TriggerEvent<TView>>? events = null,
        IEnumerable<string>? hooks = null,
        int priority = UpdatePriorities.Normal,
        object? owner = null,
        string? stableKey = null)
    {
        Name = name;
        Kind = kind;

        if (stableKey is not null && string.IsNullOrWhiteSpace(stableKey))
        {
            throw new ArgumentException("稳定键不能为空白字符串（未声明请传 null）。", nameof(stableKey));
        }

        HasDeclaredStableKey = stableKey is not null;
        StableKey = stableKey ?? (string.IsNullOrWhiteSpace(name) ? typeof(TView).Name : name!);
        Id = TriggerId.FromKey(StableKey);

        if (bandType is not null && !bandType.IsEnum)
        {
            throw new ArgumentException(
                $"band 方案必须是 C# 枚举类型；'{bandType.FullName}' 不是枚举。", nameof(bandType));
        }

        _bandType = bandType;
        _hooks = ValidateAndCopyHooks(hooks, kind);
        _mountPriority = priority;
        _owner = owner;

        if (events is not null)
        {
            foreach (var item in events)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(events), "初始事件集合不能包含 null 元素。");
                }

                var entry = AddEvent(item.Name, item.Handler, item.Band, item.Priority);
                _initialRegistrations.Add(new TriggerRegistration(this, entry.Seq, item.Name));
            }
        }
    }

    /// <summary>S3 hooks 校验与拷贝（构造期一致性校验）：每项非空、非纯空白、不重复；主动+非空＝拒绝。</summary>
    private static string[] ValidateAndCopyHooks(IEnumerable<string>? hooks, TriggerKind kind)
    {
        if (hooks is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var hook in hooks)
        {
            if (hook is null)
            {
                throw new ArgumentException("hooks 不能包含 null 元素。", nameof(hooks));
            }

            if (string.IsNullOrWhiteSpace(hook))
            {
                throw new ArgumentException("hook 不能为空或纯空白字符串。", nameof(hooks));
            }

            if (list.Contains(hook))
            {
                throw new ArgumentException($"hooks 列表包含重复项 '{hook}'（同一列表中不允许重复）。", nameof(hooks));
            }

            list.Add(hook);
        }

        if (list.Count > 0 && kind == TriggerKind.Active)
        {
            throw new ArgumentException(
                "主动触发器（Kind=Active）不能声明 hooks（矛盾声明：主动触发器不参与挂载）。", nameof(hooks));
        }

        return list.ToArray();
    }

    /// <summary>触发器名称（可选；用于事件流 source 与调试；未命名时 source 以视图类型名退化标识）。</summary>
    public string? Name { get; }

    /// <summary>触发器种类（运行期可读；S3 起与「挂载条件」关联——仅 Passive 且 hooks 非空可挂载）。</summary>
    public TriggerKind Kind { get; }

    /// <summary>
    /// 稳定键（S-C1；定义级身份来源）：作者声明值，未声明时回退派生（展示名，空白再退视图类型名）。
    /// 展示名"同名不消歧"，故须由作者保证"种类"键唯一；回退派生属弱身份（见 <see cref="HasDeclaredStableKey"/>）。
    /// </summary>
    public string StableKey { get; }

    /// <summary>是否由作者显式声明稳定键；false＝弱身份（回退派生，审查链中标注）。</summary>
    public bool HasDeclaredStableKey { get; }

    /// <summary>触发器种类的稳定标识（S-C1；＝<see cref="StableKey"/> 的 64 位稳定哈希；跨对局/跨机器一致）。</summary>
    public TriggerId Id { get; }

    /// <summary>hook 声明（构造期；声明顺序保留；S3 挂载机制内部使用、公共面不暴露）。</summary>
    internal IReadOnlyList<string> Hooks => _hooks;

    /// <summary>挂载优先级声明（构造期；任意 int；S3 挂载机制内部使用、公共面不暴露）。</summary>
    internal int MountPriority => _mountPriority;

    /// <summary>所有者声明（构造期；可选、可为 null；S3 卸载机制内部使用、公共面不暴露）。</summary>
    internal object? Owner => _owner;

    /// <summary>当前挂载的总线（null＝未挂载；实例级状态；仅由总线 Mount/UnmountOwner 读写）。</summary>
    internal Bus? MountedBus { get; set; }

    /// <summary>
    /// 构造期初始事件的注册句柄（与初始事件集合声明序一一对应；只读）。
    /// 用途：使构造期装配的注册项可寻址（供撤销、moding（逻辑替换）等经句柄寻址的场景）；
    /// 「初始事件集合与注册 API 为等价通道」⇒ 两通道注册项皆可寻址、能力面等价。
    /// </summary>
    public IReadOnlyList<TriggerRegistration> InitialRegistrations => _initialRegistrations;

    /// <summary>注册一个不带 band 的事件（落默认区段；仅默认 band 方案可用）。返回注册句柄（S4 加性扩展；可用于 <see cref="Unregister"/> 撤销）。</summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    /// <exception cref="ArgumentException">事件名为空；默认方案下 band 校验不适用项；downstream 含 null/空白/重复项。</exception>
    /// <exception cref="ArgumentOutOfRangeException">优先级越界（须满足 0 ≤ 优先级 &lt; 1000）。</exception>
    /// <param name="downstream">声明的下游触发器稳定键（S-C2 加性；可选；不改运行行为，仅供审查链静态图取边）。</param>
    public TriggerRegistration Register(string name, Func<TView, Context, CancellationToken, Task> handler, int priority = 0, IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null)
    {
        var entry = AddEvent(name, handler, band: null, priority, downstream, seams);
        return new TriggerRegistration(this, entry.Seq, name);
    }

    /// <summary>注册一个带 band 的事件（band 成员须与触发器声明的方案一致；专门方案下必须显式携带）。返回注册句柄（S4 加性扩展；可用于 <see cref="Unregister"/> 撤销）。</summary>
    /// <exception cref="ArgumentNullException">handler 或 band 为 null。</exception>
    /// <exception cref="ArgumentException">事件名为空；band 成员与已声明方案不一致（二选一保护）；downstream 含 null/空白/重复项。</exception>
    /// <exception cref="ArgumentOutOfRangeException">band 值或优先级越界（band 须为自然数且 ≤ 1,000,000）。</exception>
    /// <param name="downstream">声明的下游触发器稳定键（S-C2 加性；可选；不改运行行为，仅供审查链静态图取边）。</param>
    public TriggerRegistration Register(string name, Func<TView, Context, CancellationToken, Task> handler, Enum band, int priority = 0, IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null)
    {
        ArgumentNullException.ThrowIfNull(band);
        var entry = AddEvent(name, handler, band, priority, downstream, seams);
        return new TriggerRegistration(this, entry.Seq, name);
    }

    /// <summary>
    /// 注册一条**效果销毁 handler**（保留槽；E-D1）：保留槽与 band 方案无关，**默认存在**且排序恒在全部普通事件之后
    /// （普通事件按 band 值/优先级排序，保留槽单独成段）；普通 <see cref="Register(string, Func{TView, Context, CancellationToken, Task}, int, IEnumerable{string}?)"/>
    /// 没有入口可写入本槽，故其位置不可被覆写。槽内多事件按 `priority` 升序、注册序兜底；可用 <see cref="Unregister"/> 撤销。
    /// 需要更早执行销毁 handler 的效果，请改用普通 band 注册（自选区段）。
    /// </summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    /// <exception cref="ArgumentException">事件名为空；downstream 含 null/空白/重复项。</exception>
    /// <exception cref="ArgumentOutOfRangeException">优先级越界（须满足 0 ≤ 优先级 &lt; 1000）。</exception>
    public TriggerRegistration RegisterDisposal(
        string name,
        Func<TView, Context, CancellationToken, Task> handler,
        int priority = 0,
        IEnumerable<string>? downstream = null, IEnumerable<string>? seams = null)
    {
        var entry = AddEvent(name, handler, band: null, priority, downstream, isDisposalSlot: true);
        return new TriggerRegistration(this, entry.Seq, name);
    }

    /// <summary>
    /// 撤销一条注册（S4 加性扩展：注册项移除能力；对 S2 注册面的受控补充）。
    /// 语义：句柄指向本触发器且对应注册项仍存在 → 移除并返回 true；重复撤销（条目已移除）/句柄不属于本触发器 → 幂等无操作、返回 false、不抛错。
    /// 快照语义：执行期间的撤销不影响本轮已开始的迭代（与注册侧一致）；被撤销条目不再参与后续执行。
    /// 注册项撤销时其 moding（逻辑替换）项随之失效（避免悬空）：不再被解析选用。
    /// </summary>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    public bool Unregister(TriggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!ReferenceEquals(registration.TriggerRef, this))
        {
            return false; // 非本触发器句柄：幂等无操作
        }

        var index = _events.FindIndex(e => e.Seq == registration.Seq);
        if (index < 0)
        {
            return false; // 已撤销/不存在：幂等无操作
        }

        var entry = _events[index];
        _events.RemoveAt(index);
        entry.ClearWrappers(); // 注册项撤销 ⇒ 其包装器随之失效（避免悬空）
        return true;
    }

    /// <summary>
    /// 注册一条 moding（逻辑替换）项：以 <paramref name="target"/> 句柄锚定目标注册项，之后该注册项执行时改用本条 delegate 逻辑
    /// （纯替换——moding 生效时原逻辑不执行；机制不提供任何指向原逻辑的调用途径：原逻辑不作参数暴露、无 next/proceed 式入口）。
    /// 解析语义：每次执行目标 handler 时动态解析「当前最后一个未注销的 moding 项」并采用（栈语义——栈序＝moding 注册序，与事件名/band/优先级无关）；
    /// 注销后回退上一项，全部注销回退原逻辑。装配期（子类/构建时）与运行时为同一注册面（仅调用时点区分）。
    /// 约束：moding 逻辑与 handler 本体一致，为单一处理单元（读取/计算/数据改写/记录/判定）；
    /// 禁止在 handler（含其 moding 逻辑）内进行流程编排（调用其它流程/触发器、串联多个处理单元、组织多步骤序列）——编排发生在流程层。
    /// 无效目标（句柄不属本触发器/目标注册项已撤销）＝幂等无操作、不生效（返回 null、不抛错）。
    /// 快照语义：执行期间的增删不打断已开始的迭代与进行中 handler 的已解析选择；被注销项不再被选用、新增项自下一次解析点生效。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 或 moding 为 null。</exception>
    /// <summary>注册前置包装器（挂到 handler 条目）：核心前 LIFO 执行；可就地改写 Context，不可替换入参引用、不可短路。</summary>
    public TriggerWrapperRegistration RegisterBeforeWrapper(
        TriggerRegistration target, Func<TView, Context, CancellationToken, Task> wrapper)
        => RegisterWrapperCore(target, WrapperPhase.Before, seam: null, wrapper);

    /// <summary>注册前置包装器（挂到 handler 的具名动作缝）：该缝被 Context.SeamAsync 调用时按 LIFO 执行。</summary>
    public TriggerWrapperRegistration RegisterBeforeWrapper(
        TriggerRegistration target, string seam, Func<TView, Context, CancellationToken, Task> wrapper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seam);
        return RegisterWrapperCore(target, WrapperPhase.Before, seam, wrapper);
    }

    /// <summary>注册后置包装器（挂到 handler 条目）：核心后 FIFO 执行；触发器侧为尾部副作用。</summary>
    public TriggerWrapperRegistration RegisterAfterWrapper(
        TriggerRegistration target, Func<TView, Context, CancellationToken, Task> wrapper)
        => RegisterWrapperCore(target, WrapperPhase.After, seam: null, wrapper);

    /// <summary>注册替换包装器：与 handler 同签名；只替换核心段，最后一个未注销者生效。</summary>
    public TriggerWrapperRegistration RegisterReplaceWrapper(
        TriggerRegistration target, Func<TView, Context, CancellationToken, Task> wrapper)
        => RegisterWrapperCore(target, WrapperPhase.Replace, seam: null, wrapper);

    private TriggerWrapperRegistration RegisterWrapperCore(
        TriggerRegistration target, WrapperPhase phase, string? seam,
        Func<TView, Context, CancellationToken, Task> wrapper)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(wrapper);

        if (!ReferenceEquals(target.TriggerRef, this))
        {
            throw new ArgumentException("包装器目标句柄不属于本触发器（跨触发器挂载被拒绝）。", nameof(target));
        }

        var entry = FindEntry(target.Seq)
            ?? throw new ArgumentException("包装器目标注册项已撤销（不存在）。", nameof(target));

        if (seam is not null && !entry.HasSeam(seam))
        {
            throw new ArgumentException(
                $"动作缝 '{seam}' 未在注册项 '{target.Name}' 上声明（未声明缝的挂载被拒绝）。", nameof(seam));
        }

        var seq = _wrapperSeq++;
        entry.AddWrapper(phase, seam, wrapper, seq);
        return new TriggerWrapperRegistration(this, target.Seq, seq, phase, seam);
    }

    /// <summary>注销一条包装器（句柄即权限）；句柄无效/已撤销＝幂等无操作。</summary>
    public bool UnregisterWrapper(TriggerWrapperRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!ReferenceEquals(registration.TriggerRef, this))
        {
            return false;
        }

        var entry = FindEntry(registration.TargetSeq);
        return entry is not null && entry.RemoveWrapper(registration.WrapperSeq);
    }

    /// <summary>
    /// 合法性验证（公开验证入口；J2：经绑定验证判定器执行——未绑定＝恒合法）。
    /// 接收本次触发数据中的原始引用列表（data 第一层引用类值；按引用相等去重；保持插入序；无引用＝空列表），
    /// 返回本次触发是否合法（true＝合法；＝<see cref="EvaluateValidation"/> 的布尔消费面）。
    /// 调用时点：每次 <see cref="InvokeAsync"/> 内部固定先调用（空转检查之后、ctx/视图绑定之前）；
    /// 外部亦可在触发前直接调用（同一入口、无缓存/无短路——每次完整调用；内外判定源唯一——同一绑定判定器、每次实时执行）。
    /// 契约：只读、无副作用；应保持轻量（每次触发均被调用）。
    /// 返回 false＝本次触发被拒绝：仅本次取消（不绑视图、不执行事件；写 validation:rejected 留痕、流标记
    /// <see cref="ExecutionOutcome.ValidationRejected"/>、不传染嵌套链）。
    /// 判定器抛出异常（取消类除外）＝按契约兜底处理（捕获＋记录＋流标记 <see cref="ExecutionOutcome.ContractFailure"/>＋安全结束，不外传）。
    /// </summary>
    /// <param name="refs">原始引用列表（data 第一层引用类值、按引用相等去重、保持插入序；无引用＝空列表）。</param>
    public bool Validate(IReadOnlyList<Ref<Entity>> refs) => EvaluateValidation(refs).IsValid;

    /// <summary>
    /// 验证判定求值（取数形态；公开面）：经绑定验证判定器执行，返回「合法性＋（不合法时）拒绝类别」。
    /// 未绑定＝恒合法（<see cref="ValidationVerdict.Valid"/>——保持既有缺省语义）。
    /// 与 <see cref="Validate"/> 同一绑定、同一调用路径（每次实时执行、无缓存短路）；供入口映射按取数语义消费
    /// （类别缺失/不可辨识时由消费侧降级为一般性失败原因——不伪造具体类别）。
    /// 被判定对象经绑定登记的提供器（<see cref="BindValidation"/> 的 subjectProvider）在调用时求取。
    /// </summary>
    /// <param name="refs">原始引用列表（data 第一层引用类值、按引用相等去重、保持插入序；无引用＝空列表）。</param>
    /// <returns>验证判定结果（合法性＋拒绝类别）。</returns>
    /// <exception cref="ArgumentNullException">refs 为 null。</exception>
    public ValidationVerdict EvaluateValidation(IReadOnlyList<Ref<Entity>> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);

        if (_validationBinding is null)
        {
            return ValidationVerdict.Valid; // 未绑定＝恒合法（缺省语义）
        }

        var subject = _validationSubjectProvider?.Invoke(refs);
        return _validationBinding.InvokeValidation(refs, subject, _validationPlayerProvider?.Invoke(refs));
    }

    /// <summary>
    /// 绑定验证判定器（J2；装配期一次建立、生命周期内不可变——不提供解绑/重绑/运行期换绑通道；
    /// 改变验证行为的唯一通道＝moding 改写（全局生效、注销回退））。
    /// 绑定标的经「按名解析」获得（对局路径＝注册表条目等价句柄；独立构造路径＝内置默认判定器实例；
    /// 解析动作即校验——未注册名由解析调用点在装配期 fail-fast）。
    /// 未绑定＝恒合法（<see cref="Validate"/> 恒真、<see cref="EvaluateValidation"/> 恒合法）。
    /// </summary>
    /// <param name="binding">判定器绑定（<see cref="JudicatorBinding.FromRegistration"/>／<see cref="JudicatorBinding.FromStandalone"/>）。</param>
    /// <param name="subjectProvider">被判定对象提供器（可选；从当次调用输入提取被判定对象引用——如固定卡引用或 refs 首位；
    /// 缺省＝无被判定对象（null））。</param>
    /// <exception cref="ArgumentNullException">binding 为 null。</exception>
    /// <exception cref="InvalidOperationException">重复绑定被拒绝（绑定＝装配期一次性声明动作——fail-fast，不幂等宽容）。</exception>
    public void BindValidation(
        JudicatorBinding binding,
        Func<IReadOnlyList<Ref<Entity>>, Ref<Entity>?>? subjectProvider = null,
        Func<IReadOnlyList<Ref<Entity>>, int?>? playerProvider = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_validationBinding is not null)
        {
            throw new InvalidOperationException(
                $"触发器 '{DisplayName}' 已绑定验证判定器 '{_validationBinding.Name}'（重复绑定被拒绝——绑定＝装配期一次建立、生命周期内不可变）。");
        }

        _validationBinding = binding;
        _validationSubjectProvider = subjectProvider;
        _validationPlayerProvider = playerProvider;
    }

    /// <summary>
    /// 统一入口（框架提供；规范收敛点）：以就绪数据创建本次执行 ctx（沿用「入料拷贝一次」语义；data 为 null 视作空载体）
    /// → 合法性验证（每次固定先调用：空转检查之后、ctx/视图绑定之前；经绑定验证判定器执行；输入＝本次触发数据第一层引用收集）
    /// → 绑定新建视图会话 → 按排序键顺序执行事件链 → 返回事件流。本身不触发转接（不隐式调用 Translate）。
    /// 嵌套调用自动感知执行栈：子流自动挂载到触发者流（无触发者时挂载到传入引擎的总流）；
    /// 已中断状态下的链上新执行空转（不执行任何事件、不进行视图绑定、不调用验证、正常返回空流）。
    /// 验证拒绝（<see cref="Validate"/> 返回 false）＝仅本次取消：不绑视图、不执行事件；写留痕（validation:rejected）并标记
    /// <see cref="ExecutionOutcome.ValidationRejected"/>；不传染嵌套链（区别于 Interrupt 的链级停止）。
    /// 验证通过后的执行错误（含绑定失败：必填缺失、视图声明非法等；及验证自身异常）被捕获处理：
    /// 记录（Error 级；source＝触发器展示名；keywords 含 exception:{类型名}）＋安全结束（返回流；流标记
    /// <see cref="ExecutionOutcome.ContractFailure"/>），不外传；取消类异常穿透上抛（最外层拿不到流）。
    /// 事件内业务异常被隔离记录并继续（结局仍为 <see cref="ExecutionOutcome.Normal"/>——记录与标记分离）。
    /// </summary>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    /// <exception cref="OperationCanceledException">取消类异常穿透上抛（不隔离）。</exception>
    public async Task<EventStream> InvokeAsync(
        LogicEngine engine, IDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var parent = ExecutionFrame.Current;
        var label = DisplayName;
        var stream = new EventStream();
        stream.AttachTo(
            parent?.Stream ?? engine.RootStream,
            parent is null ? label : ExecutionFrame.BuildSource(parent.TriggerLabel, parent.CurrentEventName));

        var frame = new ExecutionFrame(parent, stream, label, engine);
        ExecutionFrame.Current = frame;
        try
        {
            if (ExecutionFrame.IsChainInterrupted(parent))
            {
                return stream; // 空转：不执行任何事件、不进行视图绑定、不调用验证
            }

            // 验证（空转检查之后、ctx/视图绑定之前；经绑定验证判定器执行——未绑定＝恒合法；每次触发固定先调用）：
            // 输入＝本次触发数据第一层引用收集（去重、插入序；无引用＝空列表）。
            if (!Validate(CollectReferences(data)))
            {
                // 不合法：仅本次取消——不绑视图、不执行事件；写留痕、标记、返回流（不传染嵌套链）。
                stream.Write(
                    LogEntryKind.Log,
                    LogLevel.Warning,
                    label,
                    "触发被验证拒绝（Validate 返回 false）：本次取消（不绑定视图、不执行事件）。",
                    new[] { "validation:rejected" },
                    data: null);
                stream.Outcome = ExecutionOutcome.ValidationRejected;
                return stream;
            }

            // 效果运行时（E1 加性）：若本触发器的所有者是**带宿主的效果实例**（动态/预制体效果），
            // 把宿主卡注入视图数据——供动态（csx）handler 取得"施动卡"（CardEventView 的 [Optional][Read] Host）。
            // 入料拷贝一次：复制字典后再注入，不污染事件流载荷。
            var effectiveData = data;
            if (_owner is Orc.Cards.Effect ownerEffect
                && ownerEffect.HasHost
                && ownerEffect.HostOrNull is { } hostCard)
            {
                var enriched = data is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(data);
                enriched["Host"] = hostCard;
                effectiveData = enriched;
            }

            // 验证通过 ⇒ 执行：绑定新建视图会话 → 执行事件链；结构性错误由下方契约兜底捕获。
            var ctx = new Context(effectiveData);
            ctx.AttachFrame(frame);
            frame.AttachContext(ctx);

            var view = ContextViewBinder.Create<TView>(ctx);
            try
            {
                await RunEventsAsync(view, ctx, frame, ct);
            }
            finally
            {
                ((IContextView)(object)view).Seal();
            }

            return stream;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消类异常不隔离、穿透上抛（含验证/绑定/执行阶段）
        }
        catch (Exception ex)
        {
            // 契约兜底：验证/绑定/执行阶段的结构性错误（绑定失败、视图声明非法、验证自身异常等）
            // ——记录（与既有隔离记录同构）＋失败标记＋安全结束（返回流、不外传）。
            WriteContractFailure(stream, label, ex);
            stream.Outcome = ExecutionOutcome.ContractFailure;
            return stream;
        }
        finally
        {
            ExecutionFrame.Current = parent;
        }
    }

    /// <summary>
    /// 从本次触发数据（data 载体）收集原始引用（验证输入；收集规则）：
    /// 仅 data 第一层值（不递归进入嵌套字典/列表——引用需平铺放置为显式契约）；
    /// 凡引用类对象（实现 <see cref="IRefInfo"/> 的类型）一律收集、不限目标类型（库内唯一引用实例化点＝<see cref="Entity.Ref"/>，实例类型为基类视角的 Ref&lt;Entity&gt;）；
    /// 按引用相等去重；保持 data 插入序；无引用＝空列表。
    /// </summary>
    private static List<Ref<Entity>> CollectReferences(IDictionary<string, object?>? data)
    {
        var refs = new List<Ref<Entity>>();
        if (data is null)
        {
            return refs;
        }

        foreach (var value in data.Values)
        {
            if (value is Ref<Entity> reference && !refs.Contains(reference, ReferenceEqualityComparer.Instance))
            {
                refs.Add(reference);
            }
        }

        return refs;
    }

    /// <summary>契约兜底记录（与事件隔离记录同构）：Error 级；source＝触发器展示名（纯展示名、无事件上下文）；
    /// keywords 含 exception:{类型名}；data 含 exceptionType/message。</summary>
    private static void WriteContractFailure(EventStream stream, string label, Exception ex)
    {
        stream.Write(
            LogEntryKind.Log,
            LogLevel.Error,
            label,
            ex.Message,
            new[] { $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
    }

    private async Task RunEventsAsync(TView view, Context ctx, ExecutionFrame frame, CancellationToken ct)
    {
        var snapshot = SnapshotSorted();
        foreach (var item in snapshot)
        {
            if (frame.Stopped || frame.Interrupted)
            {
                break;
            }

            frame.CurrentEventName = item.Name;
            var before = item.SnapshotBefore();
            var after = item.SnapshotAfter();
            var replace = item.LastReplaceWrapper;

            try
            {
                frame.SeamExecutor = (seam, seamCt) => item.RunSeamAsync(view, ctx, seam, seamCt);

                foreach (var wrapper in before)
                {
                    await wrapper(view, ctx, ct);
                }

                if (replace is not null)
                {
                    await replace(view, ctx, ct);
                }
                else
                {
                    await item.Handler(view, ctx, ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteEventFailure(frame, item.Name, ex);
                continue;
            }
            finally
            {
                frame.SeamExecutor = null;
            }

            foreach (var wrapper in after)
            {
                try
                {
                    await wrapper(view, ctx, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WriteEventFailure(frame, item.Name, ex);
                }
            }
        }
    }

    private static void WriteEventFailure(ExecutionFrame frame, string eventName, Exception ex)
    {
        frame.Stream.Write(
            LogEntryKind.Log,
            LogLevel.Error,
            ExecutionFrame.BuildSource(frame.TriggerLabel, eventName),
            ex.Message,
            new[] { $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
    }

    /// <summary>按事件注册序查找注册项；不存在＝null（已撤销/非本触发器句柄的静默判定依据）。</summary>
    private EventEntry? FindEntry(long seq)
    {
        var index = _events.FindIndex(e => e.Seq == seq);
        return index < 0 ? null : _events[index];
    }

    private EventEntry AddEvent(
        string name,
        Func<TView, Context, CancellationToken, Task> handler,
        Enum? band,
        int priority,
        IEnumerable<string>? downstream = null,
        IEnumerable<string>? seams = null,
        bool isDisposalSlot = false)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("事件名不能为空（用途：日志与事件流定位）。", nameof(name));
        }

        if (priority < 0 || priority >= PriorityUpperBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority), priority, $"事件内优先级须满足 0 ≤ 优先级 < {PriorityUpperBound}。");
        }

        var bandValue = isDisposalSlot ? 0 : ResolveBandValue(band);
        var entry = new EventEntry(
            name, handler, bandValue, priority, _seq++, ValidateDownstream(downstream),
            ValidateSeams(seams), isDisposalSlot);
        _events.Add(entry);
        return entry;
    }

    /// <summary>downstream 声明校验与拷贝（S-C2；每项非空、非纯空白、不重复；null＝空）。</summary>
    /// <summary>动作缝声明校验与拷贝：每项非空、非纯空白、不重复；null＝空。</summary>
    private static string[] ValidateSeams(IEnumerable<string>? seams)
    {
        if (seams is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var seam in seams)
        {
            if (seam is null)
            {
                throw new ArgumentException("seams 不能包含 null 元素。", nameof(seams));
            }

            if (string.IsNullOrWhiteSpace(seam))
            {
                throw new ArgumentException("seam 不能为空或纯空白字符串。", nameof(seams));
            }

            if (list.Contains(seam))
            {
                throw new ArgumentException($"seams 列表包含重复项 '{seam}'。", nameof(seams));
            }

            list.Add(seam);
        }

        return list.ToArray();
    }

    private static string[] ValidateDownstream(IEnumerable<string>? downstream)
    {
        if (downstream is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var key in downstream)
        {
            if (key is null)
            {
                throw new ArgumentException("downstream 不能包含 null 元素。", nameof(downstream));
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("downstream 不能包含空或纯空白项。", nameof(downstream));
            }

            if (list.Contains(key))
            {
                throw new ArgumentException($"downstream 列表包含重复项 '{key}'。", nameof(downstream));
            }

            list.Add(key);
        }

        return list.ToArray();
    }

    /// <summary>
    /// 事件只读枚举（S-C2 加性面）：返回本触发器全部注册项的信息快照（执行序——排序键序、同键注册序）。
    /// 含运行期注册项与构造期装配项；不含 handler 委托本体（以句柄 <see cref="TriggerEventInfo.Seq"/> 标识）。
    /// </summary>
    public IReadOnlyList<TriggerEventInfo> Events
    {
        get
        {
            var snapshot = SnapshotSorted();
            var result = new TriggerEventInfo[snapshot.Length];
            for (var i = 0; i < snapshot.Length; i++)
            {
                result[i] = snapshot[i].ToInfo();
            }

            return result;
        }
    }

    /// <summary>band 成员解析与二选一保护校验（默认方案／专门方案的判定矩阵）。</summary>
    private int ResolveBandValue(Enum? band)
    {
        if (_bandType is null)
        {
            // 默认方案：省略 band → 落 default 区段；仅接受默认枚举成员。
            if (band is null)
            {
                return 0;
            }

            if (band.GetType() != typeof(DefaultBands))
            {
                throw new ArgumentException(
                    $"触发器未声明专门 band 方案（默认方案），不接受枚举 '{band.GetType().Name}' 的成员；如需自定义区段，请经构造参数 bandType 显式声明专门枚举方案。",
                    nameof(band));
            }
        }
        else
        {
            // 专门方案：必须显式携带本枚举成员。
            if (band is null)
            {
                throw new ArgumentException(
                    $"触发器已声明专门 band 方案 '{_bandType.Name}'，注册必须显式携带该枚举成员（省略 band 被拒绝）。",
                    nameof(band));
            }

            if (band.GetType() != _bandType)
            {
                throw new ArgumentException(
                    $"band 成员所属枚举 '{band.GetType().Name}' 与触发器声明的方案 '{_bandType.Name}' 不一致（他类枚举成员被拒绝）。",
                    nameof(band));
            }
        }

        long value;
        try
        {
            value = Convert.ToInt64(band);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(band), band, $"band 值超出支持范围（须为自然数且 ≤ {MaxBandValue:N0}）。");
        }

        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(band), value, "band 值必须为自然数（≥ 0）。");
        }

        if (value > MaxBandValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(band), value, $"band 值超过上限 {MaxBandValue:N0}（防打包溢出）。");
        }

        return (int)value;
    }

    private EventEntry[] SnapshotSorted()
    {
        var snapshot = _events.ToArray();
        Array.Sort(snapshot, static (a, b) =>
        {
            // E-D1：保留槽单独成段、恒在普通事件之后（与 band 数值无关——位置不可被普通注册覆写）。
            var c = a.IsDisposalSlot.CompareTo(b.IsDisposalSlot);
            if (c != 0)
            {
                return c;
            }

            c = a.SortKey.CompareTo(b.SortKey);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });
        return snapshot;
    }

    /// <summary>source/调试/总线读面用显示名：名称（非空白）或视图类型名（未命名退化）。</summary>
    internal string DisplayName => string.IsNullOrWhiteSpace(Name) ? typeof(TView).Name : Name!;

    // ---------- ITriggerMetadata（S-C3 显式实现：内部只读面经非泛型接口对外暴露） ----------

    /// <inheritdoc />
    string ITriggerMetadata.DisplayName => DisplayName;

    /// <inheritdoc />
    IReadOnlyList<string> ITriggerMetadata.HookNames => _hooks;

    /// <inheritdoc />
    object? ITriggerMetadata.Owner => _owner;

    /// <inheritdoc />
    int ITriggerMetadata.MountPriority => _mountPriority;

    /// <inheritdoc />
    bool ITriggerMetadata.IsMounted => MountedBus is not null;

    private sealed class EventEntry
    {
        private readonly List<WrapperEntry> _beforeWrappers = new();
        private readonly List<WrapperEntry> _afterWrappers = new();
        private readonly List<WrapperEntry> _replaceWrappers = new();
        private readonly Dictionary<string, SeamEntry> _seams;

        internal EventEntry(
            string name,
            Func<TView, Context, CancellationToken, Task> handler,
            int bandValue,
            int priority,
            long seq,
            string[] downstream,
            string[] seams,
            bool isDisposalSlot = false)
        {
            Name = name;
            Handler = handler;
            BandValue = bandValue;
            Priority = priority;
            Seq = seq;
            Downstream = downstream;
            IsDisposalSlot = isDisposalSlot;

            _seams = new Dictionary<string, SeamEntry>(StringComparer.Ordinal);
            foreach (var seam in seams)
            {
                _seams.Add(seam, new SeamEntry(seam));
            }
        }

        internal string Name { get; }

        internal Func<TView, Context, CancellationToken, Task> Handler { get; }

        /// <summary>声明的下游触发器稳定键（S-C2；空数组＝未声明）。</summary>
        internal string[] Downstream { get; }

        /// <summary>是否位于保留槽（效果销毁 handler；恒在普通事件之后执行）。</summary>
        internal bool IsDisposalSlot { get; }

        /// <summary>投影为只读审阅信息（S-C2；含当前 moding 项数）。</summary>
        internal TriggerEventInfo ToInfo() => new(
            Name, BandValue, Priority, Seq, Downstream,
            _beforeWrappers.Count + _afterWrappers.Count + _replaceWrappers.Count,
            _beforeWrappers.Count, _afterWrappers.Count, _replaceWrappers.Count,
            _seams.Count, SumSeamWrapperCount(), IsDisposalSlot);

        internal int BandValue { get; }

        internal int Priority { get; }

        internal long Seq { get; }

        /// <summary>排序键＝band 值×1000＋band 内优先级。</summary>
        internal long SortKey => (long)BandValue * PriorityUpperBound + Priority;

        /// <summary>
        /// 当前生效的 moding（逻辑替换）逻辑：最后一个注册且未注销者；无＝null（回退原 handler）。
        /// 列表按 moding 注册序追加（尾＝最后注册者）；执行时点解析，不做注册期换绑。
        /// </summary>
        /// <summary>是否声明了指定动作缝。</summary>
        internal bool HasSeam(string seam) => _seams.ContainsKey(seam);

        internal Func<TView, Context, CancellationToken, Task>? LastReplaceWrapper
            => _replaceWrappers.Count == 0 ? null : _replaceWrappers[^1].Handler;

        internal void AddWrapper(WrapperPhase phase, string? seam, Func<TView, Context, CancellationToken, Task> handler, long seq)
            => AddWrapperCore(phase, seam, new WrapperEntry(handler, seq));

        /// <summary>按 moding 注册序移除一项；不存在＝false（幂等）。</summary>
        private void AddWrapperCore(WrapperPhase phase, string? seam, WrapperEntry entry)
        {
            switch (phase)
            {
                case WrapperPhase.Before:
                    if (seam is null)
                    {
                        _beforeWrappers.Add(entry);
                    }
                    else
                    {
                        _seams[seam].Add(entry);
                    }

                    break;

                case WrapperPhase.After:
                    _afterWrappers.Add(entry);
                    break;

                case WrapperPhase.Replace:
                    _replaceWrappers.Add(entry);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(phase), phase, "未定义的包装器类别。");
            }
        }

        internal bool RemoveWrapper(long seq)
        {
            if (RemoveFrom(_beforeWrappers, seq)
                || RemoveFrom(_afterWrappers, seq)
                || RemoveFrom(_replaceWrappers, seq))
            {
                return true;
            }

            foreach (var seam in _seams.Values)
            {
                if (seam.Remove(seq))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>清空全部 moding（注册项撤销时调用：moding 随之失效、避免悬空）。</summary>
        internal void ClearWrappers()
        {
            _beforeWrappers.Clear();
            _afterWrappers.Clear();
            _replaceWrappers.Clear();
            foreach (var seam in _seams.Values)
            {
                seam.Clear();
            }
        }

        internal Func<TView, Context, CancellationToken, Task>[] SnapshotBefore()
        {
            var snapshot = _beforeWrappers.ToArray();
            Array.Reverse(snapshot);
            return Array.ConvertAll(snapshot, static w => w.Handler);
        }

        internal Func<TView, Context, CancellationToken, Task>[] SnapshotAfter()
            => Array.ConvertAll(_afterWrappers.ToArray(), static w => w.Handler);

        internal async Task RunSeamAsync(TView view, Context ctx, string seam, CancellationToken ct)
        {
            if (!_seams.TryGetValue(seam, out var entry))
            {
                throw new KeyNotFoundException($"动作缝 '{seam}' 未在注册项 '{Name}' 上声明（调用被拒绝）。");
            }

            foreach (var wrapper in entry.SnapshotBefore())
            {
                await wrapper(view, ctx, ct);
            }
        }

        private int SumSeamWrapperCount()
        {
            var total = 0;
            foreach (var seam in _seams.Values)
            {
                total += seam.Count;
            }

            return total;
        }

        private static bool RemoveFrom(List<WrapperEntry> list, long seq)
        {
            var index = list.FindIndex(w => w.Seq == seq);
            if (index < 0)
            {
                return false;
            }

            list.RemoveAt(index);
            return true;
        }

        private sealed class WrapperEntry
        {
            internal WrapperEntry(Func<TView, Context, CancellationToken, Task> handler, long seq)
            {
                Handler = handler;
                Seq = seq;
            }

            internal Func<TView, Context, CancellationToken, Task> Handler { get; }

            /// <summary>包装器注册序（触发器内唯一，作包装器项身份）。</summary>
            internal long Seq { get; }
        }

        private sealed class SeamEntry
        {
            private readonly List<WrapperEntry> _wrappers = new();

            internal SeamEntry(string name)
            {
                Name = name;
            }

            internal string Name { get; }

            internal int Count => _wrappers.Count;

            internal void Add(WrapperEntry entry) => _wrappers.Add(entry);

            internal bool Remove(long seq)
            {
                var index = _wrappers.FindIndex(w => w.Seq == seq);
                if (index < 0)
                {
                    return false;
                }

                _wrappers.RemoveAt(index);
                return true;
            }

            internal void Clear() => _wrappers.Clear();

            internal Func<TView, Context, CancellationToken, Task>[] SnapshotBefore()
            {
                var snapshot = _wrappers.ToArray();
                Array.Reverse(snapshot);
                return Array.ConvertAll(snapshot, static w => w.Handler);
            }
        }
    }
}

/// <summary>
/// 注册句柄（S4 加性扩展）：<see cref="Trigger{TView}.Register(string, Func{TView, Context, CancellationToken, Task}, int)"/> 的返回值，
/// 标识触发器内的一条注册项，可用作 <see cref="Trigger{TView}.Unregister"/> 的撤销依据。
/// 仅由注册面创建；不含可变状态。
/// </summary>
public sealed class TriggerRegistration
{
    internal TriggerRegistration(object triggerRef, long seq, string name)
    {
        TriggerRef = triggerRef;
        Seq = seq;
        Name = name;
    }

    /// <summary>注册项的事件名（可读标识；不承担唯一键职责——同名事件可存在多条，撤销以句柄为准）。</summary>
    public string Name { get; }

    /// <summary>所属触发器引用（撤销时按引用相等匹配；内部使用）。</summary>
    internal object TriggerRef { get; }

    /// <summary>注册序（触发器内唯一，作为注册项身份；内部使用）。</summary>
    internal long Seq { get; }
}

/// <summary>
/// moding（逻辑替换）注册句柄：<see cref="Trigger{TView}.RegisterReplaceWrapper"/> 成功时返回，
/// 标识某注册项上的一条 moding 项，可用作 <see cref="Trigger{TView}.UnregisterWrapper"/> 的注销依据。
/// 仅由注册面创建；不含可变状态。
/// </summary>
/// <summary>
/// 包装器注册句柄：标识某注册项（或某动作缝）上的一条包装器项，可用作 Trigger.UnregisterWrapper 的注销依据。
/// 仅由注册面创建；不含可变状态。
/// </summary>
public sealed class TriggerWrapperRegistration
{
    internal TriggerWrapperRegistration(
        object triggerRef, long targetSeq, long wrapperSeq, WrapperPhase phase, string? seam)
    {
        TriggerRef = triggerRef;
        TargetSeq = targetSeq;
        WrapperSeq = wrapperSeq;
        Phase = phase;
        Seam = seam;
    }

    internal object TriggerRef { get; }

    internal long TargetSeq { get; }

    internal long WrapperSeq { get; }

    /// <summary>包装器类别（前置／后置／替换）。</summary>
    public WrapperPhase Phase { get; }
    /// <summary>动作缝名（仅前置挂到缝上时非 null）。</summary>
    public string? Seam { get; }

}

/// <summary>
/// 事件注册项的只读审阅信息（S-C2；审查链与枚举面用）：
/// 事件名、band 值与 band 内优先级、注册序（句柄）、声明的下游触发器稳定键、包装器与动作缝计数。
/// 不含 handler 委托本体（委托无稳定身份，身份＝(所属触发器 Id, <see cref="Seq"/>)）。
/// </summary>
public sealed class TriggerEventInfo
{
    internal TriggerEventInfo(
        string name,
        int bandValue,
        int priority,
        long seq,
        IReadOnlyList<string> downstream,
        int wrapperCount,
        int beforeWrapperCount,
        int afterWrapperCount,
        int replaceWrapperCount,
        int seamCount,
        int seamWrapperCount,
        bool isDisposalSlot = false)
    {
        Name = name;
        BandValue = bandValue;
        Priority = priority;
        Seq = seq;
        Downstream = downstream;
        WrapperCount = wrapperCount;
        BeforeWrapperCount = beforeWrapperCount;
        AfterWrapperCount = afterWrapperCount;
        ReplaceWrapperCount = replaceWrapperCount;
        SeamCount = seamCount;
        SeamWrapperCount = seamWrapperCount;
        IsDisposalSlot = isDisposalSlot;
    }

    /// <summary>事件名（日志/定位用；不承担唯一键职责）。</summary>
    public string Name { get; }

    /// <summary>band 值（区段数值）。</summary>
    public int BandValue { get; }

    /// <summary>band 内优先级。</summary>
    public int Priority { get; }

    /// <summary>注册序（触发器内唯一，作事件身份；执行排序兜底键）。</summary>
    public long Seq { get; }

    /// <summary>声明的下游触发器稳定键（空＝未声明）。</summary>
    public IReadOnlyList<string> Downstream { get; }

    /// <summary>当前生效的 moding（逻辑替换）项数（0＝原逻辑）。</summary>
    /// <summary>当前挂载的包装器总数（前置＋后置＋替换）。</summary>
    public int WrapperCount { get; }

    /// <summary>前置包装器数量。</summary>
    public int BeforeWrapperCount { get; }

    /// <summary>后置包装器数量。</summary>
    public int AfterWrapperCount { get; }

    /// <summary>替换包装器数量（&gt;0 时核心 handler 被最后一个替换）。</summary>
    public int ReplaceWrapperCount { get; }

    /// <summary>声明的动作缝数量。</summary>
    public int SeamCount { get; }

    /// <summary>全部动作缝上的前置包装器总数。</summary>
    public int SeamWrapperCount { get; }

    /// <summary>是否位于保留槽（效果销毁 handler；恒在普通事件之后执行）。</summary>
    public bool IsDisposalSlot { get; }
}

/// <summary>
/// 执行帧（内部）：一次执行（顶层或嵌套）的运行期会话。
/// 承担：①当前执行者感知（AsyncLocal 栈；执行会话入/出栈——子流挂载、Interrupt 传播、空转判定共用）；
/// ②检查点状态（Stopped/Interrupted）与 ctx 状态查询面共用的唯一状态源；
/// ③source 构成素材（触发器显示名＋当前事件名）。
/// </summary>
internal sealed class ExecutionFrame
{
    private static readonly AsyncLocal<ExecutionFrame?> CurrentFrame = new();

    /// <summary>当前执行的栈顶帧（AsyncLocal；会话进入时入栈、退出时还原）。</summary>
    internal static ExecutionFrame? Current
    {
        get => CurrentFrame.Value;
        set => CurrentFrame.Value = value;
    }

    private Context? _context;

    internal ExecutionFrame(ExecutionFrame? parent, EventStream stream, string triggerLabel, LogicEngine? engine)
    {
        Parent = parent;
        Stream = stream;
        TriggerLabel = triggerLabel;
        Engine = engine;
    }

    /// <summary>父帧（触发者执行会话；无父时为 null——顶层）。</summary>
    internal ExecutionFrame? Parent { get; }

    /// <summary>本次执行的流。</summary>
    internal EventStream Stream { get; }

    /// <summary>本次执行所属引擎（S-C2；供 <see cref="Context.Engine"/> 读取；嵌套执行沿调用传入）。</summary>
    internal LogicEngine? Engine { get; }

    /// <summary>触发器显示名（名称或类型名）。</summary>
    internal string TriggerLabel { get; }

    /// <summary>当前正在执行的事件名（source 构成用；未进入事件时为 null）。</summary>
    internal string? CurrentEventName { get; set; }

    internal Func<string, CancellationToken, Task>? SeamExecutor { get; set; }

    /// <summary>本级停止（Stop）标记。</summary>
    internal bool Stopped { get; set; }

    /// <summary>链中断（Interrupt）标记。</summary>
    internal bool Interrupted { get; set; }

    internal void AttachContext(Context context) => _context = context;

    /// <summary>Stop（仅本级）：标记本层剩余事件停止；不影响父层与子执行；写入事件流。</summary>
    internal void RequestStop()
    {
        if (_context is null)
        {
            return; // 未关联执行会话（执行路径之外）防御：无操作
        }

        Stopped = true;
        Stream.Write(
            LogEntryKind.Log,
            LogLevel.Info,
            BuildSource(TriggerLabel, CurrentEventName),
            "执行被停止（Stop）：本级剩余事件不再执行。",
            new[] { "stop" },
            data: null);
    }

    /// <summary>静默停止（仅本级；不写事件流条目——disposing 守卫过滤专用）。</summary>
    internal void RequestStopSilent()
    {
        if (_context is null)
        {
            return;
        }

        Stopped = true;
    }

    /// <summary>
    /// Interrupt（链级）：标记当前活跃执行栈全部帧（Interrupt 所在层、全部祖先层，以及正在运行的更深子层）；
    /// 各层在各自下一事件边界检查后停止；已中断状态下链上新发起的执行空转。写入事件流。
    /// </summary>
    internal void RequestInterrupt()
    {
        if (_context is null)
        {
            return;
        }

        Stream.Write(
            LogEntryKind.Log,
            LogLevel.Warning,
            BuildSource(TriggerLabel, CurrentEventName),
            "执行被中断（Interrupt）：动作链取消，链上剩余事件不再执行。",
            new[] { "interrupt" },
            data: null);

        var top = Current;
        if (top is not null)
        {
            for (var frame = top; frame is not null; frame = frame.Parent)
            {
                frame.Interrupted = true;
            }
        }
        else
        {
            for (var frame = this; frame is not null; frame = frame.Parent)
            {
                frame.Interrupted = true;
            }
        }
    }

    /// <summary>沿帧链（含起点帧）检查是否存在中断标记（用于已中断状态下的空转判定）。</summary>
    internal static bool IsChainInterrupted(ExecutionFrame? frame)
    {
        for (var f = frame; f is not null; f = f.Parent)
        {
            if (f.Interrupted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>source 构成：触发器显示名（未命名时已退化为类型名）＋事件名（无事件上下文时省略）。</summary>
    internal static string BuildSource(string triggerLabel, string? eventName)
        => string.IsNullOrEmpty(eventName) ? triggerLabel : $"{triggerLabel}/{eventName}";
}
