using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S2 验收（内核实体模型）：无主动／被动分类字段、无 band、预制体继承与整段覆写、
/// 可见性位标志（默认全关／子只收紧）、handler 全局懒注册一份、成员表装载期静态可枚举、
/// 执行序＝自有边 → 外来边声明序 → 框架边。
/// </summary>
public class KernelModelTests
{
    // ---------- 模型里不存在「主动／被动」分类字段与 band 概念 ----------

    [Fact]
    public void 成员模型不存在主动被动分类字段()
    {
        var members = typeof(MemberDefinition).GetProperties().Select(static p => p.Name).ToArray();

        Assert.DoesNotContain(members, static n => n.Contains("Kind", StringComparison.Ordinal));
        Assert.DoesNotContain(members, static n => n.Contains("Active", StringComparison.Ordinal));
        Assert.DoesNotContain(members, static n => n.Contains("Passive", StringComparison.Ordinal));
    }

    [Fact]
    public void 模型命名空间内不存在band概念()
    {
        var modelTypes = typeof(MemberDefinition).Assembly.GetTypes()
            .Where(static t => t.Namespace == "Orc.Core.Model")
            .ToArray();

        Assert.NotEmpty(modelTypes);
        Assert.DoesNotContain(modelTypes, static t => t.Name.Contains("Band", StringComparison.Ordinal));

        // handler 的参数形态里也没有 band 位（old Trigger.Register 的 band 参数在模型侧不存在）。
        var parameters = typeof(KernelHandler).GetMethod("Invoke")!.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.DoesNotContain(parameters, static p => p.ParameterType.Name.Contains("Band", StringComparison.Ordinal));
    }

    // ---------- 继承与覆写（R3a） ----------

    [Fact]
    public void 子预制体覆写父成员为整段替换且父声明不受影响()
    {
        var trace = new List<string>();
        var parentMember = new MemberDefinition(
            "deploy", ViewSpec.Empty, MemberVisibility.All, new[] { ModelKit.Trace(trace, "parent") });
        var childMember = new MemberDefinition(
            "deploy", ViewSpec.Empty, MemberVisibility.Callable, new[] { ModelKit.Trace(trace, "child") });

        var catalog = new PrefabCatalog()
            .Add(ModelKit.CardBase(parentMember))
            .Add(ModelKit.UnitCard(childMember));

        var parent = catalog.Resolve("card.base");
        var child = catalog.Resolve("card.unit");

        // 覆写＝整段替换：子成员只有一个定义来源（读子预制体即知最终行为）。
        Assert.Same(parentMember, parent.FindMember("deploy"));
        Assert.Same(childMember, child.FindMember("deploy"));
        Assert.Single(child.FindMember("deploy")!.OwnEdges);

        // 继承链可枚举（根→叶子）。
        Assert.Equal(new[] { "card.base", "card.unit" }, child.Chain);
    }

    [Fact]
    public void 子预制体放宽父可见性被拒绝()
    {
        var parent = new MemberDefinition("deploy", ViewSpec.Empty, MemberVisibility.Callable);
        var child = new MemberDefinition("deploy", ViewSpec.Empty, MemberVisibility.All);

        var catalog = new PrefabCatalog()
            .Add(ModelKit.CardBase(parent))
            .Add(ModelKit.UnitCard(child));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("card.unit"));
        Assert.Equal(PrefabErrorCategories.Visibility, error.Category);
        Assert.Contains("只能收紧", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 继承会一并继承成员视图与私有字段()
    {
        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition(
                "card.base",
                EntityKind.Card,
                members: new[]
                {
                    new MemberDefinition(
                        "played",
                        ViewSpec.Of(("card", ViewFieldType.Entity)),
                        MemberVisibility.All),
                },
                privateFields: new[] { new PrivateFieldDefinition("charges", ViewFieldType.Number, 2) }))
            .Add(ModelKit.UnitCard());

        var type = catalog.Resolve("card.unit");
        var member = type.FindMember("played");

        Assert.NotNull(member);
        Assert.True(member!.View.Contains("card"));
        Assert.Equal(2, type.FindPrivateField("charges")!.InitialValue);
    }

    // ---------- 可见性（R5） ----------

    [Fact]
    public void 可见性默认全关且位标志可任意组合()
    {
        var member = new MemberDefinition("m");

        Assert.Equal(MemberVisibility.None, member.Visibility);
        Assert.False(member.Has(MemberVisibility.Callable));
        Assert.False(member.Has(MemberVisibility.Hostable));
        Assert.False(member.Has(MemberVisibility.Mountable));

        var combo = new MemberDefinition("m", visibility: MemberVisibility.Hostable | MemberVisibility.Mountable);
        Assert.True(combo.Has(MemberVisibility.Hostable));
        Assert.True(combo.Has(MemberVisibility.Mountable));
        Assert.False(combo.Has(MemberVisibility.Callable));
    }

    [Fact]
    public void 子预制体可收紧父可见性()
    {
        var catalog = new PrefabCatalog()
            .Add(ModelKit.CardBase(new MemberDefinition("m", visibility: MemberVisibility.All)))
            .Add(ModelKit.UnitCard(new MemberDefinition("m", visibility: MemberVisibility.Callable)));

        var type = catalog.Resolve("card.unit");

        Assert.Equal(MemberVisibility.Callable, type.FindMember("m")!.Visibility);
    }

    // ---------- handler 全局懒注册（R4） ----------

