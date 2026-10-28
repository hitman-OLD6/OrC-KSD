using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S8（组件实体）的内核验收：构造／装配分离（<c>active-when</c>）、同 prefab 至多一份、
/// 复装＝重建实例、装配失败上浮、<c>requires-host</c> 校验、注销兜底（<c>framework:disposal</c> 位次与可见性）、
/// 一次性组件（<c>framework:one-shot</c>）。
/// </summary>
public sealed class ComponentAssemblyTests
{
    // ==================== ① 构造／装配分离（R3i） ====================

    /// <summary>构造不装配：构造后没有边、组件不响应任何信号（两种时点都一样）。</summary>
    [Fact]
    public void Construct_DoesNotAssemble_NoEdgesBeforePhase()
    {
        var runtime = Scene(out var host, trace: out _);

        var attachment = runtime.ConstructComponent(host, "comp.react", "react");

        Assert.False(attachment.IsAssembled);
        Assert.Empty(attachment.Handles);
        Assert.Equal(0, runtime.Engine.EdgesFor(Updates.CardDestroyed).Count);
    }

    /// <summary><c>loaded</c> 的组件在宿主加载后装配、<c>placed</c> 的在入场后装配；两者不会同时装配同一实例。</summary>
    [Fact]
    public void LoadedAssemblesOnLoad_PlacedAssemblesOnPlace_NotBoth()
    {
        var runtime = Scene(out var host, trace: out _, placedWhen: ActiveWhen.Placed, loadedWhen: ActiveWhen.Loaded);

        var placed = runtime.ConstructComponent(host, "comp.placed", "p");
        var loaded = runtime.ConstructComponent(host, "comp.loaded", "l");

        Assert.Equal(1, runtime.AssembleComponents(host, ActiveWhen.Loaded));
        Assert.True(loaded.IsAssembled);
        Assert.False(placed.IsAssembled);

        Assert.Equal(0, runtime.AssembleComponents(host, ActiveWhen.Loaded)); // 幂等
        Assert.Equal(1, runtime.AssembleComponents(host, ActiveWhen.Placed));
        Assert.True(placed.IsAssembled);
        Assert.False(runtime.AssembleComponents(host, ActiveWhen.Placed) > 0); // 不重复装配
    }

    /// <summary>同一 prefab 在同一宿主上至多一份实例（R3i：重复引用＝拒绝，不引入组件参数）。</summary>
    [Fact]
    public void DuplicateComponentPrefab_OnSameHost_IsRejected()
    {
        var runtime = Scene(out var host, trace: out _);
        runtime.ConstructComponent(host, "comp.react", "a");

        var error = Assert.Throws<PrefabLoadException>(() => runtime.ConstructComponent(host, "comp.react", "b"));
        Assert.Contains("comp.react", error.Message, StringComparison.Ordinal);
    }

    /// <summary>装配失败上浮：<c>requires-host</c> 不覆盖实际宿主＝拒绝（不产生"少了某个组件的卡"）。</summary>
    [Fact]
    public void RequiresHostNotCoveringActualHost_IsRejected()
    {
        var runtime = Scene(out var host, trace: out _, requiresHost: HostKind.Order);
        var attachment = runtime.ConstructComponent(host, "comp.react", "r");

        var error = Assert.Throws<PrefabLoadException>(() => attachment.Assemble());
        Assert.Contains("requires-host", error.Message, StringComparison.Ordinal);
    }

    // ==================== ② 复装＝重建实例（R3i） ====================

    /// <summary>复装＝新建组件实体、私有字段复位（不提供"复用实例 + 重跑初始化"的路径）。</summary>
    [Fact]
    public void Reassemble_CreatesNewInstance_PrivateFieldsReset()
    {
        var runtime = Scene(out var host, trace: out _);

        var first = runtime.ConstructComponent(host, "comp.stateful", "s1");
        first.Component.SetPrivateField("hits", 7);
        Assert.Equal(7, first.Component.GetPrivateField("hits"));

        runtime.DisposeComponent(first);
        Assert.True(first.IsDisposed);

        var second = runtime.ConstructComponent(host, "comp.stateful", "s2");
        Assert.NotSame(first.Component, second.Component);
        Assert.Equal(0, second.Component.GetPrivateField("hits")); // 私有字段复位
    }

    /// <summary>卸载＝逐句柄撤销：组件卸载后它在信号上的边全部撤销，实体也从存活集合移除。</summary>
    [Fact]
    public void DisposeComponent_RevokesAllEdges_AndDestroysEntity()
    {
        var runtime = Scene(out var host, trace: out _);
        var attachment = runtime.AddComponent(host, "comp.react", "r");

        Assert.Equal(2, runtime.Engine.EdgesFor(Updates.CardDestroyed).Count); // 作者边 + 框架注销边

        runtime.DisposeComponent(attachment);

        Assert.Equal(0, runtime.Engine.EdgesFor(Updates.CardDestroyed).Count);
        Assert.DoesNotContain(attachment.Component, runtime.Entities);
        Assert.DoesNotContain(attachment, runtime.Components);

        // 幂等：重复卸载无异常、无副作用。
        runtime.DisposeComponent(attachment);
    }

