using Orc.Core.Model;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S10 入口一**（R4 扩展／R24／D13）：<see cref="AssetEditor.DescribePrefab"/> 的预制体视图。
/// <para>
/// 断言集中在四件事上：① 一次查询给出 R24 第 1 条的全部面；② 无草案与有草案**结构一致**
/// （<c>draft: null</c> vs 有内容）；③ 父缺失＝**诊断不抛**；④ 与装载期展开**同源**（展开经
/// <see cref="PrefabCatalog.Resolve"/>，成员表是合并后的结果，不是文件里的增量）。
/// </para>
/// </summary>
public class PrefabDescriptionTests : IDisposable
{
    private const string ParentId = "test.parent";
    private const string DraftPath = "modings/test.child.draft.moding.json";

    private readonly string _root;
    private readonly string _pack;

    public PrefabDescriptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-desc-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));

        Write("effects-v4/test.parent.component.json", Parent);
    }

    [Fact]
    public void 无草案_结构与有草案一致_成员全部为继承()
    {
        var editor = NewEditor();
        var description = editor.DescribePrefab(_pack, ParentId);

        Assert.Null(description.Draft);
        Assert.Empty(description.Diagnostics);

        var prefab = Assert.IsType<PrefabSummary>(description.Prefab);
        Assert.Equal(ParentId, prefab.Id);
        Assert.True(prefab.AuthorizesModing);
        Assert.Equal("unit", prefab.RequiresHost);

        // 一次查询拿到成员表 ＋ 视图字段与可写性（R24 第 1 条）。
        var member = Assert.Single(description.Members);
        Assert.Equal("onDeployed", member.Name);
        Assert.Equal("self#onDeployed", member.Address);
        Assert.True(member.Inherited);
        Assert.False(member.Overridden);
        Assert.False(member.Added);
        Assert.Equal("script", member.HandlerSource);
        Assert.True(member.Overridable);

        var defense = Assert.Single(member.ViewFields, f => f.Name == "Defense");
        Assert.True(defense.Writable);
        Assert.Equal("number", defense.Type);
    }

    [Fact]
    public void 可挂载信号清单带timing_可包装成员的写入字段与视图声明同源()
    {
        var description = NewEditor().DescribePrefab(_pack, ParentId);

        // ① 可挂载信号清单（R24）：每条必须带 timing——否则前端会把 load 时机挂给 placed 组件。
        var placed = Assert.Single(description.MountableTargets, t => t.Address == "engine#card.placed");
        Assert.Equal("placed", placed.Timing);
        Assert.All(description.MountableTargets, t => Assert.False(string.IsNullOrEmpty(t.Timing)));

        // ② 可包装成员：(self#…, 三种相位, seam 清单, 可写字段)。
        var wrappable = Assert.Single(description.WrappableMembers, m => m.Address == "self#onDeployed");
        Assert.Equal(new[] { "before", "after", "replace" }, wrappable.SupportedPhases);
        Assert.Equal(new[] { "Defense" }, wrappable.WritableFields);

        // ③ 「与 view.fields[].writable 同源」：本成员的只读字段不得出现在可写清单里（R17 的前端依据）。
        var member = Assert.Single(description.Members);
        Assert.DoesNotContain("Card", wrappable.WritableFields);
        Assert.False(member.ViewFields.Single(f => f.Name == "Card").Writable);
    }

    [Fact]
    public void 有草案_重载与新增分别标注_父成员仍是继承()
    {
        Write(DraftPath, Draft);

        var description = NewEditor().DescribePrefab(_pack, ParentId, DraftPath);

        var draft = Assert.IsType<PrefabDraftSummary>(description.Draft);
        Assert.Equal("test.child.draft", draft.DraftAssetId);
        Assert.Equal(ParentId, draft.DeclaredParentId);
        Assert.Equal(new[] { "onDeployed" }, draft.OverriddenMembers);
        Assert.Equal(new[] { "onNew" }, draft.AddedMembers);

        var overridden = Assert.Single(description.Members, m => m.Name == "onDeployed");
        Assert.True(overridden.Overridden);
        Assert.False(overridden.Inherited);
        Assert.Equal("dsl", overridden.HandlerSource);

        var added = Assert.Single(description.Members, m => m.Name == "onNew");
        Assert.True(added.Added);
        Assert.False(added.Inherited);
        Assert.Equal("script", added.HandlerSource);
    }

    [Fact]
    public void 父未注册_返回诊断而不抛异常()
    {
        var description = NewEditor().DescribePrefab(_pack, "test.missing");

        Assert.Null(description.Prefab);
        Assert.Empty(description.Members); // 不展示半张表（R1 第 2 条）
        var diagnostic = Assert.Single(description.Diagnostics);
        Assert.Equal(PrefabDescriptionDiagnostic.ParentMissing, diagnostic.Code);
        Assert.Contains("test.missing", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 包根不存在_同样走诊断()
    {
        var description = NewEditor().DescribePrefab(Path.Combine(_root, "nope"), ParentId);

        Assert.Null(description.Prefab);
        Assert.Equal(PrefabDescriptionDiagnostic.PackMissing, Assert.Single(description.Diagnostics).Code);
    }

    [Fact]
    public void 草案声明的父不一致_给诊断但不阻断()
    {
        Write(DraftPath, Draft.Replace($"\"extends\": \"{ParentId}\"", "\"extends\": \"test.other\""));

        var description = NewEditor().DescribePrefab(_pack, ParentId, DraftPath);

        Assert.Contains(description.Diagnostics, d => d.Code == PrefabDescriptionDiagnostic.DraftParentMismatch);
        Assert.NotNull(description.Prefab);
    }

    [Fact]
    public void 子资产的展开表是合并结果_证明复用装载期展开()
    {
        // 子组件资产 extends 父（父授权 moding），只声明自己新增的成员。
        Write("effects-v4/test.child.component.json", Child);

        var description = NewEditor().DescribePrefab(_pack, "test.child");

        Assert.Equal(new[] { "onDeployed", "onChild" }, description.Members.Select(m => m.Name));
        Assert.Equal("test.parent", description.Members[0].DeclaredBy);
        Assert.Equal("test.child", description.Members[1].DeclaredBy);
        Assert.Contains("test.parent", Assert.IsType<PrefabSummary>(description.Prefab).Chain);
    }

    [Fact]
    public void 未授权的覆写_展开即拒绝并给父与成员名()
    {
        // 父没打开 authorize-moding：子一覆写就过不了装载期的同一个门（R2），因此 DescribePrefab 也拒绝展开。
        Write("effects-v4/test.parent.component.json", Parent.Replace("\"authorize-moding\": true,", string.Empty));
        Write("effects-v4/test.child.component.json", OverridingChild);

        var description = NewEditor().DescribePrefab(_pack, "test.child");

        var diagnostic = Assert.Single(description.Diagnostics, d => d.Code == PrefabDescriptionDiagnostic.ParentInvalid);
        Assert.Contains("onDeployed", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("test.parent", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private AssetEditor NewEditor()
        => new(new AssetRegistry(), new EditableRootPolicy(_root));

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
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

    private const string Parent = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "test.parent",
            "authorize-moding": true,
            "requires-host": "unit",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onDeployed",
                "visibility": [ "callable", "hostable" ],
                "view": {
                  "fields": [
                    { "name": "Defense", "type": "number", "writable": true, "required": false },
                    { "name": "Card", "type": "entity", "writable": false, "required": false }
                  ]
                },
                "handler": {
                  "id": "test.parent#onDeployed",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    private const string Child = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "test.child",
            "extends": "test.parent",
            "requires-host": "unit",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onChild",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.child#onChild",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    private const string OverridingChild = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "test.child",
            "extends": "test.parent",
            "requires-host": "unit",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onDeployed",
                "visibility": [ "callable", "hostable" ],
                "view": {
                  "fields": [
                    { "name": "Defense", "type": "number", "writable": true, "required": false },
                    { "name": "Card", "type": "entity", "writable": false, "required": false }
                  ]
                },
                "handler": {
                  "id": "test.child#onDeployed",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    private const string Draft = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "test.child.draft",
            "product": { "id": "test.child" },
            "extends": "test.parent",
            "requires-host": "unit",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onDeployed",
                "visibility": [ "callable", "hostable" ],
                "view": {
                  "fields": [
                    { "name": "Defense", "type": "number", "writable": true, "required": false }
                  ]
                },
                "handler": {
                  "id": "test.child.draft#onDeployed",
                  "entry": "HandleAsync",
                  "dsl": "handler-dsl/onDeployed.hdsl.json",
                  "dslHash": "sha256:0000"
                }
              },
              {
                "name": "onNew",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.child.draft#onNew",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;
}
