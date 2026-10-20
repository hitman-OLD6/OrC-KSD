using System.Security.Cryptography;
using System.Text;
using Orc.Core;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
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
    public void Pack_List_Excludes_Temporary_Directories()
    {
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

    [Fact]
    public void Commit_Rejects_Symlink_Escape()
    {
        Begin();
        var outside = Path.Combine(Path.GetTempPath(), "orc-editor-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_packageRoot + ".tmp", "cards", "link");
        if (!TryCreateDirectoryLink(link, outside))
        {
            Directory.Delete(outside, recursive: true);
            return; // 平台不允许建链：本机不适用
        }

        var exception = Assert.Throws<AssetEditorException>(() => _editor.Commit("cards/link/evil.card.json", "{}"));

        Assert.Equal(AssetEditorErrorCode.PathDenied, exception.Code);
        Assert.False(File.Exists(Path.Combine(outside, "evil.card.json")));
        Directory.Delete(outside, recursive: true);
    }

    // ── 转正 ────────────────────────────────────────────────────────────────

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

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
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
