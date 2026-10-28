namespace Orc.Core.Model;

/// <summary>审查图的一个实体节点（S10／R7：实体—成员—挂载边图）。</summary>
/// <param name="Name">实体实例名。</param>
/// <param name="PrefabId">预制体 id（＝类）。</param>
/// <param name="Kind">实体类别（引擎／卡牌／组件／handler；旧路径对象标 <c>legacy</c>）。</param>
/// <param name="Hosts">宿主种类（类型预制体专用；其余 None）。</param>
/// <param name="Host">宿主实体名（组件才有；null＝无宿主）。</param>
/// <param name="OneShot">是否为一次性组件（R3k）。</param>
/// <param name="DisposalSignal">兜底信号（组件才有值）。</param>
/// <param name="Origin">来源（model＝新模型实体；legacy-effect／legacy-trigger＝旧路径对象）。</param>
/// <param name="Members">成员名（声明序）。</param>
public sealed record AuditEntityNode(
    string Name,
    string PrefabId,
    string Kind,
    HostKind Hosts,
    string? Host,
    bool OneShot,
    string? DisposalSignal,
    string Origin,
    IReadOnlyList<string> Members);

/// <summary>审查图的一个成员节点（R7：某实体有哪些成员、各有什么能力）。</summary>
/// <param name="Entity">所属实体名。</param>
/// <param name="Name">成员名。</param>
/// <param name="Layer">成员声明所在层（内核／游戏／资产／运行期）。</param>
/// <param name="Visibility">可见性位标志。</param>
/// <param name="IsSignal">是否为信号（时机入口）。</param>
/// <param name="Timing">信号时机（非信号＝null）。</param>
/// <param name="DeclaredBy">声明该成员的预制体 id。</param>
/// <param name="EdgeCount">该成员上的外来边数。</param>
public sealed record AuditMemberNode(
    string Entity,
    string Name,
    PrefabLayer Layer,
    MemberVisibility Visibility,
    bool IsSignal,
    SignalTiming? Timing,
    string? DeclaredBy,
    int EdgeCount);

/// <summary>
/// 审查图的一条挂载边（R7／R1g／R1h）。
/// <para>
/// <see cref="Target"/> 是**声明形态**的地址（<c>engine#card.destroyed</c>／<c>host#targeting</c>）；
/// 旧路径的 hook 若没有对应信号成员，则形如 <c>hook#u</c> 且 <see cref="GraphGap"/>＝true（"未解析＝图缺口"的呈现保留）。
/// </para>
/// </summary>
/// <param name="Target">目标成员地址（声明形态）。</param>
/// <param name="TargetMember">目标成员名（不含作用域前缀）。</param>
/// <param name="Declaration">声明原文（作者写下的字符串；旧路径＝hook 名或触发器稳定键）。</param>
/// <param name="DeclaringEntity">来源实体名（组件／卡／引擎／效果）。</param>
/// <param name="DeclaringPrefab">来源预制体 id（旧路径＝来源对象类型名）。</param>
/// <param name="DeclaringKind">来源类别（engine／card／component／effect／trigger）。</param>
/// <param name="Member">被挂载的成员名（边指向的成员）。</param>
/// <param name="Source">边来源（四类闭合：explicit／injected／framework:disposal／framework:one-shot）。</param>
/// <param name="OrderSource">位次来源（边级，R1h）。</param>
/// <param name="Position">执行序位次（1 起；同一目标成员内）。</param>
/// <param name="MemberLayer">目标成员所在层。</param>
/// <param name="GraphGap">是否为图缺口（旧路径 hook 未对应任何信号成员）。</param>
/// <param name="SourceObject">来源对象（只读诊断用；按引用比对可避免依赖显示名）。</param>
public sealed record AuditMemberEdge(
    string Target,
    string TargetMember,
    string Declaration,
    string DeclaringEntity,
    string DeclaringPrefab,
    string DeclaringKind,
    string Member,
    EdgeSource Source,
    EdgeOrderSource OrderSource,
    int Position,
    PrefabLayer MemberLayer,
    bool GraphGap,
    object? SourceObject);

/// <summary>审查图的一条包装关系（R1i／R7：谁包装了谁、相位、声明序位次）。</summary>
/// <param name="Target">被包装成员的地址。</param>
/// <param name="Phase">相位（before／after／replace）。</param>
/// <param name="Handler">包装器 handler 资产 id（框架合成的守卫以 <c>framework:…</c> 呈现）。</param>
/// <param name="Order">声明序。</param>
/// <param name="Sequence">登记序。</param>
/// <param name="DeclaringEntity">来源实体名。</param>
/// <param name="Source">来源（一次性守卫＝<c>framework:one-shot</c>；作者包装＝<c>explicit</c>）。</param>
public sealed record AuditWrap(
    string Target,
    WrapperPhase Phase,
    string Handler,
    string Order,
    long Sequence,
    string DeclaringEntity,
    EdgeSource Source);

