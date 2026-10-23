using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 官方语料**全量装载**冒烟（S10 b）：`scripts/import_kards_cards.py` 产出的 1646 张数据体
/// 经 <see cref="CardDataLoader"/> 全量读入 → 装配进 <see cref="Match"/> → 一张卡打出（部署/扣费/单位化）。
/// 口径说明：官方语料**不含效果字段**（无 effects/trigger/ability），故本用例验证的是
/// "元数据 ＋ 花费 ＋ 数值 ＋ 词条（含未实现项分流）"这一层——不涉及卡牌特有效果行为。
/// 依赖：需先运行 `python scripts/import_kards_cards.py`（产出 `outputs/kards-cards/`）；目录不存在＝跳过。
/// </summary>
public class OfficialCardDataSmokeTests
{
    private const string SampleUnitId = "13e_dragons"; // 官方单位卡：1/2、France、Standard、步兵、blitz、kredits 0

    [Fact]
    public async Task Official_Corpus_Loads_Fully_And_One_Card_Can_Be_Deployed()
    {
        var directory = Path.Combine(FindRepoRoot(), "outputs", "kards-cards");
        if (!Directory.Exists(directory))
        {
            return; // 未生成产出：跳过（依赖导入脚本——非本用例职责）
        }

        // ---------- ① 全量读入（无坏文件/无隔离失败） ----------
        var loaded = CardDataLoader.LoadDirectory(directory);

        Assert.Empty(loaded.Failures);
        Assert.Equal(1646, loaded.Definitions.Count);

        // 官方语料无效果字段 ⇒ 无内联预制体、无效果声明。
        Assert.Empty(loaded.Prefabs);
        Assert.Empty(loaded.EffectDeclarations);

        // ---------- ② 抽样核对：花费/数值/类型/国籍/词条分流 ----------
        var dragons = loaded.Definitions.Single(entry => entry.Id == SampleUnitId).Definition;
        Assert.Equal(0, dragons.DeployCost);   // kredits → 部署费
        Assert.Equal(0, dragons.OperateCost);  // operationCost → 行动费
        Assert.Equal(1, dragons.Attack);
        Assert.Equal(2, dragons.Defense);
        Assert.Equal(Faction.France, dragons.Faction);
        Assert.Equal(Rarity.Standard, dragons.Rarity);
        Assert.Equal(CardCategory.Unit, dragons.Category);
        Assert.Equal(new[] { UnitType.Infantry }, dragons.UnitTypes);
        Assert.Equal(new[] { KeywordIds.Blitz }, dragons.Keywords.Select(item => item.Id));

        // 未实现词条（官方其余未实现标识如 bond/alpine/salvage/…；`shock` 经批 5、`guard` 经批 4 转正式承载）
        // ＝只读留痕、不 fail-fast。
        Assert.Contains(loaded.Definitions, entry => entry.Definition.UnmappedAttributes.Count > 0);

        // ---------- ③ 装配进对局并打出一张官方卡 ----------
        var engine = new LogicEngine();
        foreach (var prefab in loaded.Prefabs)
        {
            engine.Prefabs.RegisterPrefab(prefab);
        }

        var effectRegistry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in loaded.EffectDeclarations)
        {
            effectRegistry.DeclarePrefab(cardId, prefabIds);
        }

        var deck = new CardList(Enumerable.Repeat(SampleUnitId, 10));
        var match = new Match(
            deck,
            new CardList(Enumerable.Repeat(SampleUnitId, 10)),
            loaded.Definitions,
            seed: 42,
            firstPlayerIndex: null,
            options: new MatchOptions { SkipMulligan = true },
            targeterBridge: null,
            effectRegistry: effectRegistry,
            packageActivator: TestHost.Activator);

        await match.Initialize();

        var player = match.Players[0];
        var unit = (UnitCard)match.CardLibrary.Instantiate(SampleUnitId);
        await unit.LoadAsync(player);
        player.Hand.Add(unit);

        var supportLine = match.Battlefield.PlayerASupportLine;
        var pointsBefore = player.Points;
        var playResult = await match.PlayManager.PlayUnitAsync(unit, supportLine.GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Success, playResult.Status);
        Assert.Same(unit, supportLine[1]); // 落在插入点 1＝支援线 HQ 之后（位置即元素顺序）

        // 数值经加载链就绪（组件 loader 驱动：battleStats）。
        var stats = unit.GetData<BattleStatsData>();
        Assert.Equal(1, stats.Attack);
        Assert.Equal(2, stats.Defense);

        // 部署费 0 ⇒ 点数不变。
        Assert.Equal(pointsBefore, player.Points);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
