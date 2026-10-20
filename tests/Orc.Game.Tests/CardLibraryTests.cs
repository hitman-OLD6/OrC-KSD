using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡牌库与名单实例化：注册/查询读面、重复注册拒绝、实例化装配（名称＋数据组件〔阵营〔国籍〕＋部署费／对战——S10 重构后〕）、
/// 边界清单④（Instantiate 三情形：空→空集 / 未注册→抛错 / 重复 id→独立实例并保序）。
/// </summary>
public class CardLibraryTests
{
    private static CardLibrary CreateLibrary(out LogicEngine engine, int definitionCount = 5)
    {
        engine = new LogicEngine();
        var library = new CardLibrary(engine);
        foreach (var entry in GameTestData.CreateDefinitions(definitionCount))
        {
            library.Register(entry.Id, entry.Definition);
        }

        return library;
    }

    [Fact]
    public void Register_Get_Contains_And_Definitions_Work()
    {
        var library = CreateLibrary(out _);

        Assert.True(library.Contains("c01"));
        Assert.False(library.Contains("zz9")); // 存在性查询：不抛错
        Assert.False(library.Contains(null!));
        Assert.False(library.Contains("  "));

        var definition = library.Get("c01"); // 取得定义
        Assert.Equal("卡01", definition.Name);
        Assert.Equal(1, definition.DeployCost);
        Assert.Equal(2, definition.OperateCost);
        Assert.Equal(3, definition.Attack);
        Assert.Equal(4, definition.Defense);

        Assert.Equal(5, library.Definitions.Count); // 枚举读面
        Assert.True(library.Definitions.ContainsKey("c05"));
    }

    [Fact]
    public void Register_Duplicate_Id_Is_Rejected()
    {
        var library = CreateLibrary(out _);
        Assert.Throws<InvalidOperationException>(() => library.Register("c01", new CardDefinition("重复", 0, 0, 0, 0, faction: Faction.Germany, rarity: Rarity.Standard)));
    }

    [Fact]
    public void Register_Validates_Arguments()
    {
        var library = CreateLibrary(out _);
        Assert.Throws<ArgumentException>(() => library.Register("", new CardDefinition("空白", 0, 0, 0, 0, faction: Faction.Germany, rarity: Rarity.Standard)));
        Assert.Throws<ArgumentException>(() => library.Register("  ", new CardDefinition("空白", 0, 0, 0, 0, faction: Faction.Germany, rarity: Rarity.Standard)));
        Assert.Throws<ArgumentNullException>(() => library.Register(null!, new CardDefinition("空", 0, 0, 0, 0, faction: Faction.Germany, rarity: Rarity.Standard)));
        Assert.Throws<ArgumentNullException>(() => library.Register("c99", null!));
    }

    [Fact]
    public void Get_Unregistered_Id_Throws()
    {
        var library = CreateLibrary(out _);
        Assert.Throws<KeyNotFoundException>(() => library.Get("zz9"));
    }

    [Fact]
    public void Instantiate_Empty_List_Produces_Empty_Set() // 边界④三情形之一
    {
        var library = CreateLibrary(out _);
        var set = new CardList().Instantiate(library);

        Assert.Empty(set);
    }

    [Fact]
    public void Instantiate_Unregistered_Id_Throws() // 边界④三情形之二
    {
        var library = CreateLibrary(out _);
        var list = new CardList(new[] { "c01", "zz9" });

        Assert.Throws<KeyNotFoundException>(() => list.Instantiate(library));
    }

    [Fact]
    public void Instantiate_Duplicate_Ids_Produce_Independent_Instances_And_Preserve_Order() // 边界④三情形之三
    {
        var library = CreateLibrary(out _);
        var list = new CardList(new[] { "c02", "c01", "c02" });

        var set = list.Instantiate(library);

        Assert.Equal(3, set.Count);
        Assert.Equal("卡02", set[0].Name); // 保序（与名单一致）
        Assert.Equal("卡01", set[1].Name);
        Assert.Equal("卡02", set[2].Name);
        Assert.NotSame(set[0], set[2]); // 重复 id → 独立实例
        Assert.NotSame(set[0], set[1]);
    }

    [Fact]
    public void Instantiate_Assigns_Name_And_Split_Data_Components_From_Definition() // 2A 迁移：四合一 → 拆分两组件；S10 重构：指挥点花费并入「阵营〔国籍〕＋部署费」合并组件
    {
        var library = CreateLibrary(out _);
        var set = new CardList(new[] { "c03" }).Instantiate(library);

        var card = set[0];
        Assert.Equal("卡03", card.Name); // 名称取自定义
        var factionCost = card.GetData<FactionCostData>(); // 阵营〔国籍〕＋部署费（合并组件——S10）
        Assert.Equal(3, factionCost.DeployCost);
        Assert.Equal(Faction.Germany, factionCost.Faction);
        var stats = card.GetData<BattleStatsData>(); // 对战（行动费 / 攻 / 防——初始值）
        Assert.Equal(4, stats.OperateCost);
        Assert.Equal(5, stats.Attack);
        Assert.Equal(6, stats.Defense);
    }

    [Fact]
    public void Four_In_One_Stats_Type_Is_Retired() // 2A：四合一退役（类型不再存在——同一数据无双真源）
    {
        Assert.Null(Type.GetType("Orc.Game.Cards.CardStatsData, Orc.Game"));
    }

    [Fact]
    public void Replace_只替换已存在键_并记录域() // S3/R14：测试段遮蔽专供入口
    {
        var library = CreateLibrary(out _);
        var replacement = new CardDefinition("替换卡", 9, 9, 9, 9, faction: Faction.Germany, rarity: Rarity.Standard);

        library.Replace("c01", replacement, AssetDomain.Test);

        Assert.Same(replacement, library.Get("c01"));
        Assert.Equal(AssetDomain.Test, library.DomainOf("c01"));
        Assert.True(library.IsTestAsset("c01"));
        Assert.Equal(5, library.Definitions.Count); // 未新增条目
        Assert.False(library.IsTestAsset("c02"));
        Assert.Equal(AssetDomain.Formal, library.DomainOf("c02"));
    }

    [Fact]
    public void Replace_缺失键_拒绝()
    {
        var library = CreateLibrary(out _);
        var definition = new CardDefinition("新卡", 0, 0, 0, 0, faction: Faction.Germany, rarity: Rarity.Standard);

        Assert.Throws<KeyNotFoundException>(() => library.Replace("no.such.card", definition, AssetDomain.Test));
        Assert.False(library.Contains("no.such.card"));
    }
}
