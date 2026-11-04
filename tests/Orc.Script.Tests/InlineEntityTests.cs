using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Script.Tests;

/// <summary>
/// **R39 裁决 17**：<c>inline</c> 产物可以装配实体——`CreateEntity` 先查对局级表、未命中再查目录；
/// 命中用该条目的可执行类型（含 handler 自有边）建实体；表条目不写入 <see cref="PrefabCatalog"/>。
/// </summary>
public class InlineEntityTests
{
    private const string ReaderScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
        + "{\n"
        + "    dynamic owner = view.Owner;\n"
        + "    owner.seen = (int)owner.damage;\n"
        + "    return Task.CompletedTask;\n"
        + "};";

    [Fact]
    public async Task inline产物可建实体_且handler真的执行()
    {
        var runtime = Runtime();
        var instance = Instantiate(runtime);

        RegisterHandler(runtime, instance);

        var host = runtime.CreateEntity("card.base", "宿主");
        var entity = runtime.CreateEntity(instance.CacheKey!, "inline组件", host);
        runtime.Bind(entity);

        Assert.Same(host, entity.Host);
        Assert.Contains(entity, runtime.Entities);

        await runtime.RunMemberAsync(entity, "onHook");

        Assert.Equal(7, entity.GetPrivateField("seen")); // 不是空壳：handler 读到了注入的 damage
    }

    [Fact]
    public void 同键再次实例化_命中同一展开结果与同一可执行类型()
    {
        var runtime = Runtime();
        var first = Instantiate(runtime);
        var second = Instantiate(runtime);

        Assert.Same(first, second);                       // 直接返回缓存条目，不重复展开
        Assert.Same(first.Expanded.Type, second.Expanded.Type);
        Assert.Equal(1, runtime.InlinePrefabs.Count);

        RegisterHandler(runtime, first);
        var host = runtime.CreateEntity("card.base", "宿主");
        var entityA = runtime.CreateEntity(first.CacheKey!, "A", host);
        var entityB = runtime.CreateEntity(second.CacheKey!, "B", host);

        Assert.Same(entityA.Type, entityB.Type);          // 可执行类型也只构建一次
        Assert.NotSame(entityA, entityB);                 // 实体名由调用方给，各自独立
    }

    [Fact]
    public void 合法资产id不命中inline表_目录可见集合不变()
    {
        var runtime = Runtime();
        runtime.Catalog.Add(new PrefabDefinition(
            "comp.plain",
            EntityKind.Component,
            requiresHost: HostKind.Any,
            members: new[] { new MemberDefinition("tick", ViewSpec.Empty, MemberVisibility.Callable) }));

        var before = runtime.Catalog.Definitions.Count;
        var instance = Instantiate(runtime);
        var after = runtime.Catalog.Definitions.Count;

        Assert.Equal(before, after);                          // inline 不写入目录
        Assert.False(runtime.Catalog.Contains(instance.CacheKey!));
        Assert.False(runtime.InlinePrefabs.TryGet("comp.plain", out _)); // 合法资产 id 不命中表

        var plain = runtime.CreateEntity("comp.plain", "普通组件");
        Assert.Equal("comp.plain", plain.Type.Id);            // 合法 id 的解析与改动前一致
        Assert.NotEqual(instance.CacheKey, plain.Type.Id);
    }

    [Fact]
    public void 宿主销毁级联销毁inline实体_运行时丢弃则表清空()
    {
        var runtime = Runtime();
        var instance = Instantiate(runtime);
        RegisterHandler(runtime, instance);

        var host = runtime.CreateEntity("card.base", "宿主");
        var attachment = runtime.AddComponent(host, instance.CacheKey!, "inline组件");
        var entity = attachment.Component;

        Assert.Contains(entity, runtime.Entities);

        host.Destroy(); // 宿主离场＝组件销毁（R3i）

        Assert.DoesNotContain(entity, runtime.Entities);
        Assert.DoesNotContain(host, runtime.Entities);

        runtime.InlinePrefabs.Clear();
        Assert.Equal(0, runtime.InlinePrefabs.Count);

        // 新的运行时＝新的表（不跨对局复用）。
        Assert.Equal(0, Runtime().InlinePrefabs.Count);
    }

    private static OrchestrationRuntime Runtime()
        => new(
            new PrefabCatalog()
                .Add(new PrefabDefinition(
                    "engine",
                    EntityKind.Engine,
                    members: new[]
                    {
                        // 组件兜底信号（框架注销边）要求引擎实体声明该信号。
                        new MemberDefinition("card.destroyed", ViewSpec.Empty, MemberVisibility.All),
                    }))
                .Add(new PrefabDefinition("card.base", EntityKind.Card)),
            new HandlerRegistry());

    private static InstantiatedPrefab Instantiate(OrchestrationRuntime runtime)
    {
        var template = Template();
        return PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 7m,
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Mode = ExpansionMode.Inline,
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
            InlineTable = runtime.InlinePrefabs,
        });
    }

    private static void RegisterHandler(OrchestrationRuntime runtime, InstantiatedPrefab instance)
    {
        var handler = instance.Expanded.Asset.Members.Single(m => m.Name == "onHook").Handler!;
        runtime.Handlers.RegisterScript(
            handler.Id,
            handler.Script!,
            new CSharpScriptEvaluator(),
            ViewSpec.Empty,
            new[] { "seen", "damage" });
    }

    private static ComponentAsset Template()
        => new(
            "tpl.inline.entity",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("seen", ViewFieldType.Number, 0) },
            members: new[]
            {
                new ComponentMemberAsset(
                    "onHook", MemberVisibility.Callable, ViewSpec.Empty, Handler: null,
                    Slot: MemberSlotDeclaration.Handler),
            },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number) });
}
