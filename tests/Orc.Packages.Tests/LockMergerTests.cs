using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S4 验收：lock 合并——按域分别合并（跨域绝不互相合并）、同资产不同版本＝冲突并指名双方与版本、
/// 同一版本去重；未入 lock 的版本不参与加载集合。
/// </summary>
public class LockMergerTests
{
    private static readonly Guid RifleV1 = Guid.Parse("11111111-1111-4111-8111-000000000001");
    private static readonly Guid RifleV2 = Guid.Parse("11111111-1111-4111-8111-000000000002");

    private static AssetLock Lock(string packageId, AssetDomain domain, params AssetLockEntry[] entries)
        => AssetLock.Create(packageId, domain, entries);

    private static AssetLockEntry Rifle(Guid guid, string version)
        => new(guid, AssetKind.Card, "pack/a/rifle", AssetVersion.Parse(version));

    [Fact]
    public void 同资产同版本_去重无冲突()
    {
        var result = LockMerger.Merge(AssetDomain.Formal, [
            Lock("a", AssetDomain.Formal, Rifle(RifleV1, "1.0.0")),
            Lock("b", AssetDomain.Formal, Rifle(RifleV1, "1.0.0")),
        ]);

        Assert.True(result.Ok);
        Assert.Single(result.Entries);
    }

    [Fact]
    public void 同资产不同版本_冲突并指名双方与版本()
    {
        var result = LockMerger.Merge(AssetDomain.Formal, [
            Lock("pack.a", AssetDomain.Formal, Rifle(RifleV1, "1.0.0")),
            Lock("pack.b", AssetDomain.Formal, Rifle(RifleV2, "1.1.0")),
        ]);

        Assert.False(result.Ok);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(AssetKind.Card, conflict.Kind);
        Assert.Equal("pack/a/rifle", conflict.AppId);
        Assert.Equal(new AssetVersion(1, 0, 0), conflict.FirstVersion);
        Assert.Equal(new AssetVersion(1, 1, 0), conflict.SecondVersion);

        var message = conflict.ToString();
        Assert.Contains("pack/a/rifle", message);
        Assert.Contains("1.0.0", message);
        Assert.Contains("1.1.0", message);
        Assert.Contains("pack.a", message);
        Assert.Contains("pack.b", message);
    }

    [Fact]
    public void 跨域绝不互相合并_测试lock不参与正式合并()
    {
        var locks = new[]
        {
            Lock("a", AssetDomain.Formal, Rifle(RifleV1, "1.0.0")),
            Lock("a", AssetDomain.Test, Rifle(RifleV2, "1.0.0.1")),
        };

        var formal = LockMerger.Merge(AssetDomain.Formal, locks);
        var test = LockMerger.Merge(AssetDomain.Test, locks);

        Assert.True(formal.Ok);
        Assert.True(test.Ok); // 若跨域合并，这里会是版本冲突
        Assert.Equal(RifleV1, Assert.Single(formal.Entries).AssetGuid);
        Assert.Equal(RifleV2, Assert.Single(test.Entries).AssetGuid);
    }

    [Fact]
    public void 合并全部_两域各自独立判定()
    {
        var results = LockMerger.MergeAll(new[]
        {
            Lock("a", AssetDomain.Formal, Rifle(RifleV1, "1.0.0")),
            Lock("b", AssetDomain.Formal, Rifle(RifleV2, "1.1.0")),
            Lock("a", AssetDomain.Test, Rifle(RifleV1, "1.0.0.1")),
        });

        Assert.Equal(2, results.Count);
        var formal = Assert.Single(results, r => r.Domain == AssetDomain.Formal);
        var test = Assert.Single(results, r => r.Domain == AssetDomain.Test);
        Assert.False(formal.Ok);   // 正式域冲突
        Assert.True(test.Ok);      // 测试域无冲突
        Assert.Single(test.Entries);
    }

    [Fact]
    public void 未入lock的版本不参与加载集合()
    {
        var registry = new AssetRegistry();
        var packageGuid = Guid.Parse("b0070000-0000-4000-8000-0000000000e1");
        foreach (var version in new[] { "1.0.0", "1.1.0" })
        {
            var assetVersion = AssetVersion.Parse(version);
            registry.Register(new AssetRegistration(
                new AssetMetadata(
                    AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, "rifle", assetVersion),
                    AssetKind.Card, "rifle", assetVersion, null, null, [], []),
                AssetDomain.Formal,
                "a",
                "pack/a/rifle",
                AssetMetadataSource.Sidecar,
                Card: null,
                Prefab: null,
                SourcePath: null));
        }

        var merged = LockMerger.Merge(AssetDomain.Formal, [
            Lock("a", AssetDomain.Formal, new AssetLockEntry(
                AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, "rifle", AssetVersion.Parse("1.0.0")),
                AssetKind.Card,
                "pack/a/rifle",
                AssetVersion.Parse("1.0.0"))),
        ]);

        var loaded = merged.Entries.Select(entry => registry.Find(entry.AssetGuid)).ToArray();

        // 注册表有两个版本；加载集合只取 lock 里的那一个。
        Assert.Equal(2, registry.VersionsOf("a", AssetKind.Card, "rifle").Count);
        Assert.Single(loaded);
        Assert.Equal(new AssetVersion(1, 0, 0), loaded[0]!.Version);
    }
}
