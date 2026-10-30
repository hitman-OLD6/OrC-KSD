using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Loading;
using Orc.Game.Decks;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S4 验收（R9／D20-D24）：卡牌的可构筑标记。
/// 缺省＝可构筑（存量卡包零改动）；<c>VeteranOf</c> 非空即不可构筑（显式声明覆盖不了，但只记警报、卡照常可玩）；
/// 类型错＝非法项（不生成）；写面「读宽写窄」（true 不落盘、false 落盘）。
/// </summary>
public class CardDeckableTests
{
    private const string Body = """
    {
      "schemaVersion": 1,
      "id": "demo.a",
      "name": "甲",
      "components": [
        { "component": "typeCategory", "type": ["infantry"] },
        { "component": "factionCost", "faction": "Germany", "kredits": 1 },
        { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
        { "component": "tagData", "rarity": "Standard" }
      ]
    }
    """;

    [Fact]
    public void 缺省字段_卡可构筑()
    {
        var parsed = CardDataLoader.ParseLenient(Body);

        Assert.NotNull(parsed.Definition);
        Assert.True(parsed.Definition!.Deckable);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void 显式声明_决定可构筑(string literal, bool expected)
    {
        var parsed = CardDataLoader.ParseLenient(WithRootField($"\"deckable\": {literal},"));

        Assert.NotNull(parsed.Definition);
        Assert.Equal(expected, parsed.Definition!.Deckable);
    }

    [Fact]
    public void 老兵卡_无需声明即不可构筑()
    {
        var parsed = CardDataLoader.ParseLenient(Body.Replace(
            """{ "component": "tagData", "rarity": "Standard" }""",
            """{ "component": "tagData", "rarity": "Standard" }, { "component": "keywords", "attributes": ["VeteranOf:demo.a"] }"""));

        Assert.NotNull(parsed.Definition);
        Assert.NotNull(parsed.Definition!.VeteranOf);
        Assert.False(parsed.Definition.Deckable);
    }

    [Fact]
    public void 老兵卡显式声明可构筑_仍不可构筑且只留警报()
    {
        var parsed = CardDataLoader.ParseLenient(WithRootField("\"deckable\": true,").Replace(
            """{ "component": "tagData", "rarity": "Standard" }""",
            """{ "component": "tagData", "rarity": "Standard" }, { "component": "keywords", "attributes": ["VeteranOf:demo.a"] }"""));

        // 卡照常生成、照常可玩——只有一条警报档字段级诊断。
        Assert.NotNull(parsed.Definition);
        Assert.False(parsed.Definition!.Deckable);
        Assert.False(parsed.HasInvalidIssue);

        var issue = Assert.Single(parsed.Issues, item => item.Path == "deckable");
        Assert.Equal(CardIssueSeverity.Warning, issue.Severity);
        Assert.Equal(CardIssueReason.IgnoredByRule, issue.Reason);
    }

    [Theory]
    [InlineData("\"deckable\": \"no\",")]
    [InlineData("\"deckable\": 1,")]
    [InlineData("\"deckable\": null,")]
    public void 类型错_判非法且不生成(string field)
    {
        var parsed = CardDataLoader.ParseLenient(WithRootField(field));

        Assert.True(parsed.IsParsed);
        Assert.True(parsed.HasInvalidIssue);
        Assert.Null(parsed.Definition);
        Assert.Contains(parsed.Issues, item => item.Path == "deckable" && item.IsInvalid);
    }

    [Fact]
    public void 写出侧_true不落盘_false落盘且重读一致()
    {
        var deckable = CardDataJson.Deserialize(Body, out _);
        var json = CardDataJson.Serialize(deckable, out _);
        Assert.DoesNotContain("deckable", json, StringComparison.Ordinal);

        var blocked = CardDataJson.Deserialize(WithRootField("\"deckable\": false,"), out _);
        var blockedJson = CardDataJson.Serialize(blocked, out _);
        Assert.Contains("\"deckable\": false", blockedJson, StringComparison.Ordinal);

        var roundTrip = CardDataLoader.ParseLenient(blockedJson);
        Assert.NotNull(roundTrip.Definition);
        Assert.False(roundTrip.Definition!.Deckable);
    }

    [Fact]
    public void 不可构筑卡出现在卡组_报not_deckable并点名()
    {
        const string id = "pack/p/derived";
        var pool = new Dictionary<string, CardDefinition>(StringComparer.Ordinal)
        {
            [id] = new CardDefinition("衍生卡", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard, deckable: false),
        };

        var violations = DeckRules.Validate(new[] { id }, pool, requiredSize: 0);

        var violation = Assert.Single(violations);
        Assert.Equal("not-deckable", violation.Kind);
        Assert.Equal(id, violation.CardId);
    }

    [Fact]
    public void 不可构筑卡_lenient照报_none不报()
    {
        const string id = "pack/p/derived";
        var pool = new Dictionary<string, CardDefinition>(StringComparer.Ordinal)
        {
            [id] = new CardDefinition("衍生卡", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard, deckable: false),
        };

        Assert.Contains(DeckRules.Validate([id], pool, 0, DeckRuleTier.Lenient), v => v.Kind == "not-deckable");
        Assert.Empty(DeckRules.Validate([id], pool, 0, DeckRuleTier.None));
    }

    /// <summary>把根字段插在 <c>schemaVersion</c> 之后（模拟作者手写的位置）。</summary>
    private static string WithRootField(string field)
        => Body.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, " + field);
}
