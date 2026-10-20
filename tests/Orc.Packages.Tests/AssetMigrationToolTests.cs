using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S10a 验收：迁移工具＝<see cref="AssetMigrationTool"/> 的**纯函数**（<see cref="MigrationPlan"/> 可断言）＋
/// <c>tools/Orc.Tools.Migrate</c> 薄 CLI。覆盖：dry-run 不写盘、幂等（二次全 Skip／无版本漂移）、
/// Skip/Overwrite(--force) 判定、plan 结构、不可迁移源的障碍报告、迁移提示可复制，以及迁移结果可被注册面消费。
/// </summary>
public sealed class AssetMigrationToolTests : IDisposable
{
    private const string PackId = "mig.sample";
    private static readonly Guid PackGuid = Guid.Parse("b0070000-0000-4000-8000-0000000000d1");

    private readonly string _base = Path.Combine(Path.GetTempPath(), "orc-migrate-" + Guid.NewGuid().ToString("N"));

    public AssetMigrationToolTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PackRoot => Path.Combine(_base, "samples", "card-pack-" + PackId);

    // ─────────────────────────────────────────────────── dry-run / apply

    [Fact]
    public void 默认计划_dry_run不写盘_目录内容字节不变()
    {
        WriteLegacyPackage();
        var before = Snapshot(_base);

        var plan = AssetMigrationTool.Plan(PackRoot);

        Assert.True(plan.HasPendingWrites);
        Assert.False(plan.IsFullySkipped);
        Assert.Equal(before, Snapshot(_base)); // 计划是纯函数：不新增／不改写任何文件
        Assert.False(File.Exists(Path.Combine(PackRoot, "lock.formal.json")));
    }