    [Fact]
    public void 同一handler资产被一百个预制体引用时全局只有一份()
    {
        var handlers = new HandlerRegistry();
        var calls = 0;
        handlers.RegisterFactory("shared", () =>
        {
            calls++;
            return handlers.Register("shared", (_, _, _) => Task.CompletedTask);
        });

        var assets = new List<HandlerAsset>();
        for (var i = 0; i < 100; i++)
        {
            var prefab = new PrefabDefinition(
                $"p{i}",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition("m", ownEdges: new[] { new OwnedEdge(handlers.Resolve("shared")) }),
                });
            assets.Add(prefab.Members[0].OwnEdges[0].Asset!);
        }

        Assert.Equal(1, handlers.Count);
        Assert.Equal(1, calls);
        Assert.All(assets, asset => Assert.Same(assets[0], asset));
    }

    // ---------- 成员表装载期静态枚举 ----------

    [Fact]
    public void 成员表可在装载期静态枚举且声明序可推出()
    {
        var catalog = new PrefabCatalog()
            .Add(ModelKit.CardBase(new MemberDefinition("base-a"), new MemberDefinition("base-b")))
            .Add(ModelKit.UnitCard(new MemberDefinition("deploy")));

        var type = catalog.Resolve("card.unit");

        Assert.Equal(new[] { "base-a", "base-b", "deploy" }, type.MemberNames);
        Assert.True(type.HasMember("deploy"));
        Assert.Null(type.FindMember("not-exists"));
    }

    // ---------- 执行序：自有边 → 外来边声明序 → 框架边 ----------

    [Fact]
    public async Task 成员执行序由自有边外来边声明序与框架边推出()
    {
        var trace = new List<string>();

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(new MemberDefinition(
                "pipeline",
                ViewSpec.Empty,
                MemberVisibility.All,
                new[] { ModelKit.Trace(trace, "own") })))
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "card.unit",
                EntityKind.Card,
                extends: "card.base",
                hosts: HostKind.Unit,
                components: new[] { "comp.first", "comp.second" }))
            .Add(new PrefabDefinition(
                "comp.first",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition("a", visibility: MemberVisibility.Mountable, ownEdges: new[] { ModelKit.Trace(trace, "a") }),
                    new MemberDefinition("dispose", visibility: MemberVisibility.Mountable, ownEdges: new[] { ModelKit.Trace(trace, "dispose1") }),
                },
                mounts: new[] { new MountDeclaration("engine#pipeline", "a") }))
            .Add(new PrefabDefinition(
                "comp.second",
                EntityKind.Component,
                members: new[] { new MemberDefinition("b", visibility: MemberVisibility.Mountable, ownEdges: new[] { ModelKit.Trace(trace, "b") }) },
                mounts: new[] { new MountDeclaration("engine#pipeline", "b") }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var unit = runtime.CreateEntity("card.unit", "单位");
        var first = runtime.CreateEntity("comp.first", "组件1", unit);
        var second = runtime.CreateEntity("comp.second", "组件2", unit);

        runtime.Bind(first);
        runtime.Bind(second);

        // 框架注销边：位次＝该组件的最后一个挂载项（framework:disposal 的位次语义）。
        runtime.Engine.EdgesFor("pipeline").Attach(
            first,
            first.Type.FindMember("dispose")!,
            EdgeSource.FrameworkDisposal,
            EdgeOrderSource.Declared,
            DeclarationOrder.FrameworkLast(1));

        await runtime.RunMemberAsync(runtime.Engine, "pipeline");

        Assert.Equal(new[] { "own", "a", "dispose1", "b" }, trace);
    }

    // ---------- 运行时注册（R1e／R1h）：注册后立即可寻址与挂载；失败不留残留 ----------

    [Fact]
    public async Task 运行时注册的成员立即可被寻址与挂载且重名注册被拒绝不留残留()
    {
        var trace = new List<string>();

        var runtime = new OrchestrationRuntime(
            new PrefabCatalog()
                .Add(ModelKit.EnginePrefab())
                .Add(ModelKit.CardBase())
                .Add(new PrefabDefinition(
                    "comp.late",
                    EntityKind.Component,
                    members: new[]
                    {
                        new MemberDefinition(
                            "later",
                            ViewSpec.Empty,
                            MemberVisibility.Mountable,
                            new[] { ModelKit.Trace(trace, "later") }),
                    })),
            new HandlerRegistry());

        // 运行时注册＝创建新的可寻址成员（不是"任意字符串 Emit"）。
        runtime.RegisterMember(
            runtime.Engine,
            new MemberDefinition(
                "sandbox.custom",
                ViewSpec.Empty,
                MemberVisibility.All,
                new[] { ModelKit.Trace(trace, "signal") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.late", "组件", host);

        // 注册后立即可被挂载声明（运行期挂边）引用。
        runtime.MountAtRuntime(component, "later", "engine#sandbox.custom");
        Assert.Equal(1, runtime.Engine.EdgesFor("sandbox.custom").Count);

        await runtime.RunMemberAsync(runtime.Engine, "sandbox.custom");
        Assert.Equal(new[] { "signal", "later" }, trace);

        // 重名＝拒绝注册，且不残留：原成员的边集合不被改写。
        var duplicate = Assert.Throws<PrefabLoadException>(() => runtime.RegisterMember(
            runtime.Engine,
            new MemberDefinition(
                "sandbox.custom",
                ViewSpec.Empty,
                MemberVisibility.All,
                new[] { ModelKit.Trace(trace, "dup") })));

        Assert.Equal(PrefabErrorCategories.Structure, duplicate.Category);
        Assert.Single(runtime.Engine.FindMember("sandbox.custom")!.OwnEdges);

        trace.Clear();
        await runtime.RunMemberAsync(runtime.Engine, "sandbox.custom");
        Assert.Equal(new[] { "signal", "later" }, trace);
    }
}
