using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// **S29／R39 裁决 10**：一个共用展开核 <see cref="PrefabExpander.Expand"/> ＋ 两个输入面
/// （值表面 <see cref="PrefabExpander.Instantiate"/>／声明面 Expand）——合并、填空位、饱和判定各只有一份实现。
/// </summary>
public class PrefabExpansionTests
{
    private const string CsxScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    [Fact]
    public void 声明面与值表面走同一个核_得到同一形态()
    {
        var template = Template();
        var draft = Draft("tpl.demo");

        var byArgs = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 5m,
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        });

        var byDeclaration = PrefabExpander.Expand(
            new ExpandRequest
            {
                Catalog = new PrefabCatalog().Add(template.ToStructuralPrefabDefinition()),
                Declaration = draft,
                ResultId = "prod.demo",
                Declarations = Declarations(template, draft),
                Parent = new PrefabCatalog()
                    .Add(template.ToStructuralPrefabDefinition())
                    .Resolve(template.Id),
                SaturationSource = new PrefabCatalog()
                    .Add(template.ToStructuralPrefabDefinition())
                    .Resolve(template.Id),
            },
            new List<PrefabLoadError>());

        Assert.NotNull(byDeclaration);

        // 形态一致：成员名、handler 文本、空位声明都存在且相同（差别只在 id／身份）。
        Assert.Equal(
            byArgs.Expanded.Asset.Members.Select(m => m.Name),
            byDeclaration!.Asset.Members.Select(m => m.Name));
        Assert.Equal(
            byArgs.Expanded.Asset.Members.Select(m => m.Handler?.Script),
            byDeclaration.Asset.Members.Select(m => m.Handler?.Script));
        Assert.All(byDeclaration.Asset.Members, m => Assert.NotNull(m.Slot));

        // 两个输入面都落了同一个最终值（变量值走 schema，不进成员）。
        Assert.Equal(5m, Assert.IsType<decimal>(byArgs.Expanded.Asset.Slots.Single(s => s.Name == "damage").Value));
        Assert.Equal(3m, Assert.IsType<decimal>(byDeclaration.Asset.Slots.Single(s => s.Name == "damage").Value));
    }

    [Fact]
    public void args覆盖声明期已填值_结果取args而不是声明值()
    {
        var template = Template(damage: 3m);

        var withArgs = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 5m,
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        });

        var withoutArgs = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        });

        Assert.Equal(5m, Assert.IsType<decimal>(withArgs.Expanded.Asset.Slots.Single(s => s.Name == "damage").Value));
        Assert.Equal(3m, Assert.IsType<decimal>(withoutArgs.Expanded.Asset.Slots.Single(s => s.Name == "damage").Value));
    }

    [Fact]
    public void sealed的空位不许再填()
    {
        var template = Template(damage: 3m, damageSealed: true);

        var error = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 5m,
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        }));

        Assert.Contains("sealed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void args装不进新增成员_未知键即拒绝()
    {
        var template = Template();

        var error = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 5m,
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
                ["onExtra"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        }));

        Assert.Contains("不在资产", error.Message, StringComparison.Ordinal);
        Assert.Contains("不能新增成员", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void inline与named形态完全一致_差别只在身份()
    {
        var template = Template();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 5m,
            ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
        };

        var inline = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = args,
            Mode = ExpansionMode.Inline,
            Declarations = Declarations(template),
        });

        var named = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = args,
            Mode = ExpansionMode.Named,
            Id = "effect.fireball",
            Declarations = Declarations(template),
        });

        Assert.Equal("effect.fireball", named.Id);
        Assert.NotNull(inline.CacheKey);
        Assert.Null(named.CacheKey);
        Assert.NotEqual(inline.Id, named.Id);

        Assert.Equal(inline.ArgsHash, named.ArgsHash);
        Assert.Equal(
            inline.Expanded.Asset.Members.Select(m => m.Handler!.Script),
            named.Expanded.Asset.Members.Select(m => m.Handler!.Script));
        Assert.Equal(
            inline.Expanded.Asset.Slots.Select(s => (s.Name, s.Value)),
            named.Expanded.Asset.Slots.Select(s => (s.Name, s.Value)));
    }

    [Fact]
    public void named的id必须由调用方提供_缺id或保留前缀或重名都拒绝()
    {
        var template = Template();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 5m,
            ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
        };

        var missing = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = args, Mode = ExpansionMode.Named, Declarations = Declarations(template),
        }));
        Assert.Contains("必须由调用方提供资产 id", missing.Message, StringComparison.Ordinal);

        var reserved = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = args, Mode = ExpansionMode.Named, Id = "template.x",
            Declarations = Declarations(template),
        }));
        Assert.Contains("保留前缀", reserved.Message, StringComparison.Ordinal);

        var duplicated = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = args, Mode = ExpansionMode.Named, Id = "tpl.demo",
            Declarations = Declarations(template),
        }));
        Assert.Contains("重名", duplicated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void inline缓存键由模板与args派生_同参数同键且不进资产命名空间()
    {
        var template = Template();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 5m,
            ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
        };
        var other = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 7m,
            ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
        };

        var first = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = args, Declarations = Declarations(template),
        });
        var again = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = args, Declarations = Declarations(template),
        });
        var changed = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template, Arguments = other, Declarations = Declarations(template),
        });

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.StartsWith("inline/", first.CacheKey!, StringComparison.Ordinal);
        Assert.DoesNotContain("template.", first.CacheKey!, StringComparison.Ordinal);
    }

    [Fact]
    public void handler三种kind_csx与assembly都能填_dsl缺编译器报能力缺失()
    {
        var template = Template();

        var csx = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 1m,
                ["onHook"] = HandlerSlotArgument.Csx(CsxScript),
            },
            Declarations = Declarations(template),
        });
        Assert.Equal(CsxScript, csx.Expanded.Asset.Members.Single().Handler!.Script);

        var assembly = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 1m,
                ["onHook"] = HandlerSlotArgument.Assembly("asm.onHook"),
            },
            Declarations = Declarations(template),
        });
        Assert.Equal("asm.onHook", assembly.Expanded.Asset.Members.Single().Handler!.AssemblyKey);

        var dslError = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["damage"] = 1m,
                ["onHook"] = HandlerSlotArgument.Dsl("handler-dsl/x.hdsl.json"),
            },
            Declarations = Declarations(template),
        }));
        Assert.Contains("未装配 handler DSL 编译器", dslError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未填的资产级空位_饱和判定拒绝并列出未填充位()
    {
        var template = Template(damage: null);
        var draft = Draft("tpl.demo", withMember: true);
        var catalog = new PrefabCatalog().Add(template.ToStructuralPrefabDefinition());
        var parent = new PrefabCatalog().Add(template.ToStructuralPrefabDefinition()).Resolve(template.Id);
        var errors = new List<PrefabLoadError>();

        var expanded = PrefabExpander.Expand(
            new ExpandRequest
            {
                Catalog = catalog,
                Declaration = draft,
                ResultId = "prod.demo",
                Declarations = Declarations(template, draft),
                Parent = parent,
                SaturationSource = parent,
            },
            errors);

        Assert.NotNull(expanded);
        var gap = Assert.Single(expanded!.Unfilled);
        Assert.Equal(SlotScope.Asset, gap.Scope);
        Assert.Equal("damage", gap.Name);
        Assert.Contains(errors, e => e.Message.Contains("未填", StringComparison.Ordinal));
    }

    [Fact]
    public void 实例化必须饱和_成员空位没拿到值就拒绝()
    {
        var template = new ComponentAsset(
            "tpl.legacy",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[]
            {
                // 模板形态（S28 迁移后）：slot 声明 + 没有 handler ⇒ 未填空位。
                new ComponentMemberAsset(
                    "onHook", MemberVisibility.Callable, ViewSpec.Empty, Handler: null,
                    Slot: MemberSlotDeclaration.Handler),
            },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>());

        var error = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal),
            Declarations = Declarations(template),
        }));

        Assert.Contains("空位", error.Message, StringComparison.Ordinal);
        Assert.Contains("tpl.legacy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 摊平与实例化两条入口_未填空位错误清单逐条相同()
    {
        // S32 验收：同一份声明从两条入口进来，未填空位的错误清单（成员名 ＋ 模板名）必须一致。
        var template = Template(damage: null);

        var instantiateError = Assert.Throws<PrefabLoadException>(() => PrefabExpander.Instantiate(
            new InstantiationRequest
            {
                Template = template,
                Arguments = new Dictionary<string, object?>(StringComparer.Ordinal),
                Declarations = Declarations(template),
            }));

        var draft = Draft("tpl.demo", withMember: false);
        var errors = new List<PrefabLoadError>();
        PrefabExpander.Expand(
            new ExpandRequest
            {
                Catalog = new PrefabCatalog().Add(template.ToStructuralPrefabDefinition()),
                Declaration = draft,
                ResultId = "prod.demo",
                Declarations = Declarations(template, draft),
                Parent = new PrefabCatalog()
                    .Add(template.ToStructuralPrefabDefinition())
                    .Resolve(template.Id),
                SaturationSource = new PrefabCatalog()
                    .Add(template.ToStructuralPrefabDefinition())
                    .Resolve(template.Id),
            },
            errors);

        var flattened = errors.Select(static e => e.Message).ToArray();
        Assert.Equal(2, flattened.Length); // 成员空位 ＋ 资产级空位
        Assert.All(flattened, message => Assert.Contains(message, instantiateError.Message, StringComparison.Ordinal));
        Assert.Contains("onHook", instantiateError.Message, StringComparison.Ordinal);
        Assert.Contains("damage", instantiateError.Message, StringComparison.Ordinal);
        Assert.Contains("tpl.demo", instantiateError.Message, StringComparison.Ordinal);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static ComponentAsset Template(decimal? damage = 3m, bool damageSealed = false)
        => new(
            "tpl.demo",
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
                    ViewSpec.Empty,
                    Handler: null,
                    Slot: MemberSlotDeclaration.Handler),
            },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            // 覆写资格来自成员级 slot（上面的 onHook），不再有资产级开关（S32）。
            slots: new[]
            {
                new AssetSlotDeclaration("damage", SlotType.Number, damage, damageSealed),
            });

    private static ComponentAsset Draft(string parentId, bool withMember = true)
        => new(
            "draft.demo",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: withMember
                ? new[]
                {
                    new ComponentMemberAsset(
                        "onHook",
                        MemberVisibility.Callable,
                        ViewSpec.Empty,
                        new ComponentHandlerAsset(
                            HandlerContent.AddressOf(CsxScript, generated: false), "HandleAsync", CsxScript, null),
                        Slot: MemberSlotDeclaration.Handler),
                }
                : Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            extends: parentId);

    private static IReadOnlyDictionary<string, ComponentAsset> Declarations(params ComponentAsset[] assets)
        => assets.ToDictionary(static a => a.Id, StringComparer.Ordinal);
}
