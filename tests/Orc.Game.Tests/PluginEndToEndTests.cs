using Orc.Core;
using Orc.Game.Judicators;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 资产化 S13 端到端验收：插件冷加载 → 快照 → 建局激活（csx 插件注册的判定器对局内可用，与内置插件共存）
/// → 卡包热加载只对新建对局生效（既有对局不受影响）。
/// </summary>
public sealed class PluginEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-e2e-" + Guid.NewGuid().ToString("N"));

    public PluginEndToEndTests() => Directory.CreateDirectory(_root);

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

    private const string CustomJudicatorScript = """
class E2EJudicator : Judicator
{
    public override object[]? Invoke(object[]? args) => new object[] { 42 };
}

Action<IPluginRegistrationContext> Register = ctx =>
{
    ctx.RegisterJudicator("e2e.custom", new E2EJudicator());
    ctx.DeclareDependency("json:e2e@deadbeef#1.0.0");
};
""";

    [Fact]
    public async Task 插件冷加载_建局激活_csx判定器与内置共存()
    {
        WriteCsxPlugin("e2e", CustomJudicatorScript);

        var host = EngineHost.Start(_root);
        Assert.Empty(host.LoadFailures);
        Assert.Single(host.Catalog.Packages);

        // 目录（自定义插件）＋ 内置插件 → 合成快照
        var snapshot = AssetCatalogSnapshot.FromPackages(
            host.Catalog.Packages.Concat(new[] { BuiltInPluginPackage.Load() }).ToArray());

        var match = new Match(
            GameTestData.CreateDeck(),
            GameTestData.CreateDeck(),
            GameTestData.CreateDefinitions(),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            packageActivator: new SnapshotPackageActivator(snapshot));

        await match.Initialize();

        // ① 插件（csx）注册的判定器对局内可用
        Assert.Equal(42, Assert.IsType<int>(match.Judicators.Invoke("e2e.custom", [1])![0]));

        // ② 内置插件（程序集入口）注册的判定器同样就位
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CostCheck));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CombatTargetLegal));
    }

    [Fact]
    public async Task 卡包热加载_只对新建对局生效()
    {
        var builtIn = new[] { BuiltInPluginPackage.Load() };
        var before = AssetCatalogSnapshot.FromPackages(builtIn);

        var host = EngineHost.Start(_root);
        WriteCardPack("kards-base");
        Assert.True(host.TryLoadCardPack(Path.Combine(_root, "packs", "kards-base"), out var failure), failure);

        // 旧快照（建局前冻结）不见卡包；新快照（含热加载包 ＋ 内置）可见
        Assert.Empty(before.CardPacks);
        var after = AssetCatalogSnapshot.FromPackages(host.Catalog.Packages.Concat(builtIn).ToArray());
        Assert.Single(after.CardPacks);

        // 旧对局（热加载前建局）不受影响：正常初始化、无卡包
        var matchBefore = NewMatch(before);
        await matchBefore.Initialize();
        Assert.Empty(before.CardPacks);
        Assert.NotNull(matchBefore.Judicators.Resolve(JudicatorNames.CostCheck));

        // 新对局（热加载后建局）看到卡包
        var matchAfter = NewMatch(after);
        await matchAfter.Initialize();
        Assert.Single(after.CardPacks);
        Assert.Equal("kards-base", after.CardPacks[0].Manifest.Id);
        Assert.NotNull(matchAfter.Judicators.Resolve(JudicatorNames.CostCheck));
    }

    private static Match NewMatch(AssetCatalogSnapshot snapshot)
        => new(
            GameTestData.CreateDeck(),
            GameTestData.CreateDeck(),
            GameTestData.CreateDefinitions(),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            packageActivator: new SnapshotPackageActivator(snapshot));

    private void WriteCsxPlugin(string id, string script)
    {
        var directory = Path.Combine(_root, "plugins", id);
        Directory.CreateDirectory(Path.Combine(directory, "csx"));
        File.WriteAllText(Path.Combine(directory, "csx", "register.csx"), script);

        var manifest = new PackageManifest(
            PackageKind.Plugin, id, Guid.NewGuid(), PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(),
            new[] { new PackageAsset("csx", "register", "csx/register.csx") });
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }

    private void WriteCardPack(string id)
    {
        var directory = Path.Combine(_root, "packs", id);
        Directory.CreateDirectory(directory);
        var manifest = new PackageManifest(
            PackageKind.CardPack, id, Guid.NewGuid(), PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(),
            new[] { new PackageAsset("json", "cards", "cards.json") });
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
        File.WriteAllText(Path.Combine(directory, "cards.json"), "{\"cards\":[]}");
    }
}
