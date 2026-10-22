using Orc.Game;
using Orc.Mcp.Contracts;
using Orc.Mcp.Execution;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S4 投影面：整枚 <c>UiMatchView</c> + actions 同拍产出；只读执行面只在消费者上下文内可用。
/// </summary>
public class ProjectionTests
{
    [Fact]
    public async Task ObserveView_OutsideConsumerContext_IsRejected()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: null, aiSeats: [0]);
        await CreateMatchAsync(fixture);

        var result = await fixture.InvokeAsync("observe.view");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.EngineRejected, result.Code);
    }

    [Fact]
    public async Task ObserveView_ReturnsFullViewAndActions_InsideConsumerContext()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: null, aiSeats: [0]);
        await CreateMatchAsync(fixture);

        McpToolResult result;
        fixture.Service.Caller = new CallerView(Seat: 0);
        using (ConsumerContext.Enter())
        {
            result = await fixture.InvokeAsync("observe.view");
        }

        fixture.Service.Caller = CallerView.Anonymous;

        Assert.True(result.Ok, result.Message);

        var details = result.Details!;
        Assert.Equal("seat", details["perspective"]!["mode"]!.GetValue<string>());
        Assert.Equal(0, details["perspective"]!["seat"]!.GetValue<int>());

        // 整枚投影：手牌与战线数组都在
        var view = details["view"]!;
        Assert.NotNull(view["selfHand"]);
        Assert.NotNull(view["selfLine"]);
        Assert.NotNull(view["enemyLine"]);

        // 动作可用性：同条消息、可打出集合与手牌一致
        var actions = details["actions"]!;
        Assert.True(actions["handPlayabilityKnown"]!.GetValue<bool>());
        Assert.NotNull(actions["playableUids"]);
        Assert.NotNull(actions["canEndTurn"]);
        Assert.NotNull(actions["mulliganOpen"]);
    }

    private static async Task CreateMatchAsync(SandboxFixture fixture)
    {
        var deck = Enumerable.Repeat("ai.sample.infantry", 39).ToArray();
        var created = await fixture.Service.CreateMatchAsync(new MatchLaunchOptions
        {
            PackDirectories = [SamplePackDirectory()],
            SeatA = new MatchSeatConfig(0, deck),
            SeatB = new MatchSeatConfig(1, deck),
            Seed = 42,
            SkipMulligan = true,
            EnforceDeckRules = false,
        });

        Assert.Equal(ActionOutcome.StatusCompleted, created.Status);
    }

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
