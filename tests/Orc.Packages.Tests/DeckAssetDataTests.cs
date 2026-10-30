using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Packages.Decks;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S3 验收（R2／R12）：卡组数据体读写与构筑配置定稿。
/// 覆盖：读宽写窄、解析期就判的 count／重复、应用层 ID 全名归一、R12 推导五类用例，
/// 以及「推导先于判定、结果落到定义上」。
/// </summary>
public class DeckAssetDataTests
{
    private const string PackId = "demo";
    private const string Prefix = "pack/demo/";

    // ── 数据体读写 ───────────────────────────────────────────────────────────

    [Fact]
    public void 解析_字段齐全()
    {
        var parse = DeckDataJson.Parse(
            "{ \"schemaVersion\": 1, \"id\": \"demo.starter\", \"name\": \"示例先遣队\","
            + " \"deckConfig\": { \"mainFaction\": \"germany\", \"allyFaction\": \"soviet\" },"
            + " \"cards\": [ { \"id\": \"demo.rifleman\", \"count\": 4 }, { \"id\": \"demo.grenadier\" } ] }");

        Assert.True(parse.IsParsed);
        Assert.Empty(parse.Violations);
        var body = parse.Body!;
        Assert.Equal("demo.starter", body.Id);
        Assert.Equal("示例先遣队", body.Name);
        Assert.Equal("germany", body.MainFaction);
        Assert.Equal("soviet", body.AllyFaction);
        Assert.Equal(
            new[] { new DeckCardEntry("demo.rifleman", 4), new DeckCardEntry("demo.grenadier", 1) },
            body.Cards);
    }

