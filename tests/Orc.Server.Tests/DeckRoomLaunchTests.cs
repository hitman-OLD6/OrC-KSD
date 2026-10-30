using System.Text.Json.Nodes;
using Orc.Game.Cards;
using Orc.Mcp.Contracts;
using Orc.Protocol;
using Orc.Server.Admin;
using Orc.TestSupport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S10 验收（R6 协议面／D27-D29）：房间座位声明与房间开局同样接受卡组标识，
/// 错误码与进程内一致（<c>InvalidArguments</c>／<c>DeckInvalid</c>），快照只回显声明形态。
/// </summary>
public sealed class DeckRoomLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-roomdeck-" + Guid.NewGuid().ToString("N"));
    private readonly DeckPackFixture _pack;

    public DeckRoomLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _pack = new DeckPackFixture(Path.Combine(_root, "pack"), "room.pack");
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
    public async Task 房间以卡组标识开局_且快照只回显标识()
    {
        await using var fixture = await HostFixture.StartAsync(
            deckRules: Orc.Game.Decks.DeckRuleTier.Strict,
            packs: [_pack.Root]);

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");

        var attachA = await first.AttachAsync(ControllerAttach(0, "starter"));
        var attachB = await second.AttachAsync(ControllerAttach(1, "pack/room.pack/starter"));
        Assert.True(attachA.Code == ResultCodes.Ok, attachA.Code + "：" + attachA.Message);
        Assert.True(attachB.Code == ResultCodes.Ok, attachB.Code + "：" + attachB.Message);

        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        // 座位自己的视图：给了标识就回标识、不回展开的卡 id 列表（D28）。
        var room = await first.CallAsync("room.info", new JsonObject { ["seat"] = 0 });
        Assert.True(room.Ok, $"{room.Code}: {room.Message}");
        var seats = room.Details!["seats"]!.AsArray();
        Assert.Equal("starter", seats[0]!["deckId"]!.GetValue<string>());
        Assert.Null(seats[0]!["deck"]);

        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task 房间侧配置不一致_错误码与进程内一致()
    {
        await using var fixture = await HostFixture.StartAsync(
            deckRules: Orc.Game.Decks.DeckRuleTier.Strict,
            packs: [_pack.Root]);

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");

        await first.AttachAsync(ControllerAttach(
            0,
            "starter",
            new DeckConfigPayload { MainFaction = Faction.Germany, AllyFaction = Faction.USA }));
        await second.AttachAsync(ControllerAttach(1, "starter"));

        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        var started = await fixture.AdminAsync(AdminOps.MatchStart);

        Assert.False(started.Ok);
        Assert.Equal(ResultCodes.DeckInvalid, started.Code);
        Assert.Contains("不一致", started.Message!, StringComparison.Ordinal);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AI座位带标识_用该卡组而不是自动构筑()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats:
            [
                new SeatConfig(),
                new SeatConfig(Kind: SeatKind.Ai, DeckId: "starter"),
            ],
            deckRules: Orc.Game.Decks.DeckRuleTier.Strict,
            packs: [_pack.Root]);

        var first = await fixture.ConnectAsync("seat0");
        await first.AttachAsync(ControllerAttach(0, "starter"));
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);

        var started = await fixture.AdminAsync(AdminOps.MatchStart);

        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        Assert.False(started.Payload!["fallback"]!.GetValue<bool>());

        await first.DisposeAsync();
    }

    private SessionAttachParams ControllerAttach(int seat, string deckId, DeckConfigPayload? config = null)
        => new()
        {
            Roles = [SessionRoles.Controller],
            Seat = seat,
            DisplayName = "seat" + seat,
            DeckId = deckId,
            DeckConfig = config,
            Packages = [new PackageRef("room.pack", "1.0.0", _pack.Guid.ToString())],
        };
}
