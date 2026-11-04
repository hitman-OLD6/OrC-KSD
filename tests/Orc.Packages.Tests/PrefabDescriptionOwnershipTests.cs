using System.Reflection;
using Orc.Core.Model;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R40.8（编辑面必须能分辨「这条挂载是谁声明的」）／D51＋D53** 的 DTO 投影验收：
/// 条目带**声明者资产 id**（粒度＝真正写下它的资产），不设 `isInherited` 布尔，不做双出口。
/// </summary>
public class PrefabDescriptionOwnershipTests : IDisposable
{
    private const string RootId = "test.own.T";
    private const string MiddleId = "test.own.A";
    private const string DraftId = "test.own.draft";
    private const string DraftPath = "modings/test.own.draft.moding.json";
    private const string Played = "engine#card.played";
    private const string TemplateId = "tpl.r40.ownership";
    private const string ProductId = "r40.named.product";
    private const string Script =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    private readonly string _root;
    private readonly string _pack;
    private readonly AssetEditor _editor;

    public PrefabDescriptionOwnershipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-r40-dto-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
        File.WriteAllText(Path.Combine(_pack, "manifest.json"), Manifest);
        Write(_pack, "effects-v4/test.own.T.component.json", Root());
        Write(_pack, "effects-v4/test.own.A.component.json", Middle());
        Write(_pack, DraftPath, Draft());
        Write(_pack, $"effects-v4/{TemplateId}.component.json", Template());

