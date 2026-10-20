using Orc.Mcp;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Ai.Tests;

[Collection("Orc.Ai")]
public sealed class MatchLaunchTests
{
    private static McpService NewService()
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "orc-ai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);
        return new McpService(new McpServiceOptions
        {
            Mode = McpServiceMode.Player,
            SandboxRoot = sandboxRoot,
            Seed = 1,
        });
    }

    private static MatchLaunchOptions Launch(IReadOnlyList<string>? deckA = null, IReadOnlyList<string>? deckB = null, string? pack = null)
        => new()
        {
            PackDirectories = new[] { pack ?? AiTestHost.PackDirectory() },
            SeatA = new MatchSeatConfig(0, deckA ?? AiTestHost.SampleDeck()),
            SeatB = new MatchSeatConfig(1, deckB ?? AiTestHost.SampleDeck()),
            // 示例卡包只有 5 张卡，无法组成 39 张合法构筑；本测试验装配而非构筑规则。
            EnforceDeckRules = false,
        };

    [Fact]
    public async Task CreatesMatch_WithRealPackAndExplicitDecks()
    {
        var service = NewService();

        var outcome = await service.CreateMatchAsync(Launch());

        Assert.Equal(ActionOutcome.StatusCompleted, outcome.Status);
        Assert.NotNull(service.ActiveMatch);

        // 起手已抽取：卡组剩余 ＋ 手牌 ＝ 39。
        foreach (var player in service.ActiveMatch!.Players)
        {
            Assert.Equal(39, player.Deck.Count + player.Hand.Count);
        }

        service.DisposeMatch();
    }

    [Fact]
    public async Task RejectsDeckWithWrongSize()
    {
        var service = NewService();
        var shortDeck = AiTestHost.SampleDeck().Take(38).ToArray();

        var outcome = await service.CreateMatchAsync(Launch(deckA: shortDeck));

        Assert.Equal(ActionOutcome.StatusFailed, outcome.Status);
        Assert.Null(service.ActiveMatch);
    }

    [Fact]
    public async Task RejectsEmptyDeck()
    {
        var service = NewService();

        var outcome = await service.CreateMatchAsync(Launch(deckB: Array.Empty<string>()));

        Assert.Equal(ActionOutcome.StatusFailed, outcome.Status);
        Assert.Null(service.ActiveMatch);
    }

    [Fact]
    public async Task RejectsMissingPackDirectory()
    {
        var service = NewService();

        var outcome = await service.CreateMatchAsync(Launch(pack: Path.Combine(Path.GetTempPath(), "orc-ai-tests", "does-not-exist")));

        Assert.Equal(ActionOutcome.StatusFailed, outcome.Status);
        Assert.Null(service.ActiveMatch);
    }

    [Fact]
    public async Task RejectsUnknownCardId()
    {
        var service = NewService();
        var deck = AiTestHost.SampleDeck().Select((id, i) => i == 0 ? "no.such.card" : id).ToArray();

        var outcome = await service.CreateMatchAsync(Launch(deckB: deck));

        Assert.Equal(ActionOutcome.StatusFailed, outcome.Status);
        Assert.Null(service.ActiveMatch);
    }
}
