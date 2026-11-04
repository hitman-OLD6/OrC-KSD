using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Script.Tests;

/// <summary>
/// **S35／R39 裁决 14**：标量空位在展开期注入为**同名私有字段**，handler 用既有的
/// <c>owner.&lt;slot名&gt;</c> 读取——**零新 API**（不加 <c>ctx.Slots</c>、不加模板语法）。
/// <para>
/// 这里的端到端用例真的跑一次 csx：`owner.damage` 必须读到 <c>args</c> 的 7（不是 0、不是默认值）。
/// </para>
/// </summary>
public class SlotInjectionTests
{
    private const string ReaderScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
        + "{\n"
        + "    dynamic owner = view.Owner;\n"
        + "    owner.seen = (int)owner.damage;\n"
        + "    return Task.CompletedTask;\n"
        + "};";

    [Fact]
    public async Task number空位注入私有字段_owner点slot读到args值()
    {
        var template = Template(declaredDamage: null);
        var instantiated = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 7m,
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        var asset = instantiated.Expanded.Asset;
        var injected = Assert.Single(asset.PrivateFields, f => f.Name == "damage");
        Assert.Equal(ViewFieldType.Number, injected.Type);
        Assert.Equal(7m, injected.InitialValue);

        var seen = await RunAsync(asset, ReaderScript);

        Assert.Equal(7, seen); // owner.damage 读到 7，而不是 0／默认值
    }

    [Fact]
    public async Task 声明期value被args覆盖_注入字段取新值()
    {
        var template = Template(declaredDamage: 3m);
        var instantiated = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 7m,
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        var asset = instantiated.Expanded.Asset;
        Assert.Equal(7m, Assert.Single(asset.PrivateFields, f => f.Name == "damage").InitialValue);

        Assert.Equal(7, await RunAsync(asset, ReaderScript)); // D46：再填一次，不是声明期的 3
    }

    [Fact]
    public void 四类标量各注入一条_类型按既有封闭集映射()
    {
        var template = new ComponentAsset(
            "tpl.scalars",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[] { SlotMember() },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[]
            {
                new AssetSlotDeclaration("damage", SlotType.Number),
                new AssetSlotDeclaration("note", SlotType.String),
                new AssetSlotDeclaration("flag", SlotType.Bool),
                new AssetSlotDeclaration("element", SlotType.Enum, enumValues: new[] { "fire", "ice" }),
            });

        var instantiated = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 5m,
                ["note"] = "hello",
                ["flag"] = true,
                ["element"] = "fire",
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        var fields = instantiated.Expanded.Asset.PrivateFields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        Assert.Equal(ViewFieldType.Number, fields["damage"].Type);
        Assert.Equal(5m, fields["damage"].InitialValue);
        Assert.Equal(ViewFieldType.String, fields["note"].Type);
        Assert.Equal("hello", fields["note"].InitialValue);
        Assert.Equal(ViewFieldType.Boolean, fields["flag"].Type);
        Assert.Equal(true, fields["flag"].InitialValue);
        Assert.Equal(ViewFieldType.String, fields["element"].Type); // 推论：enum → String（存文本名）
        Assert.Equal("fire", fields["element"].InitialValue);
    }

    [Fact]
    public void asset_ref与handler不注入私有字段()
    {
        var template = new ComponentAsset(
            "tpl.ref",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[] { SlotMember() },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("target", SlotType.AssetRef) });

        var instantiated = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["target"] = "tpl.ref",
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        Assert.Empty(instantiated.Expanded.Asset.PrivateFields);
    }

    [Fact]
    public void slot名撞既有私有字段且类型不同_拒绝()
    {
        var template = new ComponentAsset(
            "tpl.conflict",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("damage", ViewFieldType.String, "x") },
            members: new[] { SlotMember() },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number) });

        var error = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 7m,
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        }));

        Assert.Contains("damage", error.Message, StringComparison.Ordinal);
        Assert.Contains("冲突", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void slot名撞成员视图字段_拒绝()
    {
        var template = new ComponentAsset(
            "tpl.view-conflict",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[]
            {
                new ComponentMemberAsset(
                    "onHook",
                    MemberVisibility.Callable,
                    ViewSpec.Of(("damage", ViewFieldType.Number)),
                    Handler: null,
                    Slot: MemberSlotDeclaration.Handler),
            },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number) });

        var error = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 7m,
                ["onHook"] = HandlerSlotArgument.Csx(ReaderScript),
            },
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        }));

        Assert.Contains("视图字段", error.Message, StringComparison.Ordinal);
        Assert.Contains("damage", error.Message, StringComparison.Ordinal);
    }

    private static async Task<int> RunAsync(ComponentAsset asset, string script)
    {
        var handlers = new HandlerRegistry();
        var evaluator = new CSharpScriptEvaluator();
        var member = asset.Members.Single(m => m.Name == "onHook");
        handlers.RegisterScript(
            member.Handler!.Id,
            script,
            evaluator,
            member.View,
            new[] { "seen", "damage" });

        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition(
                "engine",
                EntityKind.Engine,
                members: new[] { new MemberDefinition("noop", ViewSpec.Empty, MemberVisibility.All) }))
            .Add(new PrefabDefinition("card.base", EntityKind.Card))
            .Add(asset.ToPrefabDefinition(handlers.Resolve));

        var runtime = new OrchestrationRuntime(catalog, handlers);
        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity(asset.Id, "组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(component, "onHook");

        return (int)component.GetPrivateField("seen")!;
    }

    private static ComponentMemberAsset SlotMember()
        => new(
            "onHook",
            MemberVisibility.Callable,
            ViewSpec.Empty,
            Handler: null,
            Slot: MemberSlotDeclaration.Handler);

    private static ComponentAsset Template(decimal? declaredDamage)
        => new(
            "tpl.param",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("seen", ViewFieldType.Number, 0) },
            members: new[] { SlotMember() },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number, declaredDamage) });
}
