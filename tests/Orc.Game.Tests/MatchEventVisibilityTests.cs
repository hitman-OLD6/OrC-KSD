using System.Text;
using System.Text.Json;
using Orc.Game.Ui;
using Orc.Output;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 多人支持 S5：事件载荷可见性（对手手牌为唯一隐藏边界）。
/// </summary>
public class MatchEventVisibilityTests
{
    [Fact]
    public async Task OpponentOwnedEntry_IsHidden_ForViewer()
    {
        var match = await CreateMatchAsync();
        var visibility = new MatchEventVisibility(match);
        var viewer = match.Players[0];
        var opponent = match.Players[1];

        var opponentDraw = new Dictionary<string, object?>
        {
            ["player"] = opponent,
            ["card"] = opponent.Hand[0],
        };

        var ownDraw = new Dictionary<string, object?>
        {
            ["player"] = viewer,
            ["card"] = viewer.Hand[0],
        };

        Assert.True(visibility.IsEntryHidden(opponentDraw, viewer));
        Assert.False(visibility.IsEntryHidden(ownDraw, viewer));
    }

    [Fact]
    public async Task OpponentHand_IsInvisible_WhileOwnHandAndBoardAreVisible()
    {
        var match = await CreateMatchAsync();
        var visibility = new MatchEventVisibility(match);
        var viewer = match.Players[0];

        Assert.False(visibility.IsEntityVisible(match.Players[1].Hand[0], viewer));
        Assert.True(visibility.IsEntityVisible(viewer.Hand[0], viewer));
        Assert.True(visibility.IsEntityVisible(viewer.Hq, viewer));
    }

    [Fact]
    public async Task JsonExport_WritesHiddenMarker_WithoutNameOrReference()
    {
        var match = await CreateMatchAsync();
        var visibility = new MatchEventVisibility(match);
        var viewer = match.Players[0];
        var hiddenCard = match.Players[1].Hand[0];

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonValueWriter.Write(writer, hiddenCard, visibility, viewer, hideEntities: false);
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Contains("$hidden", json);
        Assert.DoesNotContain(hiddenCard.Name, json);
    }

    [Fact]
    public async Task JsonExport_WritesReferenceKey_ForVisibleEntity()
    {
        var match = await CreateMatchAsync();
        var visibility = new MatchEventVisibility(match);
        var viewer = match.Players[0];
        var visibleCard = viewer.Hand[0];

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonValueWriter.Write(writer, visibleCard, visibility, viewer, hideEntities: false);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;

        Assert.Equal(visibleCard.Id.ToString("N"), root.GetProperty("$ref").GetString());
        Assert.Equal(visibleCard.Name, root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("$alive").GetBoolean());
    }

    private static async Task<Match> CreateMatchAsync()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        return match;
    }
}
