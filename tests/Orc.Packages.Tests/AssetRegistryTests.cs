using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S3 验收：资产注册表（键＝GUID）——同资产多版本并存、**无任何定版逻辑**（只给候选）、
/// 重复 GUID 拒绝、二级索引稳定序。
/// </summary>
public class AssetRegistryTests
{
    private static readonly Guid PackageGuid = Guid.Parse("b0070000-0000-4000-8000-0000000000d1");
    private const string PackageId = "sample.registry";

    private static AssetRegistration Registration(string localId, string version, string? appId = null)
    {
        var identity = new AssetIdentity(PackageGuid, AssetKind.Card, localId, AssetVersion.Parse(version));
        var metadata = new AssetMetadata(
            identity.AssetGuid,
            AssetKind.Card,
            localId,
            AssetVersion.Parse(version),
            Anchor: null,
            Loader: null,
            DependsOn: [],
            ConflictsWith: []);
        return new AssetRegistration(
            metadata,
            AssetDomain.Formal,
            PackageId,
            appId ?? AssetAppId.Compose(PackageId, localId),
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: null);
    }

    [Fact]
    public void 同资产多版本并存注册_按GUID区分()
    {
        var registry = new AssetRegistry();
        var v1 = Registration("rifle", "1.0.0");
        var v2 = Registration("rifle", "2.0.0");

        registry.Register(v1);
        registry.Register(v2);

        Assert.Equal(2, registry.Count);
        Assert.Equal(v1.AssetGuid, registry.Find(v1.AssetGuid)!.AssetGuid);
        Assert.Equal(v2.AssetGuid, registry.Find(v2.AssetGuid)!.AssetGuid);
        Assert.NotEqual(v1.AssetGuid, v2.AssetGuid);
    }

    [Fact]
    public void 版本候选_升序_且不择版()
    {
        var registry = new AssetRegistry();
        registry.Register(Registration("rifle", "2.0.0"));
        registry.Register(Registration("rifle", "1.0.0"));

        var versions = registry.VersionsOf(PackageId, AssetKind.Card, "rifle");

        // 全部候选原样返回（定版不在此层）：1.0.0 → 2.0.0 升序，两个都在。
        Assert.Equal(2, versions.Count);
        Assert.Equal(AssetVersion.Parse("1.0.0"), versions[0].Version);
        Assert.Equal(AssetVersion.Parse("2.0.0"), versions[1].Version);
    }

