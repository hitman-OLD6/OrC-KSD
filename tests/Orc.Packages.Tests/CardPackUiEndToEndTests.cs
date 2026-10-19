using Orc.Cards;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Orc.UI;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 前端资产迁移 S9 端到端：卡包（目录约定）→ 引擎宿主冷加载 → `.ui` 插件（词条加载 / 投影提供者）
/// → 对局装配 → 每帧投影 → 运行期卡注册。
/// 程序化生成同构夹具（对应 `samples/card-pack-synthetic/`）。
/// </summary>
public sealed class CardPackUiEndToEndTests : IDisposable
{
    private const string PackId = "sample.synthetic";
    private const string PackGuid = "b0070000-0000-4000-8000-0000000000a1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-ui-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _keywordId = "sample.entrench." + Guid.NewGuid().ToString("N")[..8];

    public CardPackUiEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        KeywordRegistry.Unregister(_keywordId);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 冷加载_插件注册卡包词条_包内容带前缀_引擎级运行期卡随快照冻结()
    {
        WriteCardPack(withKeywords: true);
        var pluginsRoot = WriteUiPluginPackage();
        // 夹具须含内置插件：卡数据组件（六项）与内置判定器均由它装配（产业进程级静态面）。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();

        var host = EngineHost.FromDiscovery(
            PackageDiscovery.Discover(_root),
            evaluator: null,
            extraPackages: [BuiltInPluginPackage.Load(), .. UiPluginPackage.Packages(pluginsRoot)]);

        // ① 卡包进入引擎目录
        Assert.Contains(host.Catalog.Packages, p => p.Manifest.Id == PackId);

        // ② 词条由 `.ui` 插件（生命周期观察者）注册（内建/他包重名＝拒绝）
        Assert.True(KeywordRegistry.IsDefined(_keywordId));

        // ③ 包内容装载：卡 id 带 `pack/<包id>/` 前缀；文本→效果产出内联预制体
        var package = host.Catalog.ById(PackId).Single();
        var content = CardPackLoader.Load(package.RootDirectory, package.Manifest);
        Assert.Contains(content.Definitions, d => d.Id == "pack/sample.synthetic/sample.rifle");
        Assert.Contains(content.InlinePrefabs, p => !string.IsNullOrWhiteSpace(p.Root.Id));
        // 词条随内容一并解析（信息面）；**注册**由 `.ui` 插件按约定执行（上方断言）。
        Assert.Single(content.Keywords);
        Assert.Equal(_keywordId, content.Keywords[0].Id);

        // ④ 引擎级运行期卡注册（只追加不覆盖）＋随快照冻结
        host.CardData.Register(new RuntimeCardRegistration("runtime.temp", TempDefinition(), [], []));
        Assert.True(host.CardData.Contains("runtime.temp"));
        Assert.Single(host.CreateSnapshot().RuntimeCards);
        Assert.Throws<InvalidOperationException>(
            () => host.CardData.Register(new RuntimeCardRegistration("runtime.temp", TempDefinition(), [], [])));

        // ⑤ 卡包生命周期锁：对局进行中禁止卸载/重载
        host.LockCardPacks();
        Assert.False(host.TryUnloadCardPack(PackId, Orc.Core.AssetVersion.Parse("1.0.0"), out var lockFailure));
        Assert.Contains("对局进行中", lockFailure);
        host.UnlockCardPacks();
    }

