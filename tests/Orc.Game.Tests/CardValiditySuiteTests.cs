using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S6 三档判定套件（R5/D40/D41）：**判定表逐项**验收——非法七项、警报五项，外加 R2/D48 判非法的两项
/// （<c>id</c> 不一致、源文件缺失）与卡级聚合、默认值填充、插件追加项、档位/原因 token 冻结。
/// </summary>
public class CardValiditySuiteTests
{
    private static readonly string ExistingImage = typeof(CardValiditySuiteTests).Assembly.Location;

    private static string Card(
        string? id = "demo.a",
        string? name = "甲",
        bool typeCategory = true,
        bool factionCost = true,
        bool battleStats = true,
        bool tagData = true,
        string category = "infantry",
        string? effects = null,
        string? keywords = null)
    {
        var parts = new List<string>();
        if (typeCategory)
        {
            parts.Add($"{{ \"component\": \"typeCategory\", \"type\": [\"{category}\"] }}");
        }

        if (factionCost)
        {
            parts.Add("""{ "component": "factionCost", "faction": "Germany", "kredits": 1 }""");
        }

        if (battleStats)
        {
            parts.Add("""{ "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 }""");
        }

        if (tagData)
        {
            parts.Add("""{ "component": "tagData", "rarity": "Standard" }""");
        }

        if (keywords is not null)
        {
            parts.Add(keywords);
        }

        if (effects is not null)
        {
            parts.Add(effects);
        }

        var head = new List<string> { "\"schemaVersion\": 1" };
        if (id is not null)
        {
            head.Add($"\"id\": \"{id}\"");
        }

        if (name is not null)
        {
            head.Add($"\"name\": \"{name}\"");
        }

        return "{\n  " + string.Join(",\n  ", head)
            + ",\n  \"components\": [\n    " + string.Join(",\n    ", parts) + "\n  ]\n}";
    }

    private static CardValidityInput Input(string json) => new("pack/demo/demo.a")
    {
        MetadataLocalId = "demo.a",
        HasSidecar = true,
        RawJson = json,
        CardImagePath = ExistingImage,
        Description = "卡面描述",
    };

    private static CardFieldIssue Single(CardValidityResult result, string path)
        => Assert.Single(result.Issues, issue => issue.Path == path);

