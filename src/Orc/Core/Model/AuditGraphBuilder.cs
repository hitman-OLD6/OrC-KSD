using Orc.Cards;

namespace Orc.Core.Model;

/// <summary>
/// 审查图构建器（S10）：把**新模型实体**与**旧路径对象**投影成同一张边图。
/// <para>
/// 为什么必须同图：S3-14／S14 要求"旧订阅读面被审查面等价替代、不留两套读面"。旧效果直到批次 5 才退役，
/// 期间它们的注销兜底挂在总线上；若审查图只画新模型，这批边在图上就"消失"了——能力退化。
/// 因此旧路径的每条总线订阅都投影成一条边：来源仍按四类闭合枚举归类（<c>framework:disposal</c> 等），
/// 并在 <see cref="AuditEntityNode.Origin"/> 标 <c>legacy-*</c>，使"这条边还走旧路径"可辨。
/// </para>
/// </summary>
internal static class AuditGraphBuilder
{
    /// <summary>构建审查图（只读快照）。</summary>
    internal static AuditGraph Build(LogicEngine engine, OrchestrationRuntime? model)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var entities = new List<AuditEntityNode>();
        var members = new List<AuditMemberNode>();
        var edges = new List<AuditMemberEdge>();
        var wraps = new List<AuditWrap>();

        // 每条目标成员上"模型边"的数量——旧端点投影的位次从这里接着数（否则两侧各自从 1 开始，位次会撞）。
        var modelEdgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        if (model is not null)
        {
            CollectModel(model, entities, members, edges, wraps, modelEdgeCounts);
        }

