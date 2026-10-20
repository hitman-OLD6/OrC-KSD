using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Loading;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 宽松解析入口（R5/D40；<see cref="CardDataLoader.ParseLenient"/>／<see cref="CardDataLoader.LoadFileLenient"/>）验收：
/// 字段缺失/类型不符＝字段级问题（不抛、按 D40 分档），JSON 语法等「真无法处理」＝不可解析；
/// 同时守护**严格入口**（<see cref="CardDataLoader.LoadFiles"/>）行为不变。
/// </summary>
public sealed class CardDataLenientLoadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "orc-lenient-" + Guid.NewGuid().ToString("N"));

    public CardDataLenientLoadTests()
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string Complete = """
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
    public void ParseLenient_Complete_Card_Yields_No_Issues()
    {
        var parsed = CardDataLoader.ParseLenient(Complete);

        Assert.True(parsed.IsParsed);
        Assert.Equal("demo.a", parsed.Id);
        Assert.Empty(parsed.Issues);
        Assert.False(parsed.HasInvalidIssue);
        Assert.NotNull(parsed.Definition);
        Assert.Equal("甲", parsed.Definition!.Name);
    }

    [Fact]
    public void ParseLenient_Missing_Name_Is_Field_Issue_And_Fills_Empty_String()
    {
        var parsed = CardDataLoader.ParseLenient(Complete.Replace("\"name\": \"甲\",", string.Empty));

        Assert.True(parsed.IsParsed);
        var issue = Assert.Single(parsed.Issues, item => item.Path == "name");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal(CardIssueSeverity.Warning, issue.Severity);
        Assert.NotNull(parsed.Definition);
        Assert.Equal(string.Empty, parsed.Definition!.Name);
    }

    [Fact]
    public void ParseLenient_Missing_FactionCost_Is_Invalid_And_Generates_Nothing()
    {
        var parsed = CardDataLoader.ParseLenient(Complete.Replace(
            "{ \"component\": \"factionCost\", \"faction\": \"Germany\", \"kredits\": 1 },", string.Empty));

        Assert.True(parsed.IsParsed); // 字段问题不是「不可解析」
        var issue = Assert.Single(parsed.Issues, item => item.Path == "components.factionCost");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal(CardIssueSeverity.Invalid, issue.Severity);
        Assert.Null(parsed.Definition); // D40：非法不生成
    }

    [Fact]
    public void ParseLenient_Component_Of_Wrong_Type_Is_Field_Issue_Not_Exception()
    {
        var parsed = CardDataLoader.ParseLenient(Complete.Replace(
            "{ \"component\": \"factionCost\", \"faction\": \"Germany\", \"kredits\": 1 }",
            "{ \"component\": \"factionCost\", \"faction\": \"Germany\" }"));

        Assert.True(parsed.IsParsed);
        Assert.Contains(
            parsed.Issues,
            item => item.Path == "components.factionCost" && item.Reason == CardIssueReason.WrongType);
        Assert.Null(parsed.Definition);
    }

    [Fact]
    public void ParseLenient_Missing_Id_Reports_Issue_And_Leaves_Id_Null_For_Caller()
    {
        var parsed = CardDataLoader.ParseLenient(Complete.Replace("\"id\": \"demo.a\",", string.Empty));

        Assert.True(parsed.IsParsed);
        Assert.Null(parsed.Id); // 身份由调用方按 sidecar 声明补位
        Assert.Contains(parsed.Issues, item => item.Path == "id" && item.Reason == CardIssueReason.Missing);
        Assert.Null(parsed.Definition);
    }

    [Fact]
    public void ParseLenient_Invalid_Json_Is_Unparsable()
    {
        var parsed = CardDataLoader.ParseLenient("{ 这不是合法 JSON");

        Assert.False(parsed.IsParsed);
        Assert.NotNull(parsed.Error);
        Assert.Empty(parsed.Issues);
        Assert.Null(parsed.Definition);
    }

    [Fact]
    public void ParseLenient_Unsupported_Schema_Version_Is_Unparsable()
    {
        var parsed = CardDataLoader.ParseLenient(Complete.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9"));

        Assert.False(parsed.IsParsed);
        Assert.Contains("schema 版本不符", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLenient_Duplicate_Component_Is_Unparsable()
    {
        // 重复组件＝数据体结构性错误（哪份生效无从判断）⇒ 不静默取首条。
        var parsed = CardDataLoader.ParseLenient(Complete.Replace(
            "{ \"component\": \"tagData\", \"rarity\": \"Standard\" }",
            "{ \"component\": \"tagData\", \"rarity\": \"Standard\" }, { \"component\": \"tagData\", \"rarity\": \"Limited\" }"));

        Assert.False(parsed.IsParsed);
        Assert.Contains("重复组件", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFileLenient_Keeps_Inline_Prefab_Declarations_For_Playable_Card()
    {
        var path = Path.Combine(_directory, "demo.a.card.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "id": "demo.a",
          "name": "甲",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "effects", "inline": [
              { "schemaVersion": 2, "root": { "id": "demo.a.effect", "mainTrigger": {
                "id": "t", "stableKey": "s", "kind": "passive", "hooks": ["card.played"],
                "events": [ { "id": "e", "entry": "HandleAsync", "csx": "// 空" } ] } } } ] }
          ]
        }
        """);

        var load = CardDataLoader.LoadFileLenient(path);

        Assert.True(load.IsLoaded);
        Assert.Empty(load.Issues);
        var prefab = Assert.Single(load.Prefabs);
        Assert.Equal("demo.a.effect", prefab.Root.Id);
        var declaration = Assert.Single(load.EffectDeclarations);
        Assert.Equal("demo.a", declaration.CardId);
        Assert.Equal(new[] { "demo.a.effect" }, declaration.PrefabIds);
    }

    [Fact]
    public void LoadFileLenient_Invalid_Card_Generates_No_Definition_And_No_Effect_Declaration()
    {
        var path = Path.Combine(_directory, "demo.b.card.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "id": "demo.b",
          "components": [
            { "component": "typeCategory", "type": ["order"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """);

        var load = CardDataLoader.LoadFileLenient(path);

        Assert.True(load.IsLoaded);
        Assert.Null(load.Definition);
        Assert.Empty(load.Prefabs);
        Assert.Contains(load.Issues, item => item.Path == "components.effects");
    }

    [Fact]
    public void LoadFileLenient_Unparsable_File_Reports_Failure_Not_Issues()
    {
        var path = Path.Combine(_directory, "demo.c.card.json");
        File.WriteAllText(path, "{ not json");

        var load = CardDataLoader.LoadFileLenient(path);

        Assert.False(load.IsLoaded);
        Assert.NotNull(load.Failure);
        Assert.Empty(load.Issues);
        Assert.Null(load.Definition);
    }

    [Fact]
    public void Strict_Entry_Still_Isolates_Card_With_Missing_Required_Field()
    {
        // 守护「不改既有严格入口的行为」：严格入口（LoadFiles）仍把缺 factionCost 的卡记为失败、不产出定义。
        var broken = Path.Combine(_directory, "broken.card.json");
        File.WriteAllText(broken, """
        {
          "schemaVersion": 1,
          "id": "broken",
          "name": "坏卡",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """);
        var ok = Path.Combine(_directory, "ok.card.json");
        File.WriteAllText(ok, Complete);

        var strict = CardDataLoader.LoadFiles(new[] { ok, broken });

        Assert.Single(strict.Definitions);
        Assert.Equal("demo.a", strict.Definitions[0].Id);
        var failure = Assert.Single(strict.Failures);
        Assert.Equal(broken, failure.File);
        Assert.Contains("卡定义非法", failure.Error, StringComparison.Ordinal);
    }
}
