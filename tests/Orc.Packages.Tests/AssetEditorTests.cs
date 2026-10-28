using System.Security.Cryptography;
using System.Text;
using Orc.Core;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.TestSupport;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S8 验收：后端编辑器 <see cref="AssetEditor"/>——会话互斥、临时包位置与已存在即失败、
/// 版本规则、提交双通道、测试 lock 自动重算、转正产出（新版本＋两 lock＋versioins 历史）、放弃无残留、
/// 越界路径拒绝、无策略构造不可用、分层（<c>Orc.Packages</c> 不引用 <c>Orc.Mcp</c>）。
/// </summary>
public class AssetEditorTests : IDisposable
{
    private const string RiflemanPath = "cards/demo.rifleman.card.json";

    private readonly string _root;
    private readonly string _packsRoot;
    private readonly string _packageRoot;
    private readonly AssetRegistry _registry = new();
    private readonly AssetEditor _editor;

    public AssetEditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-editor-" + Guid.NewGuid().ToString("N"));
        _packsRoot = Path.Combine(_root, "packs");
        _packageRoot = Path.Combine(_packsRoot, "demo");
        Directory.CreateDirectory(_packsRoot);
        CopyDirectory(Path.Combine(FindRepoRoot(), "samples", "card-pack-demo"), _packageRoot);

        // 编辑器的写入面目前仍是旧格式（`AssetEditorSession` 只写 `*.prefab.json`），本文件的多条用例断言的
        // 是**编辑器对旧格式资产的行为**（提交 bump／版本一致／列举计数）。因此测试**自持**一份最简旧格式资产：
        // 数据在 `tests/Orc.Packages.Tests/Fixtures/`，不再依赖样例包（样例包的旧格式目录已随 v4-only 退场，
        // 测试数据本就不该寄生在产品数据上——夹具漂移过一次，见 PROGRESS §24）。
        var fixtures = Path.Combine(FindRepoRoot(), "tests", "Orc.Packages.Tests", "Fixtures");
        var targetEffects = Path.Combine(_packageRoot, "effects");
        Directory.CreateDirectory(targetEffects);
        foreach (var file in Directory.EnumerateFiles(fixtures, "demo.rally.prefab.json*"))
        {
            File.Copy(file, Path.Combine(targetEffects, Path.GetFileName(file)), overwrite: true);
        }

