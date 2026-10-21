using Orc.Cards;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>铜墙铁壁系列卡包（samples/card-pack-bulwark）的数据面验收：清单/内容/预检/行为制品声明。</summary>
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

        Assert.NotNull(content.EffectsDirectory);
        Assert.True(Directory.Exists(content.EffectsDirectory));
    }

    [Fact]
    public void 行为制品_可解析为预制体_且声明正确()
    {
        foreach (var id in new[] { "keyword.mobility", "keyword.entrench", "keyword.repair" })
        {
            var snapshot = PrefabJson.Deserialize(
                File.ReadAllText(Path.Combine(PackRoot, "effects", id + ".prefab.json")));
            Assert.Equal(id, snapshot.Root.Id);
        }

        var mobility = PrefabJson.Deserialize(
            File.ReadAllText(Path.Combine(PackRoot, "effects", "keyword.mobility.prefab.json")));
        var inject = Assert.Single(mobility.Root.Injects);
        Assert.Equal("行动额度触发器", inject.TargetTriggerName);
        Assert.Equal("onQuota", inject.EventId);

        var entrench = PrefabJson.Deserialize(
            File.ReadAllText(Path.Combine(PackRoot, "effects", "keyword.entrench.prefab.json")));
        Assert.Equal(2, entrench.Root.JudicatorWrappers.Count);
        var moveWrapper = entrench.Root.JudicatorWrappers.Single(w => w.JudicatorName == "move.leg.eligibility");
        Assert.Equal("after", moveWrapper.Phase);
        Assert.False(string.IsNullOrWhiteSpace(moveWrapper.Handler.CsxSource));
        Assert.Contains(
            entrench.Root.JudicatorWrappers,
            w => w.JudicatorName == "inhibit.leg.eligibility");
    }
}
