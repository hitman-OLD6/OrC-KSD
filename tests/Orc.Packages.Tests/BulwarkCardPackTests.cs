using Orc.Core;
using Orc.Core.Model;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 铜墙铁壁系列卡包（samples/card-pack-bulwark）的数据面验收：清单/内容/预检/行为制品声明。
/// <para>
/// v4-only（§23.8 第 1 项）后行为制品的**唯一来源**是 <c>effects-v4/*.component.json</c>：
/// 本文件原来的旧预制体断言（<c>PrefabJson</c> ＋ <c>injects</c>／<c>judicatorWrappers</c>）改为等价的
/// v4 组件声明断言（<c>mounts</c>／<c>judge-wraps</c>），观测面变化、被测意图不变。
/// </para>
/// </summary>
public sealed class BulwarkCardPackTests
{
    private static string PackRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../samples/card-pack-bulwark"));

    [Fact]
    public void 卡包_清单与内容_可被装载且预检通过()
    {
        Assert.True(Directory.Exists(PackRoot), $"卡包目录缺失：{PackRoot}");

        // 卡数据组件（六项内置）由内置插件装配；未装配＝组件被隔离、预检不达双零。
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));
        Assert.Equal("orc.bulwark", manifest.Id);
        Assert.Equal(PackageKind.CardPack, manifest.Kind);

        // 包版本＝内容指纹（S4/R17；S10a 迁移后不再是字面 "1.0.0"）：同内容恒得同一指纹、不含时间戳。
        Assert.Equal(PackageManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(AssetLoadTestKit.RecomputeContent(PackRoot, manifest.Id), manifest.Version);
        Assert.Empty(manifest.Dependencies);

        var content = PackageRegistrar.Register(PackRoot, manifest, new AssetRegistry()).Content;

        Assert.Equal(3, content.Definitions.Count);
        Assert.All(content.Definitions, d => Assert.StartsWith("pack/orc.bulwark/", d.Id, StringComparison.Ordinal));

        Assert.Equal(3, content.Keywords.Count);
        Assert.Contains(content.Keywords, k => k.Id == "机动");
        Assert.Contains(content.Keywords, k => k.Id == "固守");
        Assert.Contains(content.Keywords, k => k.Id == "抢修");
        Assert.All(content.Keywords, k => Assert.Single(k.Bindings));

        // v4-only：行为制品的装载目录是 components 目录（旧 effects 目录已退场，本包不再提供）。
        Assert.NotNull(content.ComponentsDirectory);
        Assert.True(Directory.Exists(content.ComponentsDirectory));
        Assert.Null(content.EffectsDirectory);
    }

    [Fact]
    public void 行为制品_可解析为组件资产_且声明正确()
    {
        foreach (var id in new[] { "keyword.mobility", "keyword.entrench", "keyword.repair" })
        {
            Assert.Equal(id, ReadComponent(id).Id);
        }

        // 机动：挂到行动额度成员上（旧 injects → 新 mounts，二者语义等价：设计端编排）。
        var mobility = ReadComponent("keyword.mobility");
        var mount = Assert.Single(mobility.Mounts);
        Assert.Equal("engine#action-budget", mount.Target);
        Assert.Equal("onQuota", mount.Member);

        // 固守：两条判定器包装（旧 judicatorWrappers → 新 judge-wraps，语义等价）。
        var entrench = ReadComponent("keyword.entrench");
        Assert.Equal(2, entrench.JudgeWraps.Count);
        var moveWrapper = entrench.JudgeWraps.Single(w => w.Judicator == "move.leg.eligibility");
        Assert.Equal(WrapperPhase.After, moveWrapper.Phase);
        Assert.False(string.IsNullOrWhiteSpace(moveWrapper.Script));
        Assert.Contains(entrench.JudgeWraps, w => w.Judicator == "inhibit.leg.eligibility");
    }

    private static ComponentAsset ReadComponent(string id)
        => AssetSchema.Read(
            File.ReadAllText(Path.Combine(PackRoot, "effects-v4", id + ".component.json")),
            id + ".component.json");
}