        // 「谁订阅了什么」的来源（A2 后）：①信号端点上的边（生产通路，桥接旧触发器）；
        // ②总线订阅（残留／测试直挂）。两者投影成**同一形态**的边，读面只有一个。
        var seenEntities = new HashSet<string>(entities.Select(static e => e.Name), StringComparer.Ordinal);
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);

        CollectSignalEndpoints(
            engine, model, entities, members, edges, seenEntities, seenMembers, modelEdgeCounts);

        return new AuditGraph(entities, members, edges, wraps);
    }

    /// <summary>只导出新模型部分的审查图（无旧引擎上下文时使用；例如内核层直接持有运行时）。</summary>
    internal static AuditGraph BuildModelOnly(OrchestrationRuntime model)
    {
        var entities = new List<AuditEntityNode>();
        var members = new List<AuditMemberNode>();
        var edges = new List<AuditMemberEdge>();
        var wraps = new List<AuditWrap>();

        CollectModel(
            model, entities, members, edges, wraps, new Dictionary<string, int>(StringComparer.Ordinal));
        return new AuditGraph(entities, members, edges, wraps);
    }

    // ---------- 新模型 ----------

    private static void CollectModel(
        OrchestrationRuntime model,
        List<AuditEntityNode> entities,
        List<AuditMemberNode> members,
        List<AuditMemberEdge> edges,
        List<AuditWrap> wraps,
        Dictionary<string, int> modelEdgeCounts)
    {
        foreach (var entity in model.Entities)
        {
            // 信号端点实体（A2 过渡件的实现细节）不按"模型实体"投影：
            // 它以旧订阅者身份出现在挂载边上（见 <see cref="CollectSignalEndpoints"/>），实体/成员节点由那里出。
            if (IsSignalEndpoint(entity))
            {
                continue;
            }

            var memberList = entity.Members;
            entities.Add(new AuditEntityNode(
                entity.Name,
                entity.Type.Id,
                entity.EntityKind.ToString().ToLowerInvariant(),
                entity.Type.Hosts,
                entity.Host?.Name,
                entity.Type.OneShot,
                entity.EntityKind == EntityKind.Component ? entity.Type.DisposalSignal : null,
                "model",
                memberList.Select(static m => m.Name).ToArray()));

            foreach (var member in memberList)
            {
                var table = entity.FindEdges(member.Name);
                members.Add(new AuditMemberNode(
                    entity.Name,
                    member.Name,
                    member.Layer,
                    member.Visibility,
                    member.IsSignal,
                    member.SignalTiming,
                    member.DeclaredBy,
                    table is null ? 0 : table.Snapshot().Count(static e => !IsSignalEndpoint(e.Declaring))));

                if (table is null)
                {
                    continue;
                }

                var ordered = table.Snapshot();
                var position = 0;
                for (var i = 0; i < ordered.Count; i++)
                {
                    var edge = ordered[i];

                    // 信号端点创建的边由 <see cref="CollectSignalEndpoints"/> 按旧订阅者身份投影（那是唯一投影点，
                    // 否则同一条边会出现两次：一次"模型组件边"、一次"旧订阅边"）。
                    if (IsSignalEndpoint(edge.Declaring))
                    {
                        continue;
                    }

                    position++;
                    edges.Add(new AuditMemberEdge(
                        NormalizeTarget(edge.Declaration, edge.Target.ToString()),
                        member.Name,
                        edge.Declaration,
                        edge.Declaring.Name,
                        edge.Declaring.Type.Id,
                        edge.Declaring.EntityKind.ToString().ToLowerInvariant(),
                        edge.Member.Name,
                        edge.Source,
                        edge.OrderSource,
                        position,
                        member.Layer,
                        GraphGap: false,
                        edge.Declaring));
                }

                if (position > 0 && entity.EntityKind == EntityKind.Engine)
                {
                    // 只有引擎成员会被旧端点挂载（SignalBridge 的目标固定是 engine#<信号>）；
                    // 组件自身成员即使与某个信号同名，也不参与该信号的位次编号。
                    modelEdgeCounts[member.Name] = position;
                }
            }

            // 一次性守卫（R3k）：框架在**成员执行外围**合成"检查 + 注销"——
            // 它不是信号上的边，而是包住整个实例执行的守卫，因此以 wraps 呈现（来源 framework:one-shot）。
            if (entity.Type.OneShot)
            {
                wraps.Add(new AuditWrap(
                    "self#*",
                    WrapperPhase.After,
                    "framework:one-shot",
                    DeclarationOrder.FrameworkLast(entity.DeclarationSegment).ToString(),
                    0,
                    entity.Name,
                    EdgeSource.FrameworkOneShot));
            }
        }

        foreach (var entry in model.Wraps.All)
        {
            wraps.Add(new AuditWrap(
                entry.Target.Key.Replace("member:", string.Empty, StringComparison.Ordinal),
                entry.Phase,
                entry.Handler.Id,
                entry.Order.ToString(),
                entry.Sequence,
                ResolveWrapOwner(model, entry.Handler.Id),
                EdgeSource.Explicit));
        }
    }

    /// <summary>该实体是否是 A2 的信号端点（旧触发器挂载的实现载体；审查面按旧订阅者身份投影）。</summary>
    private static bool IsSignalEndpoint(OrchestrationEntity entity)
        => string.Equals(entity.Type.Id, SignalEndpointPrefab.PrefabId, StringComparison.Ordinal);

    /// <summary>包装器归属实体（审查面：谁包了谁）。</summary>
    private static string ResolveWrapOwner(OrchestrationRuntime model, string handlerId)
        => model.Entities
            .FirstOrDefault(e => e.Type.Wraps.Any(w => string.Equals(w.HandlerId, handlerId, StringComparison.Ordinal)))
            ?.Name ?? "<未知>";

    /// <summary>
    /// 声明地址归一化：优先用声明原文（<c>host#targeting</c> 这类只有声明侧才有的形态），
    /// 否则退回边表地址。这正是批次 2 遗留的"<c>host#</c> 原文丢失"问题的修复点。
    /// </summary>
    private static string NormalizeTarget(string declaration, string tableAddress)
        => MemberAddress.TryParse(declaration, out _, out _) ? declaration : tableAddress;

    // ---------- 旧路径（共存期） ----------

    /// <summary>
    /// 信号端点投影（A2）：生产挂载＝触发器 → 端点实体 → <c>engine#&lt;信号&gt;</c> 边。
    /// 投影形态与旧总线订阅**逐字段一致**（实体名＝所有者名、成员名＝触发器展示名、来源＝四类闭合枚举），
    /// 因此"A2 前读总线订阅、A2 后读边"两侧断言可以一一对照。
    /// </summary>
    private static void CollectSignalEndpoints(
        LogicEngine engine,
        OrchestrationRuntime? model,
        List<AuditEntityNode> entities,
        List<AuditMemberNode> members,
        List<AuditMemberEdge> edges,
        HashSet<string> seenEntities,
        HashSet<string> seenMembers,
        Dictionary<string, int> modelEdgeCounts)
    {
        var endpoints = engine.SignalEndpoints;
        if (endpoints.Count == 0)
        {
            return;
        }

        // 按信号分组、组内保持挂载序（旧总线在同优先级下同样是"先挂先执行"）。
        var byHook = new Dictionary<string, List<SignalEndpointInfo>>(StringComparer.Ordinal);
        var hookOrder = new List<string>();
        foreach (var endpoint in endpoints)
        {
            foreach (var hook in endpoint.Hooks)
            {
                if (!byHook.TryGetValue(hook, out var list))
                {
                    list = new List<SignalEndpointInfo>();
                    byHook[hook] = list;
                    hookOrder.Add(hook);
                }

                list.Add(endpoint);
            }
        }

        foreach (var hook in hookOrder)
        {
            // 组内按旧总线的排序键（优先级升序→挂载序升序）——与边表的执行序同源。
            var list = byHook[hook]
                .OrderBy(static e => e.Priority)
                .ThenBy(static e => e.Sequence)
                .ToArray();
            var offset = modelEdgeCounts.TryGetValue(hook, out var projected) ? projected : 0;
            for (var i = 0; i < list.Length; i++)
            {
                ProjectSubscription(
                    model, hook, offset + i + 1, list[i].DisplayName, list[i].Owner,
                    list[i].DisplayName.EndsWith("/dispose", StringComparison.Ordinal)
                        ? EdgeSource.FrameworkDisposal
                        : EdgeSource.Explicit,
                    entities, members, edges, seenEntities, seenMembers);
            }
        }
    }

    /// <summary>
    /// 一条旧订阅者 → 实体／成员／边（旧总线订阅与信号端点共用同一投影：两侧读面必须可逐字段对照）。
    /// </summary>
    private static void ProjectSubscription(
        OrchestrationRuntime? model,
        string hook,
        int position,
        string displayName,
        object? owner,
        EdgeSource source,
        List<AuditEntityNode> entities,
        List<AuditMemberNode> members,
        List<AuditMemberEdge> edges,
        HashSet<string> seenEntities,
        HashSet<string> seenMembers)
    {
        var isSignal = model?.IsSignal(hook) == true;
        var layer = model?.Engine.FindMember(hook)?.Layer ?? PrefabLayer.Asset;
        var target = isSignal ? $"engine#{hook}" : $"hook#{hook}";

        var (name, kind, prefab) = DescribeOwner(owner, displayName);

        if (seenEntities.Add(name))
        {
            entities.Add(new AuditEntityNode(
                name, prefab, kind, HostKind.None, null, false, null, $"legacy-{kind}", Array.Empty<string>()));
        }

        if (seenMembers.Add($"{name}\u0001{displayName}"))
        {
            members.Add(new AuditMemberNode(
                name, displayName, PrefabLayer.Asset, MemberVisibility.Hostable,
                IsSignal: false, Timing: null, DeclaredBy: prefab, EdgeCount: 0));
        }

        edges.Add(new AuditMemberEdge(
            target,
            hook,
            displayName,
            name,
            prefab,
            $"legacy-{kind}",
            displayName,
            source,
            EdgeOrderSource.Appended, // 旧路径的次序＝优先级→注册序，不是声明序（R1g 要消灭的东西）
            position,
            layer,
            GraphGap: !isSignal,
            owner));
    }

    /// <summary>旧路径对象 → （实体名，类别，预制体视图）。</summary>
    private static (string Name, string Kind, string Prefab) DescribeOwner(object? owner, string displayName)
        => owner switch
        {
            Card card => (card.Name, "card", card.GetType().Name),
            null => (displayName, "trigger", "<无主>"),
            _ => (owner.GetType().Name, "trigger", owner.GetType().Name),
        };
}
