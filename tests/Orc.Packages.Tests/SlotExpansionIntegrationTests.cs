using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Effects;
using Orc.Game.Model;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S27／S29 的装载面与生成面**：<c>asset-ref</c> 的装载期存在性校验、
/// <c>args</c> 的 <c>dsl</c> 形态存**路径**（不内联内容）且两次展开逐字相同。
/// </summary>
public class SlotExpansionIntegrationTests : IDisposable
{
    private const string DslPath = "handler-dsl/onDeployed.hdsl.json";
    private const string Dsl = """{"schemaVersion":1,"actions":[{"op":"stop"}]}""";

    private readonly string _root;
    private readonly HandlerDslEnvironment _dsl = HandlerDslAssembly.Create();

    public SlotExpansionIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-slot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "onDeployed.hdsl.json"), Dsl);
    }

    [Fact]
    public void args的dsl形态存路径_不内联内容且两次展开逐字相同()
    {
        var template = Template();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 4m,
            ["onHook"] = HandlerSlotArgument.Dsl(DslPath),
        };

        var first = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = args,
            HandlerDsl = _dsl,
            ResolveDslPath = _ => Path.Combine(_root, "onDeployed.hdsl.json"),
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        var second = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = args,
            HandlerDsl = _dsl,
            ResolveDslPath = _ => Path.Combine(_root, "onDeployed.hdsl.json"),
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
        });

        var handler = first.Expanded.Asset.Members.Single().Handler!;

        // 产物里是生成的 csx ＋ 来源标记；**路径**记在 generated.source，DSL 内容不内联进 handler.dsl。
        Assert.NotNull(handler.Script);
        Assert.Null(handler.Dsl);
        Assert.Equal(DslPath, handler.Generated!.Source);
        Assert.Equal(HandlerContent.Hash(Dsl), handler.Generated.DslHash);
        Assert.Equal(HandlerContent.Hash(handler.Script!), handler.Generated.TextHash);

        // 同一份 args 两次展开 ⇒ 逐字相同的 csx（数字 invariant、生成确定性）。
        Assert.Equal(handler.Script, second.Expanded.Asset.Members.Single().Handler!.Script);
        Assert.Equal(first.ArgsHash, second.ArgsHash);
    }

    [Fact]
    public void asset_ref指向不存在的资产_装载期拒绝()
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(
            Path.Combine(pack, "test.ref.component.json"),
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "test.ref",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [],
                "mounts": [],
                "wraps": [],
                "slots": [ { "name": "target", "type": "asset-ref", "value": "test.missing" } ]
              }
            }
            """);

        var load = AssetPackLoader.Load(GameEnginePrefabs.CreateRuntime(), pack, evaluator: null);

        Assert.False(load.Loaded);
        Assert.Contains(load.Errors, e => e.Message.Contains("不在装载目录内", StringComparison.Ordinal));
        Assert.Contains(load.Errors, e => e.Message.Contains("test.missing", StringComparison.Ordinal));
    }

    [Fact]
    public void asset_ref指向同包资产_装载通过()
    {
        var pack = Path.Combine(_root, "pack-ok");
        Directory.CreateDirectory(pack);
        File.WriteAllText(
            Path.Combine(pack, "test.target.component.json"),
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "test.target",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [],
                "mounts": [],
                "wraps": []
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(pack, "test.ref.component.json"),
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "test.ref",
                "requires-host": "any",
                "active-when": "placed",
                "one-shot": false,
                "private-fields": [],
                "members": [],
                "mounts": [],
                "wraps": [],
                "slots": [ { "name": "target", "type": "asset-ref", "value": "test.target" } ]
              }
            }
            """);

        var load = AssetPackLoader.Load(GameEnginePrefabs.CreateRuntime(), pack, evaluator: null);

        Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => $"[{e.Category}] {e.Target}：{e.Message}")));
    }

    private static ComponentAsset Template()
        => new(
            "tpl.slot.demo",
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
