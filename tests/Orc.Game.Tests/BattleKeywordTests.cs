using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 第 2 批·A2 对战词条验收（Q&A-2）＋批 4 守护、批 5 冲击纳入口径：打标清单（本批 7 项＝闪击/奋战/烟幕/伏击/重甲X＋守护＋冲击）＋
/// 读取面（按标记筛选/计数/集合读取）＋组合场景（「获得 1 个随机对战词条」＝池构建＋PickOne＋授予；
/// 「获得全部对战词条」＝集合读取＋逐个授予——均不新增专门机制）。
/// </summary>
public class BattleKeywordTests
{
    private const string DualBattleUnitId = "u_bk_dual"; // 闪击＋奋战（双对战词条）

    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(DualBattleUnitId, new CardDefinition(
            "双对战兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Blitz), new KeywordDeclaration(KeywordIds.Fury) },
            faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    private static Match CreateMatch()
        => CommandTestKit.CreateCommandMatch(extraDefinitions: CreateDefinitions());

    [Fact]
    public void BattleKeyword_Marking_Set_And_Universe()
    {
        // 本批实现打标（7 项）：既有四枚＋重甲N（Q&A-2 判定依据＝B 站 Wiki 标注）＋守护（批 4——wiki『属于对战词条』）
        // ＋冲击（批 5——wiki『属于对战词条』）。
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Blitz));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Fury));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.SmokeScreen));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Ambush));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Armor));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Guard));
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Shock));

        // 其余本批词条不打标（wiki 无「属于对战词条」标注——按不属处理）。
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Suppressed));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Inhibited));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Mobilize));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Pincer));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Forecast));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Immune));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Intelligence));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.CannotBeSuppressed));
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.CannotBeInhibited));
        Assert.False(KeywordRegistry.IsBattleKeyword("未注册标识")); // 宽容查询
        Assert.False(KeywordRegistry.IsBattleKeyword(null!));

        // 打标全集（「已实现且打标」池边界；登记序稳定）——守护经批 4 纳入、冲击经批 5 纳入（全集 7 项）。
        Assert.Equal(
            new[] { KeywordIds.Blitz, KeywordIds.Fury, KeywordIds.SmokeScreen, KeywordIds.Ambush, KeywordIds.Armor, KeywordIds.Guard, KeywordIds.Shock },
            KeywordRegistry.BattleKeywordUniverse);
    }

    [Fact]
    public async Task BattleKeyword_CardReads_Filter_Count_And_Set()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 集合读取（按标记筛选）：双词条兵 → [闪击, 奋战]（打标全集登记序稳定）。
        var dual = await CommandTestKit.InstantiateLoadedAsync(match, player, DualBattleUnitId);
        Assert.Equal(new[] { KeywordIds.Blitz, KeywordIds.Fury }, BattleKeywordRules.GetBattleKeywords(dual));
        Assert.Equal(2, BattleKeywordRules.CountBattleKeywords(dual));

        // 条件查询：「对战词条数不小于 N」（筛选/计数面）。
        Assert.True(BattleKeywordRules.HasAtLeastBattleKeywords(dual, 2));
        Assert.False(BattleKeywordRules.HasAtLeastBattleKeywords(dual, 3));

        // 无词条卡＝空集合、计数 0（不抛错）。
        var plain = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        Assert.Empty(BattleKeywordRules.GetBattleKeywords(plain));
        Assert.Equal(0, BattleKeywordRules.CountBattleKeywords(plain));

        // 运行时授予后读取面随动（带参值词条＝标识入集合、参值经统一读口另读）。
        await plain.Keywords.GrantAsync(KeywordIds.Armor, 2);
        Assert.Equal(new[] { KeywordIds.Armor }, BattleKeywordRules.GetBattleKeywords(plain));
        Assert.Equal(2, KeywordRules.GetKeywordValue(plain, KeywordIds.Armor));

        // 死亡后登记保留（集合读取照常——登记面）。
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        await victim.Keywords.GrantAsync(KeywordIds.Armor, 2);
        await victim.ApplyDefenseDamageAsync(999);
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(new[] { KeywordIds.Armor }, BattleKeywordRules.GetBattleKeywords(victim));
    }

    [Fact]
    public async Task BattleKeyword_Random_Grant_Composition()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);

        // 「获得 1 个随机对战词条」＝池构建（打标全集）＋PickOne（B1 随机服务）＋授予（组合；确定性可复现）。
        var pool = KeywordRegistry.BattleKeywordUniverse;
        var random = MatchRandomService.ResolveFor(card);
        Assert.NotNull(random);
        var picked = random!.PickOne(pool);
        Assert.Contains(picked, pool);

        // 参值词条（重甲）被随机授予时参值取默认参值（本批取最低值 1——工程约定）。
        var granted = await card.Keywords.GrantAsync(picked, picked == KeywordIds.Armor ? 1 : null);
        Assert.True(granted);
        Assert.True(card.Keywords.Has(picked));
        if (picked == KeywordIds.Armor)
        {
            Assert.Equal(1, card.Keywords.GetValue(picked));
        }

        // 确定性复现：同种子同消费序列 → 同结果（固定种子显式化——对局 seed 42）。
        var second = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        var random2 = MatchRandomService.ResolveFor(second);
        Assert.NotNull(random2);
        var pickedAgain = random2!.PickOne(pool); // 同一流上按序消费（未必与首个相同——样本确定性由 RandomServiceTests 覆盖）
        Assert.Contains(pickedAgain, pool);
    }

    [Fact]
    public async Task BattleKeyword_GrantAll_Composition()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];
        var source = await CommandTestKit.InstantiateLoadedAsync(match, player, DualBattleUnitId); // 源：读取其集合
        var target = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);

        // 「获得 1 个单位的所有对战词条」＝集合读取＋逐个授予组合（授予面既有、不新增专门机制）。
        var keywords = BattleKeywordRules.GetBattleKeywords(source);
        foreach (var keyword in keywords)
        {
            await target.Keywords.GrantAsync(keyword, keyword == KeywordIds.Armor ? 1 : null);
        }

        Assert.Equal(new[] { KeywordIds.Blitz, KeywordIds.Fury }, BattleKeywordRules.GetBattleKeywords(target));
        Assert.Equal(2, BattleKeywordRules.CountBattleKeywords(target));

        // 全量与池一致（另一个目标：池＋逐个授予）。
        var full = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        foreach (var keyword in KeywordRegistry.BattleKeywordUniverse)
        {
            await full.Keywords.GrantAsync(keyword, keyword == KeywordIds.Armor ? 1 : null);
        }

        Assert.Equal(KeywordRegistry.BattleKeywordUniverse, BattleKeywordRules.GetBattleKeywords(full));
        Assert.Equal(7, BattleKeywordRules.CountBattleKeywords(full)); // 批 5 全集口径：6 → 7（冲击纳入）
        Assert.True(BattleKeywordRules.HasAtLeastBattleKeywords(full, 7));
    }
}
