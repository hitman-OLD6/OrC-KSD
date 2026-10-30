using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S3/S7 验收（R10／D12）：阵营归属与盟国配额、展示名必填。
/// 档位归位：<c>faction-foreign</c>／<c>ally-quota</c> 与「配国」同侧——strict 查、lenient 放宽、none 跳过。
/// </summary>
public class DeckRuleFactionCompositionTests
{
    private const string Germany = "pack/p/g";
    private const string Soviet = "pack/p/s";
    private const string Soviet2 = "pack/p/s2";
    private const string USA = "pack/p/u";
    private const string Neutral = "pack/p/n";

    private static readonly Dictionary<string, CardDefinition> Pool = new(StringComparer.Ordinal)
    {
        [Germany] = Card(Faction.Germany, "德国兵"),
        [Soviet] = Card(Faction.Soviet, "苏联兵"),
        [Soviet2] = Card(Faction.Soviet, "苏联兵二"),
        [USA] = Card(Faction.USA, "美国兵"),
        [Neutral] = Card(Faction.Neutral, "中立兵"),
    };

    [Fact]
    public void 归属_第三国卡被点名()
    {
        var deck = Flat((Germany, 2), (Soviet, 2), (USA, 1));

        var violations = DeckRules.ValidateFactionComposition(
            deck, Pool, new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet));

        var violation = Assert.Single(violations);
        Assert.Equal("faction-foreign", violation.Kind);
        Assert.Equal(USA, violation.CardId);
    }

    [Fact]
    public void 归属_同卡多张只报一条()
    {
        var violations = DeckRules.ValidateFactionComposition(
            Flat((USA, 3)), Pool, new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet));

        Assert.Single(violations);
    }

    [Fact]
    public void 归属_主国盟国中立均合法()
    {
        var violations = DeckRules.ValidateFactionComposition(
            Flat((Germany, 2), (Soviet, 2), (Neutral, 2)),
            Pool,
            new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet));

        Assert.Empty(violations);
    }

    [Fact]
    public void 配额_十二张合法十三张非法()
    {
        var configuration = new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet);

        var twelve = DeckRules.ValidateFactionComposition(Flat((Soviet, 4), (Soviet2, 8)), Pool, configuration);
        var thirteen = DeckRules.ValidateFactionComposition(Flat((Soviet, 4), (Soviet2, 9)), Pool, configuration);

        Assert.Empty(twelve);
        var violation = Assert.Single(thirteen);
        Assert.Equal("ally-quota", violation.Kind);
    }

    [Fact]
    public void 配额_中立不计入()
    {
        var violations = DeckRules.ValidateFactionComposition(
            Flat((Soviet, 4), (Soviet2, 4), (Neutral, 20)),
            Pool,
            new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet));

        Assert.Empty(violations);
    }

    [Fact]
    public void 档位_lenient放宽归属与配额()
    {
        var deck = Flat((Soviet, 4), (Soviet2, 9), (USA, 4));

        Assert.NotEmpty(DeckRules.ValidateFactionComposition(
            deck, Pool, new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet), DeckRuleTier.Strict));
        Assert.Empty(DeckRules.ValidateFactionComposition(
            deck, Pool, new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet), DeckRuleTier.Lenient));
        Assert.Empty(DeckRules.ValidateFactionComposition(
            deck, Pool, new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet), DeckRuleTier.None));
    }

    [Fact]
    public void 无配置时归属无从判_通过()
        => Assert.Empty(DeckRules.ValidateFactionComposition(Flat((USA, 4)), Pool, configuration: null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 名称_缺失或空白报name_missing(string? name)
    {
        var violation = DeckRules.ValidateName(name);

        Assert.NotNull(violation);
        Assert.Equal("name-missing", violation!.Kind);
    }

    [Fact]
    public void 名称_有值通过() => Assert.Null(DeckRules.ValidateName("示例先遣队"));

    private static List<string> Flat(params (string Id, int Count)[] cards)
    {
        var list = new List<string>();
        foreach (var (id, count) in cards)
        {
            for (var index = 0; index < count; index++)
            {
                list.Add(id);
            }
        }

        return list;
    }

    private static CardDefinition Card(string faction, string name)
        => new(name, deployCost: 1, operateCost: 1, attack: 1, defense: 1, faction: faction, rarity: Rarity.Standard);
}
