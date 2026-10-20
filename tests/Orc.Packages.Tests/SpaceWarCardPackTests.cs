using Orc.Cards;
using Orc.Game.Cards;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>太空战争卡包（samples/card-pack-space-war）的数据面验收：清单/内容/词条（含程序词条）/行为制品声明。</summary>
public sealed class SpaceWarCardPackTests
{
    private static string PackRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../samples/card-pack-space-war"));

    [Fact]
    public void 卡包_清单与内容_可被装载且无诊断()
    {
        Assert.True(Directory.Exists(PackRoot), $"卡包目录缺失：{PackRoot}");

        // 卡数据组件（六项内置）由内置插件装配；未装配＝组件被隔离、诊断不达零。
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(PackRoot, "manifest.json")));
        Assert.Equal("orc.space-war", manifest.Id);
        Assert.Equal("太空战争", manifest.Name);
        Assert.Equal(PackageKind.CardPack, manifest.Kind);
        Assert.Equal(PackageManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(PackageVersion.Compute(manifest.Id, manifest.MetadataList), manifest.Version);
        Assert.Empty(manifest.Dependencies);

        var registration = PackageRegistrar.Register(PackRoot, manifest, new AssetRegistry());
        Assert.Empty(registration.Diagnostics);

        var content = registration.Content;
        Assert.Equal(4, content.Definitions.Count);
        Assert.All(content.Definitions, d => Assert.StartsWith("pack/orc.space-war/", d.Id, StringComparison.Ordinal));
        Assert.Contains(content.Definitions, d => d.Id == "pack/orc.space-war/space-war.magnetic-armor-tank");

        Assert.Equal(4, content.Keywords.Count);
        Assert.Contains(content.Keywords, k => k.Id == "强磁护盾");
        Assert.Contains(content.Keywords, k => k.Id == "磁反应装甲");
        Assert.Contains(content.Keywords, k => k.Id == "海绵装甲");
        Assert.Contains(content.Keywords, k => k.Id == "游击");
        Assert.All(
            content.Keywords.Where(k => k.Id != "游击"),
            k => Assert.Single(k.Bindings));

        // 游击＝程序词条（move.retreat 规则），零效果绑定。
        var guerrilla = content.Keywords.Single(k => k.Id == "游击");
        Assert.Empty(guerrilla.Bindings);
        Assert.False(string.IsNullOrWhiteSpace(guerrilla.Program));

        Assert.NotNull(content.EffectsDirectory);
        Assert.True(Directory.Exists(content.EffectsDirectory));
    }

    [Fact]
    public void 词条声明_程序字段可被扫描()
    {
        var keywords = PackageScanner.ReadKeywords(PackRoot);
        Assert.Equal(4, keywords.Count);

        var guerrilla = keywords.Single(k => k.Id == "游击");
        Assert.Empty(guerrilla.Bindings);
        var program = KeywordProgram.Parse(guerrilla.Program!);
        var rule = Assert.Single(program.Rules);
        Assert.Equal("move.retreat", rule.Rule);
        Assert.Equal("1", rule.Value);
        Assert.Equal("self", rule.Scope);

        Assert.All(
            keywords.Where(k => k.Id != "游击"),
            k => Assert.Null(k.Program));
    }

    [Fact]
    public void 行为制品_可解析为预制体_且声明正确()
    {
        foreach (var id in new[] { "keyword.magnetic-shield", "keyword.magnetic-reaction", "keyword.sponge-armor" })
        {
            var snapshot = PrefabJson.Deserialize(
                File.ReadAllText(Path.Combine(PackRoot, "effects", id + ".prefab.json")));
            Assert.Equal(id, snapshot.Root.Id);
        }

        // 三条伤害规则：都在伤害处理端 Rewrite 段，按 10 → 20 → 30 分层。
        var expected = new (string Id, int Priority)[]
        {
            ("keyword.magnetic-shield", 10),
            ("keyword.magnetic-reaction", 20),
            ("keyword.sponge-armor", 30),
        };
        foreach (var (id, priority) in expected)
        {
            var snapshot = PrefabJson.Deserialize(
                File.ReadAllText(Path.Combine(PackRoot, "effects", id + ".prefab.json")));
            var inject = snapshot.Root.Injects.Single(i => i.TargetTriggerName == "伤害处理触发器");
            Assert.Equal("Rewrite", inject.BandName);
            Assert.Equal(priority, inject.Priority);
        }

        // 强磁护盾：指挥攻击 Commit 段（晚于默认转发）失去。
        var shield = PrefabJson.Deserialize(
            File.ReadAllText(Path.Combine(PackRoot, "effects", "keyword.magnetic-shield.prefab.json")));
        var attackInject = shield.Root.Injects.Single(i => i.TargetTriggerName == "单位攻击触发器");
        Assert.Equal("Commit", attackInject.BandName);
        Assert.Equal(200, attackInject.Priority);

        // 磁反应装甲：订阅指向触发器。
        var reaction = PrefabJson.Deserialize(
            File.ReadAllText(Path.Combine(PackRoot, "effects", "keyword.magnetic-reaction.prefab.json")));
        Assert.Contains(reaction.Root.Injects, i => i.TargetTriggerName == "指向触发器");
    }
}
