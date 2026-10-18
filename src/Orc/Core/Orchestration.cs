using Orc.Cards;

namespace Orc.Core;

/// <summary>
/// 编排管理器（S-C3；内核侧、引擎级）：集中登记"触发器 ↔ hook ↔ 事件（含声明的下游）"关系，
/// 提供反向依赖查询与**声明期可达链**导出（审查链真源）。节点＝触发器种类（<see cref="TriggerId"/>），
/// 节点携带实例清单（宿主/来源/装载态）。
/// 登记来源：内核自动采样（<see cref="Bus.Mount"/>）+ 装配方显式登记（未挂载主动触发器 / 动态 / 词条内嵌）；
/// 审查导出另聚合引擎卡牌上的效果主触发器与效果注入项——只读、无副作用。
/// 与运行期因果树（<see cref="EventStream"/>）无关：本管理器只描述"逻辑长什么样"。
/// </summary>
public sealed class OrchestrationManager
{
    /// <summary>审查链载荷 schema 版本（S-C10；快照/导出演进的兼容锚点）。</summary>
    public const int SchemaVersion = 1;

    private readonly LogicEngine _engine;
    private readonly List<Registration> _registered = new();
    private readonly HashSet<ITriggerMetadata> _seen = new(ReferenceEqualityComparer.Instance);

    internal OrchestrationManager(LogicEngine engine) => _engine = engine;

    /// <summary>已登记触发器数量（登记序集合大小；不含审查导出时临时聚合的卡牌效果触发器）。</summary>
    public int Count => _registered.Count;

    /// <summary>
    /// 登记一个触发器（幂等：同一实例重复登记＝无操作）。origin/host 为审查标注（缺省自动推导）。
    /// 登记同时把其 hook 名登记进引擎词汇表。
    /// </summary>
    /// <exception cref="ArgumentNullException">trigger 为 null。</exception>
    /// <exception cref="ArgumentException">trigger 未实现 <see cref="ITriggerMetadata"/>。</exception>
    public void Register(object trigger, string origin = "explicit", string? host = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger is not ITriggerMetadata metadata)
        {
            throw new ArgumentException(
                "对象不是触发器（未实现 ITriggerMetadata）。", nameof(trigger));
        }

        if (!_seen.Add(metadata))
        {
            return; // 幂等：同一实例只登记一次
        }

        foreach (var hook in metadata.HookNames)
        {
            _engine.Hooks.Register(hook);
        }

