namespace Orc.Core.Model;

/// <summary>
/// 信号成员的构造面（S3）：原 <c>hooks</c> 里的每个更新字符串，在新模型里都是一个
/// <c>engine#&lt;信号名&gt;</c> 成员——成员名**零转换**保留原点分名（R1d 定稿修正／R1e）。
/// <para>
/// 信号的"可被订阅"由 <see cref="MemberVisibility.Hostable"/> 表达（订阅＝挂载，R1b 的两条通道之一）；
/// 信号**不需要**"主动／被动"分类、也不需要优先级字段：执行序只由声明序／追加序推出（R1g）。
/// </para>
/// </summary>
public static class SignalMember
{
    /// <summary>信号成员的默认可见性（可被挂载＝可订阅；可被调用＝等价于"可被发射"）。</summary>
    public const MemberVisibility DefaultVisibility = MemberVisibility.Hostable | MemberVisibility.Callable;

    /// <summary>创建一个信号成员声明。</summary>
    /// <param name="name">信号名（＝成员名；允许含 <c>.</c>，如 <c>unit.position.changed</c>）。</param>
    /// <param name="timing">时机相位（判定"挂载是否可能生效"，S3 第 4c 条）。</param>
    /// <param name="view">载荷视图声明（null＝空视图）。</param>
    /// <param name="layer">来源层（内核／游戏／运行期；审查面据此标出来源层）。</param>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    public static MemberDefinition Create(
        string name,
        SignalTiming timing = SignalTiming.Any,
        ViewSpec? view = null,
        PrefabLayer layer = PrefabLayer.Kernel)
        => new(
            name,
            view,
            DefaultVisibility,
            ownEdges: null,
            validation: null,
            layer: layer,
            declaredBy: null,
            signalTiming: timing);

    /// <summary>
    /// 载荷视图声明：字段**全部可选**（信号的载荷允许部分缺省——如 <c>card.died</c> 的 <c>Killer</c> 可为 null／缺失）。
    /// <para>
    /// 声明在信号上的字段名即"订阅者视图可用的字段名"（R1c 的装载期包含校验以它为准）。
    /// </para>
    /// </summary>
    /// <param name="fields">字段名与封闭内建类型。</param>
    public static ViewSpec Payload(params (string Name, ViewFieldType Type)[] fields)
        => new(fields.Select(static f => new ViewFieldDefinition(f.Name, f.Type, writable: false, required: false)));

    /// <summary>单个载荷字段声明（**可选**；用于"可写与只读混排"的视图，与 <see cref="Payload(ViewFieldDefinition[])"/> 配合）。</summary>
    /// <param name="name">字段名（合法标识符）。</param>
    /// <param name="type">封闭内建类型。</param>
    /// <param name="writable">是否可写（默认只读）。</param>
    public static ViewFieldDefinition Field(string name, ViewFieldType type, bool writable = false)
        => new(name, type, writable, required: false);

    /// <summary>由字段声明构造视图（混排可写／只读时使用）。</summary>
    /// <param name="fields">字段声明。</param>
    public static ViewSpec Payload(params ViewFieldDefinition[] fields) => new(fields);
}

/// <summary>
/// 信号上的一条来源边（审查／反查用，S3 第 9 条＋S10 的导出前置）：能回答"谁挂在了这个信号上、
/// 位次几号、顺序来源是声明序还是追加序、该信号成员来自内核还是游戏层"。
/// </summary>
/// <param name="Signal">信号名（成员名）。</param>
/// <param name="Position">执行序位次（1 起；＝边表快照序）。</param>
/// <param name="DeclaringEntity">声明该边的实体实例名（组件的实例名／引擎名）。</param>
/// <param name="DeclaringPrefab">声明该边的实体预制体 id（来源实体／组件）。</param>
/// <param name="Member">被挂载的成员名（边指向的成员）。</param>
/// <param name="Source">边来源（<c>explicit</c>／<c>injected</c>／<c>framework:disposal</c>／<c>framework:one-shot</c>）。</param>
/// <param name="OrderSource">位次来源（<c>declared</c>＝声明序／<c>appended</c>＝追加序；**边级**属性，R1h）。</param>
/// <param name="MemberLayer">该信号成员声明所在的层（内核／游戏／资产／运行期）。</param>
public sealed record SignalEdgeInfo(
    string Signal,
    int Position,
    string DeclaringEntity,
    string DeclaringPrefab,
    string Member,
    EdgeSource Source,
    EdgeOrderSource OrderSource,
    PrefabLayer MemberLayer);

/// <summary>
/// 信号不可用（未注册，或同名成员不是信号）：**明确失败**，不静默、不按名兜底（R1e）。
/// <para>
/// 两个触发面：①挂载声明引用了不存在的信号（装载期，走 <see cref="PrefabLoadReport"/>）；
/// ②运行期 <c>EmitAsync</c> 指定了未注册／非信号的成员名（运行期，抛本异常并指出名字）。
/// </para>
/// </summary>
public sealed class SignalNotRegisteredException : Exception
{
    /// <summary>以信号名与原因构造。</summary>
    /// <exception cref="ArgumentNullException">signalName 为 null。</exception>
    public SignalNotRegisteredException(string signalName, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(signalName);
        SignalName = signalName;
    }

    /// <summary>出问题的信号名（成员名）。</summary>
    public string SignalName { get; }
}

