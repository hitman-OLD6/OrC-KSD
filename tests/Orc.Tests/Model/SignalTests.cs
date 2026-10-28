using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S3（信号＝引擎实体的成员触发器／总线退役）的内核验收：
/// 原 <c>hooks</c> 的每个更新字符串都是 <c>engine#&lt;信号名&gt;</c> 成员；<c>Emit</c>＝调用该成员；订阅＝挂载；
/// 执行序只由声明序／追加序推出；运行时注册是唯一的动态入口；边全部绑 owner。
/// </summary>
public sealed class SignalTests
{
    // ==================== ① 信号即引擎成员（R1e） ====================

    /// <summary>内核 4 个信号都是引擎实体的成员触发器，且成员名零转换（含点号原样保留）。</summary>
    [Fact]
    public void KernelSignals_AreEngineMembers_WithMemberNameEqualUpdateName()
    {
        var runtime = KernelRuntime();

        Assert.Equal(4, runtime.Signals.Count);
        Assert.All(runtime.Signals, member =>
        {
            Assert.True(member.IsSignal);
            // 成员名零转换：hooks: ["unit.position.changed"] → engine#unit.position.changed（R1d）。
            Assert.Equal(member.Name, MemberAddress.Create(AddressScope.Engine, member.Name).Member);
            Assert.True(member.Has(MemberVisibility.Hostable));
            Assert.Equal(PrefabLayer.Kernel, member.Layer);
        });

        Assert.Equal(
            new[] { Updates.CardPlaced, Updates.CardDestroyed, Updates.CardData, Updates.EffectRemoved },
            runtime.Signals.Select(static m => m.Name));
        Assert.True(runtime.IsSignal(Updates.CardDestroyed));
        Assert.Equal(SignalTiming.Placed, runtime.TimingOf(Updates.CardPlaced));
    }

    /// <summary>模型里不存在 <c>hooks</c> 字段（R1e 的第一条验收；旧类型随批次 5 退役，新模型不得有它）。</summary>
    [Fact]
    public void Model_HasNoHooksField()
    {
        var types = new[]
        {
            typeof(PrefabDefinition), typeof(PrefabType), typeof(MemberDefinition),
            typeof(MountDeclaration), typeof(WrapDeclaration), typeof(PrivateFieldDefinition),
        };

        foreach (var type in types)
        {
            var members = type.GetMembers()
                .Select(static m => m.Name)
                .Where(static name => name.Contains("Hook", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(members);
        }
    }

    /// <summary>初始化期不执行业务逻辑（R6／S7）：构造运行时不会执行任何成员的自有边。</summary>
    [Fact]
    public void RuntimeConstruction_DoesNotExecuteBusinessLogic()
    {
        var counter = 0;
        var prefab = new PrefabDefinition(
            "engine",
            EntityKind.Engine,
            members: new[]
            {
                SignalMember.Create(Updates.CardPlaced),
                new MemberDefinition(
                    "probe",
                    ViewSpec.Empty,
                    MemberVisibility.Callable,
                    ownEdges: new[] { ModelKit.Own((_, _) => counter++) }),
            });
        var catalog = new PrefabCatalog().Add(prefab);

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());

        Assert.Equal(0, counter);
        Assert.NotNull(runtime.Engine.FindMember("probe"));
    }

    // ==================== ② Emit＝调用成员；订阅＝挂载（执行序只由声明序推出） ====================

    /// <summary>同一信号上的多个订阅者按**组件声明序**执行，与装配（Bind）顺序无关（R1g）。</summary>
    [Fact]
    public async Task Emit_RunsSubscribers_InComponentDeclarationOrder_NotAttachOrder()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        var first = runtime.CreateEntity("comp.first", "first", host);
        var second = runtime.CreateEntity("comp.second", "second", host);

        // 反序装配：先绑声明序靠后的组件，再绑靠前的。
        runtime.Bind(second);
        runtime.Bind(first);

        await runtime.EmitAsync(Updates.CardPlaced, Payload());

        Assert.Equal(new[] { "first", "second" }, trace);
    }

    /// <summary>重建实例的边按声明序**回填原位**，不追加到末尾（R1g）。</summary>
    [Fact]
    public async Task Rebind_InsertsEdgeBackAtDeclarationPosition()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        var first = runtime.CreateEntity("comp.first", "first", host);
        var second = runtime.CreateEntity("comp.second", "second", host);
        runtime.Bind(first);
        runtime.Bind(second);

        first.Destroy(); // 组件卸载＝逐句柄撤销（R1f）
        Assert.Single(runtime.DescribeSignal(Updates.CardPlaced));

        var reborn = runtime.CreateEntity("comp.first", "first#2", host);
        runtime.Bind(reborn);