        _registered.Add(new Registration(metadata, origin, host ?? DeriveHost(metadata)));
    }

    /// <summary>已登记触发器（登记序快照）。</summary>
    public IReadOnlyList<ITriggerMetadata> Triggers
    {
        get
        {
            var result = new ITriggerMetadata[_registered.Count];
            for (var i = 0; i < _registered.Count; i++)
            {
                result[i] = _registered[i].Trigger;
            }

            return result;
        }
    }

    /// <summary>反向依赖：声明了该 hook 的已登记触发器（登记序快照）。</summary>
    public IReadOnlyList<ITriggerMetadata> TriggersWithHook(string hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        var result = new List<ITriggerMetadata>();
        foreach (var registration in _registered)
        {
            foreach (var declared in registration.Trigger.HookNames)
            {
                if (string.Equals(declared, hook, StringComparison.Ordinal))
                {
                    result.Add(registration.Trigger);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>按稳定键找已登记触发器（登记序快照）。</summary>
    public IReadOnlyList<ITriggerMetadata> TriggersWithKey(string stableKey)
    {
        ArgumentNullException.ThrowIfNull(stableKey);

        var result = new List<ITriggerMetadata>();
        foreach (var registration in _registered)
        {
            if (string.Equals(registration.Trigger.StableKey, stableKey, StringComparison.Ordinal))
            {
                result.Add(registration.Trigger);
            }
        }

        return result;
    }

    /// <summary>
    /// 构建全域审查链（S-C3）：登记触发器 + 引擎卡牌效果主触发器 + 效果注入目标触发器；按种类聚合节点。
    /// 只读、无副作用（可反复调用）。
    /// </summary>
    public AuditChain BuildAuditChain()
    {
        var all = CollectAll();
        var nodes = new List<TriggerNode>();
        foreach (var group in GroupById(all))
        {
            nodes.Add(BuildNode(group.Key, group.Value, all));
        }

        return new AuditChain(SchemaVersion, nodes, _engine.Bus.EnumerateHooks());
    }

    /// <summary>
    /// 声明期可达链（S-C3）：以指定触发器种类为起点，沿"事件声明的下游稳定键"做 BFS；
    /// 未登记的下游键＝图缺口（<see cref="AuditEdge.ResolvedTriggerId"/> 为 null，显式呈现）。
    /// </summary>
    public AuditChain QueryChain(TriggerId root)
    {
        var all = CollectAll();
        var byId = GroupById(all);
        var nodes = new List<TriggerNode>();

        if (!byId.ContainsKey(root))
        {
            return new AuditChain(SchemaVersion, nodes, _engine.Bus.EnumerateHooks()); // 起点未登记＝空链
        }

        var visited = new HashSet<TriggerId> { root };
        var queue = new Queue<TriggerId>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var node = BuildNode(id, byId[id], all);
            nodes.Add(node);

            foreach (var edge in node.Downstream)
            {
                if (edge.ResolvedTriggerId is { IsUnset: false } next && visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return new AuditChain(SchemaVersion, nodes, _engine.Bus.EnumerateHooks());
    }

    // ---------- 内部 ----------

    /// <summary>聚合全部触发器（登记项 + 卡牌效果主触发器 + 注入目标），按引用去重，保持遇见序。</summary>
    private List<Registration> CollectAll()
    {
        var result = new List<Registration>(_registered);
        var seen = new HashSet<ITriggerMetadata>(_seen, ReferenceEqualityComparer.Instance);

        void Consider(ITriggerMetadata? trigger, string origin, string host)
        {
            if (trigger is not null && seen.Add(trigger))
            {
                result.Add(new Registration(trigger, origin, host));
            }
        }

        foreach (var card in _engine.Cards)
        {
            foreach (var effect in card.Effects)
            {
                var host = $"效果:{effect.Name}@{card.Name}";
                Consider(effect.MainTrigger, "effect", host);
                Consider(effect.DisposalTriggerMetadata, "dispose", host); // E-D1：销毁触发器与主触发器并列进入审查图

                foreach (var injection in effect.Injections)
                {
                    if (injection.Target is ITriggerMetadata target)
                    {
                        Consider(target, "injection", host);
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<TriggerId, List<Registration>> GroupById(IReadOnlyList<Registration> all)
    {
        var map = new Dictionary<TriggerId, List<Registration>>();
        foreach (var registration in all)
        {
            if (!map.TryGetValue(registration.Trigger.Id, out var list))
            {
                list = new List<Registration>();
                map[registration.Trigger.Id] = list;
            }

            list.Add(registration);
        }

        return map;
    }

    /// <summary>按种类聚合为节点：实例清单取全部命中，事件/下游取该种类代表（首个实例）。</summary>
    private TriggerNode BuildNode(TriggerId id, IReadOnlyList<Registration> group, IReadOnlyList<Registration> all)
    {
        var representative = group[0].Trigger;

        var instances = new List<TriggerInstance>(group.Count);
        foreach (var registration in group)
        {
            instances.Add(new TriggerInstance(
                registration.Host, registration.Origin, registration.Trigger.IsMounted));
        }

        var downstream = new List<AuditEdge>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in representative.Events)
        {
            foreach (var key in info.Downstream)
            {
                if (!seenKeys.Add(key))
                {
                    continue;
                }

                downstream.Add(new AuditEdge(key, ResolveKey(key, all)));
            }
        }

        return new TriggerNode(id, representative, instances, downstream);
    }

    /// <summary>把下游稳定键解析为该种类的 <see cref="TriggerId"/>（未登记＝null）。</summary>
    private static TriggerId? ResolveKey(string stableKey, IReadOnlyList<Registration> all)
    {
        foreach (var registration in all)
        {
            if (string.Equals(registration.Trigger.StableKey, stableKey, StringComparison.Ordinal))
            {
                return registration.Trigger.Id;
            }
        }

        return null;
    }

    private static string DeriveHost(ITriggerMetadata metadata) => metadata.Owner switch
    {
        Effect effect => $"效果:{effect.Name}",
        null => "<无主>",
        var owner => owner.GetType().Name,
    };

    private sealed record Registration(ITriggerMetadata Trigger, string Origin, string Host);
}

/// <summary>审查链载荷（S-C3；JSON 真源的结构模型）。</summary>
public sealed class AuditChain
{
    internal AuditChain(int schemaVersion, IReadOnlyList<TriggerNode> nodes, IReadOnlyList<HookSubscriptions> hooks)
    {
        SchemaVersion = schemaVersion;
        Nodes = nodes;
        Hooks = hooks;
    }

    /// <summary>schema 版本（演进兼容锚点）。</summary>
    public int SchemaVersion { get; }

    /// <summary>触发器种类节点（每个种类恰一节点，携带实例清单）。</summary>
    public IReadOnlyList<TriggerNode> Nodes { get; }

    /// <summary>总线 hook 订阅读面（名/标识/订阅者）。</summary>
    public IReadOnlyList<HookSubscriptions> Hooks { get; }
}

/// <summary>审查链节点：一个触发器种类及其实例、事件与声明的下游边。</summary>
public sealed class TriggerNode
{
    internal TriggerNode(
        TriggerId id, ITriggerMetadata representative, IReadOnlyList<TriggerInstance> instances, IReadOnlyList<AuditEdge> downstream)
    {
        Id = id;
        StableKey = representative.StableKey;
        HasDeclaredStableKey = representative.HasDeclaredStableKey;
        DisplayName = representative.DisplayName;
        Kind = representative.Kind;
        HookNames = representative.HookNames;
        Events = representative.Events;
        Instances = instances;
        Downstream = downstream;
        Serializable = representative.Owner is ISerializableEffect; // S-C12：仅预制体（动态）效果可序列化
    }

    /// <summary>触发器种类标识。</summary>
    public TriggerId Id { get; }

    /// <summary>稳定键（定义级身份）。</summary>
    public string StableKey { get; }

    /// <summary>是否为作者显式声明（false＝弱身份）。</summary>
    public bool HasDeclaredStableKey { get; }

    /// <summary>展示名。</summary>
    public string DisplayName { get; }

    /// <summary>种类（主动/被动）。</summary>
    public TriggerKind Kind { get; }

    /// <summary>hook 名（声明顺序）。</summary>
    public IReadOnlyList<string> HookNames { get; }

    /// <summary>事件信息（执行序）。</summary>
    public IReadOnlyList<TriggerEventInfo> Events { get; }

    /// <summary>实例清单（宿主/来源/装载态）。</summary>
    public IReadOnlyList<TriggerInstance> Instances { get; }

    /// <summary>声明的下游边（稳定键 + 解析结果；未登记＝null）。</summary>
    public IReadOnlyList<AuditEdge> Downstream { get; }

    /// <summary>是否可序列化为效果快照（S-C12；仅预制体（动态）效果为 true）。</summary>
    public bool Serializable { get; }
}

/// <summary>审查链边：事件声明的下游触发器稳定键及其解析结果。</summary>
/// <param name="DownstreamKey">声明的下游稳定键。</param>
/// <param name="ResolvedTriggerId">解析到的触发器种类标识（未登记＝null＝图缺口）。</param>
public sealed record AuditEdge(string DownstreamKey, TriggerId? ResolvedTriggerId);

/// <summary>触发器实例（审查链节点附属）：宿主标签、来源与装载态。</summary>
/// <param name="Host">宿主标签（如 效果:名@卡名 / &lt;无主&gt;）。</param>
/// <param name="Origin">来源（explicit / bus-mount / effect / injection / embedded / dynamic）。</param>
/// <param name="Mounted">是否处于挂载状态（主动触发器恒 false）。</param>
public sealed record TriggerInstance(string Host, string Origin, bool Mounted);
