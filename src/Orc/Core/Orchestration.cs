using Orc.Core.Model;

namespace Orc.Core;

/// <summary>
/// 编排管理器（S-C3；内核侧、引擎级）：把**新模型的边表**投影成审查链的「节点＋实例＋下游」形态。
/// <para>
/// 节点＝<b>引擎／托管实体上的一个成员</b>（<see cref="TriggerId"/> 由成员地址派生，<c>engine#move</c>…）；
/// 节点携带该成员上的外来边（<see cref="TriggerInstance"/>），下游＝这些边声明的目标。
/// 与旧实现的差别只有数据来源：旧实现按 <c>Trigger&lt;TView&gt;</c> 实例登记，
/// 现在唯一的登记面是 <see cref="AuditGraph"/>（实体—成员—挂载边图），不存在第二份账本。
/// </para>
/// <para>
/// 与运行期因果树（<see cref="EventStream"/>）无关：本管理器只描述"逻辑长什么样"。
/// </para>
/// </summary>
public sealed class OrchestrationManager
{
    /// <summary>审查链载荷 schema 版本（S-C10；快照/导出演进的兼容锚点）。</summary>
    /// <remarks>
    /// 批次 3（S10／A-4）升到 2：订阅读面（<c>hooks</c> 段）由**实体—成员—挂载边图**
    /// （<c>entities</c>／<c>members</c>／<c>edges</c>／<c>wraps</c>）等价替代；
    /// <c>nodes</c>（成员图）保留，能力不退化。批次 7（§27）节点改由边表投影——字段不变。
    /// </remarks>
    public const int SchemaVersion = 2;

    private readonly LogicEngine _engine;

    internal OrchestrationManager(LogicEngine engine) => _engine = engine;

    /// <summary>成员节点数量（＝审查图里带边的成员数；诊断摘要用）。</summary>
    public int Count => BuildNodes(_engine.BuildAuditGraph()).Count;

    /// <summary>
    /// 构建全域审查链（S-C3）：节点＝成员，实例＝该成员上的外来边；另附实体—成员—挂载边图与包/随机记录。
    /// 只读、无副作用（可反复调用）。
    /// </summary>
    public AuditChain BuildAuditChain()
    {
        var graph = _engine.BuildAuditGraph();
        return new AuditChain(
            SchemaVersion, BuildNodes(graph), graph, _engine.LoadGate.Records, _engine.RandomSelections.ToArray());
    }

    /// <summary>
    /// 声明期可达链（S-C3）：以指定成员为起点沿"边声明的目标"做 BFS；未解析的目标＝图缺口
    /// （<see cref="AuditEdge.ResolvedTriggerId"/> 为 null，显式呈现）。
    /// </summary>
    public AuditChain QueryChain(TriggerId root)
    {
        var graph = _engine.BuildAuditGraph();
        var byId = BuildNodes(graph).ToDictionary(static n => n.Id);
        var nodes = new List<TriggerNode>();

        if (!byId.TryGetValue(root, out var start))
        {
            // 起点未登记＝空链
            return new AuditChain(
                SchemaVersion, nodes, graph, _engine.LoadGate.Records, _engine.RandomSelections.ToArray());
        }

        var visited = new HashSet<TriggerId> { root };
        var queue = new Queue<TriggerNode>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            nodes.Add(node);

            foreach (var edge in node.Downstream)
            {
                if (edge.ResolvedTriggerId is { IsUnset: false } next
                    && visited.Add(next)
                    && byId.TryGetValue(next, out var nextNode))
                {
                    queue.Enqueue(nextNode);
                }
            }
        }

