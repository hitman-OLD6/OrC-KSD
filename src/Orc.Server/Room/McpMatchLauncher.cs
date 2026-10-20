using Orc.Game;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Server.Rooms;

/// <summary>
/// 开局装配：把房间座位映射为 <see cref="MatchLaunchOptions"/> 并调用服务层建局入口。
/// <para>
/// 协议面**强制** <c>EnforceDeckRules = true</c>（卡组规则不可由远端关闭）。
/// AI 座位空卡组由注入的 <c>aiDeckFactory</c> 生成（S8 启动链路提供；未注入＝明确失败）。
/// </para>
/// </summary>
public sealed class McpMatchLauncher : IMatchLauncher
{
    private readonly Func<McpService> _service;
    private readonly IReadOnlyList<string> _packDirectories;
    private readonly Func<IReadOnlyList<string>>? _aiDeckFactory;
    private readonly bool _enforceDeckRules;

    public McpMatchLauncher(
        McpService service,
        IReadOnlyList<string> packDirectories,
        Func<IReadOnlyList<string>>? aiDeckFactory = null,
        bool enforceDeckRules = true)
        : this(() => service, packDirectories, aiDeckFactory, enforceDeckRules)
    {
        ArgumentNullException.ThrowIfNull(service);
    }

    /// <summary>延迟取服务实例（解决"房间 → 装配器 → 服务 → 房间"的构造环）。</summary>
    public McpMatchLauncher(
        Func<McpService> service,
        IReadOnlyList<string> packDirectories,
        Func<IReadOnlyList<string>>? aiDeckFactory = null,
        bool enforceDeckRules = true)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _packDirectories = packDirectories ?? throw new ArgumentNullException(nameof(packDirectories));
        _aiDeckFactory = aiDeckFactory;
        _enforceDeckRules = enforceDeckRules;
    }

    /// <inheritdoc />
    public async Task<McpToolResult> StartAsync(RoomStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var seats = request.Seats.OrderBy(seat => seat.Seat).ToArray();
        if (seats.Length != 2 || seats[0].Seat != 0 || seats[1].Seat != 1)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, "开局需要恰好两个座位（0 与 1）。");
        }

        var decks = new IReadOnlyList<string>[2];
        for (var i = 0; i < 2; i++)
        {
            var seat = seats[i];
            if (seat.Deck.Count > 0)
            {
                decks[i] = seat.Deck;
                continue;
            }

            if (string.Equals(seat.ControllerKind, Orc.Mcp.Room.ControllerKinds.Ai, StringComparison.Ordinal)
                && _aiDeckFactory is not null)
            {
                decks[i] = _aiDeckFactory();
                continue;
            }

            return McpToolResult.Failure(
                McpCodes.DeckInvalid,
                $"座位 {seat.Seat} 没有卡组（AI 卡组生成仅在服务端启动链路可用）。");
        }

        var options = new MatchLaunchOptions
        {
            PackDirectories = _packDirectories,
            SeatA = ToSeat(seats[0], decks[0]),
            SeatB = ToSeat(seats[1], decks[1]),
            // 生产＝true；端到端/示例卡包场景可关闭（与房间态校验开关同源）。
            EnforceDeckRules = _enforceDeckRules,
        };

        var outcome = await _service().CreateMatchAsync(options, ct).ConfigureAwait(false);
        return outcome.Status == ActionOutcome.StatusCompleted
            ? McpToolResult.Success("已开局。", details: outcome.Payload)
            : McpToolResult.Failure(
                outcome.FailureCode ?? McpCodes.EngineRejected,
                outcome.FailureMessage ?? "开局失败。");
    }

    private static MatchSeatConfig ToSeat(RoomSeatStart seat, IReadOnlyList<string> deck)
        => new(
            seat.Seat,
            deck,
            seat.DisplayName,
            seat.DeckConfig is { } config
                ? new PlayerDeckConfiguration(config.MainFaction, config.AllyFaction)
                : null);
}