    [Fact]
    public void 解析_count缺省为一且写窄后往返一致()
    {
        var parse = DeckDataJson.Parse(
            "{ \"schemaVersion\": 1, \"id\": \"d\", \"name\": \"n\", \"deckConfig\": { \"mainFaction\": \"germany\" },"
            + " \"cards\": [ { \"id\": \"a\" }, { \"id\": \"b\", \"count\": 2 } ] }");

        var body = parse.Body!;
        Assert.Equal(1, body.Cards[0].Count);

        var json = DeckDataJson.Serialize(body);
        Assert.DoesNotContain("\"count\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"count\": 2", json, StringComparison.Ordinal);

        var roundTrip = DeckDataJson.Parse(json).Body!;
        Assert.Equal(body.Id, roundTrip.Id);
        Assert.Equal(body.Name, roundTrip.Name);
        Assert.Equal(body.MainFaction, roundTrip.MainFaction);
        Assert.Equal(body.AllyFaction, roundTrip.AllyFaction);
        Assert.Equal(body.Cards, roundTrip.Cards);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"cards\": [ { \"id\": \"a\", \"count\": 0 } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"cards\": [ { \"id\": \"a\", \"count\": -3 } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"cards\": [ { \"id\": \"a\", \"count\": \"4\" } ] }")]
    [InlineData("{ \"schemaVersion\": 1, \"cards\": [ { \"id\": \"  \", \"count\": 1 } ] }")]
    public void 解析期即判张数与空白ID(string json)
    {
        var parse = DeckDataJson.Parse(json);

        // D50：不等展开——展开时 0 会静默变成 0 条，让「39 张」误通过。
        Assert.True(parse.IsParsed);
        Assert.NotEmpty(parse.Violations);
        Assert.Empty(parse.Body!.Cards);
    }

    [Theory]
    [InlineData("{ 这不是 JSON")]
    [InlineData("{ \"schemaVersion\": 2, \"id\": \"d\" }")]
    [InlineData("{ \"id\": \"d\" }")]
    [InlineData("[]")]
    public void 不可解析走Failure(string json)
    {
        var parse = DeckDataJson.Parse(json);

        Assert.False(parse.IsParsed);
        Assert.NotNull(parse.Failure);
        Assert.Null(parse.Body);
    }

    // ── 编译：身份与配置 ────────────────────────────────────────────────────

    [Fact]
    public void 编译_id与sidecar不一致_判非法()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("other.starter", "示例", Faction.Germany, Cards(("rifle", 1))),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "rifle")),
            requiredSize: 0);

        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Contains("不一致", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void 编译_缺deckConfig与缺主国都报faction_main_missing()
    {
        var noConfig = DeckAssetCompiler.Compile(
            "{ \"schemaVersion\": 1, \"id\": \"demo.starter\", \"name\": \"示例\", \"cards\": [] }",
            "demo.starter",
            PackId,
            new Dictionary<string, CardDefinition>(),
            requiredSize: 0);
        var noMain = DeckAssetCompiler.Compile(
            "{ \"schemaVersion\": 1, \"id\": \"demo.starter\", \"name\": \"示例\", \"deckConfig\": {}, \"cards\": [] }",
            "demo.starter",
            PackId,
            new Dictionary<string, CardDefinition>(),
            requiredSize: 0);

        Assert.False(noConfig.IsValid);
        Assert.Null(noConfig.Definition);
        Assert.Contains(noConfig.Violations, v => v.Kind == "faction-main-missing");
        Assert.False(noMain.IsValid);
        Assert.Contains(noMain.Violations, v => v.Kind == "faction-main-missing");
    }

    [Fact]
    public void 编译_缺name报name_missing且与张数违规并列()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", name: null, Faction.Germany, Cards(("g1", 1))),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1")),
            requiredSize: 39);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == "name-missing");
        Assert.Contains(result.Violations, v => v.Kind == "size");

        // 卡组自身组在逐卡检查之前（D66）——name-missing 必须是首条。
        Assert.Equal("name-missing", result.Violations[0].Kind);
    }

    [Fact]
    public void 编译_同一张卡混写裸ID与应用层ID判重复()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany,
                "[ { \"id\": \"g1\", \"count\": 2 }, { \"id\": \"pack/demo/g1\", \"count\": 2 } ]"),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1")),
            requiredSize: 0);

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations, v => v.Kind == "duplicate-entry");
        Assert.Equal(Prefix + "g1", violation.CardId);
    }

    [Fact]
    public void 编译_定义层卡引用为应用层ID全名()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany, Cards(("g1", 4), ("g2", 2))),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1"), (Faction.Germany, "g2")),
            requiredSize: 0);

        Assert.True(result.IsValid);
        Assert.Equal(
            new[] { Prefix + "g1", Prefix + "g2" },
            result.Definition!.Entries.Select(entry => entry.AppId));
        Assert.Equal(new[] { 4, 2 }, result.Definition.Entries.Select(entry => entry.Count));
    }

    // ── R12 推导 ────────────────────────────────────────────────────────────

    [Fact]
    public void R12用例一_第二大数量推导为盟国()
    {
        var result = DeckAssetCompiler.Compile(
            Deck(
                "demo.starter",
                "示例",
                Faction.Germany,
                Cards(("g1", 4), ("g2", 4), ("g3", 2), ("s1", 4), ("s2", 4), ("n1", 2))),
            "demo.starter",
            PackId,
            Pool(
                (Faction.Germany, "g1"), (Faction.Germany, "g2"), (Faction.Germany, "g3"),
                (Faction.Soviet, "s1"), (Faction.Soviet, "s2"),
                (Faction.Neutral, "n1")),
            requiredSize: 0);

        Assert.True(result.IsValid);
        Assert.Equal(Faction.Soviet, result.Definition!.DeckConfig.AllyFaction);
        Assert.True(result.Definition.AllyDerived);
        Assert.Equal(20, result.Definition.CardCount);
    }

    [Fact]
    public void R12用例二_全中立卡组仍推导出首个合法盟国()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany, Cards(("g1", 4), ("g2", 4), ("g3", 2), ("n1", 2))),
            "demo.starter",
            PackId,
            Pool(
                (Faction.Germany, "g1"), (Faction.Germany, "g2"), (Faction.Germany, "g3"),
                (Faction.Neutral, "n1")),
            requiredSize: 0);

        Assert.True(result.IsValid);
        Assert.Equal(Faction.Soviet, result.Definition!.DeckConfig.AllyFaction);
        Assert.DoesNotContain(result.Definition.Entries, entry => entry.AppId == Prefix + "s1");
    }

    [Fact]
    public void R12用例三_推导后第三国卡报faction_foreign()
    {
        var result = DeckAssetCompiler.Compile(
            Deck(
                "demo.starter",
                "示例",
                Faction.Germany,
                Cards(("g1", 4), ("g2", 4), ("g3", 2), ("s1", 4), ("s2", 4), ("u1", 3))),
            "demo.starter",
            PackId,
            Pool(
                (Faction.Germany, "g1"), (Faction.Germany, "g2"), (Faction.Germany, "g3"),
                (Faction.Soviet, "s1"), (Faction.Soviet, "s2"), (Faction.USA, "u1")),
            requiredSize: 0);

        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        var violation = Assert.Single(result.Violations, v => v.Kind == "faction-foreign");
        Assert.Equal(Prefix + "u1", violation.CardId);
    }

    [Fact]
    public void R12并列最多按登记序取先者()
    {
        // 并列时取 Faction.All 登记序在前者（soviet 在 usa 之前）——用推导本身断言，
        // 因为「soviet 与 usa 并列」的卡组整体必然非法（第三国卡 → faction-foreign）。
        var pool = Pool(
            (Faction.Germany, "g1"),
            (Faction.Soviet, "s1"), (Faction.Soviet, "s2"),
            (Faction.USA, "u1"), (Faction.USA, "u2"));
        var entries = new[]
        {
            new DeckEntry(Prefix + "g1", 4),
            new DeckEntry(Prefix + "s1", 4),
            new DeckEntry(Prefix + "s2", 4),
            new DeckEntry(Prefix + "u1", 4),
            new DeckEntry(Prefix + "u2", 4),
        };

        Assert.Equal(Faction.Soviet, AllyFactionDerivation.Derive(Faction.Germany, entries, pool));
        Assert.Equal(Faction.Soviet, AllyFactionDerivation.Derive(Faction.Germany, entries, pool));
    }

    [Fact]
    public void 推导结果永不为neutral也永不等于主国()
    {
        foreach (var main in new[] { Faction.Germany, Faction.Soviet, Faction.USA, Faction.Britain, Faction.Japan })
        {
            var ally = AllyFactionDerivation.Derive(main, [new DeckEntry(Prefix + "n1", 4)], new Dictionary<string, CardDefinition>
            {
                [Prefix + "n1"] = new CardDefinition(
                    "中立", 1, 1, 1, 1, faction: Faction.Neutral, rarity: Rarity.Standard),
            });

            Assert.NotEqual(Faction.Neutral, ally);
            Assert.NotEqual(main, ally);
        }
    }

    [Fact]
    public void R12显式声明优先_不推导且第三国照常判非法()
    {
        var result = DeckAssetCompiler.Compile(
            Deck(
                "demo.starter",
                "示例",
                Faction.Germany,
                Cards(("g1", 4), ("s1", 4), ("u1", 8)),
                ally: Faction.Soviet),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1"), (Faction.Soviet, "s1"), (Faction.USA, "u1")),
            requiredSize: 0);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == "faction-foreign" && v.CardId == Prefix + "u1");
    }

    [Fact]
    public void 盟国配额_十三张非法十二张合法()
    {
        var pool = Pool(
            (Faction.Germany, "g1"),
            (Faction.Soviet, "s1"), (Faction.Soviet, "s2"), (Faction.Soviet, "s3"), (Faction.Soviet, "s4"));

        var twelve = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany, Cards(("g1", 4), ("s1", 4), ("s2", 4), ("s3", 4))),
            "demo.starter", PackId, pool, requiredSize: 0);

        var thirteen = DeckAssetCompiler.Compile(
            Deck(
                "demo.starter",
                "示例",
                Faction.Germany,
                Cards(("g1", 4), ("s1", 4), ("s2", 4), ("s3", 4), ("s4", 1))),
            "demo.starter", PackId, pool, requiredSize: 0);

        Assert.True(twelve.IsValid);
        Assert.False(thirteen.IsValid);
        Assert.Contains(thirteen.Violations, v => v.Kind == "ally-quota");
    }

    [Fact]
    public void 中立卡不计入盟国配额()
    {
        var result = DeckAssetCompiler.Compile(
            Deck(
                "demo.starter",
                "示例",
                Faction.Germany,
                Cards(("g1", 4), ("s1", 4), ("s2", 4), ("s3", 4), ("n1", 4), ("n2", 4))),
            "demo.starter",
            PackId,
            Pool(
                (Faction.Germany, "g1"),
                (Faction.Soviet, "s1"), (Faction.Soviet, "s2"), (Faction.Soviet, "s3"),
                (Faction.Neutral, "n1"), (Faction.Neutral, "n2")),
            requiredSize: 0);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Violations, v => v.Kind == "ally-quota");
    }

    [Fact]
    public void 推导结果落在定义上_重复读取恒定()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany, Cards(("g1", 4), ("s1", 4))),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1"), (Faction.Soviet, "s1")),
            requiredSize: 0);

        var definition = result.Definition!;
        Assert.Equal(Faction.Soviet, definition.DeckConfig.AllyFaction);
        Assert.True(definition.AllyDerived);
        Assert.Equal(definition.DeckConfig.AllyFaction, definition.DeckConfig.AllyFaction);
        Assert.Equal(definition.ToCardIds(), definition.ToCardIds());
    }

    [Fact]
    public void 展开按声明顺序且长度等于张数之和()
    {
        var result = DeckAssetCompiler.Compile(
            Deck("demo.starter", "示例", Faction.Germany, Cards(("g2", 2), ("g1", 3))),
            "demo.starter",
            PackId,
            Pool((Faction.Germany, "g1"), (Faction.Germany, "g2")),
            requiredSize: 0);

        Assert.Equal(
            new[] { Prefix + "g2", Prefix + "g2", Prefix + "g1", Prefix + "g1", Prefix + "g1" },
            result.Definition!.ToCardIds());
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static string Deck(string id, string? name, string? main, string cardsJson, string? ally = null)
    {
        var nameJson = name is null ? "" : $"\"name\": \"{name}\", ";
        var config = main is null
            ? ""
            : ally is null
                ? $"\"deckConfig\": {{ \"mainFaction\": \"{main}\" }}, "
                : $"\"deckConfig\": {{ \"mainFaction\": \"{main}\", \"allyFaction\": \"{ally}\" }}, ";

        return $"{{ \"schemaVersion\": 1, \"id\": \"{id}\", {nameJson}{config}\"cards\": {cardsJson} }}";
    }

    private static string Cards(params (string Id, int Count)[] cards)
        => "[" + string.Join(", ", cards.Select(card => $"{{ \"id\": \"{card.Id}\", \"count\": {card.Count} }}")) + "]";

    private static Dictionary<string, CardDefinition> Pool(params (string Faction, string LocalId)[] cards)
        => cards.ToDictionary(
            card => Prefix + card.LocalId,
            card => new CardDefinition(
                "卡 " + card.LocalId,
                deployCost: 1,
                operateCost: 1,
                attack: 1,
                defense: 1,
                faction: card.Faction,
                rarity: Rarity.Standard),
            StringComparer.Ordinal);
}