        _editor = new AssetEditor(
            new AssetRegistry(),
            new EditableRootPolicy(_root),
            new AssetEditorOptions { PackageRoots = new[] { _root } });
    }

    [Fact]
    public void 三层链_归属报真正写下它的资产而不是直接父层()
    {
        var description = _editor.DescribePrefab(_pack, MiddleId, DraftPath);

        var member = Assert.Single(description.Members, m => m.Name == "onPlayed");
        var mount = Assert.Single(member.MountedTo);

        // T ← A ← 草案：草案只重载 handler，挂载仍是 T 写的 ⇒ 报 T，不是 A、也不是草案。
        Assert.Equal(Played, mount.Target);
        Assert.Equal("onPlayed", mount.Member);
        Assert.Equal(RootId, mount.DeclaredBy);
        Assert.NotEqual(MiddleId, mount.DeclaredBy);
        Assert.NotEqual(DraftId, mount.DeclaredBy);
    }

    [Fact]
    public void 草案自有包装表_声明者恒为草案id_且与展开表同型()
    {
        var description = _editor.DescribePrefab(_pack, MiddleId, DraftPath);

        var draftWrap = Assert.Single(description.Draft!.Wraps);
        Assert.Equal(DraftId, draftWrap.DeclaredBy);
        Assert.Equal("before", draftWrap.Phase);

        var member = Assert.Single(description.Members, m => m.Name == "onPlayed");
        var inheritedWrap = Assert.Single(member.WrappedAt);
        Assert.Equal(RootId, inheritedWrap.DeclaredBy);

        // 两张表的元素同型（前端一种解析路径）。
        Assert.Equal(typeof(PrefabWrapSummary), draftWrap.GetType());
        Assert.Equal(typeof(PrefabWrapSummary), inheritedWrap.GetType());
    }

    [Fact]
    public void DTO无第二出口_无isInherited_声明者是位置参数()
    {
        // ① 挂载/包装各只有一个列表字段（不留并行列表、不留双出口）。
        var memberProperties = typeof(PrefabMemberDescription).GetProperties()
            .Select(static p => p.Name)
            .ToArray();
        Assert.Single(memberProperties, static n => n.Contains("Mount", StringComparison.Ordinal));
        Assert.Single(memberProperties, static n => n.Contains("Wrap", StringComparison.Ordinal));
        Assert.DoesNotContain(memberProperties, static n => n.Contains("IsInherited", StringComparison.Ordinal));

        // ② 草案表字段集与今天逐字段相同（`{ target, member }`，一个字段都不加）。
        Assert.Equal(
            new[] { "Member", "Target" },
            typeof(PrefabDeclarationSummary).GetProperties().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal));

        // ③ `DeclaredBy` 是**构造参数**且**无默认值**（不能用可缺省的 init 属性）。
        AssertDeclaredByIsRequiredPositional(typeof(PrefabMountReference));
        AssertDeclaredByIsRequiredPositional(typeof(PrefabWrapSummary));

        // ④ DTO 侧没有「声明者为 null」的返回路径：类型是 string（非可空）。
        Assert.Equal(typeof(string), typeof(PrefabMountReference).GetProperty("DeclaredBy")!.PropertyType);
        Assert.Equal(typeof(string), typeof(PrefabWrapSummary).GetProperty("DeclaredBy")!.PropertyType);
    }

    [Fact]
    public void named实例化_Save前后归属相同且都等于产物id()
    {
        var session = _editor.BeginSession(EditorSessionRequest.Edit(_pack));

        _editor.InstantiateNamed(
            session.TemporaryRoot,
            TemplateId,
            new Dictionary<string, object?>(StringComparer.Ordinal),
            ProductId);

        var before = _editor.DescribePrefab(session.TemporaryRoot, ProductId);
        Assert.Equal(PrefabStage.Virtual, before.Stage);
        var beforeMount = Assert.Single(Assert.Single(before.Members, m => m.Name == "onPlayed").MountedTo);
        Assert.Equal(ProductId, beforeMount.DeclaredBy);

        _editor.SaveNamedInstance(ProductId);

        var after = _editor.DescribePrefab(session.TemporaryRoot, ProductId);
        Assert.Equal(PrefabStage.Test, after.Stage);
        var afterMount = Assert.Single(Assert.Single(after.Members, m => m.Name == "onPlayed").MountedTo);
        Assert.Equal(beforeMount.DeclaredBy, afterMount.DeclaredBy);
        Assert.Equal(ProductId, afterMount.DeclaredBy);
    }

    // ── 断言助手 ────────────────────────────────────────────────────────────

    private static void AssertDeclaredByIsRequiredPositional(Type type)
    {
        var constructor = Assert.Single(type.GetConstructors());
        var parameters = constructor.GetParameters();
        var declaredBy = Assert.Single(parameters, static p => p.Name == "DeclaredBy");

        Assert.False(declaredBy.HasDefaultValue);
        Assert.False(declaredBy.IsOptional);
        Assert.Equal(typeof(string), declaredBy.ParameterType);

        // record 的位置参数生成 `{ get; init; }`——init 只是构造期可写，实例化之后改不了；
        // 关键是它**必须有值**（位置参数打断构造点），而不是「可缺省的 init 属性」。
        var setter = type.GetProperty("DeclaredBy")!.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        Assert.Contains(
            setter!.ReturnParameter.GetRequiredCustomModifiers(),
            static modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static string Member(string ownerId)
        => $$"""
            "name": "onPlayed",
            "visibility": [ "callable", "mountable" ],
            "view": { "fields": [] },
            "slot": { "type": "handler" },
            "handler": {
              "id": "{{ownerId}}#onPlayed",
              "entry": "HandleAsync",
              "script": "{{Script}}"
            }
            """;

    private static string Root()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{RootId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [ { {{Member(RootId)}} } ],
                "mounts": [ { "target": "{{Played}}", "member": "onPlayed" } ],
                "wraps": [ { "target": "self#onPlayed", "phase": "before", "handler": "wrap.before" } ]
              }
            }
            """;

    private static string Middle()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{MiddleId}}",
                "extends": "{{RootId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [],
                "mounts": [],
                "wraps": []
              }
            }
            """;

    private static string Draft()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "{{DraftId}}",
                "product": { "id": "test.own.product" },
                "extends": "{{MiddleId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [ { {{Member(DraftId)}} } ],
                "mounts": [],
                "wraps": [ { "target": "self#onPlayed", "phase": "before", "handler": "wrap.draft" } ]
              }
            }
            """;

    private static string Template()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{TemplateId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [ { {{Member(TemplateId)}} } ],
                "mounts": [ { "target": "{{Played}}", "member": "onPlayed" } ],
                "wraps": []
              }
            }
            """;

    private const string Manifest = """
        {
          "schemaVersion": 2,
          "id": "pack.ownership",
          "guid": "11111111-2222-4333-8444-555555555556",
          "kind": "cardpack",
          "version": "b0e8242731f10fb1a3c9a35f2521bc6e",
          "metadata": []
        }
        """;

    private static void Write(string pack, string relative, string content)
    {
        var path = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
