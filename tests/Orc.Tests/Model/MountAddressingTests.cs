using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S4 验收（挂载与成员寻址）：<c>作用域#成员名</c> 只按第一个 <c>#</c> 切分、成员名可含点号；
/// 目标不存在／可见性越界／视图字段不被包含／相位混用 均装载期 fail-fast；
/// 三根静态解析、跨层合并、<c>host#</c> 按 requires-host 全部宿主类型校验、<c>self#</c> 不受可见性约束。
/// </summary>
public class MountAddressingTests
{
    private static MemberDefinition Member(
        string name,
        ViewSpec? view = null,
        MemberVisibility visibility = MemberVisibility.All,
        PrefabLayer layer = PrefabLayer.Kernel,
        string? declaredBy = null,
        params OwnedEdge[] ownEdges)
        => new(name, view ?? ViewSpec.Empty, visibility, ownEdges, validation: null, layer: layer, declaredBy: declaredBy);

    private static OrchestrationRuntime BuildRuntime(params PrefabDefinition[] definitions)
        => new(new PrefabCatalog().AddRange(definitions), new HandlerRegistry());

    // ---------- 寻址语法 ----------

    [Fact]
    public void 成员名含点号时按第一个井号切分且零转换()
    {
        var address = MemberAddress.Parse("engine#unit.position.changed");

        Assert.Equal(AddressScope.Engine, address.Scope);
        Assert.Equal("unit.position.changed", address.Member);
        Assert.Equal("engine#unit.position.changed", address.ToString());

        // 旧 hooks → engine#<hook>：纯前缀替换、无损可逆。
        foreach (var hook in new[] { "card.load", "unit.position.changed", "keyword.attack.finished" })
        {
            Assert.Equal(hook, MemberAddress.Parse($"engine#{hook}").Member);
        }
    }