/// <summary>
/// 审查与诊断面（S10／R7）：**实体—成员—挂载边图**，替代旧订阅查询读面（`GetSubscribers`／
/// <c>EnumerateHooks</c>）。
/// <para>
/// 三条硬要求（S10）：
/// ①能回答"谁订阅了某个信号"——<see cref="EdgesInto"/>；
/// ②边的来源是一等枚举、四类闭合、**不存在无来源**——<see cref="AuditMemberEdge.Source"/> 恒有值；
/// ③每条边带 <c>order-source</c> 与位次，能据此推出执行序。
/// </para>
/// <para>
/// 导出只读、无副作用、可反复调用：本类型在构建时取一次快照，不持有任何可变状态。
/// 新旧两条路径都在同一张图里（共存期）：新模型实体标 <c>model</c>、旧路径对象标 <c>legacy-*</c>，
/// 因此"谁的边"始终可辨，不需要两个读面。
/// </para>
/// </summary>
public sealed class AuditGraph
{
    /// <summary>审查导出 schema 版本（A-4：升 2，新增 entities／members／edges／wraps 四集合）。</summary>
    public const int SchemaVersion = 2;

    internal AuditGraph(
        IReadOnlyList<AuditEntityNode> entities,
        IReadOnlyList<AuditMemberNode> members,
        IReadOnlyList<AuditMemberEdge> edges,
        IReadOnlyList<AuditWrap> wraps)
    {
        Entities = entities;
        Members = members;
        Edges = edges;
        Wraps = wraps;
    }

    /// <summary>实体节点。</summary>
    public IReadOnlyList<AuditEntityNode> Entities { get; }

    /// <summary>成员节点。</summary>
    public IReadOnlyList<AuditMemberNode> Members { get; }

    /// <summary>挂载边（目标成员 → 执行序）。</summary>
    public IReadOnlyList<AuditMemberEdge> Edges { get; }

    /// <summary>包装关系（谁包装了谁、相位、声明序位次；含一次性守卫）。</summary>
    public IReadOnlyList<AuditWrap> Wraps { get; }

    /// <summary>
    /// 反查"某成员上挂了哪些来源边"（R7 的核心问题）。
    /// 参数可给完整地址（<c>engine#card.destroyed</c>／<c>host#targeting</c>）或裸成员名（<c>card.destroyed</c>）。
    /// </summary>
    /// <exception cref="ArgumentNullException">memberOrAddress 为 null。</exception>
    public IReadOnlyList<AuditMemberEdge> EdgesInto(string memberOrAddress)
    {
        ArgumentNullException.ThrowIfNull(memberOrAddress);

        return Edges
            .Where(e => string.Equals(e.Target, memberOrAddress, StringComparison.Ordinal)
                        || string.Equals(e.TargetMember, memberOrAddress, StringComparison.Ordinal))
            .OrderBy(static e => e.Position)
            .ToArray();
    }

    /// <summary>某实体**创建**的边（"这个实体挂在哪"）。</summary>
    /// <exception cref="ArgumentNullException">entity 为 null。</exception>
    public IReadOnlyList<AuditMemberEdge> EdgesOf(string entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Edges.Where(e => string.Equals(e.DeclaringEntity, entity, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>某实体的成员节点。</summary>
    /// <exception cref="ArgumentNullException">entity 为 null。</exception>
    public IReadOnlyList<AuditMemberNode> MembersOf(string entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Members.Where(m => string.Equals(m.Entity, entity, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>某目标上的包装关系（按声明序）。</summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public IReadOnlyList<AuditWrap> WrapsOn(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Wraps.Where(w => string.Equals(w.Target, target, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>按来源过滤边（闭合枚举；无 <c>unknown</c>／空来源）。</summary>
    public IReadOnlyList<AuditMemberEdge> EdgesBySource(EdgeSource source)
        => Edges.Where(e => e.Source == source).ToArray();

    /// <summary>全部一次性组件（R3k；实体 + 守卫来源）。</summary>
    public IReadOnlyList<AuditEntityNode> OneShotComponents
        => Entities.Where(e => e.OneShot).ToArray();
}
