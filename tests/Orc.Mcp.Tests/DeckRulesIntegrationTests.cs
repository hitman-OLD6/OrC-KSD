using Orc.Game;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S7 建局路径的卡组规则（真实卡包 + 服务端裁决）。
/// 示例卡包只有 5 张卡，无法组成 39 张合法构筑，故只覆盖拒绝路径。
/// </summary>
public class DeckRulesIntegrationTests
{
    [Fact]
    public async Task OverCopyLimit_IsRejected()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var deck = Enumerable.Repeat("ai.sample.infantry", 39).ToArray();

        var created = await fixture.Service.CreateMatchAsync(Launch(deck));

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Contains("同名上限", created.FailureMessage);
    }

    [Fact]
    public async Task UnknownCard_IsRejected()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var deck = Enumerable.Range(0, 39).Select(i => $"missing.{i}").ToArray();

        var created = await fixture.Service.CreateMatchAsync(Launch(deck));

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Contains("卡组解析失败", created.FailureMessage);
    }

    [Fact]
    public async Task MissingMainFaction_IsRejected()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var deck = Enumerable.Repeat("ai.sample.infantry", 39).ToArray();
        var options = new MatchLaunchOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SeatA = new MatchSeatConfig(0, deck, DeckConfiguration: new PlayerDeckConfiguration(null, null)),
            SeatB = new MatchSeatConfig(1, deck),
            EnforceDeckRules = true,
            Seed = 42,
        };

        var created = await fixture.Service.CreateMatchAsync(options);

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Contains("主国", created.FailureMessage);
    }

    private static MatchLaunchOptions Launch(IReadOnlyList<string> deck) => new()
    {
        PackDirectories = [SamplePackDirectory()],
        SeatA = new MatchSeatConfig(0, deck),
        SeatB = new MatchSeatConfig(1, deck),
        Seed = 42,
    };

    private static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }
}