        _editor = new AssetEditor(
            _registry,
            new EditableRootPolicy(_root),
            new AssetEditorOptions { PackageRoots = [_packsRoot] });
    }

    // ── 会话与临时包 ────────────────────────────────────────────────────────

    [Fact]
    public void Session_Is_Exclusive_And_Can_Restart_After_Discard()
    {
        var info = Begin();
        Assert.True(_editor.HasActiveSession);

        var second = Assert.Throws<AssetEditorException>(() => Begin());
        Assert.Equal(AssetEditorErrorCode.SessionAlreadyActive, second.Code);

        _editor.EndSession(EditorSessionEndMode.Discard);

        Assert.False(_editor.HasActiveSession);
        Assert.False(Directory.Exists(info.TemporaryRoot));

        var restarted = Begin();
        Assert.True(_editor.HasActiveSession);
        Assert.Equal(info.TemporaryRoot, restarted.TemporaryRoot);
    }

    [Fact]
    public void Temporary_Package_Is_A_Sibling_Of_The_Package_Directory()
    {
        var info = Begin();

        Assert.Equal(_packageRoot + ".tmp", info.TemporaryRoot);
        Assert.Equal(Path.GetDirectoryName(_packageRoot), Path.GetDirectoryName(info.TemporaryRoot));
        Assert.True(Directory.Exists(info.TemporaryRoot));
        Assert.False(info.IsNewPackage);
    }

    [Fact]
    public void Existing_Temporary_Package_Fails_With_Its_Name_And_Is_Not_Touched()
    {
        var temporaryRoot = _packageRoot + ".tmp";
        Directory.CreateDirectory(temporaryRoot);
        var sentinel = Path.Combine(temporaryRoot, "keep.txt");
        File.WriteAllText(sentinel, "他人残留");

        var exception = Assert.Throws<AssetEditorException>(() => Begin());

        Assert.Equal(AssetEditorErrorCode.TemporaryPackageExists, exception.Code);
        Assert.Contains(temporaryRoot, exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(sentinel)); // 不清理、不复用
        Assert.Equal("他人残留", File.ReadAllText(sentinel));
        Assert.False(_editor.HasActiveSession);
    }

    [Fact]
    public void Temporary_Package_Outside_Authorized_Root_Is_Denied()
    {
        var other = Path.Combine(_root, "other-root");
        Directory.CreateDirectory(other);
        var editor = new AssetEditor(_registry, new EditableRootPolicy(other));

        var exception = Assert.Throws<AssetEditorException>(() => editor.BeginSession(EditorSessionRequest.Edit(_packageRoot)));

        Assert.Equal(AssetEditorErrorCode.PathDenied, exception.Code);
        Assert.False(Directory.Exists(_packageRoot + ".tmp"));
    }

    [Fact]
    public void Pack_List_Only_Returns_Packages_Inside_Authorized_Roots()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), "orc-editor-outside-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepoRoot(), "samples", "card-pack-demo"), Path.Combine(outsideRoot, "outside-pack"));
        try
        {
            var editor = new AssetEditor(
                _registry,
                new EditableRootPolicy(_root),
                new AssetEditorOptions { PackageRoots = [_packsRoot, outsideRoot] });

            var packages = editor.ListPackages();
            var package = Assert.Single(packages);

            Assert.Equal("demo", package.PackageId);
            Assert.DoesNotContain(
                packages,
                info => info.RootDirectory.StartsWith(outsideRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Pack_List_Accepts_Authorized_Root_That_Is_The_Package_Itself()
    {
        CopyDirectory(Path.Combine(FindRepoRoot(), "samples", "card-pack-demo"), Path.Combine(_packsRoot, "other"));

        var editor = new AssetEditor(
            _registry,
            new EditableRootPolicy(_packageRoot),
            new AssetEditorOptions { PackageRoots = [_packsRoot] });

        var package = Assert.Single(editor.ListPackages());
        Assert.Equal("demo", package.PackageId);
        Assert.Equal(Path.GetFullPath(_packageRoot), package.RootDirectory);
    }

    [Fact]
    public void Pack_List_Silently_Skips_Outside_Package_With_Unparsable_Manifest()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), "orc-editor-outside-" + Guid.NewGuid().ToString("N"));
        var bad = Path.Combine(outsideRoot, "bad-pack");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "manifest.json"), "{ 这不是合法 manifest");
        try
        {
            var editor = new AssetEditor(
                _registry,
                new EditableRootPolicy(_root),
                new AssetEditorOptions { PackageRoots = [outsideRoot] });

            Assert.Empty(editor.ListPackages()); // 不抛错、不泄露 manifest 内容
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Pack_List_Excludes_Temporary_Directories()    {
        var before = Assert.Single(_editor.ListPackages());
        Assert.Equal("demo", before.PackageId);
        Assert.False(before.HasTemporaryPackage);

        Begin();

        var during = Assert.Single(_editor.ListPackages());
        Assert.DoesNotContain(".tmp", during.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(during.HasTemporaryPackage); // 会话期 .tmp 存在：报告占用，且 .tmp 自身不列为包
    }

    [Fact]
    public void Illegal_Sidecar_Source_Fails_Without_Residue()
    {
        var sidecarPath = Path.Combine(_packageRoot, RiflemanPath) + AssetSidecarJson.SidecarExtension;
        var sidecar = AssetSidecarJson.ReadFile(sidecarPath);
        AssetSidecarJson.WriteFile(sidecarPath, sidecar with { Source = "../outside.card.json" });

        var exception = Assert.Throws<AssetEditorException>(() => Begin());

        Assert.Equal(AssetEditorErrorCode.InvalidPackage, exception.Code);
        Assert.False(Directory.Exists(_packageRoot + ".tmp")); // 打开失败不留残留
        Assert.False(_editor.HasActiveSession);
    }

    // ── 版本规则 ────────────────────────────────────────────────────────────

    [Fact]
    public void Commit_Bumps_Version_Into_Test_Domain_By_Default()
    {
        Begin();

        var committed = _editor.Commit(RiflemanPath);

        Assert.True(committed.VersionBumped);
        Assert.Equal("pack/demo/demo.rifleman", committed.Asset.AppId);
        Assert.Equal("1.0.0.1", committed.Asset.Version.ToString());
        Assert.NotEqual(Guid.Empty, committed.Asset.AssetGuid);
        Assert.Equal(
            AssetIdentity.DeriveAssetGuid(
                PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot, "manifest.json"))).Guid,
                AssetKind.Card, "demo.rifleman", committed.Asset.Version),
            committed.Asset.AssetGuid);
    }

    [Fact]
    public void Version_Set_Rejects_Non_Increasing()
    {
        Begin();
        _editor.Commit(RiflemanPath); // 1.0.0.1

        var downgrade = Assert.Throws<AssetEditorException>(
            () => _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.0.0"));
        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, downgrade.Code);

        var equal = Assert.Throws<AssetEditorException>(
            () => _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.0.0.1"));
        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, equal.Code);

        var padded = Assert.Throws<AssetEditorException>(
            () => _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.0.0.1.0"));
        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, padded.Code);
    }

    [Fact]
    public void Version_Set_Rejects_Illegal_Format()
    {
        Begin();

        var exception = Assert.Throws<AssetEditorException>(
            () => _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.0.0.a"));

        Assert.Equal(AssetEditorErrorCode.InvalidVersion, exception.Code);
    }

    [Fact]
    public void Version_Set_Default_Increments_Tail()
    {
        Begin();
        _editor.Commit(RiflemanPath); // 1.0.0.1

        var auto = _editor.SetVersion(AssetKind.Card, "demo.rifleman", version: null);
        Assert.Equal("1.0.0.2", auto.Version.ToString());

        // 显式设定后首次提交不再自动 +1
        var manual = _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.2");
        Assert.Equal("1.2", manual.Version.ToString());
        var commit = _editor.Commit(RiflemanPath);
        Assert.False(commit.VersionBumped);
        Assert.Equal("1.2", commit.Asset.Version.ToString());

        // 之后恢复尾段 +1（1.2 的尾段＝次版本：1.2 → 1.3，与 R17 的合法示例一致）
        var next = _editor.SetVersion(AssetKind.Card, "demo.rifleman", version: null);
        Assert.Equal("1.3", next.Version.ToString());
        Assert.True(next.Version > manual.Version);
    }

    [Fact]
    public void Version_Target_Is_Recorded_With_Fresh_Guid()
    {
        Begin();
        var before = _editor.GetAsset(AssetKind.Card, "demo.rifleman");

        var after = _editor.SetVersion(AssetKind.Card, "demo.rifleman", "1.0.1");

        Assert.Equal("1.0.1", after.Version.ToString());
        Assert.NotEqual(before.AssetGuid, after.AssetGuid);
        Assert.Equal(after.AssetGuid, _editor.GetAsset(AssetKind.Card, "demo.rifleman").AssetGuid);
    }

    [Fact]
    public void Version_Set_Synchronizes_Host_And_Embedded_Assets()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");
        var before = _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay");

        var host = _editor.SetVersion(AssetKind.Effect, "demo.buff", "1.1.0");
        var embedded = _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay");

        Assert.Equal("1.1.0", host.Version.ToString());
        Assert.Equal("1.1.0", embedded.Version.ToString());
        Assert.NotEqual(before.AssetGuid, embedded.AssetGuid); // GUID 随版本重算
        var sidecar = AssetSidecarJson.ReadFile(
            Path.Combine(_packageRoot + ".tmp", "effects", "demo.buff.prefab.json") + AssetSidecarJson.SidecarExtension);
        Assert.All(sidecar.Assets, asset => Assert.Equal("1.1.0", asset.Version.ToString()));
    }

    [Fact]
    public void Version_Set_Synchronizes_All_Embedded_Entries_Of_A_Prefab()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.pair.prefab.json", "demo.pair",
            (AssetKind.Effect, "demo.pair", null, "1.0.0"),
            (AssetKind.Trigger, "demo.pair-onPlay", "onPlay", "1.0.0"),
            (AssetKind.Handler, "demo.pair-onPlay-effect", "onPlay.effect", "1.0.0"));
        _editor.Commit("effects/demo.pair.prefab.json");

        _editor.SetVersion(AssetKind.Effect, "demo.pair", "1.1.0");

        var group = _editor.ListAssets(asset =>
            string.Equals(asset.SidecarPath, "effects/demo.pair.prefab.json.meta.json", StringComparison.Ordinal));
        Assert.Equal(3, group.Count);
        Assert.All(group, asset => Assert.Equal("1.1.0", asset.Version.ToString()));
    }

    [Fact]
    public void Version_Set_On_Embedded_Asset_Also_Synchronizes_Host()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");

        _editor.SetVersion(AssetKind.Trigger, "demo.buff-onPlay", "1.1.0");

        Assert.Equal("1.1.0", _editor.GetAsset(AssetKind.Effect, "demo.buff").Version.ToString());
        Assert.Equal("1.1.0", _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay").Version.ToString());
    }

    [Fact]
    public void Version_Set_Uses_Group_Maximum_And_Heals_Historical_Split()
    {
        // 历史分裂（宿主 1.0.0 ＋ 内含 1.2.0）在会话开始前就存在。
        WritePrefab(_packageRoot, "effects/demo.split.prefab.json", "demo.split",
            (AssetKind.Effect, "demo.split", null, "1.0.0"),
            (AssetKind.Trigger, "demo.split-onPlay", "onPlay", "1.2.0"));
        Begin();

        var rejected = Assert.Throws<AssetEditorException>(
            () => _editor.SetVersion(AssetKind.Effect, "demo.split", "1.0.1"));
        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, rejected.Code);
        Assert.Contains("1.2.0", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("demo.split-onPlay", rejected.Message, StringComparison.Ordinal);

        _editor.SetVersion(AssetKind.Effect, "demo.split", "1.3.0");
        Assert.Equal("1.3.0", _editor.GetAsset(AssetKind.Effect, "demo.split").Version.ToString());
        Assert.Equal("1.3.0", _editor.GetAsset(AssetKind.Trigger, "demo.split-onPlay").Version.ToString());
    }

    [Fact]
    public void Promote_Heals_Historical_Split_Without_Changes()
    {
        WritePrefab(_packageRoot, "effects/demo.split.prefab.json", "demo.split",
            (AssetKind.Effect, "demo.split", null, "1.0.0"),
            (AssetKind.Trigger, "demo.split-onPlay", "onPlay", "1.2.0"));
        Begin();

        var result = _editor.Promote();

        var host = Assert.Single(result.Assets,
            asset => asset.Kind == AssetKind.Effect && asset.LocalId == "demo.split");
        var embedded = Assert.Single(result.Assets,
            asset => asset.Kind == AssetKind.Trigger && asset.LocalId == "demo.split-onPlay");
        Assert.Equal(host.Version, embedded.Version);
        Assert.Equal("1.2.0", host.Version.ToString()); // PromoteVersion(组内最大)

        var formal = AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!;
        Assert.Equal("1.2.0", Assert.Single(
            formal.Assets, entry => entry.AppId == "pack/demo/demo.split").Version.ToString());
        Assert.Equal("1.2.0", Assert.Single(
            formal.Assets, entry => entry.AppId == "pack/demo/demo.split-onPlay").Version.ToString());
    }

    [Fact]
    public void Commit_After_Version_Set_Does_Not_Bump_Again_Group_Wide()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");

        _editor.SetVersion(AssetKind.Effect, "demo.buff", "1.1.0");
        var committed = _editor.Commit("effects/demo.buff.prefab.json");

        Assert.False(committed.VersionBumped);
        Assert.Equal("1.1.0", committed.Asset.Version.ToString());
        Assert.Equal("1.1.0", _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay").Version.ToString());
    }

    [Fact]
    public void Commit_After_Embedded_Version_Set_Does_Not_Outrun_Embedded()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");

        _editor.SetVersion(AssetKind.Trigger, "demo.buff-onPlay", "1.1.0");
        var committed = _editor.Commit("effects/demo.buff.prefab.json");

        Assert.False(committed.VersionBumped);
        Assert.Equal(
            _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay").Version,
            committed.Asset.Version);
        Assert.Equal("1.1.0", committed.Asset.Version.ToString());
    }

    [Fact]
    public void Consecutive_Commits_Keep_Bumping_Without_Pinned_Registration()
    {
        Begin();

        var first = _editor.Commit("effects/demo.rally.prefab.json");
        var second = _editor.Commit("effects/demo.rally.prefab.json");

        Assert.True(first.VersionBumped);
        Assert.Equal("1.0.0.1", first.Asset.Version.ToString());
        Assert.True(second.VersionBumped);
        Assert.Equal("1.0.0.2", second.Asset.Version.ToString());
    }

    [Fact]
    public void Created_Asset_First_Commit_Stays_Then_Second_Bumps()
    {
        Begin();
        _editor.CreateAsset(AssetKind.Card, "demo.newcard");

        var first = _editor.Commit("cards/demo.newcard.card.json");
        var second = _editor.Commit("cards/demo.newcard.card.json");

        Assert.False(first.VersionBumped);
        Assert.Equal("1.0.0", first.Asset.Version.ToString());
        Assert.True(second.VersionBumped);
        Assert.Equal("1.0.1", second.Asset.Version.ToString());
    }

    [Fact]
    public void Version_Is_Consistent_Across_Preview_Test_Lock_And_Promoted_Lock()
    {
        Begin();
        _editor.SetVersion(AssetKind.Effect, "demo.rally", "1.1.0");

        var preview = _editor.PreviewLocks();
        Assert.Equal("1.1.0", Assert.Single(
            preview.Formal.Assets, entry => entry.AppId == "pack/demo/demo.rally").Version.ToString());
        Assert.Equal("1.1.0", Assert.Single(
            preview.Test.Assets, entry => entry.AppId == "pack/demo/demo.rally").Version.ToString());

        _editor.Promote();
        Assert.Equal("1.1.0", Assert.Single(
            AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!.Assets,
            entry => entry.AppId == "pack/demo/demo.rally").Version.ToString());
    }

    // ── 提交双通道 ──────────────────────────────────────────────────────────

    [Fact]
    public void Commit_Content_Channel_Writes_The_File()
    {
        Begin();
        var content = File.ReadAllText(Path.Combine(_packageRoot, RiflemanPath))
            .Replace("\"name\": \"步枪兵\"", "\"name\": \"步枪兵（改）\"", StringComparison.Ordinal);

        var committed = _editor.Commit(RiflemanPath, content);

        var written = File.ReadAllText(Path.Combine(_packageRoot + ".tmp", RiflemanPath));
        Assert.Contains("步枪兵（改）", written, StringComparison.Ordinal);
        Assert.Equal("1.0.0.1", committed.Asset.Version.ToString());
    }

    [Fact]
    public void Commit_Path_Channel_Reads_Frontend_Written_File()
    {
        Begin();
        var frontendPath = Path.Combine(_packageRoot + ".tmp", RiflemanPath);
        File.WriteAllText(
            frontendPath,
            File.ReadAllText(frontendPath).Replace("\"name\": \"步枪兵\"", "\"name\": \"前端直写\"", StringComparison.Ordinal));

        var committed = _editor.Commit(RiflemanPath);

        Assert.Equal("demo.rifleman", committed.Asset.LocalId);
        Assert.Contains("前端直写", File.ReadAllText(frontendPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_Rejects_Path_Escapes()
    {
        Begin();

        foreach (var path in new[]
                 {
                     "../escape.card.json",
                     Path.Combine(_root, "outside.card.json"),
                     @"\\server\share\x.card.json",
                 })
        {
            var exception = Assert.Throws<AssetEditorException>(() => _editor.Commit(path, "{}"));
            Assert.Equal(AssetEditorErrorCode.PathDenied, exception.Code);
        }

        Assert.False(File.Exists(Path.Combine(_root, "outside.card.json")));
    }

    [ReparsePointFact]
    public void Commit_Rejects_Symlink_Escape()
    {
        Begin();
        var outside = Path.Combine(Path.GetTempPath(), "orc-editor-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_packageRoot + ".tmp", "cards", "link");
        Assert.True(ReparsePointProbe.TryCreate(link, outside, out var reason), reason);

        try
        {
            var exception = Assert.Throws<AssetEditorException>(
                () => _editor.Commit("cards/link/evil.card.json", "{}"));

            Assert.Equal(AssetEditorErrorCode.PathDenied, exception.Code);
            Assert.False(File.Exists(Path.Combine(outside, "evil.card.json")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Commit_Rejects_History_Version_Directory()
    {
        Begin();
        const string relative = "versions/9.9.9/cards/x.card.json";

        var exception = Assert.Throws<AssetEditorException>(() => _editor.Commit(relative, "{}"));

        Assert.Equal(AssetEditorErrorCode.PathDenied, exception.Code);
        Assert.Contains(relative, exception.Message, StringComparison.Ordinal);
        Assert.Contains("versions/", exception.Message, StringComparison.Ordinal);

        // 拒绝发生在写入之前：临时包与正式包都不得出现该文件（连父目录都不创建）。
        Assert.False(File.Exists(Path.Combine(_packageRoot + ".tmp", "versions", "9.9.9", "cards", "x.card.json")));
        Assert.False(Directory.Exists(Path.Combine(_packageRoot + ".tmp", "versions", "9.9.9")));
        Assert.False(File.Exists(Path.Combine(_packageRoot, "versions", "9.9.9", "cards", "x.card.json")));
    }

    [Fact]
    public void CreateAsset_Rejects_History_Version_Directory_As_Source()
    {
        Begin();

        var card = Assert.Throws<AssetEditorException>(() => _editor.CreateAsset(
            AssetKind.Card, "demo.ghost", "versions/1.0.0/cards/demo.ghost.card.json"));
        Assert.Equal(AssetEditorErrorCode.PathDenied, card.Code);

        var effect = Assert.Throws<AssetEditorException>(() => _editor.CreateAsset(
            AssetKind.Effect, "demo.ghost-fx", "versions/1.0.0/effects/demo.ghost-fx.prefab.json"));
        Assert.Equal(AssetEditorErrorCode.PathDenied, effect.Code);

        // 不建目录、不写 stub、不追加 sidecar 条目。
        Assert.False(Directory.Exists(Path.Combine(_packageRoot + ".tmp", "versions", "1.0.0")));
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Card, "demo.ghost"));
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Effect, "demo.ghost-fx"));
    }

    [Fact]
    public void Commit_Prefab_Without_Host_Is_Structured_Error()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.orphan.prefab.json", "demo.orphan",
            (AssetKind.Trigger, "demo.orphan-onPlay", "onPlay", "1.0.0"));
        var sidecarPath = Path.Combine(_packageRoot + ".tmp", "effects", "demo.orphan.prefab.json")
                          + AssetSidecarJson.SidecarExtension;
        var before = File.ReadAllBytes(sidecarPath);

        var exception = Assert.Throws<AssetEditorException>(
            () => _editor.Commit("effects/demo.orphan.prefab.json"));

        Assert.Equal(AssetEditorErrorCode.InvalidContent, exception.Code);
        Assert.Contains("宿主", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(sidecarPath)); // 失败不落盘
    }

    [Fact]
    public void CreateAsset_Rejects_Source_Id_Mismatch()
    {
        Begin();

        var card = Assert.Throws<AssetEditorException>(
            () => _editor.CreateAsset(AssetKind.Card, "demo.alias", RiflemanPath));
        Assert.Equal(AssetEditorErrorCode.InvalidContent, card.Code);
        Assert.Contains("demo.rifleman", card.Message, StringComparison.Ordinal);
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Card, "demo.alias"));

        // sidecar 未追加条目
        var sidecar = AssetSidecarJson.ReadFile(
            Path.Combine(_packageRoot + ".tmp", RiflemanPath) + AssetSidecarJson.SidecarExtension);
        Assert.Single(sidecar.Assets);

        WritePrefab(_packageRoot + ".tmp", "effects/demo.rally2.prefab.json", "demo.rally2");
        var effect = Assert.Throws<AssetEditorException>(
            () => _editor.CreateAsset(AssetKind.Effect, "other", "effects/demo.rally2.prefab.json"));
        Assert.Equal(AssetEditorErrorCode.InvalidContent, effect.Code);
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Effect, "other"));
    }

    [Fact]
    public void Commit_Card_Validates_Against_Multi_Card_Sidecar()
    {
        Begin();
        const string relative = "cards/demo.multi.card.json";
        WriteCard(relative, "demo.multi");
        WriteCardSidecar(relative, "demo.alt-a", "demo.alt-b");

        var rejected = Assert.Throws<AssetEditorException>(() => _editor.Commit(relative));
        Assert.Equal(AssetEditorErrorCode.InvalidContent, rejected.Code);
        Assert.Contains("demo.alt-a", rejected.Message, StringComparison.Ordinal);

        WriteCardSidecar(relative, "demo.multi", "demo.alt-b");
        var committed = _editor.Commit(relative);
        Assert.Equal("demo.multi", committed.Asset.LocalId);
    }

    // ── 转正 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Promote_Rejects_Conflicting_History_Without_Writing()
    {
        Begin();
        _editor.Commit(RiflemanPath); // 1.0.0 → 1.0.0.1（转正将归档 versions/1.0.0）

        var conflict = Path.Combine(_packageRoot, "versions", "1.0.0", "cards", "demo.rifleman.card.json");
        Directory.CreateDirectory(Path.GetDirectoryName(conflict)!);
        File.WriteAllText(conflict, "{\"conflict\":true}");
        var before = Snapshot(_packageRoot);

        var exception = Assert.Throws<AssetEditorException>(() => _editor.Promote());

        Assert.Equal(AssetEditorErrorCode.PromoteFailed, exception.Code);
        Assert.Contains("versions/1.0.0/cards/demo.rifleman.card.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_packageRoot)); // 正式包逐字节不变
        Assert.True(Directory.Exists(_packageRoot + ".tmp")); // 临时包保留
    }

    [Fact]
    public void Promote_Skips_Identical_History_Archive_But_Still_Reports_It()
    {
        Begin();
        _editor.Commit(RiflemanPath);

        var archived = Path.Combine(_packageRoot, "versions", "1.0.0", "cards", "demo.rifleman.card.json");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Copy(Path.Combine(_packageRoot, RiflemanPath), archived);
        var bytes = File.ReadAllBytes(archived);

        var result = _editor.Promote();

        Assert.Equal(bytes, File.ReadAllBytes(archived)); // 历史字节不被改写
        Assert.Contains("versions/1.0.0", result.ArchivedVersions);
    }

    [Fact]
    public void Promote_Rolls_Back_On_Io_Failure()
    {
        Begin();
        var grenadier = "cards/demo.grenadier.card.json";
        var content = File.ReadAllText(Path.Combine(_packageRoot, grenadier))
            .Replace("\"name\": \"掷弹兵\"", "\"name\": \"掷弹兵（回滚验证）\"", StringComparison.Ordinal);
        _editor.Commit(grenadier, content);
        _editor.CreateAsset(AssetKind.Card, "demo.newcard");

        // 让「新建文件」这一步必然失败：正式包同名位置预置一个目录。
        var blocked = Path.Combine(_packageRoot, "cards", "demo.newcard.card.json");
        Directory.CreateDirectory(blocked);
        var before = Snapshot(_packageRoot);

        try
        {
            var exception = Assert.Throws<AssetEditorException>(() => _editor.Promote());

            Assert.Equal(AssetEditorErrorCode.PromoteFailed, exception.Code);
            Assert.Contains("cards/demo.newcard.card.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("回滚完整", exception.Message, StringComparison.Ordinal);
            Assert.Contains("临时包已保留", exception.Message, StringComparison.Ordinal);

            Assert.Equal(before, Snapshot(_packageRoot)); // 含「新增后又被删除」的归档文件与已改文件
            Assert.False(Directory.Exists(Path.Combine(_packageRoot, "versions"))); // 无残留空目录
            Assert.True(Directory.Exists(_packageRoot + ".tmp"));
        }
        finally
        {
            Directory.Delete(blocked, recursive: false);
        }
    }

    [Fact]
    public void Promote_Reports_Rollback_Failures_In_Single_Message()
    {
        Begin();
        _editor.Commit(RiflemanPath);

        // 只读目标：写入失败（UnauthorizedAccessException），且回滚写回同样被拒 ⇒ 回滚失败明细。
        var locked = Path.Combine(_packageRoot, "cards-support", "demo.scout.card.json");
        var attributes = File.GetAttributes(locked);
        File.SetAttributes(locked, attributes | FileAttributes.ReadOnly);
        try
        {
            var exception = Assert.Throws<AssetEditorException>(() => _editor.Promote());

            Assert.Equal(AssetEditorErrorCode.PromoteFailed, exception.Code);
            Assert.Contains("回滚失败 1 项", exception.Message, StringComparison.Ordinal);
            Assert.Contains("cards-support/demo.scout.card.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("临时包已保留", exception.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(_packageRoot + ".tmp"));
        }
        finally
        {
            File.SetAttributes(locked, attributes);
        }
    }

    [Fact]
    public void Promote_Leaves_Unchanged_Sidecar_Bytes_Intact()
    {
        Begin();
        var untouched = Path.Combine(_packageRoot, "cards-support", "demo.scout.card.json")
                        + AssetSidecarJson.SidecarExtension;
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(untouched)));

        _editor.Commit(RiflemanPath);
        _editor.Promote();

        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(untouched))));
    }

    [Fact]
    public void Promote_Keeps_Content_Fingerprint_Stable_Despite_Archive()
    {
        Begin();
        _editor.Commit(RiflemanPath);
        _editor.Promote();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot, "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(_packageRoot, "versions")));
        Assert.Equal(AssetLoadTestKit.RecomputeContent(_packageRoot, "demo"), manifest.Version); // 归档不扰动指纹
    }

    [Fact]
    public void Editor_Fingerprint_Covers_Empty_Sidecar_Outside_Conventions()
    {
        Begin();
        var bare = Path.Combine(_packageRoot + ".tmp", "units", "x.prefab.json")
                   + AssetSidecarJson.SidecarExtension;
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        var before = PackageManifest.Parse(
            File.ReadAllText(Path.Combine(_packageRoot + ".tmp", "manifest.json"))).Version;

        AssetSidecarJson.WriteFile(
            bare,
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, "demo", "units/x.prefab.json", []));

        // 任何一次结构变更都会重算指纹；这里用 commit 触发。
        _editor.Commit(RiflemanPath);
        var after = PackageManifest.Parse(
            File.ReadAllText(Path.Combine(_packageRoot + ".tmp", "manifest.json"))).Version;

        Assert.NotEqual(before, after); // 空 sidecar ＋ 非约定目录同样属于包内容
    }

    [Fact]
    public void Promote_Produces_New_Version_Two_Locks_And_History()
    {
        Begin();
        var committed = _editor.Commit(RiflemanPath); // 1.0.0 → 1.0.0.1

        var result = _editor.Promote();

        // ① 新版本（去测试段并递增：1.0.0.1 → 1.0.1）
        var promoted = Assert.Single(result.Assets, asset => asset.LocalId == "demo.rifleman");
        Assert.Equal("1.0.1", promoted.Version.ToString());
        Assert.NotEqual(committed.Asset.Version, promoted.Version);

        var sidecar = AssetSidecarJson.ReadFile(Path.Combine(_packageRoot, RiflemanPath) + AssetSidecarJson.SidecarExtension);
        var declared = Assert.Single(sidecar.Assets);
        Assert.Equal("1.0.1", declared.Version.ToString());
        Assert.Equal(promoted.AssetGuid, declared.Guid);

        // ② 两 lock：正式 lock 含转正版；测试 lock 清空
        var formal = AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!;
        Assert.Equal("1.0.1", Assert.Single(
            formal.Assets, entry => entry.AppId == "pack/demo/demo.rifleman").Version.ToString());
        Assert.Empty(AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Test)!.Assets);

        // ③ 历史目录：旧版本源文件＋sidecar
        var archivedSource = Path.Combine(_packageRoot, "versions", "1.0.0", "cards", "demo.rifleman.card.json");
        Assert.True(File.Exists(archivedSource));
        var archivedSidecar = AssetSidecarJson.ReadFile(archivedSource + AssetSidecarJson.SidecarExtension);
        Assert.Equal("versions/1.0.0/cards/demo.rifleman.card.json", archivedSidecar.Source);
        Assert.Equal("1.0.0", Assert.Single(archivedSidecar.Assets).Version.ToString());

        // ④ 去 tmp：包版本不含 -tmp，临时目录已删除
        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot, "manifest.json")));
        Assert.False(manifest.Version.IsTemporary);
        Assert.False(Directory.Exists(_packageRoot + ".tmp"));
        Assert.False(_editor.HasActiveSession);
    }

    [Fact]
    public void Promote_Keeps_Untouched_Assets_And_Does_Not_Duplicate_Their_History()
    {
        Begin();
        _editor.Commit(RiflemanPath);
        _editor.Promote();

        var formal = AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!;
        Assert.Equal("1.0.0", Assert.Single(
            formal.Assets, entry => entry.AppId == "pack/demo/demo.scout").Version.ToString());

        // 未改动资产不归档（否则 versions/ 会与其当前版重复注册同一 GUID）
        Assert.False(File.Exists(
            Path.Combine(_packageRoot, "versions", "1.0.0", "cards-support", "demo.scout.card.json")));
    }

    [Fact]
    public void Promoted_Package_Remains_Registrable()
    {
        Begin();
        _editor.Commit(RiflemanPath);
        _editor.Promote();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot, "manifest.json")));
        var registry = new AssetRegistry();

        var registration = PackageRegistrar.Register(_packageRoot, manifest, registry);

        Assert.DoesNotContain(registration.Diagnostics, diagnostic =>
            diagnostic.Code is AssetDiagnosticCode.SourceMissing or AssetDiagnosticCode.GuidMismatch);
        var versions = registry.VersionsOf("demo", AssetKind.Card, "demo.rifleman");
        Assert.Equal(2, versions.Count); // 历史 1.0.0 ＋ 当前 1.0.1
        Assert.Equal("1.0.1", versions[^1].Version.ToString());
    }

    [Fact]
    public void Discard_Leaves_No_Residue()
    {
        var before = Snapshot(_packageRoot);

        var info = Begin();
        _editor.Commit(RiflemanPath);
        _editor.CreateAsset(AssetKind.Card, "demo.discarded");
        _editor.EndSession(EditorSessionEndMode.Discard);

        Assert.False(Directory.Exists(info.TemporaryRoot));

        var after = Snapshot(_packageRoot);
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, hash) in before)
        {
            Assert.True(after.TryGetValue(path, out var current), $"放弃后文件消失：{path}");
            Assert.Equal(hash, current);
        }
        Assert.False(_editor.HasActiveSession);
    }

    // ── 新建资产／新建包／删除 ───────────────────────────────────────────────

    [Fact]
    public void Create_Asset_Starts_At_1_0_0_And_Enters_Test_Lock()
    {
        Begin();

        var created = _editor.CreateAsset(AssetKind.Card, "demo.newcard");

        Assert.Equal("1.0.0", created.Version.ToString());
        Assert.True(created.IsNew);
        Assert.True(File.Exists(Path.Combine(_packageRoot + ".tmp", created.SourcePath)));

        var testLock = _editor.PreviewLocks().Test;
        var entry = Assert.Single(testLock.Assets, asset => asset.AppId == "pack/demo/demo.newcard");
        Assert.Equal("1.0.0", entry.Version.ToString());

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot + ".tmp", "manifest.json")));
        Assert.Contains(created.SidecarPath, manifest.MetadataList, StringComparer.Ordinal);
        Assert.True(manifest.Version.IsTemporary);
    }

    [Fact]
    public void New_Package_Requires_Dependency_Package_Ids()
    {
        var exception = Assert.Throws<AssetEditorException>(
            () => _editor.BeginSession(EditorSessionRequest.CreateNew("brand-new", _packsRoot, [])));

        Assert.Equal(AssetEditorErrorCode.DependenciesRequired, exception.Code);
        Assert.False(Directory.Exists(Path.Combine(_packsRoot, "brand-new.tmp")));
    }

    [Fact]
    public void New_Package_Promote_Writes_First_Formal_Lock()
    {
        var info = _editor.BeginSession(EditorSessionRequest.CreateNew("brand-new", _packsRoot, ["demo"], "新包"));
        Assert.True(info.IsNewPackage);
        Assert.Equal(Path.Combine(_packsRoot, "brand-new.tmp"), info.TemporaryRoot);
        Assert.Equal(["demo"], info.DependsOnPackages);
        Assert.Empty(info.Warnings); // 依赖包 demo 存在：无告警

        _editor.CreateAsset(AssetKind.Card, "brand-new.alpha");
        var result = _editor.Promote();

        var packageRoot = Path.Combine(_packsRoot, "brand-new");
        Assert.True(Directory.Exists(packageRoot));
        Assert.False(Directory.Exists(info.TemporaryRoot));

        var formal = AssetLockJson.ReadPackage(packageRoot, AssetDomain.Formal)!;
        Assert.Equal("1.0.0", Assert.Single(formal.Assets).Version.ToString());
        Assert.Empty(AssetLockJson.ReadPackage(packageRoot, AssetDomain.Test)!.Assets);

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(packageRoot, "manifest.json")));
        Assert.Equal("brand-new", manifest.Id);
        Assert.False(manifest.Version.IsTemporary);
        Assert.Single(manifest.MetadataList);

        // R8 的「期望依赖包 ID」必须落盘（纯声明字段 requires），否则重开包即丢失。
        Assert.Equal(["demo"], manifest.RequiredPackages);
    }

    [Fact]
    public void Delete_Removes_Asset_From_Lock_And_Keeps_Historical_Version()
    {
        Begin();

        _editor.DeleteAsset(AssetKind.Card, "demo.rifleman");
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Card, "demo.rifleman"));

        _editor.Promote();

        var formal = AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!;
        Assert.DoesNotContain(formal.Assets, entry => entry.AppId == "pack/demo/demo.rifleman");
        Assert.False(File.Exists(Path.Combine(_packageRoot, RiflemanPath)));
        Assert.True(File.Exists(
            Path.Combine(_packageRoot, "versions", "1.0.0", "cards", "demo.rifleman.card.json")));
    }

    [Fact]
    public void Delete_Host_Cascades_To_Embedded_Assets()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");

        _editor.DeleteAsset(AssetKind.Effect, "demo.buff");

        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Effect, "demo.buff"));
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay"));

        var test = _editor.PreviewLocks().Test;
        Assert.DoesNotContain(test.Assets, entry => entry.AppId == "pack/demo/demo.buff");
        Assert.DoesNotContain(test.Assets, entry => entry.AppId == "pack/demo/demo.buff-onPlay");
    }

    [Fact]
    public void Delete_Host_Leaves_No_OrphanEmbedded_After_Promote()
    {
        WritePrefab(_packageRoot, "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        Begin();

        _editor.DeleteAsset(AssetKind.Effect, "demo.buff");
        _editor.Promote();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(_packageRoot, "manifest.json")));
        var registry = new AssetRegistry();
        PackageRegistrar.Register(_packageRoot, manifest, registry);

        var formal = AssetLockJson.ReadPackage(_packageRoot, AssetDomain.Formal)!;
        var plan = LoadListPlanner.Plan(registry, [formal]);

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Issues, issue => issue.Code == AssetLoadIssueCode.OrphanEmbedded);
        Assert.DoesNotContain(formal.Assets, entry => entry.AppId == "pack/demo/demo.buff-onPlay");
    }

    [Fact]
    public void Delete_Embedded_Asset_Keeps_Host()
    {
        Begin();
        WritePrefab(_packageRoot + ".tmp", "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        _editor.Commit("effects/demo.buff.prefab.json");

        _editor.DeleteAsset(AssetKind.Trigger, "demo.buff-onPlay");

        Assert.Equal("demo.buff", _editor.GetAsset(AssetKind.Effect, "demo.buff").LocalId);
        Assert.Throws<AssetEditorException>(() => _editor.GetAsset(AssetKind.Trigger, "demo.buff-onPlay"));
        var sidecar = AssetSidecarJson.ReadFile(
            Path.Combine(_packageRoot + ".tmp", "effects", "demo.buff.prefab.json") + AssetSidecarJson.SidecarExtension);
        Assert.Single(sidecar.Assets);
        Assert.Equal(AssetKind.Effect, sidecar.Assets[0].Kind);
    }

    [Fact]
    public void Delete_Host_Removes_Source_And_Sidecar_Formally()
    {
        WritePrefab(_packageRoot, "effects/demo.buff.prefab.json", "demo.buff",
            (AssetKind.Effect, "demo.buff", null, "1.0.0"),
            (AssetKind.Trigger, "demo.buff-onPlay", "onPlay", "1.0.0"));
        var formalSource = Path.Combine(_packageRoot, "effects", "demo.buff.prefab.json");
        Begin();

        _editor.DeleteAsset(AssetKind.Effect, "demo.buff");

        Assert.False(File.Exists(Path.Combine(_packageRoot + ".tmp", "effects", "demo.buff.prefab.json")));
        Assert.False(File.Exists(
            Path.Combine(_packageRoot + ".tmp", "effects", "demo.buff.prefab.json") + AssetSidecarJson.SidecarExtension));

        _editor.Promote();

        Assert.False(File.Exists(formalSource));
        Assert.False(File.Exists(formalSource + AssetSidecarJson.SidecarExtension));
    }

    // ── 列举／依赖／预览 ────────────────────────────────────────────────────

    [Fact]
    public void Asset_List_Supports_Lambda_And_Structured_Filters()
    {
        Begin();

        var all = _editor.ListAssets();
        Assert.Equal(6, all.Count);
        Assert.Single(_editor.ListAssets(asset => asset.Kind == AssetKind.Effect));

        var query = new EditorAssetQuery { Kind = AssetKind.Card, IdPrefix = "demo.rif" };
        var filtered = Assert.Single(_editor.ListAssets(query));
        Assert.Equal("demo.rifleman", filtered.LocalId);

        var byVersion = new EditorAssetQuery { MinVersion = new AssetVersion(1, 0, 0), MaxVersion = new AssetVersion(1, 0, 0) };
        Assert.Equal(6, _editor.ListAssets(byVersion).Count);
    }

    [Fact]
    public void Lock_Preview_Shows_Promoted_Formal_Version()
    {
        Begin();
        _editor.Commit(RiflemanPath);

        var preview = _editor.PreviewLocks();

        Assert.Equal("1.0.1", Assert.Single(
            preview.Formal.Assets, entry => entry.AppId == "pack/demo/demo.rifleman").Version.ToString());
        Assert.Equal("1.0.0.1", Assert.Single(
            preview.Test.Assets, entry => entry.AppId == "pack/demo/demo.rifleman").Version.ToString());
    }

    [Fact]
    public void Dependency_Set_Is_Stored_And_Self_Reference_Is_Rejected()
    {
        Begin();

        var updated = _editor.SetDependencies(
            AssetKind.Card,
            "demo.rifleman",
            [new AssetReference(AssetKind.Card, "demo.scout", "1.0.0")]);

        var sidecar = AssetSidecarJson.ReadFile(
            Path.Combine(_packageRoot + ".tmp", RiflemanPath) + AssetSidecarJson.SidecarExtension);
        var reference = Assert.Single(Assert.Single(sidecar.Assets).DependsOn);
        Assert.Equal("pack/demo/demo.scout", reference.AppId);
        Assert.Equal("1.0.0", reference.VersionConstraint);
        Assert.Equal("1.0.0", updated.Version.ToString());

        var self = Assert.Throws<AssetEditorException>(() => _editor.SetDependencies(
            AssetKind.Card,
            "demo.rifleman",
            [new AssetReference(AssetKind.Card, "pack/demo/demo.rifleman", "1.0.0")]));
        Assert.Equal(AssetEditorErrorCode.DependencyInvalid, self.Code);

        var badConstraint = Assert.Throws<AssetEditorException>(() => _editor.SetDependencies(
            AssetKind.Card,
            "demo.rifleman",
            [new AssetReference(AssetKind.Card, "demo.scout", "不是版本")]));
        Assert.Equal(AssetEditorErrorCode.DependencyInvalid, badConstraint.Code);
    }

    [Fact]
    public void Reload_Without_Loader_Returns_Structured_Result()
    {
        Begin();

        var result = _editor.Reload();

        Assert.False(result.Configured);
        Assert.NotNull(result.Result);
    }

    [Fact]
    public void Operations_Without_Session_Report_NoSession()
    {
        var exception = Assert.Throws<AssetEditorException>(() => _editor.ListAssets());
        Assert.Equal(AssetEditorErrorCode.NoSession, exception.Code);

        var commit = Assert.Throws<AssetEditorException>(() => _editor.Commit(RiflemanPath));
        Assert.Equal(AssetEditorErrorCode.NoSession, commit.Code);

        var promote = Assert.Throws<AssetEditorException>(() => _editor.Promote());
        Assert.Equal(AssetEditorErrorCode.NoSession, promote.Code);
    }

    // ── 分层与构造约束（编译期即禁止） ────────────────────────────────────────

    [Fact]
    public void Editor_Requires_A_Policy_In_Constructor()
    {
        var constructors = typeof(AssetEditor).GetConstructors();

        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IEditableRootPolicy)));
        Assert.NotNull(typeof(AssetEditor).GetConstructor([typeof(AssetRegistry), typeof(IEditableRootPolicy), typeof(AssetEditorOptions)]));
    }

    [Fact]
    public void Packages_Assembly_Does_Not_Reference_Mcp()
    {
        var referenced = typeof(AssetEditor).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, assembly =>
            string.Equals(assembly.Name, "Orc.Mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Path_Normalization_Has_A_Single_Source()
    {
        var guard = Path.Combine(FindRepoRoot(), "src", "Orc.Mcp", "Sandbox", "SandboxPathGuard.cs");
        var text = File.ReadAllText(guard);

        // 沙盒守卫只委托 PathNormalizer（不再自带归一逻辑）。
        Assert.Contains("PathNormalizer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNoReparsePointEscape", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFullPath", text, StringComparison.Ordinal);
        Assert.Contains("IEditableRootPolicy", text, StringComparison.Ordinal);

        var normalizer = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Orc.Packages", "Fs", "PathNormalizer.cs"));
        Assert.Contains("ResolveLinkTarget", normalizer, StringComparison.Ordinal);
        Assert.Contains("IsUncPath", normalizer, StringComparison.Ordinal);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    private EditorSessionInfo Begin() => _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));

    /// <summary>写一份「宿主 ＋ 内含」预制体与其 sidecar（<paramref name="packageRoot"/> 可为正式包或临时包）。</summary>
    private static void WritePrefab(
        string packageRoot,
        string relative,
        string hostId,
        params (AssetKind Kind, string Id, string? Anchor, string Version)[] assets)
    {
        var absolute = Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, $$"""
            {
              "schemaVersion": 2,
              "root": {
                "id": "{{hostId}}",
                "version": 1,
                "mainTrigger": {
                  "id": "t.main",
                  "stableKey": "{{hostId}}",
                  "kind": "passive",
                  "hooks": [],
                  "events": [],
                  "version": 1
                },
                "otherTriggers": [],
                "wrappers": [],
                "injects": []
              }
            }
            """);

        var packageGuid = PackageManifest.Parse(
            File.ReadAllText(Path.Combine(packageRoot, "manifest.json"))).Guid;
        var metadata = assets.Select(asset =>
        {
            var version = AssetVersion.Parse(asset.Version);
            return new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(packageGuid, asset.Kind, asset.Id, version),
                asset.Kind,
                asset.Id,
                version,
                asset.Anchor,
                Loader: null,
                DependsOn: [],
                ConflictsWith: []);
        }).ToArray();

        AssetSidecarJson.WriteFile(
            absolute + AssetSidecarJson.SidecarExtension,
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, "demo", relative, metadata));
    }

    /// <summary>在临时包内写一份卡源文件（以 demo.rifleman 为模板，仅替换 id／name）。</summary>
    private void WriteCard(string relative, string id)
    {
        var absolute = Path.Combine(_packageRoot + ".tmp", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var text = File.ReadAllText(Path.Combine(_packageRoot, RiflemanPath))
            .Replace("\"id\": \"demo.rifleman\"", $"\"id\": \"{id}\"", StringComparison.Ordinal)
            .Replace("\"name\": \"步枪兵\"", $"\"name\": \"{id}\"", StringComparison.Ordinal);
        File.WriteAllText(absolute, text);
    }

    /// <summary>在临时包内写一份声明多张卡的 sidecar。</summary>
    private void WriteCardSidecar(string relative, params string[] ids)
    {
        var packageGuid = PackageManifest.Parse(
            File.ReadAllText(Path.Combine(_packageRoot, "manifest.json"))).Guid;
        var assets = ids.Select(id => new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, id, EditorVersionRules.Initial),
                AssetKind.Card,
                id,
                EditorVersionRules.Initial,
                Anchor: null,
                Loader: null,
                DependsOn: [],
                ConflictsWith: []))
            .ToArray();

        AssetSidecarJson.WriteFile(
            Path.Combine(_packageRoot + ".tmp", relative.Replace('/', Path.DirectorySeparatorChar))
            + AssetSidecarJson.SidecarExtension,
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, "demo", relative, assets));
    }

    private static Dictionary<string, string> Snapshot(string root)
        => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 忽略临时目录清理失败。
        }
    }
}
