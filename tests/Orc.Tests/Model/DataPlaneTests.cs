using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S6 验收（数据面与写回契约）：写回只发生在 <c>view.&lt;Name&gt;</c> 通道并落宿主数据面（同链后续步骤可见）；
/// 私有字段不进数据面、公共变量不落私有字段；经 owner／owner.host 的强类型读取不受视图包含校验约束。
/// </summary>
public class DataPlaneTests
{
    private static OrchestrationRuntime Runtime(params PrefabDefinition[] definitions)
        => new(new PrefabCatalog().AddRange(definitions), new HandlerRegistry());

    [Fact]
    public async Task 视图写回落宿主数据面并对同链后续步骤可见()
    {
        object? observedByReader = null;

        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition(
                "pipeline",
                new ViewSpec(new[]
                {
                    new ViewFieldDefinition("amount", ViewFieldType.Number, writable: true, required: false),
                }),
                MemberVisibility.All,
                new[] { ModelKit.Own(static (view, _) => view.Set("amount", 7)) })),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.reader",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition(
                        "reader",
                        ViewSpec.Of(("amount", ViewFieldType.Number)),
                        MemberVisibility.Mountable,
                        new[] { ModelKit.Own((view, _) => observedByReader = view.Get("amount")) }),
                },
                mounts: new[] { new MountDeclaration("engine#pipeline", "reader") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var reader = runtime.CreateEntity("comp.reader", "读取组件", host);
        runtime.Bind(reader);

        await runtime.RunMemberAsync(runtime.Engine, "pipeline");

        // 写回落宿主数据面：同一执行链上的后续成员读到 7（不是各自私有的副本）。
        Assert.Equal(7, observedByReader);
    }

    [Fact]
    public async Task owner写入不产生数据面写回_私有字段不进数据面()
    {
        var planeHasAmmo = true;

        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition("run", ViewSpec.Empty, MemberVisibility.All)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.state",
                EntityKind.Component,
                privateFields: new[] { new PrivateFieldDefinition("ammo", ViewFieldType.Number, 3) },
                members: new[]
                {
                    new MemberDefinition(
                        "tick",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[]
                        {
                            ModelKit.Own((view, ctx) =>
                            {
                                dynamic owner = view.Owner;
                                owner.ammo = 5; // owner 通道：实体状态写入（不产生数据面写回）
                                planeHasAmmo = ctx.TryGet("ammo", out _);
                            }),
                        }),
                },
                mounts: new[] { new MountDeclaration("engine#run", "tick") }));

        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.state", "状态组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "run");

        Assert.Equal(5, component.GetPrivateField("ammo")); // 私有字段随实体实例存在
        Assert.False(planeHasAmmo);                          // 但不进数据面
    }

    [Fact]
    public async Task 经owner与owner_host的强类型读取不受视图包含校验约束()
    {
        string? hostName = null;
        object? privateValue = null;

        var runtime = Runtime(
            ModelKit.EnginePrefab(new MemberDefinition("noop", ViewSpec.Empty, MemberVisibility.All)),
            ModelKit.CardBase(),
            new PrefabDefinition(
                "comp.owner-read",
                EntityKind.Component,
                privateFields: new[] { new PrivateFieldDefinition("ammo", ViewFieldType.Number, 9) },
                members: new[]
                {
                    new MemberDefinition(
                        "read",
                        ViewSpec.Empty, // 视图不声明任何字段：owner 通道的读取不受视图包含校验约束
                        MemberVisibility.Mountable,
                        new[]
                        {
                            ModelKit.Own((view, _) =>
                            {
                                var owner = (OrchestrationEntity)view.Owner;
                                hostName = owner.Host!.Name;
                                privateValue = owner.GetPrivateField("ammo");
                            }),
                        }),
                },
                mounts: new[] { new MountDeclaration("engine#noop", "read") }));

        var host = runtime.CreateEntity("card.base", "被读取的卡");
        var component = runtime.CreateEntity("comp.owner-read", "组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "noop");

        Assert.Equal("被读取的卡", hostName);
        Assert.Equal(9, privateValue);
    }
}
