using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// **R40（挂载／包装的重复声明＝装载期拒绝）** 模型层验收：判定点＝合并核 <see cref="PrefabCatalog.Build"/>，
/// 键与范围由 R40.1／R40.2 定死；归属（<see cref="MountDeclaration.DeclaredBy"/>）由合并核填值、
/// 产物组装处改写为产物 id（R40.8／D52）。
/// </summary>
public class MountDeclarationUniquenessTests
{
    private const string Played = "engine#card.played";
    private const string Drawn = "engine#card.drawn";

    // ---------- R40.1 挂载唯一 ----------

    [Fact]
    public void 同一资产内部重复挂载_装载期拒绝并指出冲突双方()
    {
        var catalog = new PrefabCatalog()
            .Add(Asset("dup.mount", mounts: new[]
            {
                new MountDeclaration(Played, "onPlayed"),
                new MountDeclaration(Played, "onPlayed"),
            }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("dup.mount"));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains(Played, error.Message, StringComparison.Ordinal);
        Assert.Contains("onPlayed", error.Message, StringComparison.Ordinal);
        Assert.Contains("dup.mount", error.Message, StringComparison.Ordinal);
        // 一对重复＝一条错误（D50），不因「同层」放行。
        Assert.Contains("又声明了一次", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 子重声明父的挂载_拒绝并报出双方声明者id()
    {
        var catalog = new PrefabCatalog()
            .Add(Asset("r40.parent", mounts: new[] { new MountDeclaration(Played, "onPlayed") }))
            .Add(Asset("r40.child", extends: "r40.parent", mounts: new[] { new MountDeclaration(Played, "onPlayed") }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("r40.child"));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        // 报错必须指出**冲突双方**（R40.1／D50）：地址 ＋ 父资产 id ＋ 子资产 id。
        Assert.Contains(Played, error.Message, StringComparison.Ordinal);
        Assert.Contains("r40.parent", error.Message, StringComparison.Ordinal);
        Assert.Contains("r40.child", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 同一目标同一成员但作用域不同_不判重复()
    {
        // R40.1 明令：target 用原文 Ordinal 比较，不做作用域归一化——host#x 与 engine#x 是两条不同的边。
        var catalog = new PrefabCatalog()
            .Add(Asset("r40.scope.host", mounts: new[] { new MountDeclaration("host#card.played", "onPlayed") }))
            .Add(Asset("r40.scope.child", extends: "r40.scope.host", mounts: new[]
            {
                new MountDeclaration(Played, "onPlayed"),
            }));

        var type = catalog.Resolve("r40.scope.child");

        Assert.Equal(2, type.Mounts.Count);
    }

    [Fact]
    public void 新增父没有的挂载_摊平语义下两条都保留()
    {
        var catalog = new PrefabCatalog()
            .Add(Asset("r40.add.parent", mounts: new[] { new MountDeclaration(Played, "onPlayed") }))
            .Add(Asset("r40.add.child", extends: "r40.add.parent", mounts: new[]
            {
                new MountDeclaration(Drawn, "onDrawn"),
            }));

        var type = catalog.Resolve("r40.add.child");

        Assert.Equal(new[] { Played, Drawn }, type.Mounts.Select(static m => m.Target));
    }

    // ---------- R40.2 包装唯一 ----------

    [Fact]
    public void 同一资产内部重复包装_装载期拒绝()
    {
        var catalog = new PrefabCatalog()
            .Add(Asset("dup.wrap", wraps: new[]
            {
                WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.a"),
                WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.a"),
            }));

        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("dup.wrap"));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains("member:self#onPlayed", error.Message, StringComparison.Ordinal);
        Assert.Contains("wrap.a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 子重声明父的包装_拒绝_不同handler同目标同相位不判重复()
    {
        var duplicated = new PrefabCatalog()
            .Add(Asset("r40.wrap.parent", wraps: new[]
            {
                WrapDeclaration.ForSeam("self#onPlayed", "seam.x", "wrap.a"),
            }))
            .Add(Asset("r40.wrap.child", extends: "r40.wrap.parent", wraps: new[]
            {
                WrapDeclaration.ForSeam("self#onPlayed", "seam.x", "wrap.a"),
            }));

        var error = Assert.Throws<PrefabLoadException>(() => duplicated.Resolve("r40.wrap.child"));
        Assert.Contains("r40.wrap.parent", error.Message, StringComparison.Ordinal);
        Assert.Contains("r40.wrap.child", error.Message, StringComparison.Ordinal);
        Assert.Contains("@seam.x", error.Message, StringComparison.Ordinal);

        // handler 不同＝两条不同的包装项（R40.2 的键含 handler）。
        var distinct = new PrefabCatalog()
            .Add(Asset("r40.wrap2.parent", wraps: new[]
            {
                WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.a"),
            }))
            .Add(Asset("r40.wrap2.child", extends: "r40.wrap2.parent", wraps: new[]
            {
                WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.b"),
            }));

        Assert.Equal(2, distinct.Resolve("r40.wrap2.child").Wraps.Count);
    }

    [Fact]
    public void 判定器包装_父子各包同一判定器_不判重复()
    {
        // 判定器包装的 handlerId 含资产 id（`judge|资产id`，AssetModel.Project）⇒ 父子各包一层＝两条不同的包装项。
        // 这里走真实的 asset → prefab 投影（不用手写 WrapDeclaration），确保口径由 `Project()` 决定。
        var parent = JudgeAsset("r40.judge.parent");
        var child = JudgeAsset("r40.judge.child", parent.Id);

        var catalog = new PrefabCatalog()
            .Add(parent.ToStructuralPrefabDefinition())
            .Add(child.ToStructuralPrefabDefinition());

        var type = catalog.Resolve(child.Id);

        Assert.Equal(2, type.Wraps.Count);
        Assert.Equal(
            new[] { "damage|r40.judge.parent", "damage|r40.judge.child" },
            type.Wraps.Select(static w => w.HandlerId));
    }

    // ---------- R40.8 归属（D52） ----------

    [Fact]
    public void 归属_合并核填本资产id_沿链透传真正的声明者()
    {
        var catalog = new PrefabCatalog()
            .Add(Asset(
                "r40.own.T",
                mounts: new[] { new MountDeclaration(Played, "onPlayed") },
                wraps: new[] { WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.before") }))
            .Add(Asset("r40.own.A", extends: "r40.own.T"))
            .Add(Asset("r40.own.C", extends: "r40.own.A"));

        var type = catalog.Resolve("r40.own.C");

        // 三层链：归属仍是真正写它的 T，不是直接父层 A，也不是叶子 C。
        Assert.Equal("r40.own.T", Assert.Single(type.Mounts).DeclaredBy);
        Assert.Equal("r40.own.T", Assert.Single(type.Wraps).DeclaredBy);

        // 不变量：进目录后每条都带归属（DTO 侧无判空分支）。
        Assert.All(type.Mounts, static m => Assert.NotNull(m.DeclaredBy));
        Assert.All(type.Wraps, static w => Assert.NotNull(w.DeclaredBy));
    }

    [Fact]
    public void 直接构造的声明对象归属为null_进目录后恒非null()
    {
        var raw = new MountDeclaration(Played, "onPlayed");
        Assert.Null(raw.DeclaredBy);

        // WithDeclarer 返回副本，不改原对象。
        var copy = raw.WithDeclarer("r40.fill");
        Assert.Null(raw.DeclaredBy);
        Assert.Equal("r40.fill", copy.DeclaredBy);
        Assert.Equal((raw.Target, raw.Member), (copy.Target, copy.Member));

        var type = new PrefabCatalog().Add(Asset("r40.fill", mounts: new[] { raw })).Resolve("r40.fill");
        Assert.Equal("r40.fill", Assert.Single(type.Mounts).DeclaredBy);
        Assert.All(type.Wraps, static w => Assert.NotNull(w.DeclaredBy));
    }

    [Fact]
    public void 同模板两次named实例化_产物归属各自独立且模板类型不被改写()
    {
        var template = Template();
        var declarations = new[] { template }.ToDictionary(static a => a.Id, StringComparer.Ordinal);

        // 模板自身的展开表（对应 PrefabCatalog._resolved 缓存的同一实例）。
        var templateType = new PrefabCatalog()
            .Add(template.ToStructuralPrefabDefinition())
            .Resolve(template.Id);
        Assert.Equal(template.Id, Assert.Single(templateType.Mounts).DeclaredBy);

        var first = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Mode = ExpansionMode.Named,
            Id = "r40.instance.one",
            Declarations = declarations,
        });
        var second = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Mode = ExpansionMode.Named,
            Id = "r40.instance.two",
            Declarations = declarations,
        });

        // 产物条目是**新对象**，各自的声明者是各自的产物 id。
        Assert.Equal("r40.instance.one", Assert.Single(first.Expanded.Asset.Mounts).DeclaredBy);
        Assert.Equal("r40.instance.two", Assert.Single(second.Expanded.Asset.Mounts).DeclaredBy);

        // 模板的展开表**没有被第一次实例化污染**（缓存不污染，R40 的 NOTE）。
        Assert.Equal(template.Id, Assert.Single(templateType.Mounts).DeclaredBy);
        Assert.NotSame(first.Expanded.Asset.Mounts[0], second.Expanded.Asset.Mounts[0]);
    }

    [Fact]
    public void inline产物的归属等于内部缓存键()
    {
        var template = Template();
        var inline = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Mode = ExpansionMode.Inline,
            Declarations = new[] { template }.ToDictionary(static a => a.Id, StringComparer.Ordinal),
        });

        Assert.Equal(inline.CacheKey, Assert.Single(inline.Expanded.Asset.Mounts).DeclaredBy);
    }

    [Fact]
    public void 产物JSON的挂载包装字段集不变_归属不落盘()
    {
        var template = Template();
        var named = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Mode = ExpansionMode.Named,
            Id = "r40.serialized",
            Declarations = new[] { template }.ToDictionary(static a => a.Id, StringComparer.Ordinal),
        });

        var json = AssetSchema.Write(named.Expanded.Asset);

        // R40 红线：`*.component.json` 的 mounts[]／wraps[] 字段集与今天完全一致。
        Assert.DoesNotContain("declaredBy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("declarer", json, StringComparison.OrdinalIgnoreCase);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var asset = document.RootElement.GetProperty("asset");
        var mount = Assert.Single(asset.GetProperty("mounts").EnumerateArray());
        Assert.Equal(
            new[] { "member", "target" },
            mount.EnumerateObject().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal));
        var wrap = Assert.Single(asset.GetProperty("wraps").EnumerateArray());
        Assert.Equal(
            new[] { "handler", "phase", "target" },
            wrap.EnumerateObject().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal));

        // 读回后的声明对象归属为 null（填值只在 Build／产物组装两处）。
        var reread = AssetSchema.Read(json, "r40.serialized");
        Assert.Null(Assert.Single(reread.Mounts).DeclaredBy);
        Assert.Null(Assert.Single(reread.Wraps).DeclaredBy);
    }

    // ---------- 夹具 ----------

    private static PrefabDefinition Asset(
        string id,
        string? extends = null,
        IEnumerable<MountDeclaration>? mounts = null,
        IEnumerable<WrapDeclaration>? wraps = null)
        => new(
            id,
            EntityKind.Component,
            extends: extends,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition(
                    "onPlayed",
                    visibility: MemberVisibility.Mountable | MemberVisibility.Callable),
            },
            mounts: mounts,
            wraps: wraps);

