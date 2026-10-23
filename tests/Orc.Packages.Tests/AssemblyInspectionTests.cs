using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S3 验收：<see cref="MatchCardAssembly.Inspect"/> 是**判定旁路**——与 <see cref="MatchCardAssembly.Register"/>
/// 同一套实现、纯读、不抛（结构性非法与空输入都走结果），并给出逐包行 ＋ 结构化冲突 ＋ 装载摘要。
/// </summary>
public sealed class AssemblyInspectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-inspect-" + Guid.NewGuid().ToString("N"));

    public AssemblyInspectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Inspect_Empty_Returns_EmptyPool_Without_Throw()
    {
        var inspection = MatchCardAssembly.Inspect([]);

        Assert.False(inspection.Ok);
        Assert.Empty(inspection.Packages);
        Assert.Equal(AssemblyPlanSummary.Zero, inspection.Plan);

        var issue = Assert.Single(inspection.Issues);
        Assert.Equal(AssetLoadIssueCode.EmptyPool, issue.Code);
        Assert.False(issue.IsFatal);
    }

    [Fact]
    public void Register_Empty_Still_Throws_ArgumentException()
    {
        var error = Assert.Throws<ArgumentException>(() => MatchCardAssembly.Register([]));
        Assert.Contains("至少需要一个卡包目录。", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_Valid_Pack_Reports_Row_And_Plan()
    {
        var pack = CreatePack("s3.pack", "rifle", "s3 rifle");

        var inspection = MatchCardAssembly.Inspect([pack]);

        Assert.True(inspection.Ok);
        var row = Assert.Single(inspection.Packages);
        Assert.True(row.Ok);
        Assert.Equal("s3.pack", row.PackageId);
        Assert.False(row.WillRemove);
        Assert.Null(row.ErrorCode);
        Assert.Equal(pack, row.Directory);
        Assert.True(inspection.Plan.DefinitionCount > 0);
        Assert.True(inspection.Plan.CardCount > 0);
        Assert.Equal(1, inspection.Plan.FormalLayers);
    }

    [Fact]
    public void Inspect_Missing_Directory_Reports_Row_While_Register_Throws()
    {
        var missing = Path.Combine(_root, "card-pack-missing");

        var inspection = MatchCardAssembly.Inspect([missing]);

        Assert.False(inspection.Ok);
        var row = Assert.Single(inspection.Packages);
        Assert.False(row.Ok);
        Assert.Equal(PackageProbeCodes.DirectoryMissing, row.ErrorCode);
        Assert.True(row.WillRemove);

        // 既有装配链行为不变（同一 message 口径）。
        var error = Assert.Throws<DirectoryNotFoundException>(() => MatchCardAssembly.Register([missing]));
        Assert.Contains("卡包目录不存在", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_Message_For_Directory_Without_Manifest_Is_Unchanged()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);

        var error = Assert.Throws<InvalidDataException>(() => MatchCardAssembly.Register([bare]));
        Assert.Contains("不是卡包（缺 manifest.json）", error.Message, StringComparison.Ordinal);

        var inspection = MatchCardAssembly.Inspect([bare]);
        Assert.Equal(PackageProbeCodes.ManifestMissing, Assert.Single(inspection.Packages).ErrorCode);
    }

    [Fact]
    public void Inspect_Lock_Conflict_Carries_Structured_Conflict()
    {
        var pack = CreatePack("s3.conflict", "rifle", "s3 rifle");
        var copy = CopyPack(pack, "s3.conflict", Path.Combine(_root, "copy"));
        Assert.NotEqual(pack, copy);

        // 同一 (kind, appId) 在两个目录里锁定不同版本 ⇒ LockConflict（保留原始对象，不拍平成字符串）。
        BumpLockedCard(copy, "s3.conflict", "rifle", "2.0.0");

        var inspection = MatchCardAssembly.Inspect([pack, copy]);

        Assert.False(inspection.Ok);
        var issue = Assert.Single(inspection.Issues, item => item.Code == AssetLoadIssueCode.LockConflict);
        Assert.True(issue.IsFatal);
        Assert.NotNull(issue.Conflict);
        Assert.Equal("pack/s3.conflict/rifle", issue.Conflict!.AppId);
        Assert.Equal("s3.conflict", issue.Conflict.FirstPackageId);
        Assert.Equal("s3.conflict", issue.Conflict.SecondPackageId);
        Assert.Equal("1.0.0", issue.Conflict.FirstVersion.ToString());
        Assert.Equal("2.0.0", issue.Conflict.SecondVersion.ToString());
    }

    [Fact]
    public void Inspect_Does_Not_Write_To_Disk()
    {
        var pack = CreatePack("s3.readonly", "rifle", "s3 rifle");
        var before = Snapshot(pack);

        _ = MatchCardAssembly.Inspect([pack]);
        _ = MatchCardAssembly.Inspect([]);

        Assert.Equal(before, Snapshot(pack));
    }

    // ─────────────────────────────────────────────── 夹具

    private static string CardJson(string cardId, string name) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{cardId}}",
          "name": "{{name}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "neutral", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
            { "component": "tagData", "rarity": "standard" }
          ]
        }
        """;

    /// <summary>造一个最小卡包（id 由目录名 <c>card-pack-{包id}</c> 派生；元数据由迁移工具产出）。</summary>
    private string CreatePack(string packageId, string cardId, string name)
    {
        var pack = Path.Combine(_root, "card-pack-" + packageId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));
        File.WriteAllText(Path.Combine(pack, "cards", cardId + ".card.json"), CardJson(cardId, name));
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));
        return pack;
    }

    private static string CopyPack(string source, string packageId, string parent)
    {
        var target = Path.Combine(parent, "card-pack-" + packageId);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    /// <summary>把某张卡在 lock 与 sidecar 里的版本整体抬高一档（模拟「同 id 不同版本并存」的内容漂移）。</summary>
    private static void BumpLockedCard(string pack, string packageId, string cardId, string version)
    {
        var parsed = AssetVersion.Parse(version);
        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(pack, "manifest.json")));
        var appId = AssetAppId.Compose(packageId, cardId);
        var guid = AssetIdentity.DeriveAssetGuid(manifest.Guid, AssetKind.Card, cardId, parsed);

        var sidecarPath = Path.Combine(pack, "cards", cardId + ".card.json.meta.json");
        var sidecar = JsonNode.Parse(File.ReadAllText(sidecarPath))!.AsObject();
        var asset = sidecar["assets"]!.AsArray()[0]!.AsObject();
        asset["version"] = version;
        asset["guid"] = guid.ToString();
        File.WriteAllText(sidecarPath, sidecar.ToJsonString());

        var lockPath = AssetLockJson.PathFor(pack, AssetDomain.Formal);
        var current = AssetLockJson.ReadFile(lockPath);
        var entries = current.Assets
            .Select(entry => string.Equals(entry.AppId, appId, StringComparison.Ordinal)
                ? new AssetLockEntry(guid, AssetKind.Card, appId, parsed)
                : entry)
            .ToArray();
        AssetLockJson.WriteFile(lockPath, AssetLock.Create(packageId, AssetDomain.Formal, entries));
    }

    /// <summary>目录内容快照（相对路径 ＋ 内容哈希）：用于断言「零写盘」。</summary>
    private static string Snapshot(string directory)
    {
        var lines = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(directory, path) + "="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        return string.Join('|', lines);
    }
}
