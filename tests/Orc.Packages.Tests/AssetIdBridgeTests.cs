using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S3 验收：ID 中间层——键＝<c>(kind, appId)</c> 二元组、应用层 ID 不含种类、
/// 单域只解析到一个版本、测试遮蔽不污染正式域、**歧义（≥2 个不同应用层 ID）与多版本分离**。
/// </summary>
public class AssetIdBridgeTests
{
    private static readonly Guid FormalGuid = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid TestGuid = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void 键为种类加应用层ID_卡与效果可同ID()
    {
        var bridge = new AssetIdBridge();
        var appId = AssetAppId.Compose("a", "rifle");
        var effectGuid = Guid.NewGuid();

        bridge.Bind(AssetDomain.Formal, AssetKind.Card, appId, FormalGuid);
        bridge.Bind(AssetDomain.Formal, AssetKind.Effect, appId, effectGuid);

        Assert.Equal(FormalGuid, bridge.Resolve(AssetDomain.Formal, AssetKind.Card, appId).ResolvedGuid);
        Assert.Equal(effectGuid, bridge.Resolve(AssetDomain.Formal, AssetKind.Effect, appId).ResolvedGuid);
    }

    [Fact]
    public void 应用层ID不含种类()
    {
        var appId = AssetAppId.Compose("a", "rifle");

        Assert.Equal("pack/a/rifle", appId);
        Assert.DoesNotContain("card", appId, StringComparison.OrdinalIgnoreCase);
        Assert.True(AssetAppId.TrySplit(appId, out var packageId, out var localId));
        Assert.Equal("a", packageId);
        Assert.Equal("rifle", localId);
        Assert.False(AssetAppId.IsAppId("pack/a"));
        Assert.False(AssetAppId.IsAppId("a/rifle"));
    }

    [Fact]
    public void 单域解析_只到一个版本()
    {
        var bridge = new AssetIdBridge();
        var appId = AssetAppId.Compose("a", "rifle");
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, appId, FormalGuid);

        var resolution = bridge.Resolve(AssetDomain.Formal, AssetKind.Card, appId);

        Assert.Equal(IdResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(appId, resolution.AppId);
        Assert.Equal(FormalGuid, resolution.ResolvedGuid);
        Assert.Empty(resolution.Candidates);
    }

    [Fact]
    public void 测试遮蔽_合并视图生效_但不污染正式域()
    {
        var bridge = new AssetIdBridge();
        var appId = AssetAppId.Compose("a", "rifle");
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, appId, FormalGuid);
        bridge.Bind(AssetDomain.Test, AssetKind.Card, appId, TestGuid);

        Assert.Equal(TestGuid, bridge.Resolve(AssetKind.Card, appId).ResolvedGuid);              // 合并视图：测试遮蔽
        Assert.Equal(FormalGuid, bridge.Resolve(AssetDomain.Formal, AssetKind.Card, appId).ResolvedGuid); // 正式域不受污染
        Assert.Equal(TestGuid, bridge.Resolve(AssetDomain.Test, AssetKind.Card, appId).ResolvedGuid);
    }

    [Fact]
    public void 裸局部ID_歧义_两个不同应用层ID()
    {
        var bridge = new AssetIdBridge();
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, AssetAppId.Compose("a", "rifle"), FormalGuid);
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, AssetAppId.Compose("b", "rifle"), Guid.NewGuid());

        var resolution = bridge.Resolve(AssetKind.Card, "rifle");

        Assert.Equal(IdResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.AssetGuid);
        Assert.Equal(["pack/a/rifle", "pack/b/rifle"], resolution.Candidates);
    }

    [Fact]
    public void 裸局部ID_同名多版本不是歧义()
    {
        var bridge = new AssetIdBridge();
        var appId = AssetAppId.Compose("a", "rifle");

        // 同一应用层 ID 先绑定 1.0.0，再重新定版到 1.1.0（覆盖）——候选仍只有一个应用层 ID。
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, appId, FormalGuid);
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, appId, TestGuid);

        var resolution = bridge.Resolve(AssetKind.Card, "rifle");

        Assert.Equal(IdResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(appId, resolution.AppId);
        Assert.Equal(TestGuid, resolution.ResolvedGuid);
        Assert.Single(resolution.Candidates);
    }

    [Fact]
    public void 未绑定_返回未找到_不抛错()
    {
        var bridge = new AssetIdBridge();

        Assert.Equal(IdResolutionStatus.NotFound, bridge.Resolve(AssetKind.Card, "pack/a/rifle").Status);
        Assert.Equal(IdResolutionStatus.NotFound, bridge.Resolve(AssetKind.Card, "rifle").Status);
        Assert.Equal(IdResolutionStatus.NotFound, bridge.Resolve(AssetKind.Card, "").Status);
    }

    [Fact]
    public void 绑定非法应用层ID_拒绝()
    {
        var bridge = new AssetIdBridge();

        Assert.Throws<ArgumentException>(() => bridge.Bind(AssetDomain.Formal, AssetKind.Card, "rifle", FormalGuid));
        Assert.ThrowsAny<ArgumentException>(() => bridge.Bind(AssetDomain.Formal, AssetKind.Card, "pack/a/rifle", Guid.Empty));
    }

    [Fact]
    public void 绑定清单_按域可枚举()
    {
        var bridge = new AssetIdBridge();
        bridge.Bind(AssetDomain.Formal, AssetKind.Card, AssetAppId.Compose("a", "rifle"), FormalGuid);
        bridge.Bind(AssetDomain.Test, AssetKind.Card, AssetAppId.Compose("a", "rifle"), TestGuid);

        Assert.Equal(2, bridge.Bindings().Count);
        Assert.Single(bridge.Bindings(AssetDomain.Formal));
        Assert.Single(bridge.Bindings(AssetDomain.Test));

        bridge.Clear();
        Assert.Empty(bridge.Bindings());
    }

    [Fact]
    public void 中间层不持有DTO_只存GUID映射()
    {
        // 全仓只有注册面（AssetRegistry）持有 DTO：中间层的任何字段都不得是 DTO 类型。
        var dtoTypes = new[]
        {
            typeof(AssetRegistration),
            typeof(Orc.Game.Cards.CardDefinition),
            typeof(Orc.Cards.EffectSnapshot),
        };

        var fieldTypes = typeof(AssetIdBridge)
            .GetFields(System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.NotEmpty(fieldTypes);
        Assert.DoesNotContain(fieldTypes, type => dtoTypes.Contains(type));

        // 对外只见 GUID：绑定清单的成员是四个值字段，不含任何 DTO。
        var binding = new AssetBinding(AssetDomain.Formal, AssetKind.Card, AssetAppId.Compose("a", "rifle"), FormalGuid);
        Assert.Equal(FormalGuid, binding.AssetGuid);
        Assert.DoesNotContain(
            typeof(AssetBinding).GetProperties(),
            property => dtoTypes.Contains(property.PropertyType));
    }
}
