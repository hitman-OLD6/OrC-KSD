using Orc.Core.Model;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R34／R35 前端接口**：两条开发路径共用 <see cref="AssetEditor.DescribePrefab"/>——
/// 传本包效果 id＝改已有、传模板 id（配模板库根）＝从模板新建，**返回结构一致**。
/// <para>
/// 前端只需要两个布尔判断能不能编辑（<c>IsTemplate</c>／<c>RequiresImplementation></c>）与两段文案
/// （<c>Label</c>／<c>Description</c>，null＝回退显示 id／成员名），不必自己推导继承链。
/// </para>
/// </summary>
public class TemplateDescriptionTests : IDisposable
{
    private const string ParentId = "demo.parent";
    private const string TemplateId = "template.unit.deployed.self";
    private const string DraftPath = "modings/demo.child.draft.moding.json";

    private readonly string _root;
    private readonly string _pack;
    private readonly string _templateRoot;

    public TemplateDescriptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-template-desc-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        _templateRoot = Path.Combine(_root, "templates");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
        Directory.CreateDirectory(Path.Combine(_templateRoot, "effects-v4"));

        File.Copy(
            Path.Combine(RepoTemplateDirectory, TemplateId + ".component.json"),
            Path.Combine(_templateRoot, "effects-v4", TemplateId + ".component.json"));