    [Fact]
    public async Task 对局装配_投影提供者产出整盘视图_对局级运行期卡注册可用()
    {
        WriteCardPack(withKeywords: false);
        var pluginsRoot = WriteUiPluginPackage();
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();

        var host = EngineHost.FromDiscovery(
            PackageDiscovery.Discover(_root), evaluator: null,
            extraPackages: [BuiltInPluginPackage.Load(), .. UiPluginPackage.Packages(pluginsRoot)]);
        var snapshot = host.CreateSnapshot();

        var contents = snapshot.CardPacks
            .Select(p => CardPackLoader.Load(p.RootDirectory, p.Manifest)).ToArray();
        var definitions = contents.SelectMany(c => c.Definitions).ToArray();

        var registry = new CardEffectRegistry();
        var deck = Enumerable.Repeat("pack/sample.synthetic/sample.rifle", 8).ToArray();
        var match = new Match(
            new CardList(deck), new CardList(deck), definitions,
            seed: 7, firstPlayerIndex: 0, effectRegistry: registry,
            packageActivator: new SnapshotPackageActivator(snapshot));
        CardPackAssembler.Attach(registry, match.Engine, contents);
        await match.Initialize();

        // ① 投影提供者由 `.ui` 插件在 match/preCommand 相注册；前端以 matchId + artLookup 构造实例
        var provider = match.UiProjections.CreateProvider(artLookup: null, matchId: "e2e-1");
        Assert.NotNull(provider);

        // ② 每帧投影：整盘只读视图（对手手牌只降为数，不产卡视图）
        var view = provider!.Project(match, match.Players[0]);
        Assert.Equal("e2e-1", view.MatchId);
        Assert.NotEmpty(view.SelfHand);
        Assert.Empty(view.EnemyLine);

        // ③ 进入"进行"态后可做对局级运行期卡注册（只追加；新实例经 ID 水位线判"构筑之外"）
        await match.MulliganDone(match.Players[0]);
        await match.MulliganDone(match.Players[1]);
        Assert.Equal(MatchState.InProgress, match.State);

        match.CardService.RegisterCard(new RuntimeCardRegistration("runtime.in-match", TempDefinition(), [], []));
        var created = await match.CardService.CreateAsync("runtime.in-match", match.Players[0]);
        Assert.NotNull(created);
    }

    // ------------------------------------------------------------------ 夹具

    private static CardDefinition TempDefinition() => new(
        "临时卡", deployCost: 1, operateCost: 1, attack: 1, defense: 1,
        unitTypes: ["infantry"], faction: "germany", rarity: "standard");

    private string WriteCardPack(bool withKeywords)
    {
        var pack = Path.Combine(_root, "packs", PackId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));
        File.WriteAllText(Path.Combine(pack, "manifest.json"), $$"""
        {
          "kind": "cardpack",
          "id": "{{PackId}}",
          "guid": "{{PackGuid}}",
          "version": "1.0.0",
          "dependsOn": [],
          "assets": []
        }
        """);
        File.WriteAllText(Path.Combine(pack, "cards", "sample.rifle.card.json"), """
        {
         "schemaVersion": 1,
         "id": "sample.rifle",
         "name": "示例步枪兵",
         "components": [
          { "component": "typeCategory", "type": ["infantry"] },
          { "component": "factionCost", "faction": "germany", "kredits": 1 },
          { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
          { "component": "tagData", "rarity": "standard" }
         ]
        }
        """);
        File.WriteAllText(Path.Combine(pack, "cards", "sample.quartermaster.card.json"), """
        {
         "schemaVersion": 1,
         "id": "sample.quartermaster",
         "name": "示例军需官",
         "components": [
          { "component": "typeCategory", "type": ["infantry"] },
          { "component": "factionCost", "faction": "germany", "kredits": 2 },
          { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 4 },
          { "component": "tagData", "rarity": "limited" }
         ]
        }
        """);
        File.WriteAllText(Path.Combine(pack, "texts.json"), """
        {
         "sample.quartermaster": "部署：抽 1 张牌。"
        }
        """);
        if (withKeywords)
        {
            File.WriteAllText(Path.Combine(pack, "keywords.json"), $$"""
            [
             {
              "id": "{{_keywordId}}",
              "label": "固守",
              "battle": false,
              "bindings": [
               { "prefabId": "sample.entrench.effect", "effectName": "sample.entrench" }
              ]
             }
            ]
            """);
        }

        return pack;
    }

    private string WriteUiPluginPackage()
    {
        var pluginsRoot = Path.Combine(_root, "plugins");
        var directory = Path.Combine(pluginsRoot, UiPluginPackage.DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), $$"""
        {
          "kind": "plugin",
          "id": "{{UiPluginPackage.PackageId}}",
          "guid": "{{UiPluginPackage.PackageGuid}}",
          "version": "1.0.0",
          "dependsOn": [],
          "assets": []
        }
        """);
        return pluginsRoot;
    }
}