    // ==================== ③ 注销兜底（R1f） ====================

    /// <summary>
    /// 框架注销边：来源 <c>framework:disposal</c>、位次＝该组件所有已声明挂载之后、宿主销毁即触发销毁。
    /// </summary>
    [Fact]
    public async Task FrameworkDisposalEdge_IsLast_AndDisposesComponent()
    {
        var runtime = Scene(out var host, trace: out var trace);
        runtime.AddComponent(host, "comp.two", "two");

        var edges = runtime.Engine.EdgesFor(Updates.CardDestroyed).Snapshot();
        Assert.Equal(3, edges.Count); // 两条作者边 + 一条框架注销边
        Assert.Equal(new[] { "first", "second" }, edges.Take(2).Select(static e => e.Member.Name));
        Assert.Equal(ComponentDisposalPolicy.DisposalMemberName, edges[^1].Member.Name);
        Assert.Equal(EdgeSource.FrameworkDisposal, edges[^1].Source);
        Assert.Equal("engine#card.destroyed", edges[^1].Declaration);

        await runtime.EmitAsync(Updates.CardDestroyed);

        Assert.Equal(new[] { "first", "second" }, trace); // 作者边先于框架注销边
        Assert.Empty(runtime.Components); // 框架注销边把组件实例一并销毁
    }

    /// <summary>任何组件卸载后，它在 <c>engine.card-destroyed</c> 上的边全部撤销（含框架注销边）。</summary>
    [Fact]
    public async Task FrameworkDisposal_LeavesNoEdgesBehind()
    {
        var runtime = Scene(out var host, trace: out _);
        runtime.AddComponent(host, "comp.two", "two");

        await runtime.EmitAsync(Updates.CardDestroyed);

        Assert.Empty(runtime.Engine.EdgesFor(Updates.CardDestroyed).All);
        Assert.Empty(runtime.Components);
    }

    /// <summary>显式声明的组件同样会在宿主销毁时注销（兜底隐式＝不依赖作者声明）。</summary>
    [Fact]
    public async Task ComponentWithoutAuthorDisposalEdge_StillDisposedWithHost()
    {
        var runtime = Scene(out var host, trace: out _);
        var attachment = runtime.AddComponent(host, "comp.silent", "silent");

        Assert.Equal(1, runtime.Engine.EdgesFor(Updates.CardDestroyed).Count); // 只有框架注销边

        await runtime.EmitAsync(Updates.CardDestroyed);

        Assert.True(attachment.IsDisposed);
    }