    [Fact]
    public void 未知作用域与缺失分隔符被明确拒绝()
    {
        var unknown = Assert.Throws<ArgumentException>(() => MemberAddress.Parse("engine.damage-apply#on-before"));
        Assert.Contains("未知作用域", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("engine.damage-apply", unknown.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<ArgumentException>(() => MemberAddress.Parse("card.destroyed"));
        Assert.Contains("缺少作用域分隔符", missing.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<ArgumentException>(() => MemberAddress.Parse("host#   "));
        Assert.Contains("成员名为空", empty.Message, StringComparison.Ordinal);

        // 显示名（旧 injects 的定位方式）不带作用域分隔符 ⇒ 直接拒绝，不按名兜底。
        Assert.False(MemberAddress.TryParse("伤害处理触发器", out _, out var displayNameError));
        Assert.Contains("缺少作用域分隔符", displayNameError!, StringComparison.Ordinal);
    }

    // ---------- 目标不存在／可见性 ----------

    [Fact]
    public void 目标成员不存在时装载期报错并指出作用域与成员名()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.bad-target",
                EntityKind.Component,
                members: new[] { Member("edge", visibility: MemberVisibility.Mountable) },
                mounts: new[] { new MountDeclaration("engine#missing-member", "edge") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.bad-target", "组件", host);

        var plan = MountPlanner.Plan(runtime, component);

        Assert.False(plan.Report.IsValid);
        var error = Assert.Single(plan.Report.Errors);
        Assert.Equal(PrefabErrorCategories.Addressing, error.Category);
        Assert.Equal("engine#missing-member", error.Target);
        Assert.Contains("missing-member", error.Message, StringComparison.Ordinal);

        Assert.Throws<PrefabLoadException>(() => runtime.Bind(component));
    }

    [Fact]
    public void 跨实体挂载要求目标可作宿主且来源可被挂载()
    {
        var notHostable = BuildRuntime(
            ModelKit.EnginePrefab(Member("signal", visibility: MemberVisibility.Callable)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.a",
                EntityKind.Component,
                members: new[] { Member("edge", visibility: MemberVisibility.Mountable) },
                mounts: new[] { new MountDeclaration("engine#signal", "edge") }));

        var hostA = notHostable.CreateEntity("card.base", "宿主");
        var componentA = notHostable.CreateEntity("comp.a", "组件", hostA);
        var planA = MountPlanner.Plan(notHostable, componentA);
        Assert.False(planA.Report.IsValid);
        Assert.Equal(PrefabErrorCategories.Visibility, planA.Report.Errors[0].Category);
        Assert.Contains("可作宿主", planA.Report.Errors[0].Message, StringComparison.Ordinal);

        var notMountable = BuildRuntime(
            ModelKit.EnginePrefab(Member("signal")),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.b",
                EntityKind.Component,
                members: new[] { Member("edge", visibility: MemberVisibility.Callable) },
                mounts: new[] { new MountDeclaration("engine#signal", "edge") }));

        var hostB = notMountable.CreateEntity("card.base", "宿主");
        var componentB = notMountable.CreateEntity("comp.b", "组件", hostB);
        var planB = MountPlanner.Plan(notMountable, componentB);
        Assert.False(planB.Report.IsValid);
        Assert.Equal(PrefabErrorCategories.Visibility, planB.Report.Errors[0].Category);
        Assert.Contains("可被挂载", planB.Report.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 视图字段不在宿主视图与自身成员之内时拒绝挂载并报告缺失字段()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member(
                "damage-apply",
                ViewSpec.Of(("amount", ViewFieldType.Number)))),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.leak",
                EntityKind.Component,
                members: new[]
                {
                    Member(
                        "edge",
                        ViewSpec.Of(("amount", ViewFieldType.Number), ("secret", ViewFieldType.String)),
                        MemberVisibility.Mountable),
                },
                mounts: new[] { new MountDeclaration("engine#damage-apply", "edge") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.leak", "组件", host);
        var plan = MountPlanner.Plan(runtime, component);

        Assert.False(plan.Report.IsValid);
        var error = Assert.Single(plan.Report.Errors);
        Assert.Equal(PrefabErrorCategories.View, error.Category);
        Assert.NotNull(error.MissingFields);
        Assert.Equal(new[] { "secret" }, error.MissingFields!);
    }

    [Fact]
    public void 挂载声明中出现replace语义时装载期拒绝()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member("signal")),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.channel-mix",
                EntityKind.Component,
                members: new[] { Member("edge", visibility: MemberVisibility.Mountable) },
                mounts: new[] { new MountDeclaration("engine#signal", "edge", phase: "replace") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.channel-mix", "组件", host);
        var plan = MountPlanner.Plan(runtime, component);

        Assert.False(plan.Report.IsValid);
        Assert.Equal(PrefabErrorCategories.Channel, plan.Report.Errors[0].Category);
        Assert.Contains("两条通道不可混用", plan.Report.Errors[0].Message, StringComparison.Ordinal);
    }

    // ---------- 三根静态解析 ----------

    [Fact]
    public void engine_host_self三根在装载期静态解析()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member("signal")),
            ModelKit.CardBase(),
            ModelKit.UnitCard(Member("played")),
            ModelKit.OrderCard(Member("played")),
            ModelKit.CounterCard(Member("played")),
            ModelKit.HqCard(Member("played")),
            new PrefabDefinition(
                "comp.three-roots",
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[]
                {
                    Member("own"),
                    Member("edge", visibility: MemberVisibility.Mountable),
                },
                mounts: new[]
                {
                    new MountDeclaration("engine#signal", "edge"),
                    new MountDeclaration("host#played", "edge"),
                    new MountDeclaration("self#own", "edge"),
                }));

        var host = runtime.CreateEntity("card.unit", "单位");
        var component = runtime.CreateEntity("comp.three-roots", "组件", host);

        var plan = MountPlanner.Plan(runtime, component);
        plan.Report.ThrowIfInvalid();

        Assert.Equal(3, plan.Mounts.Count);
        Assert.Contains(plan.Mounts, static m => m.Address.Scope == AddressScope.Engine);
        Assert.Contains(plan.Mounts, static m => m.Address.Scope == AddressScope.Host);
        Assert.Contains(plan.Mounts, static m => m.Address.Scope == AddressScope.Self);