        return new AuditChain(
            SchemaVersion, nodes, graph, _engine.LoadGate.Records, _engine.RandomSelections.ToArray());
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 把审查图的成员节点投影为成员节点（带边的成员各一节点，声明序）。
    /// 节点 id＝成员地址的稳定哈希（跨对局/跨机器一致，与旧 <c>TriggerId</c> 同一构造）。
    /// </summary>
    private static List<TriggerNode> BuildNodes(AuditGraph graph)
    {
        var addressOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in graph.Members)
        {
            addressOf[$"{member.Entity}#{member.Name}"] = $"{member.Entity}#{member.Name}";
        }

        var incoming = new Dictionary<string, List<AuditMemberEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            var key = $"{EngineOf(edge.Target)}#{edge.TargetMember}";
            if (!incoming.TryGetValue(key, out var list))
            {
                list = new List<AuditMemberEdge>();
                incoming[key] = list;
            }

            list.Add(edge);
        }

        var nodes = new List<TriggerNode>();
        foreach (var member in graph.Members)
        {
            var address = $"{member.Entity}#{member.Name}";
            if (!incoming.TryGetValue(address, out var edges))
            {
                continue; // 无外来边的成员不是节点（节点图只描述"可执行/被订阅"的面）
            }

            // 信号的时机入口是"等更新"（被动）；流程成员是"被调用"（主动）——沿用旧词，语义不变。
            var kind = member.IsSignal ? "passive" : "active";

            var instances = new List<TriggerInstance>(edges.Count);
            var steps = new List<MemberStep>(edges.Count);
            var downstream = new List<AuditEdge>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                instances.Add(new TriggerInstance(edge.DeclaringEntity, edge.Source.ToString(), true));
                steps.Add(new MemberStep(edge.Member, i + 1, edge.OrderSource.ToString().ToLowerInvariant()));

                if (seenKeys.Add(edge.Target) && addressOf.ContainsKey(edge.Target))
                {
                    downstream.Add(new AuditEdge(edge.Target, TriggerId.FromKey(edge.Target)));
                }
            }

            nodes.Add(new TriggerNode(
                TriggerId.FromKey(address),
                address,
                hasDeclaredStableKey: true,
                address,
                kind,
                Array.Empty<string>(),
                steps,
                instances,
                downstream));
        }

        return nodes;
    }

    /// <summary>边目标（<c>作用域#成员</c>）的作用域段；无 <c>#</c> 时按引擎处理。</summary>
    private static string EngineOf(string target)
        => target.Contains('#') ? target[..target.IndexOf('#')] : "engine";
}

/// <summary>审查链载荷（S-C3；JSON 真源的结构模型）。</summary>
public sealed class AuditChain
{
    internal AuditChain(
        int schemaVersion,
        IReadOnlyList<TriggerNode> nodes,
        Orc.Core.Model.AuditGraph graph,
        IReadOnlyList<PackLoadRecord> packages,
        IReadOnlyList<RandomSelectionRecord> randomSelectors)
    {
        SchemaVersion = schemaVersion;
        Nodes = nodes;
        Graph = graph;
        Packages = packages;
        RandomSelectors = randomSelectors;
    }

    /// <summary>schema 版本（演进兼容锚点）。</summary>
    public int SchemaVersion { get; }

    /// <summary>成员节点（每个"被订阅/带边"的成员恰一节点，携带边清单）。</summary>
    public IReadOnlyList<TriggerNode> Nodes { get; }

    /// <summary>
    /// 实体—成员—挂载边图（S10／R7）：能回答"谁订阅了某个信号"（<see cref="Orc.Core.Model.AuditGraph.EdgesInto"/>）
    /// 以及边的来源／位次。
    /// </summary>
    public Orc.Core.Model.AuditGraph Graph { get; }

    /// <summary>
    /// 包装载记录（S14 第 3 条）：成功／被跳过的可选包／失败的必需包，逐条含失败项。
    /// 这是"哪些包没生效"的唯一导出面（作者据此知道效果为何什么都没发生）。
    /// </summary>
    public IReadOnlyList<PackLoadRecord> Packages { get; }