        Assert.Equal(
            new[] { "comp.first", "comp.second" },
            runtime.DescribeSignal(Updates.CardPlaced).Select(static e => e.DeclaringPrefab));
        await runtime.EmitAsync(Updates.CardPlaced, Payload());
        Assert.Equal(new[] { "first", "second" }, trace);
    }

    /// <summary>同一信号上 declared 与 appended 边共存，且 <c>order-source</c> 按**边**标注（R1h）。</summary>
    [Fact]
    public async Task DeclaredAndAppended_Coexist_WithPerEdgeOrderSource()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        var declared = runtime.CreateEntity("comp.first", "declared", host);
        runtime.Bind(declared);

        var appended = runtime.CreateEntity("comp.second", "appended", host);
        runtime.MountAtRuntime(appended, RunMember, "engine#" + Updates.CardPlaced);

        var edges = runtime.DescribeSignal(Updates.CardPlaced);
        Assert.Equal(2, edges.Count);
        Assert.Equal(EdgeOrderSource.Declared, edges[0].OrderSource);
        Assert.Equal(EdgeOrderSource.Appended, edges[1].OrderSource);
        Assert.Equal(new[] { 1, 2 }, edges.Select(static e => e.Position));

        await runtime.EmitAsync(Updates.CardPlaced, Payload());
        Assert.Equal(new[] { "first", "second" }, trace);
    }

    /// <summary>运行期挂的边绑 owner：owner 销毁后边消失（不存在无主永久边，R1h）。</summary>
    [Fact]
    public void RuntimeEdge_IsOwnerBound_AndRevokedWithOwner()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        var owner = runtime.CreateEntity("comp.second", "temp", host);

        runtime.MountAtRuntime(owner, RunMember, "engine#" + Updates.CardPlaced);
        Assert.Single(runtime.DescribeSignal(Updates.CardPlaced));

        owner.Destroy();
        Assert.Empty(runtime.DescribeSignal(Updates.CardPlaced));
    }

    // ==================== ③ 运行时注册＝唯一动态入口（R1e／R1h） ====================

    /// <summary>运行时注册后立即可被寻址与挂载（自定义更新名的唯一进入方式）。</summary>
    [Fact]
    public async Task RegisterSignal_MakesItAddressableAndMountable()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        var component = runtime.CreateEntity("comp.custom", "custom", host);

        // 注册前：发射＝明确失败；挂载声明引用＝装载期明确失败。
        var notRegistered = await Assert.ThrowsAsync<SignalNotRegisteredException>(
            () => runtime.EmitAsync(CustomSignal));
        Assert.Equal(CustomSignal, notRegistered.SignalName);

        var loadError = Assert.Throws<PrefabLoadException>(() => runtime.Bind(component));
        Assert.Contains(CustomSignal, loadError.Message, StringComparison.Ordinal);
        Assert.Contains("engine", loadError.Message, StringComparison.Ordinal);

        // 注册后：可被发射，也可被挂载声明引用（同一门面、两个动作）。
        runtime.RegisterSignal(CustomSignal);
        runtime.Bind(component);
        await runtime.EmitAsync(CustomSignal);

        Assert.Equal(new[] { "custom" }, trace);
        Assert.Equal(PrefabLayer.Runtime, runtime.RequireSignal(CustomSignal).Layer);
    }

    /// <summary>注册与挂载是两个独立动作：注册**不**产生任何边（R1h：不存在"注册即挂载"的合并入口）。</summary>
    [Fact]
    public void RegisterSignal_DoesNotMountAnything()
    {
        var runtime = KernelRuntime();
        runtime.RegisterSignal(CustomSignal);

        Assert.Empty(runtime.DescribeSignal(CustomSignal));
        Assert.Equal(5, runtime.Signals.Count);
    }

    /// <summary>同名注册幂等；与既有**非信号**成员撞名＝拒绝且不留残留。</summary>
    [Fact]
    public void RegisterSignal_IsIdempotent_AndRejectsNonSignalCollision()
    {
        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition(
                GameEngineId,
                EntityKind.Engine,
                extends: KernelSignals.PrefabId,
                members: new[] { new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Callable) }));
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry(), GameEngineId);

        var first = runtime.RegisterSignal(CustomSignal);
        var second = runtime.RegisterSignal(CustomSignal);
        Assert.Same(first, second);

        Assert.Throws<SignalNotRegisteredException>(() => runtime.RegisterSignal("probe"));
        Assert.NotNull(runtime.Engine.FindMember("probe"));
        Assert.Equal(5, runtime.Signals.Count);
    }

    /// <summary>非信号成员不可被 Emit（"任意字符串直接 Emit"的路径不存在，S3 第 12 条）。</summary>
    [Fact]
    public async Task Emit_RejectsNonSignalMember()
    {
        var prefab = new PrefabDefinition(
            "engine",
            EntityKind.Engine,
            members: new[]
            {
                SignalMember.Create(Updates.CardPlaced),
                new MemberDefinition("flow", ViewSpec.Empty, MemberVisibility.Callable),
            });
        var runtime = new OrchestrationRuntime(new PrefabCatalog().Add(prefab), new HandlerRegistry());

        var error = await Assert.ThrowsAsync<SignalNotRegisteredException>(() => runtime.EmitAsync("flow"));
        Assert.Equal("flow", error.SignalName);
    }

    // ==================== ④ 无意义挂载＝装载期拒绝（S3 第 4c 条） ====================

    /// <summary><c>active-when: placed</c> 的组件挂加载期信号＝装载期拒绝（永不生效的配置）。</summary>
    [Fact]
    public void PlacedComponent_OnLoadSignal_IsRejectedAtLoadTime()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host);
        runtime.RegisterSignal(LoadSignal, SignalTiming.Load);
        var component = runtime.CreateEntity("comp.load", "early", host);

        var error = Assert.Throws<PrefabLoadException>(() => runtime.Bind(component));
        var item = Assert.Single(error.Errors);
        Assert.Equal(PrefabErrorCategories.Timing, item.Category);
        Assert.Contains("active-when=placed", item.Message, StringComparison.Ordinal);
        Assert.Contains(LoadSignal, item.Target, StringComparison.Ordinal);
    }

    /// <summary><c>active-when: loaded</c> 的组件挂加载期信号＝合法。</summary>
    [Fact]
    public async Task LoadedComponent_OnLoadSignal_IsAllowed()
    {
        var trace = new List<string>();
        var runtime = Scene(trace, out var host, activeWhen: ActiveWhen.Loaded);
        runtime.RegisterSignal(LoadSignal, SignalTiming.Load);
        var component = runtime.CreateEntity("comp.load", "early", host);

        runtime.Bind(component);
        await runtime.EmitAsync(LoadSignal);

        Assert.Equal(new[] { "load" }, trace);
    }

    // ==================== ⑤ 引擎实体＝对局级单例（R6b） ====================

    /// <summary>对局内不存在第二个引擎实体。</summary>
    [Fact]
    public void SecondEngineEntity_IsRejected()
    {
        var runtime = KernelRuntime();

        var error = Assert.Throws<InvalidOperationException>(() => runtime.CreateEntity("engine", "engine#2"));
        Assert.Contains("引擎实体", error.Message, StringComparison.Ordinal);
        Assert.Equal("engine", runtime.Engine.Name);
    }

    // ==================== ⑥ 载荷键同源（守护） ====================

    /// <summary>内核信号的载荷键与 <c>Orc.Cards.PayloadKeys</c> 同源（防两处字面值漂移）。</summary>
    [Fact]
    public void KernelSignalPayloadKeys_MatchLegacyPayloadKeys()
    {
        Assert.Equal(PayloadKeys.Card, KernelSignals.PayloadCard);
        Assert.Equal(PayloadKeys.Effect, KernelSignals.PayloadEffect);

        var placed = KernelSignals.CreateEnginePrefab().Members.Single(m => m.Name == Updates.CardPlaced);
        Assert.True(placed.View.Contains(PayloadKeys.Card));
    }

    // ==================== 场景构件 ====================

    private const string CustomSignal = "custom.placed";
    private const string LoadSignal = "test.load";
    private const string RunMember = "run";
    private const string GameEngineId = "engine.game";

    private static OrchestrationRuntime KernelRuntime()
        => new(KernelSignals.CreateCatalog(), new HandlerRegistry());

    private static IReadOnlyDictionary<string, object?> Payload()
        => new Dictionary<string, object?> { [PayloadKeys.Card] = new object() };

    /// <summary>
    /// 场景：内核引擎预制体＋一张单位卡类型预制体＋四个组件预制体（声明序 first → second → load → custom）。
    /// 每个组件都有两个成员：<c>run</c>（带自有边，被挂载声明引用）与 <c>link</c>（供运行期挂载用）。
    /// </summary>
    private static OrchestrationRuntime Scene(
        List<string> trace, out OrchestrationEntity host, ActiveWhen activeWhen = ActiveWhen.Placed)
    {
        var catalog = new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(new PrefabDefinition(
                "card.unit",
                EntityKind.Card,
                hosts: HostKind.Unit,
                components: new[] { "comp.first", "comp.second", "comp.load", "comp.custom" }))
            .Add(Component("comp.first", "first", Updates.CardPlaced, trace, activeWhen))
            .Add(Component("comp.second", "second", Updates.CardPlaced, trace, activeWhen))
            .Add(Component("comp.load", "load", LoadSignal, trace, activeWhen))
            .Add(Component("comp.custom", "custom", CustomSignal, trace, activeWhen));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        host = runtime.CreateEntity("card.unit", "host");
        return runtime;
    }

    private static PrefabDefinition Component(
        string id, string label, string signal, List<string> trace, ActiveWhen activeWhen)
        => new(
            id,
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            members: new[]
            {
                new MemberDefinition(
                    RunMember,
                    ViewSpec.Empty,
                    MemberVisibility.Mountable,
                    ownEdges: new[] { ModelKit.Own((_, _) => trace.Add(label)) }),
            },
            mounts: new[] { new MountDeclaration("engine#" + signal, RunMember) },
            activeWhen: activeWhen);
}