        Write("effects-v4/demo.parent.component.json", ParentWithLabels);
    }

    [Fact]
    public void 模板id可解析_两条路径返回结构一致_IsTemplate与RequiresImplementation可见()
    {
        var editor = NewEditor(_templateRoot);

        var template = editor.DescribePrefab(_pack, TemplateId);
        var own = editor.DescribePrefab(_pack, ParentId);

        Assert.Empty(template.Diagnostics);
        Assert.Empty(own.Diagnostics);

        // 结构一致（前端只有一套渲染逻辑）：同一 DTO 形状、成员表都在。
        Assert.NotNull(template.Prefab);
        Assert.NotNull(own.Prefab);
        Assert.Single(template.Members);
        Assert.Equal(template.Members[0].GetType(), own.Members[0].GetType());

        // R34 第 2 条：两个布尔把「模板」与「必须实现」压成可直接用的判断。
        Assert.True(template.Prefab!.IsTemplate);
        Assert.False(own.Prefab!.IsTemplate);

        var onHook = Assert.Single(template.Members);
        Assert.Equal("onHook", onHook.Name);              // R34 第 3 条：内置模板成员名固定
        Assert.True(onHook.RequiresImplementation);
        Assert.Null(onHook.HandlerId);

        Assert.All(own.Members, m => Assert.False(m.RequiresImplementation));

        // R35：模板文案由生成器派生写入，DTO 原样读到。
        Assert.Equal("部署时（本单位）", template.Prefab.Label);
    }

    [Fact]
    public void 有草案时结构一致_覆写成员不再要求实现且标签读草案()
    {
        Write(DraftPath, Draft);
        var editor = NewEditor(_templateRoot);

        var withDraft = editor.DescribePrefab(_pack, TemplateId, DraftPath);
        var withoutDraft = editor.DescribePrefab(_pack, TemplateId);

        Assert.NotNull(withDraft.Draft);
        Assert.Null(withoutDraft.Draft);
        Assert.Equal(withoutDraft.Members.Count, withDraft.Members.Count); // 同一成员表，只是标注不同

        var onHook = Assert.Single(withDraft.Members);
        Assert.True(onHook.Overridden);
        Assert.False(onHook.Inherited);
        // 子覆写后不再要求实现（与摊平期的抽象合并同源，R31 第 3 条）。
        Assert.False(onHook.RequiresImplementation);
        Assert.Equal("填一个动作", onHook.Label);
    }

    [Fact]
    public void 未配置模板库时传模板id_报template_root_missing且文案直指配置动作()
    {
        var description = NewEditor(templateRoot: null).DescribePrefab(_pack, TemplateId);

        var diagnostic = Assert.Single(description.Diagnostics);
        Assert.Equal(PrefabDescriptionDiagnostic.TemplateRootMissing, diagnostic.Code);
        Assert.Equal(TemplateId, diagnostic.Target);
        Assert.Contains("请在 AssetEditor 构造时传入模板库根路径", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(description.Prefab); // 不抛异常，返回空结构（D13 口径）
    }

    [Fact]
    public void 普通组件填label与description后原样读到_未声明则为null()
    {
        var description = NewEditor(templateRoot: null).DescribePrefab(_pack, ParentId);
        var prefab = description.Prefab!;

        Assert.Equal("演示用组件", prefab.Label);
        Assert.Equal("给前端看的一段说明", prefab.Description);

        var labelled = Assert.Single(description.Members, m => m.Name == "onPlayed");
        Assert.Equal("打出时触发", labelled.Label);

        var unlabelled = Assert.Single(description.Members, m => m.Name == "onIdle");
        Assert.Null(unlabelled.Label); // 未声明 ⇒ null，前端回退显示成员名
    }

    [Fact]
    public void schemaVersion不升_新增字段是增量变更()
    {
        // R35 末条：未声明 label 的存量资产装载行为与 DTO 输出不变 ⇒ 版本不动。
        Assert.Equal(4, AssetSchema.Version);

        var asset = AssetSchema.Read(ParentWithLabels, "parent");
        Assert.Equal("演示用组件", asset.Label);

        // 未声明时既不写字段、也不产生非 null 值。
        var minimal = AssetSchema.Read(
            ParentWithLabels
                .Replace("\"label\": \"演示用组件\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"description\": \"给前端看的一段说明\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"label\": \"打出时触发\",", string.Empty, StringComparison.Ordinal),
            "parent");

        Assert.Null(minimal.Label);
        Assert.Null(minimal.Description);
        Assert.All(minimal.Members, m => Assert.Null(m.Label));
        Assert.DoesNotContain("\"label\"", AssetSchema.Write(minimal), StringComparison.Ordinal);
    }

    [Fact]
    public void 未声明文案的存量资产_DTO输出null且装载行为不变()
    {
        // R35 第 1 条：未声明 label 的存量资产——DTO 字段为 null（前端回退显示 id／成员名），
        // 装载与 schemaVersion 都不变。
        Write(
            "effects-v4/demo.plain.component.json",
            ParentWithLabels
                .Replace("\"label\": \"演示用组件\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"description\": \"给前端看的一段说明\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"label\": \"打出时触发\",", string.Empty, StringComparison.Ordinal)
                .Replace("\"id\": \"demo.parent\"", "\"id\": \"demo.plain\"", StringComparison.Ordinal)
                .Replace("demo.parent#onPlayed", "demo.plain#onPlayed", StringComparison.Ordinal)
                .Replace("demo.parent#onIdle", "demo.plain#onIdle", StringComparison.Ordinal));

        var description = NewEditor(templateRoot: null).DescribePrefab(_pack, "demo.plain");

        Assert.Empty(description.Diagnostics);
        Assert.Null(description.Prefab!.Label);
        Assert.Null(description.Prefab.Description);
        Assert.All(description.Members, m => Assert.Null(m.Label));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private AssetEditor NewEditor(string? templateRoot)
        => new(
            new AssetRegistry(),
            new EditableRootPolicy(_root),
            new AssetEditorOptions { TemplateRoot = templateRoot });

    private static string RepoTemplateDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? AppContext.BaseDirectory;
            return Path.Combine(root, "packs", "effect-templates", "effects-v4");
        }
    }

    private static readonly string ParentWithLabels = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "demo.parent",
            "authorize-moding": true,
            "label": "演示用组件",
            "description": "给前端看的一段说明",
            "requires-host": "unit",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onPlayed",
                "label": "打出时触发",
                "visibility": [ "mountable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "demo.parent#onPlayed",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              },
              {
                "name": "onIdle",
                "visibility": [ "mountable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "demo.parent#onIdle",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
            ],
            "mounts": [ { "target": "engine#card.played", "member": "onPlayed" } ],
            "wraps": []
          }
        }
        """;

    private static readonly string Draft = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "demo.child.draft",
            "product": { "id": "demo.child" },
            "extends": "template.unit.deployed.self",
            "requires-host": "unit",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onHook",
                "label": "填一个动作",
                "visibility": [ "mountable" ],
                "view": { "fields": [
                  { "name": "Unit", "type": "entity", "writable": false, "required": false },
                  { "name": "Position", "type": "entity", "writable": false, "required": false },
                  { "name": "Source", "type": "entity", "writable": false, "required": false }
                ] },
                "handler": {
                  "id": "demo.child.draft#onHook",
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
}
