using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S10（审查与诊断面）的内核验收：实体—成员—挂载边图替代旧总线订阅读面；
/// 边来源四类闭合、<c>order-source</c> 边级可辨、<c>framework:disposal</c>／<c>framework:one-shot</c> 可见、
/// <c>host#</c> 声明原文可还原、导出只读可反复调用。
/// </summary>
public sealed class AuditGraphTests
{
    // ==================== ① 实体—成员—边（R7） ====================

    /// <summary>可导出"某实体有哪些成员、各自挂在哪"。</summary>
    [Fact]
    public void Audit_ExportsEntitiesMembersAndEdges()
    {
        var runtime = Scene(out var host, out _);
        runtime.AddComponent(host, "comp.target", "命中");

        var graph = runtime.BuildAuditGraph();

        var component = Assert.Single(graph.Entities, static e => e.Kind == "component");
        Assert.Equal("comp.target", component.PrefabId);
        Assert.Equal("卡", component.Host);
        Assert.Contains("react", component.Members);

        var member = Assert.Single(graph.MembersOf("命中"), static m => m.Name == "react");
        Assert.Equal(PrefabLayer.Kernel, member.Layer); // 组件成员在测试里按内核层声明
        Assert.True(member.Visibility.HasFlag(MemberVisibility.Mountable));

        var edges = graph.EdgesOf("命中");
        Assert.Equal(2, edges.Count); // 作者边 + 框架注销边
        Assert.All(edges, static e => Assert.Equal("engine#" + Updates.CardDestroyed, e.Target));
    }

    /// <summary>可反查"某宿主成员上挂了哪些来源实体/组件"（替代 <c>Bus.GetSubscribers</c>）。</summary>
    [Fact]
    public void EdgesInto_AnswersWhoMountsOnSignal()
    {
        var runtime = Scene(out var host, out _);
        runtime.AddComponent(host, "comp.target", "命中");
        runtime.AddComponent(host, "comp.other", "旁路");

        var graph = runtime.BuildAuditGraph();
        var edges = graph.EdgesInto($"engine#{Updates.CardDestroyed}");

        Assert.Equal(4, edges.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, edges.Select(static e => e.Position));
        Assert.Contains(edges, static e => e.DeclaringEntity == "命中" && e.Source == EdgeSource.FrameworkDisposal);
        Assert.Contains(edges, static e => e.DeclaringEntity == "旁路" && e.Source == EdgeSource.Explicit);

        // 裸成员名同样可查（审查面不必记得作用域前缀）。
        Assert.Equal(4, graph.EdgesInto(Updates.CardDestroyed).Count);
    }

    /// <summary>边来源枚举闭合：任何边都能归入四类之一（不存在 unknown／空来源）。</summary>
    [Fact]
    public void EdgeSource_IsClosed_NoUnknown()
    {
        var runtime = Scene(out var host, out _);
        runtime.AddComponent(host, "comp.target", "命中");

        var graph = runtime.BuildAuditGraph();
        Assert.NotEmpty(graph.Edges);

        var known = new[]
        {
            EdgeSource.Explicit, EdgeSource.Injected,
            EdgeSource.FrameworkDisposal, EdgeSource.FrameworkOneShot,
        };

        Assert.All(graph.Edges, e => Assert.Contains(e.Source, known));
        Assert.Equal(graph.Edges.Count, known.Sum(s => graph.EdgesBySource(s).Count));
    }

    /// <summary>每条边带 <c>order-source</c> 与位次，能据此推出实际执行序（R1g／R1h）。</summary>
    [Fact]
    public async Task Edges_CarryOrderSourceAndPosition_ThatMatchExecutionOrder()
    {
        var runtime = Scene(out var host, out var trace);
        runtime.AddComponent(host, "comp.target", "命中");

        var graph = runtime.BuildAuditGraph();
        var edges = graph.EdgesInto($"engine#{Updates.CardDestroyed}");
        Assert.All(edges, static e => Assert.Equal(EdgeOrderSource.Declared, e.OrderSource));

        await runtime.EmitAsync(Updates.CardDestroyed);

        // 执行序 == 审查面按 (order-source, 位次) 排出的序（前两条＝作者边／框架注销边，其作者边执行了）。
        Assert.Equal(new[] { "react" }, trace);
    }

