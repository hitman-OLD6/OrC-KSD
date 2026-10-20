using Orc.Game.Cards;
using Orc.Game.Decks;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S7 卡组规则：同名上限分档、主国/盟国配对。
/// （内容级校验走 `Orc.Mcp.Tests` 的建局路径，用真实卡包 id。）
/// </summary>
public class DeckRulesTests
{
    [Theory]
    [InlineData(Rarity.Standard, 4)]
    [InlineData(Rarity.Limited, 3)]
    [InlineData(Rarity.Special, 2)]
    [InlineData(Rarity.Elite, 1)]
    public void CopyLimit_FollowsRarity(string rarity, int expected)
        => Assert.Equal(expected, DeckRules.CopyLimit(rarity));

    [Fact]
    public void CopyLimit_UnknownRarity_FallsBackToStandard()
        => Assert.Equal(4, DeckRules.CopyLimit("mystery"));

    [Fact]
    public void ValidateFaction_NullConfiguration_Passes()
        => Assert.Null(DeckRules.ValidateFaction(null));

    [Fact]
    public void ValidateFaction_MissingMain_IsRejected()
    {
        var violation = DeckRules.ValidateFaction(new PlayerDeckConfiguration(null, Faction.Soviet));

        Assert.NotNull(violation);
        Assert.Equal("faction-main-missing", violation!.Kind);
    }

    [Fact]
    public void ValidateFaction_NonMajorMain_IsRejected()
    {
        var violation = DeckRules.ValidateFaction(new PlayerDeckConfiguration(Faction.France, null));

        Assert.NotNull(violation);
        Assert.Equal("faction-main-invalid", violation!.Kind);
    }

    [Fact]
    public void ValidateFaction_AllyEqualsMain_IsRejected()
    {
        var violation = DeckRules.ValidateFaction(new PlayerDeckConfiguration(Faction.Germany, Faction.Germany));

        Assert.NotNull(violation);
        Assert.Equal("faction-ally-invalid", violation!.Kind);
    }

    [Fact]
    public void ValidateFaction_NeutralAlly_IsRejected()
    {
        var violation = DeckRules.ValidateFaction(new PlayerDeckConfiguration(Faction.Germany, Faction.Neutral));

        Assert.NotNull(violation);
        Assert.Equal("faction-ally-invalid", violation!.Kind);
    }

    [Fact]
    public void ValidateFaction_ValidPairing_Passes()
        => Assert.Null(DeckRules.ValidateFaction(new PlayerDeckConfiguration(Faction.Germany, Faction.Italy)));

    [Fact]
    public void Validate_SizeMismatch_IsReported()
    {
        var violations = DeckRules.Validate(["a", "b"], new Dictionary<string, CardDefinition>(), requiredSize: 39);

        Assert.Contains(violations, v => v.Kind == "size");
        Assert.Contains(violations, v => v.Kind == "unknown-card");
    }
}