    /// <summary>组件预制体把 <c>effect.removed</c> 声明为兜底信号＝装载期拒绝（R1f）。</summary>
    [Fact]
    public void DisposalSignalEffectRemoved_IsRejectedAtLoad()
    {
        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition(
                "comp.bad",
                EntityKind.Component,
                members: new[] { new MemberDefinition("run", visibility: MemberVisibility.Mountable) },
                mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", "run") },
                disposalSignal: Updates.EffectRemoved));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("comp.bad"));
        Assert.Contains(Updates.EffectRemoved, error.Message, StringComparison.Ordinal);
    }

    /// <summary>作者不得声明 <c>framework.</c> 前缀的成员（框架保留名）。</summary>
    [Fact]
    public void ReservedFrameworkMemberName_IsRejectedAtLoad()
    {
        var catalog = new PrefabCatalog().Add(new PrefabDefinition(
            "comp.reserved",
            EntityKind.Component,
            members: new[] { new MemberDefinition("framework.one-shot") }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("comp.reserved"));
        Assert.Contains("framework.", error.Message, StringComparison.Ordinal);
    }

    // ==================== ④ 一次性（R3k） ====================

    /// <summary>
    /// 一次性组件：任一成员首次执行后即消费；**同一执行链内连续两次命中**，第二次不执行。
    /// </summary>
    [Fact]
    public async Task OneShot_RunsOnce_ThenSecondHitIsRejected()
    {
        var runtime = Scene(out _, trace: out var trace, oneShot: true);
        var host = runtime.CreateEntity("card.unit", "卡");
        var attachment = runtime.AddComponent(host, "comp.react", "once");

        var first = await runtime.RunMemberAsync(attachment.Component, "react");
        var second = await runtime.RunMemberAsync(attachment.Component, "react");

        Assert.True(first.Executed);
        Assert.False(second.Executed); // 第二次不执行（执行前置位）
        Assert.True(second.ValidationRejected);
        Assert.Equal(new[] { "react" }, trace);
        Assert.True(attachment.Component.OneShotConsumed);

        // 一次性消费后仍能被注销（框架保留成员不参与一次性判定——否则组件永远无法卸载）。
        await runtime.EmitAsync(Updates.CardDestroyed);
        Assert.True(attachment.IsDisposed);
    }

    /// <summary>一次性组件无任何外部可触发边＝装载期拒绝（它永不执行、也就永不注销）。</summary>
    [Fact]
    public void OneShotWithoutTriggerableEdge_IsRejected()
    {
        var catalog = new PrefabCatalog().Add(new PrefabDefinition(
            "comp.idle",
            EntityKind.Component,
            members: new[] { new MemberDefinition("run", visibility: MemberVisibility.Mountable) },
            oneShot: true));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("comp.idle"));
        Assert.Contains("one-shot", error.Message, StringComparison.Ordinal);
    }

    /// <summary>一次性位是实体内置状态：不占作者私有字段；重建实例后复位。</summary>
    [Fact]
    public async Task OneShotFlag_IsBuiltInState_AndResetsOnRebuild()
    {
        var runtime = Scene(out _, trace: out _, oneShot: true);
        var host = runtime.CreateEntity("card.unit", "卡");
        var first = runtime.AddComponent(host, "comp.react", "once");

        await runtime.RunMemberAsync(first.Component, "react");
        Assert.True(first.Component.OneShotConsumed);
        Assert.Null(first.Component.Type.FindPrivateField("__oneShot")); // 不占作者字段

        runtime.DisposeComponent(first); // 卸载＝销毁实例（复装的前提）
        var second = runtime.AddComponent(host, "comp.react", "once2");
        Assert.False(second.Component.OneShotConsumed); // 新实例＝复位
    }

    // ==================== ⑤ 装配期零副作用与失败不留痕 ====================

    /// <summary>装配本身不执行任何业务逻辑（R6／R8 第 17 条：初始化期零执行、零事件）。</summary>
    [Fact]
    public void Assemble_DoesNotExecuteBusinessLogic()
    {
        var runtime = Scene(out var host, trace: out var trace);
        runtime.AddComponent(host, "comp.two", "two");

        Assert.Empty(trace); // 装配只建边，不跑成员
    }

    /// <summary>装配失败不留半装配状态：没有任何边被建立，组件可被安全丢弃。</summary>
    [Fact]
    public void FailedAssemble_LeavesNoEdges()
    {
        var runtime = Scene(out var host, trace: out _, requiresHost: HostKind.Order);
        var attachment = runtime.ConstructComponent(host, "comp.two", "two");

        Assert.Throws<PrefabLoadException>(() => attachment.Assemble());

        Assert.Empty(runtime.Engine.EdgesFor(Updates.CardDestroyed).All);
        Assert.False(attachment.IsAssembled);
    }

    /// <summary><c>active-when</c> 缺省＝<c>placed</c>，且缺省值在**声明层**可读到（不靠 C# 默认参数隐式决定）。</summary>
    [Fact]
    public void ActiveWhen_DefaultsToPlaced_AndIsVisibleInDeclaration()
    {
        var definition = new PrefabDefinition(
            "comp.default",
            EntityKind.Component,
            members: new[] { new MemberDefinition("run", visibility: MemberVisibility.Mountable) });

        Assert.Equal(ActiveWhen.Placed, definition.ActiveWhen); // 声明 dto 上可见
        Assert.Equal(ActiveWhen.Placed, new PrefabCatalog().Add(definition).Resolve("comp.default").ActiveWhen);
    }

    /// <summary>非可组合实体声明组件＝装载期拒绝（R3d：可组合是实体的一种声明能力）。</summary>
    [Fact]
    public void NonComposableEntityDeclaringComponents_IsRejected()
    {
        var catalog = new PrefabCatalog().Add(new PrefabDefinition(
            "handler.host",
            EntityKind.Handler,
            components: new[] { "comp.react" }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("handler.host"));
        Assert.Contains("可组合", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 注销兜底的规则只有一处实现（<see cref="ComponentDisposalPolicy"/>）：
    /// 旧 <c>Effect</c> 构造入口随类型一并删除，归一化不再有"第二份结果"可对齐——
    /// 本测试只锁策略类自身的三条规则（默认项恒在最前／去重保序／拒绝自指）。
    /// </summary>
    [Fact]
    public void DisposalRules_HaveSingleSource()
    {
        // 策略类：默认项恒在最前、去重保序。
        Assert.Equal(
            new[] { Updates.CardDestroyed, GameUpdatesLikeSignal },
            ComponentDisposalPolicy.NormalizeSignals(new[] { GameUpdatesLikeSignal, Updates.CardDestroyed }, "x"));

        // 去重保序（缺省项恒含、恒在首位——"恒含"是契约，不是"仅当声明里出现"）。
        Assert.Equal(
            new[] { Updates.CardDestroyed, GameUpdatesLikeSignal },
            ComponentDisposalPolicy.NormalizeSignals(
                new[] { GameUpdatesLikeSignal, GameUpdatesLikeSignal }, "x"));

        // 必须拒绝 effect.removed（自指）。
        Assert.Throws<ArgumentException>(
            () => ComponentDisposalPolicy.NormalizeSignals(new[] { Updates.EffectRemoved }, "x"));
    }

    /// <summary>测试用自定义兜底信号（避开与真实信号的耦合）。</summary>
    private const string GameUpdatesLikeSignal = "turn.start.before";

    /// <summary>
    /// 初始化期（装配）没有任何"读对局状态"的入口（R1h／R6）：装配方法不接收视图／上下文／引擎状态。
    /// 这是靠 **API 形状** 物理强制的边界，不是文档约定。
    /// </summary>
    [Fact]
    public void Assembler_TakesNoRuntimeViewOrContext()
    {
        var assemble = typeof(ComponentAttachment).GetMethod(nameof(ComponentAttachment.Assemble))!;
        Assert.Empty(assemble.GetParameters()); // 只有 this：拿不到血量／场上／随机数／回合

        var construct = typeof(OrchestrationRuntime).GetMethod(nameof(OrchestrationRuntime.ConstructComponent))!;
        var parameters = construct.GetParameters().Select(static p => p.ParameterType.Name).ToArray();
        Assert.Equal(new[] { "OrchestrationEntity", "String", "String" }, parameters); // 宿主／预制体／名字
    }

    // ==================== 场景构件 ====================

    /// <summary>
    /// 引擎（内核 4 信号）＋一张单位卡类型预制体 ＋ 四个组件预制体：
    /// <c>comp.react</c>（挂 card.destroyed 一个成员）、<c>comp.two</c>（两个作者边）、
    /// <c>comp.silent</c>（无作者边）、<c>comp.loaded</c>／<c>comp.placed</c>（时点各一）、<c>comp.stateful</c>（私有字段）。
    /// </summary>
    private static OrchestrationRuntime Scene(
        out OrchestrationEntity host,
        out List<string> trace,
        ActiveWhen placedWhen = ActiveWhen.Placed,
        ActiveWhen loadedWhen = ActiveWhen.Loaded,
        HostKind requiresHost = HostKind.Any,
        bool oneShot = false)
    {
        var log = new List<string>();
        trace = log;

        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition("card.unit", EntityKind.Card, hosts: HostKind.Unit))
            .Add(Component("comp.react", "react", requiresHost, log, placedWhen, oneShot: oneShot))
            .Add(new PrefabDefinition(
                "comp.two",
                EntityKind.Component,
                requiresHost: requiresHost,
                members: new[]
                {
                    Member("first", log),
                    Member("second", log),
                },
                mounts: new[]
                {
                    new MountDeclaration($"engine#{Updates.CardDestroyed}", "first"),
                    new MountDeclaration($"engine#{Updates.CardDestroyed}", "second"),
                },
                activeWhen: placedWhen))
            .Add(new PrefabDefinition(
                "comp.silent",
                EntityKind.Component,
                requiresHost: requiresHost,
                members: new[] { Member("run", log) },
                activeWhen: placedWhen))
            .Add(new PrefabDefinition(
                "comp.loaded",
                EntityKind.Component,
                requiresHost: requiresHost,
                members: new[] { Member("run", log) },
                mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", "run") },
                activeWhen: loadedWhen))
            .Add(new PrefabDefinition(
                "comp.placed",
                EntityKind.Component,
                requiresHost: requiresHost,
                members: new[] { Member("run", log) },
                mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", "run") },
                activeWhen: ActiveWhen.Placed))
            .Add(new PrefabDefinition(
                "comp.stateful",
                EntityKind.Component,
                requiresHost: requiresHost,
                members: new[] { Member("run", log) },
                privateFields: new[] { new PrivateFieldDefinition("hits", ViewFieldType.Number, 0) }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        host = runtime.CreateEntity("card.unit", "卡");
        return runtime;
    }

    private static PrefabDefinition Component(
        string id, string member, HostKind requiresHost, List<string> log, ActiveWhen activeWhen, bool oneShot)
        => new(
            id,
            EntityKind.Component,
            requiresHost: requiresHost,
            members: new[] { Member(member, log) },
            mounts: new[] { new MountDeclaration($"engine#{Updates.CardDestroyed}", member) },
            activeWhen: activeWhen,
            oneShot: oneShot);

    private static MemberDefinition Member(string name, List<string> log)
        => new(
            name,
            ViewSpec.Empty,
            MemberVisibility.Mountable,
            ownEdges: new[] { ModelKit.Trace(log, name) });
}