    /// <summary>同一成员上混有 <c>declared</c> 与 <c>appended</c> 时逐边可辨（R1h）。</summary>
    [Fact]
    public void OrderSource_IsPerEdge_NotPerSignal()
    {
        var runtime = Scene(out var host, out _);
        var attachment = runtime.AddComponent(host, "comp.target", "命中");

        // 运行期往同一信号上再挂一条边（追加序）。
        runtime.MountAtRuntime(attachment.Component, "react", $"engine#{Updates.CardDestroyed}");

        var edges = runtime.BuildAuditGraph().EdgesInto($"engine#{Updates.CardDestroyed}");
        Assert.Contains(edges, static e => e.OrderSource == EdgeOrderSource.Declared);
        Assert.Contains(edges, static e => e.OrderSource == EdgeOrderSource.Appended);

        // 追加序边恒排在声明序边之后（R1h）。
        var last = edges.OrderBy(static e => e.Position).Last();
        Assert.Equal(EdgeOrderSource.Appended, last.OrderSource);
    }

    /// <summary>审查图能列出全部 <c>framework:disposal</c> 边（R1f／R3f）。</summary>
    [Fact]
    public void FrameworkDisposalEdges_AreEnumerable()
    {
        var runtime = Scene(out var host, out _);
        runtime.AddComponent(host, "comp.target", "命中");
        runtime.AddComponent(host, "comp.other", "旁路");

        var disposal = runtime.BuildAuditGraph().EdgesBySource(EdgeSource.FrameworkDisposal);

        Assert.Equal(2, disposal.Count);
        Assert.All(disposal, static e =>
        {
            Assert.Equal(ComponentDisposalPolicy.DisposalMemberName, e.Member);
            Assert.Equal($"engine#{Updates.CardDestroyed}", e.Target);
        });
    }

    /// <summary>审查面把一次性组件标出（<c>framework:one-shot</c>），与 <c>framework:disposal</c> 并列。</summary>
    [Fact]
    public void OneShot_IsVisibleWithFrameworkOneShotSource()
    {
        var runtime = Scene(out var host, out _, oneShot: true);
        runtime.AddComponent(host, "comp.target", "一次性");

        var graph = runtime.BuildAuditGraph();

        var entity = Assert.Single(graph.OneShotComponents);
        Assert.Equal("一次性", entity.Name);

        var guard = Assert.Single(graph.Wraps, static w => w.Source == EdgeSource.FrameworkOneShot);
        Assert.Equal("一次性", guard.DeclaringEntity);
        Assert.Equal("framework:one-shot", guard.Handler);
    }

    /// <summary>包装关系可导出：谁包装了谁、相位、声明序位次（R1i／R7）。</summary>
    [Fact]
    public void Wraps_Export_TargetPhaseAndOrder()
    {
        var runtime = Scene(out var host, out _);
        var handler = runtime.Handlers.Register("wrap.before", (view, ctx, ct) => Task.CompletedTask);
        runtime.Wraps.Register(
            WrapTarget.ForMember(runtime.Engine.AddressOf(Updates.CardDestroyed)),
            WrapperPhase.Before,
            handler,
            new DeclarationOrder(1, 0, 0));

        var wrap = Assert.Single(runtime.BuildAuditGraph().WrapsOn($"engine#{Updates.CardDestroyed}"));
        Assert.Equal(WrapperPhase.Before, wrap.Phase);
        Assert.Equal("wrap.before", wrap.Handler);
        Assert.Equal("1/0/0", wrap.Order);
    }

    /// <summary>导出只读、可反复调用、无副作用（R7）。</summary>
    [Fact]
    public async Task Audit_IsReadOnlyAndRepeatable()
    {
        var runtime = Scene(out var host, out _);
        runtime.AddComponent(host, "comp.target", "命中");

        var first = runtime.BuildAuditGraph();
        var second = runtime.BuildAuditGraph();

        Assert.Equal(first.Entities.Count, second.Entities.Count);
        Assert.Equal(first.Members.Count, second.Members.Count);
        Assert.Equal(first.Edges.Count, second.Edges.Count);
        Assert.Equal(first.Wraps.Count, second.Wraps.Count);

        // 反复导出不改变任何边（无副作用）。
        var before = runtime.Engine.EdgesFor(Updates.CardDestroyed).All.Count;
        _ = runtime.BuildAuditGraph();
        Assert.Equal(before, runtime.Engine.EdgesFor(Updates.CardDestroyed).All.Count);

        await Task.CompletedTask;
    }

