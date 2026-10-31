using Orc.Core;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S10 编辑出口**（D11）：提交白名单与 kind→路径映射扩到 <c>.hdsl.json</c> 与 moding 草案。
/// <para>
/// 关键语义：DSL 文档**不是资产**——写盘即完成，不进资产声明、不 bump 版本、不重算 lock（有意）；
/// 草案**是**资产（kind＝Moding，只进测试 lock）。
/// </para>
/// </summary>
public class AssetEditorDslCommitTests : IDisposable
{
    private readonly string _root;
    private readonly string _packageRoot;
    private readonly AssetEditor _editor;

    public AssetEditorDslCommitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-editor-dsl-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "packs", "demo");
        CopyDirectory(Path.Combine(FindRepoRoot(), "samples", "card-pack-demo"), _packageRoot);

        _editor = new AssetEditor(
            new AssetRegistry(),
            new EditableRootPolicy(_root),
            new AssetEditorOptions { PackageRoots = [Path.Combine(_root, "packs")] });
    }

    [Fact]
    public void Dsl文档可写入_且不产生资产与版本变化()
    {
        var info = _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));
        var before = _editor.ListAssets();

        var result = _editor.Commit("handler-dsl/probe.hdsl.json", """{"schemaVersion":1,"actions":[]}""");

        Assert.Null(result.Asset); // DSL 没有资产身份
        Assert.False(result.VersionBumped);
        Assert.True(File.Exists(Path.Combine(info.TemporaryRoot, "handler-dsl", "probe.hdsl.json")));

        // lock／版本对 DSL 完全无感（D11，有意）：资产集合与测试 lock 都不动。
        Assert.Equal(before.Count, _editor.ListAssets().Count);
        Assert.DoesNotContain(
            _editor.PreviewLocks().Test.Assets,
            entry => entry.AppId.Contains("probe", StringComparison.Ordinal));
    }

    [Fact]
    public void Moding草案可写入_按Moding种类登记进测试lock()
    {
        _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));

        var result = _editor.Commit("modings/demo.probe.draft.moding.json", Draft);

        var asset = Assert.IsType<EditorAssetInfo>(result.Asset);
        Assert.Equal(AssetKind.Moding, asset.Kind);
        Assert.Equal("demo.probe.draft", asset.LocalId);
        Assert.Equal("modings/demo.probe.draft.moding.json", asset.SourcePath);

        Assert.Contains(_editor.ListAssets(), a => a.Kind == AssetKind.Moding && a.LocalId == "demo.probe.draft");
        Assert.Contains(
            _editor.PreviewLocks().Test.Assets,
            entry => entry.Kind == AssetKind.Moding
                     && entry.AppId.EndsWith("/demo.probe.draft", StringComparison.Ordinal));
    }

    [Fact]
    public void Moding草案缺extends_拒绝()
    {
        _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));

        var exception = Assert.Throws<AssetEditorException>(
            () => _editor.Commit("modings/x.moding.json", Draft.Replace("\"extends\": \"demo.rally\",", string.Empty)));

        Assert.Equal(AssetEditorErrorCode.InvalidContent, exception.Code);
        Assert.Contains("extends", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 其它扩展名仍被拒绝()
    {
        _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));

        var exception = Assert.Throws<AssetEditorException>(() => _editor.Commit("misc/x.txt", "hi"));

        Assert.Equal(AssetEditorErrorCode.InvalidContent, exception.Code);
        Assert.Contains("不支持", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Moding的默认源路径是modings下的moding文件()
    {
        // kind→路径映射（S10 的实事之一）：Moding 增一条，落 modings/<localId>.moding.json。
        var info = _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));
        var draftPath = Path.Combine(info.TemporaryRoot, "modings", "demo.auto.draft.moding.json");
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        File.WriteAllText(draftPath, Draft.Replace("demo.probe.draft", "demo.auto.draft", StringComparison.Ordinal));

        var asset = _editor.CreateAsset(AssetKind.Moding, "demo.auto.draft");

        Assert.Equal(AssetKind.Moding, asset.Kind);
        Assert.Equal("modings/demo.auto.draft.moding.json", asset.SourcePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根（OrcEngine.sln）。");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }

    private const string Draft = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "demo.probe.draft",
            "product": { "id": "demo.probe" },
            "extends": "demo.rally",
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
}
