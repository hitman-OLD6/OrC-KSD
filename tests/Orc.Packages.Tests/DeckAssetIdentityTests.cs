using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S1 验收（R1）：卡组成为第六种资产种类——token 读写、GUID 派生、资产名文法全部复用既有实现；
/// 既有 0..4 的种类排序与派生 GUID 必须逐字不变（否则存量的 lock／资产名会集体失效）。
/// </summary>
public class DeckAssetIdentityTests
{
    private static readonly Guid PackageGuid = Guid.Parse("ab12cd34-0000-4000-8000-000000000001");

    /// <summary>派生基线（改种类 token 或派生输入会立刻打红——lock 兼容性的护栏）。</summary>
    [Theory]
    [InlineData(AssetKind.Card, "a441bf16-b42f-40b9-8b90-016b62ce73c8")]
    [InlineData(AssetKind.Effect, "c2789e63-32ba-cd41-da27-70c0fc278924")]
    [InlineData(AssetKind.Trigger, "d3b414c1-21f6-904a-1dda-8b6dd1d79f45")]
    [InlineData(AssetKind.Handler, "94a0e29f-cd4b-1a1a-22f6-be9b485e8b0a")]
    [InlineData(AssetKind.Service, "9130a0ad-c4a9-3e42-fe29-82768759f5cc")]
    public void 既有种类派生GUID不变(AssetKind kind, string expected)
    {
        var actual = AssetIdentity.DeriveAssetGuid(PackageGuid, kind, "rifleman", AssetVersion.Parse("1.0.0"));
        Assert.Equal(Guid.Parse(expected), actual);
    }

    [Fact]
    public void 种类枚举值_追加不插队()
    {
        Assert.Equal(0, (int)AssetKind.Card);
        Assert.Equal(1, (int)AssetKind.Effect);
        Assert.Equal(2, (int)AssetKind.Trigger);
        Assert.Equal(3, (int)AssetKind.Handler);
        Assert.Equal(4, (int)AssetKind.Service);
        Assert.Equal(5, (int)AssetKind.Deck);
    }

    [Theory]
    [InlineData("deck", AssetKind.Deck)]
    [InlineData(" DECK ", AssetKind.Deck)]
    public void 种类token_deck可读(string token, AssetKind expected)
    {
        Assert.True(AssetKindTokens.TryParse(token, out var kind));
        Assert.Equal(expected, kind);
        Assert.Equal("deck", AssetKindTokens.Token(AssetKind.Deck));
    }

    [Fact]
    public void sidecar声明deck可读出并派生稳定GUID()
    {
        var json = """
        {
          "schemaVersion": 1,
          "packageId": "demo",
          "source": "decks/demo.starter.deck.json",
          "assets": [
            {
              "guid": "551465c6-d3ab-0c3a-7399-a8129fef32d0",
              "kind": "deck",
              "id": "demo.starter",
              "version": "1.0.0",
              "dependsOn": []
            }
          ]
        }
        """;

        var sidecar = AssetSidecarJson.Parse(json);
        var asset = Assert.Single(sidecar.Assets);
        Assert.Equal(AssetKind.Deck, asset.Kind);

        var derived = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Deck, "demo.starter", asset.Version);
        Assert.Equal(derived, asset.Guid);
        Assert.Equal(derived, AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Deck, "demo.starter", asset.Version));
    }

    [Fact]
    public void 资产名形如deck局部ID_包GUID8_版本()
    {
        var metadata = new AssetMetadata(
            AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Deck, "demo.starter", AssetVersion.Parse("1.0.0")),
            AssetKind.Deck,
            "demo.starter",
            AssetVersion.Parse("1.0.0"),
            Anchor: null,
            Loader: null,
            DependsOn: [],
            ConflictsWith: []);

        var registration = new AssetRegistration(
            metadata,
            AssetDomain.Formal,
            "demo",
            "pack/demo/starter",
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: "decks/demo.starter.deck.json")
        {
            PackageGuid = PackageGuid,
        };

        Assert.Equal("deck：demo.starter@ab12cd34#1.0.0", registration.AssetName);
    }
}
