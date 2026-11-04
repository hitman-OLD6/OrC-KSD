using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// **S30／R39 裁决 13**：<c>inline</c> 的对局级展开表——随运行时实例丢弃、不跨对局复用，
/// 不进资产命名空间（不落盘／不进 lock／不进资产库）。
/// </summary>
public class InlineExpansionTableTests
{
    private const string Script =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    [Fact]
    public void inline产物进对局级表_两个运行时互不共享()
    {
        var template = Template();
        var runtimeA = new OrchestrationRuntime(
            new PrefabCatalog().Add(new PrefabDefinition("engine", EntityKind.Engine)), new HandlerRegistry());
        var runtimeB = new OrchestrationRuntime(
            new PrefabCatalog().Add(new PrefabDefinition("engine", EntityKind.Engine)), new HandlerRegistry());

        var instance = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Inline,
            Declarations = Declarations(template),
            InlineTable = runtimeA.InlinePrefabs,
        });

        Assert.Equal(1, runtimeA.InlinePrefabs.Count);
        Assert.Equal(0, runtimeB.InlinePrefabs.Count); // 不跨对局复用（Constraint 3）
        Assert.True(runtimeA.InlinePrefabs.TryGet(instance.CacheKey!, out var cached));
        Assert.Same(instance.Expanded.Type, cached!.Expanded.Type);

        // 同模板 ＋ 同 args ⇒ 同键、幂等（不重复登记）。
        PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Inline,
            Declarations = Declarations(template),
            InlineTable = runtimeA.InlinePrefabs,
        });
        Assert.Equal(1, runtimeA.InlinePrefabs.Count);
    }

    [Fact]
    public void inline缓存键不进资产命名空间_named记录不进inline表()
    {
        var template = Template();
        var instance = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Inline,
            Declarations = Declarations(template),
        });

        Assert.StartsWith("inline/", instance.CacheKey!, StringComparison.Ordinal);
        Assert.DoesNotContain("template.", instance.CacheKey!, StringComparison.Ordinal);

        var named = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Named,
            Id = "effect.fireball",
            Declarations = Declarations(template),
        });

        var table = new InlinePrefabTable();
        Assert.Throws<ArgumentException>(() => table.Add(named));
        Assert.Equal(0, table.Count);
    }

    private static IReadOnlyDictionary<string, object?> Args()
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 7m,
            ["onHook"] = HandlerSlotArgument.Csx(Script),
        };

    private static IReadOnlyDictionary<string, ComponentAsset> Declarations(ComponentAsset template)
        => new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template };

    private static ComponentAsset Template()
        => new(
            "tpl.inline",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
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