    [Fact]
    public void 应用后_每个源文件有且仅有一份sidecar_manifest为v2_两lock与sidecar一致()
    {
        WriteLegacyPackage();

        var result = AssetMigrationTool.Apply(AssetMigrationTool.Plan(PackRoot));

        Assert.NotEmpty(result.WrittenFiles);
        Assert.Equal(result.WrittenFiles.Count, result.WrittenFiles.Distinct(StringComparer.Ordinal).Count());

        // ① sidecar：每个资产源文件一份、文件名可由源文件名唯一推出。
        foreach (var source in new[] { "cards/rifle.card.json", "effects/rally.prefab.json" })
        {
            var sidecarPath = Path.Combine(PackRoot, source + AssetSidecarJson.SidecarExtension);
            Assert.True(File.Exists(sidecarPath), $"缺少 sidecar：{sidecarPath}");
            var sidecar = AssetSidecarJson.ReadFile(sidecarPath);
            Assert.Equal(PackId, sidecar.PackageId);
            Assert.Equal(source, sidecar.Source);
            Assert.Single(sidecar.Assets);
            Assert.Equal(new AssetVersion(1, 0, 0), sidecar.Assets[0].Version);
        }

        // ② manifest：v2、metadata 非空、无 dependsOn、包版本＝内容指纹。
        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));
        Assert.Equal(PackageManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Empty(manifest.Dependencies);
        Assert.Equal(
            new[] { "cards/rifle.card.json.meta.json", "effects/rally.prefab.json.meta.json" },
            manifest.MetadataList);
        Assert.Equal(PackageVersion.Compute(PackId, manifest.MetadataList), manifest.Version);

        // ③ 两 lock：域各自独立、内容与 sidecar 声明一致。
        var formal = AssetLockJson.ReadPackage(PackRoot, AssetDomain.Formal);
        var test = AssetLockJson.ReadPackage(PackRoot, AssetDomain.Test);
        Assert.NotNull(formal);
        Assert.NotNull(test);
        Assert.Equal(AssetDomain.Formal, formal!.Domain);
        Assert.Equal(AssetDomain.Test, test!.Domain);
        Assert.Equal(2, formal.Assets.Count);
        Assert.Equal(formal.Assets, test.Assets);
        Assert.Contains(formal.Assets, entry => entry.Kind == AssetKind.Card && entry.AppId == $"pack/{PackId}/rifle");
        Assert.Contains(formal.Assets, entry => entry.Kind == AssetKind.Effect && entry.AppId == $"pack/{PackId}/rally");
    }

    [Fact]
    public void 重复执行_第二次全Skip且无版本漂移()
    {
        WriteLegacyPackage();
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(PackRoot));
        var afterFirst = Snapshot(_base);

        var plan = AssetMigrationTool.Plan(PackRoot);
        var result = AssetMigrationTool.Apply(plan); // 再次 apply：无待写文件

        Assert.True(plan.IsFullySkipped);
        Assert.Equal(0, plan.CountOf(MigrationAction.Create));
        Assert.Equal(0, plan.CountOf(MigrationAction.Overwrite));
        Assert.All(plan.Entries, entry => Assert.Equal(MigrationAction.Skip, entry.Action));
        Assert.Empty(result.WrittenFiles);
        Assert.Equal(afterFirst, Snapshot(_base)); // 幂等：无重复条目、无版本漂移

        foreach (var entry in plan.Entries)
        {
            Assert.All(entry.Assets, asset => Assert.Equal(new AssetVersion(1, 0, 0), asset.Version));
        }
    }

    // ─────────────────────────────────────────────────── Skip / Overwrite

    [Fact]
    public void 既有sidecar_默认Skip_只有force才Overwrite并重写()
    {
        WriteLegacyPackage();
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(PackRoot));
        var sidecarPath = Path.Combine(PackRoot, "cards", "rifle.card.json.meta.json");
        var original = File.ReadAllText(sidecarPath);

        // 人工改动：模拟"已升版"的既有 sidecar（迁移不得回退它）。
        var upgraded = original
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"1.2.0\"", StringComparison.Ordinal);
        File.WriteAllText(sidecarPath, upgraded);

        var skipPlan = AssetMigrationTool.Plan(PackRoot);
        var skipEntry = Assert.Single(skipPlan.Entries, e => e.SourceFile == "cards/rifle.card.json");
        Assert.Equal(MigrationAction.Skip, skipEntry.Action);
        AssetMigrationTool.Apply(skipPlan);
        Assert.Equal(upgraded, File.ReadAllText(sidecarPath)); // 默认绝不改写既有 sidecar

        var forcedPlan = AssetMigrationTool.Plan(PackRoot, force: true);
        var forcedEntry = Assert.Single(forcedPlan.Entries, e => e.SourceFile == "cards/rifle.card.json");
        Assert.Equal(MigrationAction.Overwrite, forcedEntry.Action);
        AssetMigrationTool.Apply(forcedPlan);
        Assert.Equal(
            new AssetVersion(1, 0, 0),
            AssetSidecarJson.ReadFile(sidecarPath).Assets[0].Version); // --force 才回到迁移初始值
    }

    // ─────────────────────────────────────────────────── plan 结构

    [Fact]
    public void 计划结构_条目含源与sidecar相对路径_动作与声明资产()
    {
        WriteLegacyPackage();

        var plan = AssetMigrationTool.Plan(PackRoot);

        Assert.Equal(Path.GetFullPath(PackRoot), plan.PackageRoot);
        Assert.Equal(PackId, plan.PackageId);
        Assert.Empty(plan.Obstacles);
        Assert.Equal("manifest.json", plan.Manifest.RelativePath);
        Assert.Equal("lock.formal.json", plan.FormalLock.RelativePath);
        Assert.Equal("lock.test.json", plan.TestLock.RelativePath);
        Assert.Equal(MigrationAction.Create, plan.FormalLock.Action);
        Assert.Equal(MigrationAction.Create, plan.TestLock.Action);

        var card = Assert.Single(plan.Entries, entry => entry.SourceFile == "cards/rifle.card.json");
        Assert.Equal("cards/rifle.card.json.meta.json", card.SidecarFile);
        Assert.Equal(MigrationAction.Create, card.Action);
        var asset = Assert.Single(card.Assets);
        Assert.Equal(AssetKind.Card, asset.Kind);
        Assert.Equal("rifle", asset.Id);
        Assert.Equal(new AssetVersion(1, 0, 0), asset.Version);
        Assert.Equal(
            AssetIdentity.DeriveAssetGuid(PackGuid, AssetKind.Card, "rifle", new AssetVersion(1, 0, 0)),
            asset.Guid);

        // 条目按源文件序稳定（确定性计划）。
        Assert.Equal(
            plan.Entries.Select(entry => entry.SourceFile).OrderBy(path => path, StringComparer.Ordinal),
            plan.Entries.Select(entry => entry.SourceFile));
        Assert.Equal(5, plan.PendingWrites().Count); // 2 sidecar ＋ manifest ＋ 两 lock
    }

    [Fact]
    public void 无manifest的包_按目录名派生包id并生成v2清单()
    {
        var root = Path.Combine(_base, "samples", "card-pack-embed");
        WriteCard(root, "cards", "embed.rifle");

        var plan = AssetMigrationTool.Plan(root);

        Assert.Equal("embed", plan.PackageId);
        Assert.Equal(MigrationAction.Create, plan.Manifest.Action);
        Assert.NotNull(plan.Manifest.Content);
        Assert.Contains("\"schemaVersion\": 2", plan.Manifest.Content, StringComparison.Ordinal);
        AssetMigrationTool.Apply(plan);

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        Assert.Equal("embed", manifest.Id);
        Assert.Equal(AssetMigrationTool.DerivePackageGuid("embed"), manifest.Guid);
        Assert.Equal(PackageManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Single(manifest.MetadataList);
    }

    [Fact]
    public void 历史版本目录不生成sidecar()
    {
        WriteLegacyPackage();
        WriteCard(PackRoot, "versions/2.0.0/cards", "rifle");

        var plan = AssetMigrationTool.Plan(PackRoot);

        Assert.DoesNotContain(plan.Entries, entry => entry.SourceFile.Contains("versions/", StringComparison.Ordinal));
        Assert.Equal(2, plan.Entries.Count);
    }

    // ─────────────────────────────────────────────────── 不猜

    [Fact]
    public void 源文件不可解析_记障碍且不生成sidecar()
    {
        WriteLegacyPackage();
        File.WriteAllText(Path.Combine(PackRoot, "cards", "broken.card.json"), "{ 这不是合法 JSON");

        var plan = AssetMigrationTool.Plan(PackRoot);

        var obstacle = Assert.Single(plan.Obstacles);
        Assert.Equal("cards/broken.card.json", obstacle.File);
        Assert.Contains("不可解析", obstacle.Reason, StringComparison.Ordinal);

        var entry = Assert.Single(plan.Entries, e => e.SourceFile == "cards/broken.card.json");
        Assert.Equal(MigrationAction.Skip, entry.Action);
        Assert.False(entry.SidecarExists);

        AssetMigrationTool.Apply(plan);
        Assert.False(File.Exists(Path.Combine(PackRoot, "cards", "broken.card.json.meta.json")));
    }

    [Fact]
    public void 同一资产id出现在两个源文件_记包级障碍()
    {
        WriteLegacyPackage();
        WriteCard(PackRoot, "cards-support", "rifle"); // 与 cards/rifle 同 id ⇒ 同 GUID

        var plan = AssetMigrationTool.Plan(PackRoot);

        Assert.Contains(
            plan.Obstacles,
            obstacle => obstacle.Reason.Contains("被多个源文件声明", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────── 宿主报错可操作

    [Fact]
    public void 迁移提示_含包绝对路径与可复制命令()
    {
        var root = Path.Combine(_base, "samples", "card-pack-broken");
        Directory.CreateDirectory(root);

        var message = MigrationGuidance.ForPackage(root, "包 X 缺少 sidecar");

        Assert.Contains(Path.GetFullPath(root), message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project", message, StringComparison.Ordinal);
        Assert.Contains("Orc.Tools.Migrate", message, StringComparison.Ordinal);
        Assert.Contains("--apply", message, StringComparison.Ordinal);
        Assert.Contains($"--package \"{Path.GetFullPath(root)}\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 注册面_缺sidecar诊断附带可执行迁移命令()
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();
        WriteLegacyPackage();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));
        var result = PackageRegistrar.Register(PackRoot, manifest, new AssetRegistry());

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == AssetDiagnosticCode.SidecarMissing);
        Assert.Contains(Path.GetFullPath(PackRoot), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("--apply", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 注册面_迁移后可注册且无结构性错误()
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();
        WriteLegacyPackage();
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(PackRoot));

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));
        var registry = new AssetRegistry();
        var result = PackageRegistrar.Register(PackRoot, manifest, registry);

        Assert.Empty(result.Diagnostics); // sidecar 齐备 ⇒ 无「缺 sidecar」等资产级诊断
        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Assets, a => a.Kind == AssetKind.Card && a.AppId == $"pack/{PackId}/rifle");
        Assert.Contains(result.Assets, a => a.Kind == AssetKind.Effect && a.AppId == $"pack/{PackId}/rally");

        // lock 与注册结果一致（lock 内容由 sidecar 声明生成，注册结果由同一 sidecar 派生）。
        var formal = AssetLockJson.ReadPackage(PackRoot, AssetDomain.Formal)!;
        Assert.Equal(
            formal.Assets.Select(entry => entry.AssetGuid).OrderBy(guid => guid),
            result.Assets.Select(asset => asset.AssetGuid).OrderBy(guid => guid));
    }

    // ─────────────────────────────────────────────────── 样例包（真实资产）

    [Fact]
    public void 样例包_每个源文件都有sidecar且清单为v2与两lock一致()
    {
        var samples = SampleRoot();
        var packages = AssetMigrationTool.EnumeratePackageRoots(samples);
        Assert.NotEmpty(packages);

        foreach (var root in packages)
        {
            var plan = AssetMigrationTool.Plan(root);
            Assert.True(plan.IsFullySkipped, $"{root} 未迁移完成（仍有待写文件）");
            Assert.Empty(plan.Obstacles);

            var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            Assert.Equal(PackageManifest.CurrentSchemaVersion, manifest.SchemaVersion);
            Assert.Empty(manifest.Dependencies);
            Assert.NotEmpty(manifest.MetadataList);

            foreach (var entry in plan.Entries)
            {
                Assert.True(File.Exists(Path.Combine(root, entry.SidecarFile)), $"缺少 sidecar：{entry.SidecarFile}");
                Assert.Contains(entry.SidecarFile, manifest.MetadataList);
            }

            var formal = AssetLockJson.ReadPackage(root, AssetDomain.Formal);
            var test = AssetLockJson.ReadPackage(root, AssetDomain.Test);
            Assert.NotNull(formal);
            Assert.NotNull(test);
            Assert.Equal(
                plan.Entries.SelectMany(entry => entry.Assets).Select(asset => asset.Guid).OrderBy(guid => guid),
                formal!.Assets.Select(entry => entry.AssetGuid).OrderBy(guid => guid));
            Assert.Equal(formal.Assets, test!.Assets);
        }
    }

    // ─────────────────────────────────────────────────── 夹具

    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples");
            if (Directory.Exists(Path.Combine(candidate, "card-pack-synthetic")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/。");
    }

    /// <summary>迁移前形态：v1 清单（<c>dependsOn</c>＋空 <c>assets</c>）＋ 一张卡 ＋ 一个预制体 ＋ 无 sidecar。</summary>
    private void WriteLegacyPackage()
    {
        Directory.CreateDirectory(PackRoot);
        File.WriteAllText(Path.Combine(PackRoot, "manifest.json"), $$"""
        {
          "kind": "cardpack",
          "id": "{{PackId}}",
          "name": "迁移示例",
          "guid": "{{PackGuid}}",
          "version": "1.0.0",
          "dependsOn": [],
          "assets": []
        }
        """);
        WriteCard(PackRoot, "cards", "rifle");
        WritePrefab(PackRoot, "effects", "rally");
    }

    private static void WriteCard(string root, string relativeDirectory, string id)
    {
        var directory = Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".card.json"), $$"""
        {
         "schemaVersion": 1,
         "id": "{{id}}",
         "name": "示例卡 {{id}}",
         "components": [
          { "component": "typeCategory", "type": ["infantry"] },
          { "component": "factionCost", "faction": "germany", "kredits": 1 },
          { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
          { "component": "tagData", "rarity": "standard" }
         ]
        }
        """);
    }

    private static void WritePrefab(string root, string relativeDirectory, string id)
    {
        var directory = Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".prefab.json"), $$"""
        {
          "schemaVersion": 2,
          "root": {
            "id": "{{id}}",
            "version": 1,
            "mainTrigger": {
              "id": "t.main",
              "stableKey": "迁移示例效果",
              "kind": "passive",
              "viewType": "Orc.Cards.CardEventView",
              "hooks": [],
              "events": []
            },
            "otherTriggers": [],
            "wrappers": [],
            "injects": []
          }
        }
        """);
    }

    /// <summary>目录快照（相对路径 → 字节数＋最后写时间戳的摘要）：用于断言"未写盘"。</summary>
    private static string Snapshot(string root)
        => string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{Path.GetRelativePath(root, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                }));
}
