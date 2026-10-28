using Orc.Core;
using Orc.Core.Model;
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
        Assert.Equal(AssetLoadTestKit.RecomputeContent(PackRoot, manifest.Id), manifest.Version);
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

        // v4-only：行为制品的装载目录是 components 目录（旧 effects 目录已退场，本包不再提供）。
        Assert.NotNull(content.ComponentsDirectory);
        Assert.True(Directory.Exists(content.ComponentsDirectory));
        Assert.Null(content.EffectsDirectory);
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
    public void 行为制品_可解析为组件资产_且声明正确()
    {
        foreach (var id in new[] { "keyword.magnetic-shield", "keyword.magnetic-reaction", "keyword.sponge-armor" })
        {
            Assert.Equal(id, ReadComponent(id).Id);
        }

        // 三条伤害规则：都在伤害处理成员上作 before 包装（旧「伤害处理触发器 + Rewrite 段」的等价落点）。
        // 旧 priority 10/20/30 的「分层」在新模型**没有对应物**（R1i：band 与 priority 数值退役，
        // 同相位内相对次序一律由声明序推出）——因此这里只断言结构，不再断言数值分层。
        foreach (var id in new[] { "keyword.magnetic-shield", "keyword.magnetic-reaction", "keyword.sponge-armor" })
        {
            var wrap = Assert.Single(ReadComponent(id).Wraps.Where(w => w.Target.Address.ToString() == "engine#damage-apply"));
            Assert.Equal(WrapperPhase.Before, wrap.Phase);
        }

        // 强磁护盾：单位攻击成员上的 after 包装（旧「单位攻击触发器 + Commit 段 + priority 200」的等价落点；
        // Commit ＝「默认转发之后」，与 after 相位同义）。
        var shield = ReadComponent("keyword.magnetic-shield");
        var attackWrap = Assert.Single(shield.Wraps.Where(w => w.Target.Address.ToString() == "engine#unit-attack"));
        Assert.Equal(WrapperPhase.After, attackWrap.Phase);

        // 磁反应装甲：挂到宿主指向成员上（旧「订阅指向触发器」的等价落点：订阅＝挂载）。
        var reaction = ReadComponent("keyword.magnetic-reaction");
        var mount = Assert.Single(reaction.Mounts);
        Assert.Equal("host#targeting", mount.Target);
        Assert.Equal("onTargeted", mount.Member);
    }

    private static ComponentAsset ReadComponent(string id)
        => AssetSchema.Read(
            File.ReadAllText(Path.Combine(PackRoot, "effects-v4", id + ".component.json")),
            id + ".component.json");
}