/// <summary>
/// 编排运行时的**信号面**（S3）：信号＝引擎实体的成员触发器；<c>Emit</c>＝对该成员的调用；订阅＝挂载。
/// <para>
/// **注册与挂载是两个动作、同一门面**（R1h）：<see cref="RegisterSignal"/> 创建新的可寻址成员；
/// <see cref="MountAtRuntime"/> 往已有成员上挂一条边。二者不合并（作用于不同对象），代码里也没有合并入口。
/// </para>
/// <para>
/// **没有"任意字符串直接 Emit"的路径**（S3 第 12 条）：<see cref="EmitAsync"/> 只接受已声明／已注册的信号成员，
/// 其余一律 <see cref="SignalNotRegisteredException"/>。自定义更新名（沙盒的 <c>custom.placed</c> 等）只能经
/// <see cref="RegisterSignal"/> 进入 <c>engine</c> 命名空间。
/// </para>
/// </summary>
public sealed partial class OrchestrationRuntime
{
    /// <summary>全部信号成员（内核＋游戏层＋运行期注册；声明序）。</summary>
    public IReadOnlyList<MemberDefinition> Signals
        => Engine.Members.Where(static m => m.IsSignal).ToArray();

    /// <summary>该名字是否是信号成员（未声明／非信号＝false）。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public bool IsSignal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Engine.FindMember(name)?.IsSignal == true;
    }

    /// <summary>取信号时机（未声明／非信号＝null）。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public SignalTiming? TimingOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Engine.FindMember(name)?.SignalTiming;
    }

    /// <summary>
    /// 运行时注册新信号（R1e）：注册后**立即可被寻址与挂载**（同一 <c>engine</c> 根下的成员表追加一项）。
    /// <para>
    /// 同名幂等：重复注册同名信号＝返回已存在的那个成员（同一地址、同一身份；首次注册决定时机与视图）。
    /// 同名但**不是信号**＝拒绝（不覆盖既有成员，不留半个可寻址成员）。
    /// </para>
    /// </summary>
    /// <param name="name">信号名（＝成员名；允许含 <c>.</c>）。</param>
    /// <param name="timing">时机相位（默认与装配批次无关）。</param>
    /// <param name="view">载荷视图声明（null＝空视图）。</param>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    /// <exception cref="SignalNotRegisteredException">同名成员已存在但不是信号。</exception>
    /// <exception cref="PrefabLoadException">注册失败（成员表拒绝）。</exception>
    public MemberDefinition RegisterSignal(string name, SignalTiming timing = SignalTiming.Any, ViewSpec? view = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var existing = Engine.FindMember(name);
        if (existing is not null)
        {
            if (!existing.IsSignal)
            {
                throw new SignalNotRegisteredException(
                    name,
                    $"注册信号 '{name}' 失败：引擎实体上已存在同名成员，但它不是信号成员（注册被拒绝，未产生任何注册残留）。");
            }

            return existing;
        }

        return Engine.RegisterMember(SignalMember.Create(name, timing, view, PrefabLayer.Runtime));
    }

    /// <summary>取信号成员；不存在或不是信号＝<see cref="SignalNotRegisteredException"/>（消息指出名字与作用域）。</summary>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    /// <exception cref="SignalNotRegisteredException">未声明／非信号。</exception>
    public MemberDefinition RequireSignal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var member = Engine.FindMember(name);
        if (member is null)
        {
            throw new SignalNotRegisteredException(
                name,
                $"信号 '{name}' 未注册：引擎实体（预制体 '{Engine.Type.Id}'）下没有成员 '{name}'，"
                + "且它也不是经运行时注册进入 engine 命名空间的信号（引用未注册触发器＝明确失败，R1e）。");
        }

        if (!member.IsSignal)
        {
            throw new SignalNotRegisteredException(
                name,
                $"成员 'engine#{name}' 存在但不是信号成员（时机入口）：只有信号可被 Emit 调用；"
                + "流程成员请直接调用其所属实体上的成员。");
        }

        return member;
    }

    /// <summary>
    /// 发射（S3 第 2 条）：<c>Emit</c>＝**对该成员触发器的调用**，payload 进入该成员调用的数据面。
    /// <para>
    /// 执行序完全由边表决定（自有边 → 声明序外来边 → 追加序外来边），不存在优先级数字（R1g）。
    /// 与旧总线的差异（**有意为之**）：订阅者异常不再被吞掉记录，而是原样上抛（fail-fast）；
    /// 旧订阅面的"隔离＋继续"口径已随总线门面在 b5 退役，新路径不再复制它。
    /// </para>
    /// </summary>
    /// <param name="signal">信号名（成员名，零转换）。</param>
    /// <param name="payload">载荷（进入数据面；null＝空载荷）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="SignalNotRegisteredException">未声明／非信号。</exception>
    public Task<MemberExecutionResult> EmitAsync(
        string signal, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
    {
        var member = RequireSignal(signal);
        var ctx = new Context(payload is null ? null : new Dictionary<string, object?>(payload));
        return Engine.ExecuteMemberAsync(member, Engine.AddressOf(signal), ctx, ct);
    }

    /// <summary>
    /// 反查"某信号上有哪些来源边"（S3 第 9 条）：按**执行序**列出边，逐条标出位次、顺序来源、
    /// 来源实体／预制体，以及该信号成员来自内核还是游戏层。
    /// </summary>
    /// <exception cref="SignalNotRegisteredException">未声明／非信号。</exception>
    public IReadOnlyList<SignalEdgeInfo> DescribeSignal(string signal)
    {
        var member = RequireSignal(signal);
        var table = Engine.FindEdges(signal);
        if (table is null)
        {
            return Array.Empty<SignalEdgeInfo>();
        }

        var edges = table.Snapshot();
        var result = new SignalEdgeInfo[edges.Count];
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            result[i] = new SignalEdgeInfo(
                signal,
                i + 1,
                edge.Declaring.Name,
                edge.Declaring.Type.Id,
                edge.Member.Name,
                edge.Source,
                edge.OrderSource,
                member.Layer);
        }

        return result;
    }
}