    // ==================== ② host# 声明原文（批次 2 遗留限制） ====================

    /// <summary>
    /// <c>host#</c> 挂载的边能还原声明原文地址——边表地址是宿主自身视角（<c>self#…</c>），
    /// 只看表地址无法还原作者写下的 <c>host#targeting</c>；审查面以声明原文为准。
    /// </summary>
    [Fact]
    public void HostScopeMount_PreservesDeclarationAddress()
    {
        var log = new List<string>();
        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition(
                "card.unit",
                EntityKind.Card,
                hosts: HostKind.Unit,
                members: new[]
                {
                    new MemberDefinition(
                        "targeting",
                        ViewSpec.Empty,
                        MemberVisibility.Hostable | MemberVisibility.Callable),
                }))
            .Add(new PrefabDefinition(
                "comp.hit",
                EntityKind.Component,
                requiresHost: HostKind.Unit,
                members: new[] { ModelKit.Member("on-target", log) },
                mounts: new[] { new MountDeclaration("host#targeting", "on-target") }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var host = runtime.CreateEntity("card.unit", "卡");
        var attachment = runtime.AddComponent(host, "comp.hit", "命中");

        // 边表地址是宿主视角（self#targeting），声明原文另存。
        var table = host.FindEdges("targeting");
        Assert.NotNull(table);
        Assert.Equal("self#targeting", table!.Address.ToString());
        Assert.Equal("host#targeting", table.All[0].Declaration);

        // 审查面导出的是**声明原文**（作者可据此反查自己写的哪一条）。
        var edge = Assert.Single(runtime.BuildAuditGraph().EdgesOf("命中"), static e => e.Target == "host#targeting");
        Assert.Equal("host#targeting", edge.Target);
        Assert.Equal("targeting", edge.TargetMember);
        Assert.Equal(attachment.Type.Id, edge.DeclaringPrefab);
    }

    // ==================== ③ 旧路径投影（S3-14 的基础） ====================

    /// <summary>
    /// 旧总线的每条订阅都投影成一条边：来源按四类闭合归入（效果销毁触发器＝<c>framework:disposal</c>），
    /// 因此"谁订阅了什么"只有审查面一个读面（S3-14 的等价替代）。
    /// </summary>
    [Fact]
    public void LegacySubscriptions_AreProjectedAsEdges()
    {
        var engine = new LogicEngine();
        var owner = new object();
        engine.AttachMemberEdge(
            Updates.CardDestroyed,
            "攻击时回血/dispose",
            owner,
            new OwnedEdge((KernelHandler)((_, _, _) => Task.CompletedTask)));

        var graph = engine.BuildAuditGraph();
        var edge = Assert.Single(graph.EdgesInto($"engine#{Updates.CardDestroyed}"));

        Assert.Equal(EdgeSource.FrameworkDisposal, edge.Source); // 名以 /dispose 结尾 ⇒ 注销边
        Assert.False(edge.GraphGap);
        Assert.Same(owner, edge.SourceObject);
    }

    /// <summary>未对应任何信号成员的 hook＝图缺口（保留"未解析下游＝图缺口"的呈现）。</summary>
    [Fact]
    public void UnknownHook_IsAGraphGap()
    {
        var engine = new LogicEngine();
        engine.AttachMemberEdge(
            "u", "自定义", null, new OwnedEdge((KernelHandler)((_, _, _) => Task.CompletedTask)));

        var edge = Assert.Single(engine.BuildAuditGraph().EdgesInto("u"));
        Assert.True(edge.GraphGap);
        Assert.Equal("hook#u", edge.Target);
    }

    // ==================== 场景构件 ====================

    private static OrchestrationRuntime Scene(out OrchestrationEntity host, out List<string> trace, bool oneShot = false)
    {
        var log = new List<string>();
        trace = log;

        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition("card.unit", EntityKind.Card, hosts: HostKind.Unit))
            .Add(new PrefabDefinition(
                "comp.target",
                EntityKind.Component,
                requiresHost: HostKind.Unit,
                members: new[] { ModelKit.Member("react", log) },
                mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", "react") },
                oneShot: oneShot))
            .Add(new PrefabDefinition(
                "comp.other",
                EntityKind.Component,
                requiresHost: HostKind.Unit,
                members: new[] { ModelKit.Member("react", log) },
                mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", "react") }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        host = runtime.CreateEntity("card.unit", "卡");
        return runtime;
    }
}