    /// <summary>带一条判定器包装的组件资产（走真实的 asset → prefab 投影）。</summary>
    private static ComponentAsset JudgeAsset(string id, string? extends = null)
        => new(
            id,
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            // 子不重声明成员（重声明＝覆写，需要父把它声明成 slot；本用例只关心包装合并）。
            members: extends is null
                ? new[]
                {
                    new ComponentMemberAsset(
                        "onPlayed",
                        MemberVisibility.Mountable | MemberVisibility.Callable,
                        ViewSpec.Empty,
                        Handler: null),
                }
                : Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: new[]
            {
                new ComponentJudgeWrapAsset("damage", WrapperPhase.Before, "Judge", "result"),
            },
            extends: extends);

    /// <summary>带一条挂载 ＋ 一条包装的模板资产（值表面实例化用）。</summary>
    private static ComponentAsset Template()
        => new(
            "r40.tpl",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[]
            {
                new ComponentMemberAsset(
                    "onPlayed",
                    MemberVisibility.Mountable | MemberVisibility.Callable,
                    ViewSpec.Empty,
                    Handler: null),
            },
            mounts: new[] { new MountDeclaration(Played, "onPlayed") },
            wraps: new[] { WrapDeclaration.ForMember("self#onPlayed", WrapperPhase.Before, "wrap.before") },
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>());
}
