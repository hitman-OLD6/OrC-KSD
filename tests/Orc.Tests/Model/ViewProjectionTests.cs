using System.Dynamic;
using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S5 验收（上下文视图投影，内核侧）：全仓唯一承载视图、封闭类型集拒绝嵌套、
/// 必填缺失首次执行 fail-fast、视图名字空间只含声明字段（私有字段走 owner）、
/// 同名禁止、运行期写入闸门、Owner 由框架回填、Host 转换语义不变。
/// </summary>
public class ViewProjectionTests
{
    private static OrchestrationRuntime Runtime(params PrefabDefinition[] definitions)
        => new(new PrefabCatalog().AddRange(definitions), new HandlerRegistry());

    [Fact]
    public void 全仓只有一个承载视图类型()
    {
        var carriers = typeof(ViewCarrier).Assembly.GetTypes()
            .Where(static t => t.Name.EndsWith("ViewCarrier", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(carriers);
        Assert.Same(typeof(ViewCarrier), carriers[0]);
        Assert.True(typeof(DynamicObject).IsAssignableFrom(typeof(ViewCarrier)));
    }

    [Fact]
    public void 封闭类型集只含八个成员且拒绝嵌套结构()
    {
        Assert.Equal(
            new[]
            {
                "Entity", "Number", "String", "Boolean",
                "EntityArray", "NumberArray", "StringArray", "BooleanArray",
            },
            Enum.GetNames<ViewFieldType>());

        Assert.Equal(ViewFieldType.NumberArray, ViewFieldTypes.Parse("number[]"));
        Assert.Throws<ArgumentException>(() => ViewFieldTypes.Parse("map<string,number>"));
        Assert.Throws<ArgumentException>(() => ViewFieldTypes.Parse("number[][]"));
        Assert.Throws<ArgumentException>(() => ViewFieldTypes.Parse("object"));
        Assert.Throws<ArgumentException>(() => ViewFieldTypes.Parse("entity?"));
    }

    [Fact]
    public async Task 必填字段缺失在首次执行时fail_fast()
    {
        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition(
                "needs-amount",
                ViewSpec.Of(("amount", ViewFieldType.Number)),
                MemberVisibility.All,
                new[] { ModelKit.Own(static (_, _) => { }) })));

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => runtime.RunMemberAsync(runtime.Engine, "needs-amount"));

        Assert.Contains("amount", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 视图名字空间只含声明字段_私有字段只能走owner()
    {
        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition("pipe", ViewSpec.Empty, MemberVisibility.All)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.private",
                EntityKind.Component,
                privateFields: new[] { new PrivateFieldDefinition("ammo", ViewFieldType.Number, 3) },
                members: new[]
                {
                    new MemberDefinition(
                        "read-private-via-view",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[] { ModelKit.Own(static (view, ctx) => _ = view.Get("ammo")) }),
                    new MemberDefinition(
                        "read-private-via-owner",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[] { ModelKit.Own(static (view, _) => Assert.Equal(3, ((OrchestrationEntity)view.Owner).GetPrivateField("ammo"))) }),
                },
                mounts: new[] { new MountDeclaration("engine#pipe", "read-private-via-view") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.private", "组件", host);
        runtime.Bind(component);

        var error = await Assert.ThrowsAsync<PermissionDeniedException>(
            () => runtime.RunMemberAsync(runtime.Engine, "pipe"));
        Assert.Contains("未在视图声明中", error.Message, StringComparison.Ordinal);

        // owner 通道可读同一字段。
        await runtime.RunMemberAsync(component, "read-private-via-owner");
    }

    [Fact]
    public void 视图字段名与私有字段同名时装载期拒绝()
    {
        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab())
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "comp.collision",
                EntityKind.Component,
                privateFields: new[] { new PrivateFieldDefinition("ammo", ViewFieldType.Number, 3) },
                members: new[]
                {
                    new MemberDefinition("m", ViewSpec.Of(("ammo", ViewFieldType.Number)), MemberVisibility.All),
                }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("comp.collision"));

        Assert.Equal(PrefabErrorCategories.View, error.Category);
        Assert.Contains("同名", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 运行期写入闸门拒绝未声明字段与只读字段()
    {
        ViewCarrier? captured = null;
        var errors = new List<Exception>();
        var data = new Dictionary<string, object?>
        {
            ["readonlyField"] = 1,
        };

        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition(
                "writes",
                new ViewSpec(new[]
                {
                    new ViewFieldDefinition("readonlyField", ViewFieldType.Number, required: false),
                    new ViewFieldDefinition("writableField", ViewFieldType.Number, writable: true, required: false),
                }),
                MemberVisibility.All,
                new[]
                {
                    ModelKit.Own((view, _) =>
                    {
                        captured = view;
                        Try(() => view.Set("readonlyField", 2), errors);   // 只读
                        Try(() => view.Set("nope", 2), errors);            // 未声明
                        Try(() => view.Set("writableField", "text"), errors); // 类型不符
                        view.Set("writableField", 5);                      // 合法写入
                    }),
                })));

        await runtime.RunMemberAsync(runtime.Engine, "writes", data);

        Assert.Equal(3, errors.Count);
        Assert.IsType<PermissionDeniedException>(errors[0]);
        Assert.Contains("只读", errors[0].Message, StringComparison.Ordinal);
        Assert.IsType<PermissionDeniedException>(errors[1]);
        Assert.Contains("未在视图声明中", errors[1].Message, StringComparison.Ordinal);
        Assert.IsType<InvalidCastException>(errors[2]);

        Assert.NotNull(captured);

        // 执行会话结束后视图已封：任何写入都被拒绝。
        Assert.Throws<InvalidOperationException>(() => captured!.Set("writableField", 1));

        static void Try(Action action, List<Exception> sink)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                sink.Add(ex);
            }
        }
    }

    [Fact]
    public async Task Owner由框架回填而不来自数据面()
    {
        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition(
                "who",
                ViewSpec.Empty,
                MemberVisibility.All,
                new[] { ModelKit.Own(static (view, _) => Assert.IsType<OrchestrationEntity>(view.Owner)) })));

        var data = new Dictionary<string, object?> { ["Owner"] = "spoofed" };
        await runtime.RunMemberAsync(runtime.Engine, "who", data);
    }

    [Fact]
    public async Task Host是真实宿主对象且转换语义不变()
    {
        OrchestrationEntity? seenHost = null;
        object? seenOwner = null;

        var catalog = new PrefabCatalog()
            .Add(ModelKit.EnginePrefab(new MemberDefinition("noop", ViewSpec.Empty, MemberVisibility.All)))
            .Add(ModelKit.CardBase())
            .Add(new PrefabDefinition(
                "comp.host-read",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition(
                        "read-host",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[]
                        {
                            ModelKit.Own((view, _) =>
                            {
                                seenHost = view.Host as OrchestrationEntity;
                                seenOwner = view.Owner;
                            }),
                        }),
                },
                mounts: new[] { new MountDeclaration("engine#noop", "read-host") }));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var host = runtime.CreateEntity("card.base", "卡牌");
        var component = runtime.CreateEntity("comp.host-read", "组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "noop");

        Assert.Same(host, seenHost); // Host 就是真实实体对象（转换语义不变）
        Assert.Same(component, seenOwner);
    }
}
