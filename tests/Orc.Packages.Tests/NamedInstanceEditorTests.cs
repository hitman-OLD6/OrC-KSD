using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S30／S31**：<c>named</c> 的三级身份（虚拟记录 → <c>Save</c> 测试段 → 转正正式段）与
/// 出身记录 <c>origin</c>（不是依赖、不承担重建）。
/// </summary>
public class NamedInstanceEditorTests : IDisposable
{
    private const string TemplateId = "tpl.param";
    private const string ProductId = "effect.fireball";
    private const string Script =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    private readonly string _root;
    private readonly string _pack;
    private readonly AssetEditor _editor;

    public NamedInstanceEditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-named-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        File.WriteAllText(Path.Combine(_pack, "manifest.json"), Manifest);
        Write(_pack, "effects-v4/tpl.param.component.json", Template);

        _editor = new AssetEditor(
            new AssetRegistry(),
            new EditableRootPolicy(_root),
            new AssetEditorOptions { PackageRoots = new[] { _root } });
    }

    [Fact]
    public void 虚拟记录可描述为Virtual_但正式解析面不认()
    {
        var session = _editor.BeginSession(EditorSessionRequest.Edit(_pack));

        var record = _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId);

        var records = _editor.ListNamedInstances();
        Assert.Equal(ProductId, Assert.Single(records).Id);

        // 未 Save ⇒ 不落盘（不进资产库／sidecar／lock）。
        Assert.False(File.Exists(Path.Combine(session.TemporaryRoot, "effects-v4", ProductId + ".component.json")));

        // 编辑器会话内可见（三态之一：Virtual）。
        var virtualDescription = _editor.DescribePrefab(session.TemporaryRoot, ProductId);
        Assert.Equal(PrefabStage.Virtual, virtualDescription.Stage);
        Assert.Equal("component", virtualDescription.Prefab!.Kind);
        Assert.Single(virtualDescription.Members);
        Assert.Equal(record.Origin.ArgsHash, virtualDescription.Prefab.Origin!.ArgsHash);

        // 正式解析面（PrefabCatalog）不认：卡数据引用即**明确报错**（不静默）。
        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition("card.test", EntityKind.Card, components: new[] { ProductId }));
        var error = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("card.test"));
        Assert.Contains(ProductId, error.Message, StringComparison.Ordinal);
        Assert.Contains("未登记", error.Message, StringComparison.Ordinal);
        Assert.Contains("named", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save进测试段_转正进正式段_三态可区分()
    {
        var session = _editor.BeginSession(EditorSessionRequest.Edit(_pack));
        _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId);

        var saved = _editor.SaveNamedInstance(ProductId);
        Assert.Equal(ProductId, saved.Asset!.LocalId);
        Assert.Empty(_editor.ListNamedInstances()); // 已转为盘上资产

        var productPath = Path.Combine(session.TemporaryRoot, "effects-v4", ProductId + ".component.json");
        Assert.True(File.Exists(productPath));

        var testLock = AssetLockJson.ReadPackage(session.TemporaryRoot, AssetDomain.Test)!;
        Assert.Contains(testLock.Assets, asset => asset.AppId == AssetAppId.Compose("pack.named", ProductId));
        var formalInTemp = AssetLockJson.ReadPackage(session.TemporaryRoot, AssetDomain.Formal);
        Assert.True(formalInTemp is null || formalInTemp.Assets.Count == 0, "Save 不得写正式 lock");

        var testStage = _editor.DescribePrefab(session.TemporaryRoot, ProductId);
        Assert.Equal(PrefabStage.Test, testStage.Stage);

        _editor.Promote();

        var formal = AssetLockJson.ReadPackage(_pack, AssetDomain.Formal)!;
        Assert.Contains(formal.Assets, asset => asset.AppId == AssetAppId.Compose("pack.named", ProductId));
        var testAfterPromote = AssetLockJson.ReadPackage(_pack, AssetDomain.Test)!;
        Assert.DoesNotContain(testAfterPromote.Assets, asset => asset.AppId == AssetAppId.Compose("pack.named", ProductId));

        var formalStage = _editor.DescribePrefab(_pack, ProductId);
        Assert.Equal(PrefabStage.Formal, formalStage.Stage);
        Assert.Equal(ProductId, formalStage.Prefab!.Id);
    }

    [Fact]
    public void 同id两处都拒绝_named不覆盖()
    {
        var session = _editor.BeginSession(EditorSessionRequest.Edit(_pack));
        _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId);

        var duplicateVirtual = Assert.Throws<AssetEditorException>(
            () => _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId));
        Assert.Equal(AssetEditorErrorCode.AssetExists, duplicateVirtual.Code);

        _editor.SaveNamedInstance(ProductId);

        // Save 后再用同 id 建虚拟记录 ⇒ 与资产库比对（早失败）。
        var duplicateSaved = Assert.Throws<AssetEditorException>(
            () => _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId));
        Assert.Equal(AssetEditorErrorCode.AssetExists, duplicateSaved.Code);

        // 已保存的记录不能再次 Save（virtual 记录已移出）。
        var missing = Assert.Throws<AssetEditorException>(() => _editor.SaveNamedInstance(ProductId));
        Assert.Equal(AssetEditorErrorCode.AssetNotFound, missing.Code);
    }

    [Fact]
    public void origin随产物导出_且不是依赖()
    {
        var session = _editor.BeginSession(EditorSessionRequest.Edit(_pack));
        var record = _editor.InstantiateNamed(session.TemporaryRoot, TemplateId, Args(), ProductId);
        _editor.SaveNamedInstance(ProductId);

        var productPath = Path.Combine(session.TemporaryRoot, "effects-v4", ProductId + ".component.json");
        var json = File.ReadAllText(productPath);

        using (var document = JsonDocument.Parse(json))
        {
            var origin = document.RootElement.GetProperty("asset").GetProperty("origin");
            Assert.Equal(TemplateId, origin.GetProperty("templateId").GetString());
            Assert.Equal(record.Origin.ArgsHash, origin.GetProperty("argsHash").GetString());
            Assert.Equal(7m, origin.GetProperty("args").GetProperty("damage").GetDecimal());
            Assert.Equal("csx", origin.GetProperty("args").GetProperty("onHook").GetProperty("kind").GetString());
        }

        // 读回仍带 origin（schema 往返）。
        Assert.Equal(TemplateId, AssetSchema.Read(json, "product").Origin!.TemplateId);

        // **不是依赖**：只把产物放进一个空目录（模板与草案都不存在），照常装载。
        var isolated = Path.Combine(_root, "isolated", "effects-v4");
        Directory.CreateDirectory(isolated);
        File.Copy(productPath, Path.Combine(isolated, ProductId + ".component.json"));

        var load = AssetPackLoader.Load(
            GameEnginePrefabs.CreateRuntime(),
            isolated,
            new CSharpScriptEvaluator(),
            new CSharpSyncScriptEvaluator());

        Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => e.Message)));
        Assert.Equal(new[] { ProductId }, load.AssetIds);
    }

    private static IReadOnlyDictionary<string, object?> Args()
        => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["damage"] = 7m,
            ["onHook"] = HandlerSlotArgument.Csx(Script),
        };

    private static void Write(string pack, string relative, string content)
    {
        var path = Path.Combine(pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string Manifest = """
        {
          "schemaVersion": 2,
          "id": "pack.named",
          "guid": "11111111-2222-4333-8444-555555555555",
          "kind": "cardpack",
          "version": "b0e8242731f10fb1a3c9a35f2521bc6d",
          "metadata": []
        }
        """;

    private const string Template = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "tpl.param",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [ { "name": "seen", "type": "number", "initial": 0 } ],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "slot": { "type": "handler" }
              }
            ],
            "mounts": [],
            "wraps": [],
            "slots": [ { "name": "damage", "type": "number", "value": 3 } ]
          }
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