    /// <summary>
    /// 本对局用到的随机选择器（R2c／R7）：<c>{ viewer, selector }</c>。
    /// 只有显式允许随机（<c>MatchOptions.AllowRandomTargeting</c>）并真正取值时才有条目——
    /// 它回答"这局哪里用了随机"，也是复盘"结果为何不可复现"的入口。
    /// </summary>
    public IReadOnlyList<RandomSelectionRecord> RandomSelectors { get; }
}

/// <summary>审查链节点：一个成员及其实例（外来边）、步骤与声明的下游。</summary>
public sealed class TriggerNode
{
    internal TriggerNode(
        TriggerId id,
        string stableKey,
        bool hasDeclaredStableKey,
        string displayName,
        string kind,
        IReadOnlyList<string> hookNames,
        IReadOnlyList<MemberStep> events,
        IReadOnlyList<TriggerInstance> instances,
        IReadOnlyList<AuditEdge> downstream)
    {
        Id = id;
        StableKey = stableKey;
        HasDeclaredStableKey = hasDeclaredStableKey;
        DisplayName = displayName;
        Kind = kind;
        HookNames = hookNames;
        Events = events;
        Instances = instances;
        Downstream = downstream;
        // S-C12：可序列化面随旧 `ISerializableEffect`（动态效果）删除——v4 组件走资产面，不在此投影。
        Serializable = false;
    }

    /// <summary>成员标识（＝成员地址的稳定哈希）。</summary>
    public TriggerId Id { get; }

    /// <summary>稳定键（这里＝成员地址，如 <c>engine#move</c>；定义级身份）。</summary>
    public string StableKey { get; }

    /// <summary>是否为作者显式声明（成员地址恒为显式声明）。</summary>
    public bool HasDeclaredStableKey { get; }

    /// <summary>展示名（＝成员地址）。</summary>
    public string DisplayName { get; }

    /// <summary>种类：<c>active</c>（流程成员，被调用）／<c>passive</c>（信号成员，等更新）。</summary>
    public string Kind { get; }

    /// <summary>目标信号名（成员不是信号＝空）。</summary>
    public IReadOnlyList<string> HookNames { get; }

    /// <summary>执行步骤（外来边，按边表顺序）。</summary>
    public IReadOnlyList<MemberStep> Events { get; }

    /// <summary>实例清单（来源实体／来源类别／装载态）。</summary>
    public IReadOnlyList<TriggerInstance> Instances { get; }

    /// <summary>声明的下游边（目标地址 + 解析结果；未登记＝null）。</summary>
    public IReadOnlyList<AuditEdge> Downstream { get; }

    /// <summary>是否可序列化为效果快照（S-C12；当前恒 false）。</summary>
    public bool Serializable { get; }
}

/// <summary>成员的一步执行（外来边）：来源成员名、位次（1 起）、位次来源（<c>declared</c>／<c>appended</c>）。</summary>
/// <param name="Name">边指向的成员名（来源实体上被执行的成员）。</param>
/// <param name="Seq">执行序（1 起；同一目标成员内）。</param>
/// <param name="Position">位次来源（声明序／追加序）。</param>
public sealed record MemberStep(string Name, int Seq, string Position);

/// <summary>审查链边：边声明的目标地址及其解析结果。</summary>
/// <param name="DownstreamKey">目标地址（<c>作用域#成员</c>）。</param>
/// <param name="ResolvedTriggerId">解析到的成员标识（未登记＝null＝图缺口）。</param>
public sealed record AuditEdge(string DownstreamKey, TriggerId? ResolvedTriggerId);

/// <summary>成员实例（审查链节点附属）：来源实体、来源类别与装载态。</summary>
/// <param name="Host">来源实体名。</param>
/// <param name="Origin">来源类别（explicit／injected／framework:disposal／framework:one-shot）。</param>
/// <param name="Mounted">是否处于挂载状态（边恒为已挂载）。</param>
public sealed record TriggerInstance(string Host, string Origin, bool Mounted);