    [Fact]
    public void Complete_Card_Is_Valid_And_Generates_Definition()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card()));

        Assert.Equal(CardLoadTier.Valid, result.Tier);
        Assert.Empty(result.Issues);
        Assert.True(result.IsPlayable);
        Assert.NotNull(result.Definition);
        Assert.Equal("甲", result.Definition!.Name);
        Assert.Equal(1, result.Definition.DeployCost);
        Assert.Equal(3, result.Definition.Defense);
    }

    // ── D40 非法七项 ──

    [Fact]
    public void Missing_Id_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(id: null)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition); // 非法不生成
        var issue = Single(result, "id");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.True(issue.IsInvalid);
        Assert.Equal("", issue.DefaultValue);
    }

    [Fact]
    public void Missing_FactionCost_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(factionCost: false)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.factionCost").Reason);
    }

    [Fact]
    public void Missing_TypeCategory_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(typeCategory: false)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.typeCategory").Reason);
    }

    [Fact]
    public void Unit_Without_BattleStats_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(battleStats: false)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.battleStats").Reason);
    }

    [Fact]
    public void Command_Without_Effects_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(category: "order", battleStats: false)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.effects").Reason);
    }

    [Fact]
    public void Counter_With_Empty_Effect_List_Is_Invalid()
    {
        var json = Card(
            category: "countermeasure",
            battleStats: false,
            effects: """{ "component": "effects", "prefabs": [] }""");

        var result = CardValiditySuite.BuiltIn.Evaluate(Input(json));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.effects").Reason);
    }

    [Fact]
    public void Effects_Referencing_Unknown_Asset_Is_Invalid()
    {
        var json = Card(effects: """{ "component": "effects", "prefabs": ["demo.missing"] }""");

        // 无引用存在性判定源 ⇒ 不臆断（不得凭空判非法）。
        Assert.Equal(CardLoadTier.Valid, CardValiditySuite.BuiltIn.Evaluate(Input(json)).Tier);

        var strict = CardValiditySuite.BuiltIn.Evaluate(Input(json) with { EffectReferenceExists = _ => false });

        Assert.Equal(CardLoadTier.Invalid, strict.Tier);
        Assert.Null(strict.Definition);
        var issue = Single(strict, "components.effects.prefabs[0]");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal("demo.missing", issue.DefaultValue);
    }

    [Fact]
    public void Invalid_Version_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card()) with { InvalidVersion = "1.2.x" });

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.VersionInvalid, Single(result, "version").Reason);
    }

    // ── D40 警报五项 ──

    [Fact]
    public void Missing_Name_Is_Warning_And_Fills_Empty_String()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(name: null)));

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.NotNull(result.Definition);
        Assert.Equal("", result.Definition!.Name); // 类型默认值＝空串
        var issue = Single(result, "name");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.False(issue.IsInvalid);
        Assert.Equal("", issue.DefaultValue);
    }

    [Fact]
    public void Missing_Card_Image_Is_Warning_And_Playable()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card()) with { CardImagePath = null });

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.True(result.IsPlayable);
        Assert.Equal(CardIssueReason.Missing, Single(result, "image").Reason);
    }

    [Fact]
    public void Card_Image_Path_Without_File_Is_Warning()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(
            Input(Card()) with { CardImagePath = "art/not-there.png" });

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.Equal(CardIssueReason.SourceMissing, Single(result, "image").Reason);
    }

    [Fact]
    public void Missing_Description_Is_Warning()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card()) with { Description = "  " });

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.True(result.IsPlayable);
        Assert.Equal(CardIssueReason.Missing, Single(result, "description").Reason);
    }

    [Fact]
    public void Missing_TagData_Is_Warning_And_Defaults_To_Standard_Rarity()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(tagData: false)));

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.NotNull(result.Definition);
        Assert.Equal(Rarity.Standard, result.Definition!.Rarity); // 合成默认组件
        Assert.Equal(CardIssueReason.Missing, Single(result, "components.tagData").Reason);
    }

    [Fact]
    public void Unknown_Keyword_Is_Warning_And_Located_By_Index()
    {
        var json = Card(keywords: """{ "component": "keywords", "attributes": ["no-such-keyword"] }""");
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(json));

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.True(result.IsPlayable);
        var issue = Single(result, "components.keywords.attributes[0]");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal("no-such-keyword", issue.DefaultValue);
    }

    // ── R2/D48：id 不一致、孤儿源文件 ──

    [Fact]
    public void Id_Mismatch_With_Sidecar_Is_Invalid()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(id: "demo.b")));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.IdMismatch, Single(result, "id").Reason);

        // 无 sidecar 声明（合成元数据）时无可比对项——不误报。
        var noSidecar = CardValiditySuite.BuiltIn.Evaluate(
            Input(Card(id: "demo.b")) with { MetadataLocalId = null, HasSidecar = false });
        Assert.DoesNotContain(noSidecar.Issues, issue => issue.Reason == CardIssueReason.IdMismatch);
    }

    [Fact]
    public void Missing_Source_File_Is_Invalid()
    {
        var empty = new CardValidityInput("pack/demo/demo.a")
        {
            MetadataLocalId = "demo.a",
            SourcePath = Path.Combine(Path.GetTempPath(), "orc-s6-not-there.card.json"),
            RawJson = null,
        };

        var result = CardValiditySuite.BuiltIn.Evaluate(empty);

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Equal(CardIssueReason.SourceMissing, Single(result, "source").Reason);
    }

    [Fact]
    public void Missing_Sidecar_Is_Warning_Only()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(
            Input(Card()) with { HasSidecar = false, MetadataLocalId = null });

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.True(result.IsPlayable);
        Assert.Equal(CardIssueReason.SidecarMissing, Single(result, "sidecar").Reason);
    }

    // ── 卡级聚合 / 默认值 / 插件追加 ──

    [Fact]
    public void Any_Invalid_Item_Makes_Whole_Card_Invalid()
    {
        // 同时缺 tagData（警报）与 battleStats（非法）⇒ 整卡非法，且两类问题都留痕。
        var result = CardValiditySuite.BuiltIn.Evaluate(Input(Card(tagData: false, battleStats: false)));

        Assert.Equal(CardLoadTier.Invalid, result.Tier);
        Assert.Null(result.Definition);
        Assert.Contains(result.Issues, issue => issue.Reason == CardIssueReason.Missing && !issue.IsInvalid);
        Assert.Contains(result.InvalidIssues, issue => issue.Path == "components.battleStats");
    }

    [Fact]
    public void Multiple_Warnings_Keep_Card_Playable()
    {
        var result = CardValiditySuite.BuiltIn.Evaluate(new CardValidityInput("pack/demo/demo.a")
        {
            MetadataLocalId = "demo.a",
            RawJson = Card(name: null, tagData: false),
            CardImagePath = null,
            Description = null,
        });

        Assert.Equal(CardLoadTier.Warning, result.Tier);
        Assert.True(result.IsPlayable);
        Assert.Equal(
            new[] { "name", "components.tagData", "image", "description" },
            result.FieldPaths);
    }

    [Fact]
    public void No_Raw_Json_Falls_Back_To_Registered_Definition()
    {
        var definition = new CardDefinition(
            "乙", deployCost: 2, operateCost: 1, attack: 1, defense: 1,
            faction: Faction.Germany, rarity: Rarity.Standard);

        var result = CardValiditySuite.BuiltIn.Evaluate(new CardValidityInput("pack/demo/demo.a")
        {
            MetadataLocalId = "demo.a",
            RawJson = null,
            RegisteredDefinition = definition,
        });

        Assert.Equal(CardLoadTier.Valid, result.Tier);
        Assert.Same(definition, result.Definition);
    }

    [Fact]
    public void Plugin_Check_Appends_Issues_But_Cannot_Change_Skeleton()
    {
        var warningSuite = new CardValiditySuite(
            new[] { new FixedCheck(CardFieldIssue.Warning("tag", "string", "", CardIssueReason.WrongType)) });

        var warned = warningSuite.Evaluate(Input(Card()));
        Assert.Equal(CardLoadTier.Warning, warned.Tier);
        Assert.Contains(warned.Issues, issue => issue.Path == "tag");

        var invalidSuite = new CardValiditySuite(
            new[] { new FixedCheck(CardFieldIssue.Invalid("tag", "string", "", CardIssueReason.WrongType)) });

        var rejected = invalidSuite.Evaluate(Input(Card()));
        Assert.Equal(CardLoadTier.Invalid, rejected.Tier);
        Assert.Null(rejected.Definition);
    }

    [Fact]
    public void Tokens_Match_Frozen_Vocabulary()
    {
        Assert.Equal(
            new[] { "valid", "warning", "invalid" },
            Enum.GetValues<CardLoadTier>().Select(CardLoadTierTokens.Token));

        // 冻结词表（I10）：新增原因＝表尾追加（§34 加 scriptCompileFailed），既有值一字不动。
        Assert.Equal(
            new[]
            {
                "missing", "wrongType", "outOfRange", "sourceMissing", "sidecarMissing",
                "idMismatch", "versionInvalid", "loaderUnregistered", "conflict", "duplicateGuid",
                "scriptCompileFailed",
            },
            Enum.GetValues<CardIssueReason>().Select(CardIssueReasonTokens.Token));

        foreach (var reason in Enum.GetValues<CardIssueReason>())
        {
            Assert.True(CardIssueReasonTokens.TryParse(CardIssueReasonTokens.Token(reason), out var parsed));
            Assert.Equal(reason, parsed);
        }
    }

    private sealed class FixedCheck : ICardValidityCheck
    {
        private readonly CardFieldIssue _issue;

        internal FixedCheck(CardFieldIssue issue) => _issue = issue;

        public string Name => "fixed";

        public IEnumerable<CardFieldIssue> Evaluate(CardValidityContext context) => new[] { _issue };
    }
}
