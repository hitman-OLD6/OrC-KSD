using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S2 验收：资产版本身份（R1/D28）——GUID 只由「包稳定身份 ＋ 种类 ＋ 局部 ID ＋ 资产版本」派生，
/// **不含包版本**；版本按规范形参与（<c>1.2</c> 与 <c>1.2.0</c> 同 GUID）；升资产版本才换 GUID。
/// </summary>
public class AssetIdentityTests
{
    private static readonly Guid PackageGuid = Guid.Parse("ab12cd34-0000-4000-8000-000000000001");

    [Fact]
    public void GUID派生_同一身份恒定()
    {
        var first = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 2, 3));
        var second = new AssetIdentity(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 2, 3)).AssetGuid;

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public void GUID派生_规范形版本_1_2与1_2_0相同()
    {
        var compact = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", AssetVersion.Parse("1.2"));
        var padded = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", AssetVersion.Parse("1.2.0"));

        Assert.Equal(compact, padded);
    }

    [Fact]
    public void GUID派生_升资产版本才换GUID_包版本不在派生输入内()
    {
        var v1 = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 2, 3));
        var v2 = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 2, 4));

        Assert.NotEqual(v1, v2);

        // 派生输入只有四项：包稳定身份／种类／局部 ID／资产版本——类型上没有包版本，包内容变化不换身份。
        var identity = new AssetIdentity(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 2, 3));
        Assert.Equal(PackageGuid, identity.PackageGuid);
        Assert.Equal(new AssetVersion(1, 2, 3), identity.Version);
    }

    [Fact]
    public void GUID派生_种类或局部ID或包身份不同则GUID不同()
    {
        var baseGuid = AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman", new AssetVersion(1, 0, 0));

        Assert.NotEqual(baseGuid,
            AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Effect, "rifleman", new AssetVersion(1, 0, 0)));
        Assert.NotEqual(baseGuid,
            AssetIdentity.DeriveAssetGuid(PackageGuid, AssetKind.Card, "rifleman2", new AssetVersion(1, 0, 0)));
        Assert.NotEqual(baseGuid,
            AssetIdentity.DeriveAssetGuid(Guid.NewGuid(), AssetKind.Card, "rifleman", new AssetVersion(1, 0, 0)));
    }

    [Fact]
    public void 资产名文法_种类token与包GUID8与写法段数()
    {
        var identity = new AssetIdentity(PackageGuid, AssetKind.Card, "rifleman", AssetVersion.Parse("1.0.0"));

        Assert.Equal("card：rifleman@ab12cd34#1.0.0", identity.FormatName("ab12cd34"));
        Assert.Equal("effect", AssetKindTokens.Token(AssetKind.Effect));
        Assert.Equal("trigger", AssetKindTokens.Token(AssetKind.Trigger));
        Assert.Equal("handler", AssetKindTokens.Token(AssetKind.Handler));
    }

    [Theory]
    [InlineData("card", AssetKind.Card)]
    [InlineData(" EFFECT ", AssetKind.Effect)]
    [InlineData("Trigger", AssetKind.Trigger)]
    [InlineData("handler", AssetKind.Handler)]
    public void 种类token_读宽(string token, AssetKind expected)
    {
        Assert.True(AssetKindTokens.TryParse(token, out var kind));
        Assert.Equal(expected, kind);
    }
}
