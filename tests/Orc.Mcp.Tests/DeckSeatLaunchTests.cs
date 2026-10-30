using Orc.Game;
using Orc.Game.Cards;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.TestSupport;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S10 验收（R6／D25-D30）：开局按卡组名／ID。
/// 座位契约支持「卡组标识」三写法，且与手写列表互斥、空列表视为没给。
/// </summary>
public sealed class DeckSeatLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-seatdeck-" + Guid.NewGuid().ToString("N"));
    private readonly DeckPackFixture _pack;

    public DeckSeatLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _pack = new DeckPackFixture(Path.Combine(_root, "pack"), "seat.pack");
        for (var index = 0; index < 10; index++)
        {
            _pack.AddCard("c" + index);
        }

        _pack.AddDeck("starter", DeckPackFixture.Deck39(_pack.PackageId));
        _pack.Build();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task 座位只给卡组标识即可建局_构筑配置取自卡组()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = Assembly();

        var created = await fixture.Service.CreateMatchAsync(Options(
            assembly, new MatchSeatConfig(0, DeckId: "starter"), new MatchSeatConfig(1, DeckId: "pack/seat.pack/starter")));

        Assert.NotEqual(ActionOutcome.StatusFailed, created.Status);
    }

    [Fact]
    public async Task 卡组标识与手写列表互斥_报InvalidArguments()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = Assembly();
        var handWritten = DeckPackFixture.Deck39(_pack.PackageId)
            .SelectMany(card => Enumerable.Repeat(card.Id, card.Count))
            .ToArray();

        var created = await fixture.Service.CreateMatchAsync(Options(
            assembly,
            new MatchSeatConfig(0, Deck: handWritten, DeckId: "starter"),
            new MatchSeatConfig(1, DeckId: "starter")));

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Equal(McpCodes.InvalidArguments, created.FailureCode);
        Assert.Contains("互斥", created.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空数组等于没给_与卡组标识共存可放行()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = Assembly();

        var created = await fixture.Service.CreateMatchAsync(Options(
            assembly,
            new MatchSeatConfig(0, Deck: [], DeckId: "starter"),
            new MatchSeatConfig(1, DeckId: "starter")));

        Assert.NotEqual(ActionOutcome.StatusFailed, created.Status);
    }

    [Fact]
    public async Task 两源都空_报错且不触发包注册()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);

        // 目录故意给一个不存在的位置：若形状校验晚于装配，报错就会变成「卡包装载失败」（D30）。
        var options = new MatchLaunchOptions
        {
            PackDirectories = [Path.Combine(_root, "no-such-pack")],
            SeatA = new MatchSeatConfig(0),
            SeatB = new MatchSeatConfig(1, DeckId: "starter"),
            Seed = 42,
        };

        var created = await fixture.Service.CreateMatchAsync(options);

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Equal(McpCodes.InvalidArguments, created.FailureCode);
        Assert.Contains("未提供卡组", created.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 构筑配置与最终值一致则通过_不一致报DeckInvalid()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = Assembly();

        // 文件省略 allyFaction ⇒ 推导为 soviet；座位给 {germany, soviet} 与最终值一致。
        var matched = await fixture.Service.CreateMatchAsync(Options(
            assembly,
            new MatchSeatConfig(0, DeckId: "starter", DeckConfiguration: new PlayerDeckConfiguration(Faction.Germany, Faction.Soviet)),
            new MatchSeatConfig(1, DeckId: "starter")));
        Assert.NotEqual(ActionOutcome.StatusFailed, matched.Status);

        // 上一局已占用服务（一次一个活跃对局），换一个服务再验证不一致分支。
        using var second = new SandboxFixture(McpServiceMode.Player);
        var mismatched = await second.Service.CreateMatchAsync(Options(
            Assembly(),
            new MatchSeatConfig(0, DeckId: "starter", DeckConfiguration: new PlayerDeckConfiguration(Faction.Germany, Faction.USA)),
            new MatchSeatConfig(1, DeckId: "starter")));
        Assert.Equal(ActionOutcome.StatusFailed, mismatched.Status);
        Assert.Equal(McpCodes.DeckInvalid, mismatched.FailureCode);
        Assert.Contains("不一致", mismatched.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 按展示名命中_重名报歧义且列出候选()
    {
        // 同包再来一份同名卡组（局部 ID 不同、展示名相同）。
        var twin = new DeckPackFixture(Path.Combine(_root, "twin"), "twin.pack");
        for (var index = 0; index < 10; index++)
        {
            twin.AddCard("c" + index);
        }

        twin.AddDeck("first", DeckPackFixture.Deck39(twin.PackageId), name: "同名卡组");
        twin.AddDeck("second", DeckPackFixture.Deck39(twin.PackageId), name: "同名卡组");
        twin.Build();

        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = MatchCardAssembly.Register([_pack.Root, twin.Root], requiredDeckSize: 39);

        var created = await fixture.Service.CreateMatchAsync(Options(
            assembly, new MatchSeatConfig(0, DeckId: "同名卡组"), new MatchSeatConfig(1, DeckId: "starter")));

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Equal(McpCodes.DeckInvalid, created.FailureCode);
        Assert.Contains("twin.pack/first", created.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("twin.pack/second", created.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 卡组被lock裁剪后不可用_报DeckInvalid()
    {
        var trimmed = new DeckPackFixture(Path.Combine(_root, "trimmed"), "trim.pack");
        for (var index = 0; index < 10; index++)
        {
            // c0 的源文件在、但**不进 lock** ⇒ 卡组依赖解不出来，整份卡组被 planner 剔除。
            trimmed.AddCard("c" + index, lockIt: index != 0);
        }

        trimmed.AddDeck("starter", DeckPackFixture.Deck39(trimmed.PackageId));
        trimmed.Build();

        using var fixture = new SandboxFixture(McpServiceMode.Player);
        var assembly = MatchCardAssembly.Register([trimmed.Root], requiredDeckSize: 39);

        var created = await fixture.Service.CreateMatchAsync(Options(
            assembly, new MatchSeatConfig(0, DeckId: "starter"), new MatchSeatConfig(1, DeckId: "starter")));

        Assert.Equal(ActionOutcome.StatusFailed, created.Status);
        Assert.Equal(McpCodes.DeckInvalid, created.FailureCode);
    }

    private MatchCardAssembly Assembly() => MatchCardAssembly.Register([_pack.Root], requiredDeckSize: 39);

    private MatchLaunchOptions Options(MatchCardAssembly assembly, MatchSeatConfig seatA, MatchSeatConfig seatB) => new()
    {
        PackDirectories = [_pack.Root],
        Assembly = assembly,
        SeatA = seatA,
        SeatB = seatB,
        Seed = 42,
        SkipMulligan = true,
    };
}