        var handles = runtime.Bind(component);
        Assert.Equal(3, handles.Count);
        Assert.Equal(1, runtime.Engine.EdgesFor("signal").Count);
        Assert.Equal(1, host.EdgesFor("played").Count);
        Assert.Equal(1, component.EdgesFor("own").Count);
    }

    [Fact]
    public void 跨层成员在同一engine根下合并解析()
    {
        // 内核基础预制体声明成员 → 游戏层 extends 后追加；两者同处 engine 根，解析对来源层无差别。
        var kernel = new PrefabDefinition(
            "engine",
            EntityKind.Engine,
            members: new[]
            {
                Member("card.load", layer: PrefabLayer.Kernel, declaredBy: "engine"),
                Member("card.destroyed", layer: PrefabLayer.Kernel, declaredBy: "engine"),
            });

        var game = new PrefabDefinition(
            "engine.game",
            EntityKind.Engine,
            extends: "engine",
            layer: PrefabLayer.Game,
            members: new[]
            {
                Member("card.damaged", layer: PrefabLayer.Game, declaredBy: "engine.game"),
                Member("unit.position.changed", layer: PrefabLayer.Game, declaredBy: "engine.game"),
            });

        var catalog = new PrefabCatalog()
            .Add(kernel)
            .Add(game)
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "comp.cross-layer",
                EntityKind.Component,
                members: new[] { Member("edge", visibility: MemberVisibility.Mountable) },
                mounts: new[]
                {
                    new MountDeclaration("engine#card.destroyed", "edge"),
                    new MountDeclaration("engine#unit.position.changed", "edge"),
                }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry(), "engine.game");
        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.cross-layer", "组件", host);

        Assert.Equal(
            new[] { "card.load", "card.destroyed", "card.damaged", "unit.position.changed" },
            runtime.Engine.Type.MemberNames);

        var plan = MountPlanner.Plan(runtime, component);
        plan.Report.ThrowIfInvalid();
        Assert.Equal(2, plan.Mounts.Count);

        // 来源层可读出（审查面用），但解析对来源层无差别。
        Assert.Equal(PrefabLayer.Kernel, runtime.Engine.Type.FindMember("card.destroyed")!.Layer);
        Assert.Equal(PrefabLayer.Game, runtime.Engine.Type.FindMember("card.damaged")!.Layer);
    }

    // ---------- host# 的 requires-host 校验 ----------

    [Fact]
    public void host作用域按requiresHost允许的全部宿主类型校验()
    {
        MemberDefinition Deploy() => new("deploy", ViewSpec.Empty, MemberVisibility.All);

        PrefabDefinition Component(HostKind requiresHost) => new(
            "comp.needs-deploy",
            EntityKind.Component,
            requiresHost: requiresHost,
            members: new[] { Member("edge", visibility: MemberVisibility.Mountable) },
            mounts: new[] { new MountDeclaration("host#deploy", "edge") });

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab())
            .Add(ModelKit.CardBase())
            .Add(ModelKit.UnitCard(Deploy()))
            .Add(ModelKit.OrderCard())
            .Add(ModelKit.CounterCard())
            .Add(ModelKit.HqCard())
            .Add(Component(HostKind.Any));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var host = runtime.CreateEntity("card.unit", "单位");
        var component = runtime.CreateEntity("comp.needs-deploy", "组件", host);

        var plan = MountPlanner.Plan(runtime, component);
        Assert.False(plan.Report.IsValid);
        Assert.Contains(
            plan.Report.Errors,
            static e => e.Message.Contains("card.order", StringComparison.Ordinal) && e.Message.Contains("deploy", StringComparison.Ordinal));

        // 收窄 requires-host 到该类型后装载通过。
        var narrowed = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab())
            .Add(ModelKit.CardBase())
            .Add(ModelKit.UnitCard(Deploy()))
            .Add(Component(HostKind.Unit));
        var runtime2 = new OrchestrationRuntime(narrowed, new HandlerRegistry());
        var host2 = runtime2.CreateEntity("card.unit", "单位");
        var component2 = runtime2.CreateEntity("comp.needs-deploy", "组件", host2);

        var plan2 = MountPlanner.Plan(runtime2, component2);
        plan2.Report.ThrowIfInvalid();
        Assert.Single(plan2.Mounts);
        Assert.Equal("host#deploy", plan2.Mounts[0].Address.ToString());
    }

    [Fact]
    public void self自挂载不受可见性约束()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.self",
                EntityKind.Component,
                members: new[]
                {
                    Member("other", visibility: MemberVisibility.None),
                    Member("edge", visibility: MemberVisibility.None),
                },
                mounts: new[] { new MountDeclaration("self#other", "edge") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.self", "组件", host);

        var plan = MountPlanner.Plan(runtime, component);
        plan.Report.ThrowIfInvalid();
        Assert.Single(plan.Mounts);

        var handles = runtime.Bind(component);
        Assert.Single(handles);
        Assert.Equal(1, component.EdgesFor("other").Count);
    }

    // ---------- 运行期挂载的位次 ----------

    [Fact]
    public async Task 运行期挂载追加在声明边之后且标注appended()
    {
        var trace = new List<string>();

        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member("flow", ownEdges: new[] { ModelKit.Trace(trace, "own") })),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.declared",
                EntityKind.Component,
                members: new[] { Member("declared", visibility: MemberVisibility.Mountable, ownEdges: new[] { ModelKit.Trace(trace, "declared") }) },
                mounts: new[] { new MountDeclaration("engine#flow", "declared") }),
            new PrefabDefinition(
                "comp.runtime",
                EntityKind.Component,
                members: new[] { Member("later", visibility: MemberVisibility.Mountable, ownEdges: new[] { ModelKit.Trace(trace, "runtime") }) }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var declared = runtime.CreateEntity("comp.declared", "声明边组件", host);
        var runtimeEntity = runtime.CreateEntity("comp.runtime", "运行期组件", host);

        runtime.Bind(declared);
        var handle = runtime.MountAtRuntime(runtimeEntity, "later", "engine#flow");

        Assert.Equal(EdgeOrderSource.Appended, handle.Edge.OrderSource);

        var snapshot = runtime.Engine.EdgesFor("flow").Snapshot();
        Assert.Equal(EdgeOrderSource.Declared, snapshot[0].OrderSource);
        Assert.Equal(EdgeOrderSource.Appended, snapshot[^1].OrderSource);

        await runtime.RunMemberAsync(runtime.Engine, "flow");
        Assert.Equal(new[] { "own", "declared", "runtime" }, trace);

        // 句柄撤销（owner 销毁时逐句柄撤销，不按来源扫表）。
        Assert.True(handle.Revoke());
        Assert.False(handle.Revoke());
        Assert.Single(runtime.Engine.EdgesFor("flow").Snapshot());
    }

    [Fact]
    public void 边表支持按声明序有序插入而不是只追加()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member("flow", visibility: MemberVisibility.All)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.a",
                EntityKind.Component,
                members: new[] { Member("m", visibility: MemberVisibility.Mountable) }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.a", "组件", host);
        var member = component.FindMember("m")!;
        var table = runtime.Engine.EdgesFor("flow");

        // 先插"后声明"的边，再插"先声明"的边：快照仍按声明序（placed 批插到已存在的 loaded 边之前）。
        table.Attach(component, member, EdgeSource.Explicit, EdgeOrderSource.Declared, new DeclarationOrder(2, 0, 0));
        table.Attach(component, member, EdgeSource.Explicit, EdgeOrderSource.Declared, new DeclarationOrder(1, 0, 0));
        table.Attach(component, member, EdgeSource.Explicit, EdgeOrderSource.Appended, default);

        var snapshot = table.Snapshot();
        Assert.Equal(new[] { 1, 2 }, snapshot.Take(2).Select(static e => e.Order.Component).ToArray());
        Assert.Equal(EdgeOrderSource.Appended, snapshot[^1].OrderSource);
    }

    [Fact]
    public void 运行期边随owner销毁撤销且不存在无主永久边()
    {
        var runtime = BuildRuntime(
            ModelKit.EnginePrefab(Member("flow", visibility: MemberVisibility.All)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.a",
                EntityKind.Component,
                members: new[] { Member("m", visibility: MemberVisibility.Mountable) }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.a", "组件", host);
        runtime.MountAtRuntime(component, "m", "engine#flow");

        Assert.Equal(1, runtime.Engine.EdgesFor("flow").Count);

        component.Destroy();

        Assert.Equal(0, runtime.Engine.EdgesFor("flow").Count);
    }
}