    [Fact]
    public void 重复GUID_拒绝()
    {
        var registry = new AssetRegistry();
        var registration = Registration("rifle", "1.0.0");
        registry.Register(registration);

        var error = Assert.Throws<InvalidOperationException>(() => registry.Register(registration));
        Assert.Contains("GUID", error.Message);

        Assert.False(registry.TryRegister(registration, out var failure));
        Assert.Contains("已注册", failure);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void 应用层ID索引_不含种类()
    {
        var registry = new AssetRegistry();
        var card = Registration("rifle", "1.0.0");
        var effectIdentity = new AssetIdentity(PackageGuid, AssetKind.Effect, "rifle", new AssetVersion(1, 0, 0));
        var effect = new AssetRegistration(
            new AssetMetadata(effectIdentity.AssetGuid, AssetKind.Effect, "rifle", new AssetVersion(1, 0, 0),
                null, null, [], []),
            AssetDomain.Formal,
            PackageId,
            AssetAppId.Compose(PackageId, "rifle"),
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: null);

        registry.Register(card);
        registry.Register(effect);

        // 卡与效果可同局部 ID：应用层 ID 相同，GUID 不同（种类是并列字段，不压进字符串）。
        Assert.Equal(card.AppId, effect.AppId);
        Assert.Equal("pack/sample.registry/rifle", card.AppId);
        Assert.NotEqual(card.AssetGuid, effect.AssetGuid);
        Assert.Single(registry.ByAppId(card.AppId), entry => entry.Kind == AssetKind.Card);
        Assert.Single(registry.ByAppId(card.AppId), entry => entry.Kind == AssetKind.Effect);
    }

    [Fact]
    public void 运行期卡字典_由注册表承载()
    {
        var registry = new AssetRegistry();
        var catalog = new RuntimeCardCatalog(registry);

        catalog.Register(new Orc.Game.Cards.RuntimeCardRegistration(
            "runtime.temp", Definition(), [], []));

        Assert.True(catalog.Contains("runtime.temp"));
        Assert.Single(registry.RuntimeCards);
        Assert.Single(catalog.Snapshot());
        Assert.Throws<InvalidOperationException>(() => catalog.Register(
            new Orc.Game.Cards.RuntimeCardRegistration("runtime.temp", Definition(), [], [])));
    }

    [Fact]
    public void 运行期卡快照_冻结于创建时刻()
    {
        var registry = new AssetRegistry();
        registry.RegisterRuntimeCard(new Orc.Game.Cards.RuntimeCardRegistration(
            "runtime.first", Definition(), [], []));

        var frozen = registry.RuntimeCards; // 快照副本（冻结时机＝创建时刻）
        registry.RegisterRuntimeCard(new Orc.Game.Cards.RuntimeCardRegistration(
            "runtime.second", Definition(), [], []));

        Assert.Single(frozen);
        Assert.Equal(2, registry.RuntimeCards.Count);
    }

    [Fact]
    public void 同ID不同GUID的双包_按GUID各查本包_按包ID仍合并()
    {
        var registry = new AssetRegistry();
        var first = Guid.Parse("b0070000-0000-4000-8000-0000000000a1");
        var second = Guid.Parse("b0070000-0000-4000-8000-0000000000a2");
        registry.Register(GuidRegistration(first, "same.pack", "rifle", "1.0.0"));
        registry.Register(GuidRegistration(second, "same.pack", "rifle", "2.0.0"));

        var byFirst = Assert.Single(registry.VersionsOf(first, AssetKind.Card, "rifle"));
        Assert.Equal(AssetVersion.Parse("1.0.0"), byFirst.Version);
        var bySecond = Assert.Single(registry.VersionsOf(second, AssetKind.Card, "rifle"));
        Assert.Equal(AssetVersion.Parse("2.0.0"), bySecond.Version);

        // 既有按包 ID 的语义保持：跨包合并（同 ID 双包并存是允许的）。
        Assert.Equal(2, registry.VersionsOf("same.pack", AssetKind.Card, "rifle").Count);
    }

    [Fact]
    public void 空包GUID不入GUID索引_按包ID仍可命中()
    {
        var registry = new AssetRegistry();
        registry.Register(Registration("rifle", "1.0.0")); // PackageGuid 默认 Guid.Empty

        Assert.Empty(registry.VersionsOf(Guid.Empty, AssetKind.Card, "rifle"));
        Assert.Single(registry.VersionsOf(PackageId, AssetKind.Card, "rifle"));
    }

    [Fact]
    public void 按GUID查询_单包多版本升序()
    {
        var registry = new AssetRegistry();
        var packageGuid = Guid.Parse("b0070000-0000-4000-8000-0000000000a3");
        registry.Register(GuidRegistration(packageGuid, "p", "rifle", "2.0.0"));
        registry.Register(GuidRegistration(packageGuid, "p", "rifle", "1.0.0"));

        var versions = registry.VersionsOf(packageGuid, AssetKind.Card, "rifle");

        Assert.Equal(2, versions.Count);
        Assert.Equal(AssetVersion.Parse("1.0.0"), versions[0].Version);
        Assert.Equal(AssetVersion.Parse("2.0.0"), versions[1].Version);
    }

    private static AssetRegistration GuidRegistration(
        Guid packageGuid, string packageId, string localId, string version)
    {
        var parsed = AssetVersion.Parse(version);
        return new AssetRegistration(
            new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, localId, parsed),
                AssetKind.Card,
                localId,
                parsed,
                Anchor: null,
                Loader: null,
                DependsOn: [],
                ConflictsWith: []),
            AssetDomain.Formal,
            packageId,
            AssetAppId.Compose(packageId, localId),
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: null)
        {
            PackageGuid = packageGuid,
        };
    }

    private static Orc.Game.Cards.CardDefinition Definition() => new(
        "临时卡", deployCost: 1, operateCost: 1, attack: 1, defense: 1,
        unitTypes: ["infantry"], faction: "germany", rarity: "standard");
}
