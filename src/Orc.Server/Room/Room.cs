using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// 房间状态机（多人支持 S3）：维护座位与控制器的唯一真相，不持有任何引擎对象。
/// <para>
/// 状态：<see cref="RoomState.Lobby"/> →（全部座位就绪 + <c>room.start</c>）→ InMatch → Ended；
/// 对局态不可回到房间态。所有操作用实例锁串行（房间操作是轻量短临界区，
/// 与引擎无关；引擎侧的串行化由 `SerialWorkQueue` 负责）。
/// </para>
/// </summary>
public sealed class Room
{
    private readonly object _sync = new();
    private readonly SeatSlot?[] _seats;
    private readonly IDeckValidator? _deckValidator;
    private readonly IMatchLauncher? _launcher;
    private readonly Func<IReadOnlyList<RoomListenAddress>> _addresses;

    private RoomState _state = RoomState.Lobby;

    public Room(
        string roomId,
        int seatCount = 2,
        IDeckValidator? deckValidator = null,
        IMatchLauncher? launcher = null,
        Func<IReadOnlyList<RoomListenAddress>>? addresses = null,
        string packPolicy = "loaded",
        string? packFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        if (seatCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seatCount), seatCount, "座位数必须为正。");
        }

        RoomId = roomId;
        _seats = new SeatSlot?[seatCount];
        _deckValidator = deckValidator;
        _launcher = launcher;
        _addresses = addresses ?? (() => Array.Empty<RoomListenAddress>());
        PackPolicy = packPolicy;
        PackFingerprint = packFingerprint;
    }

    public string RoomId { get; }

    public string PackPolicy { get; }

    public string? PackFingerprint { get; }

    /// <summary>当前状态（诊断用；协议面请取 <see cref="Snapshot"/>）。</summary>
    public RoomState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>房间快照（纯 DTO）。</summary>
    public RoomSnapshot Snapshot()
    {
        lock (_sync)
        {
            var seats = new RoomSeatSnapshot[_seats.Length];
            for (var i = 0; i < _seats.Length; i++)
            {
                var slot = _seats[i];
                seats[i] = slot is null
                    ? new RoomSeatSnapshot(i, string.Empty, null, false, false)
                    : new RoomSeatSnapshot(i, slot.Kind, slot.DisplayName, true, slot.Connected);
            }

            return new RoomSnapshot(
                RoomId,
                StateName(_state),
                seats,
                PackPolicy,
                PackFingerprint,
                _addresses());
        }
    }

    /// <summary>声明座位（填充控制器）。</summary>
    public McpToolResult Claim(RoomSeatClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return Fail(McpCodes.RoomStarted, "房间已开局，不能再声明座位。");
            }

            if (!InRange(claim.Seat))
            {
                return Fail(McpCodes.InvalidArguments, $"座位号 {claim.Seat} 越界（0..{_seats.Length - 1}）。");
            }

            var existing = _seats[claim.Seat];
            if (existing is not null)
            {
                // 同令牌＝重连，幂等成功；否则视为占用冲突。
                if (claim.SeatToken == existing.SeatToken)
                {
                    existing.Connected = true;
                    return Success("已重新绑定座位。", SeatJson(claim.Seat, existing));
                }

                return Fail(McpCodes.SeatTaken, $"座位 {claim.Seat} 已被占用。");
            }

            if (claim.Deck.Count == 0)
            {
                return Fail(McpCodes.DeckInvalid, "卡组为空（卡组必须显式提供）。");
            }

            if (_deckValidator?.Validate(claim.Deck, claim.DeckConfig) is { } invalid)
            {
                return invalid;
            }

            var slot = new SeatSlot
            {
                Seat = claim.Seat,
                Kind = NormalizeKind(claim.ControllerKind),
                DisplayName = string.IsNullOrWhiteSpace(claim.DisplayName) ? $"座位{claim.Seat}" : claim.DisplayName,
                Deck = claim.Deck,
                DeckConfig = claim.DeckConfig,
                SeatToken = Guid.NewGuid().ToString("N"),
                Connected = true,
            };

            _seats[claim.Seat] = slot;
            return Success("座位已声明。", SeatJson(claim.Seat, slot));
        }
    }

    /// <summary>释放座位（房主或该座位持有者）。</summary>
    public McpToolResult Release(RoomSeatRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return Fail(McpCodes.RoomStarted, "房间已开局，不能释放座位。");
            }

            if (!InRange(release.Seat) || _seats[release.Seat] is not { } slot)
            {
                return Fail(McpCodes.InvalidArguments, $"座位 {release.Seat} 当前为空。");
            }

            if (!CanControl(slot, release.SeatToken))
            {
                return Fail(McpCodes.Unauthorized, "令牌无效（既不是房主，也不是该座位持有者）。");
            }

            _seats[release.Seat] = null;
            return Success("座位已释放。", new JsonObject { ["seat"] = release.Seat });
        }
    }

    /// <summary>生成 AI 控制器（缺省卡组由开局装配生成）。</summary>
    public McpToolResult AddAi(RoomAiSeat ai)
    {
        ArgumentNullException.ThrowIfNull(ai);

        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return Fail(McpCodes.RoomStarted, "房间已开局，不能再生成 AI。");
            }

            if (!InRange(ai.Seat))
            {
                return Fail(McpCodes.InvalidArguments, $"座位号 {ai.Seat} 越界（0..{_seats.Length - 1}）。");
            }

            if (_seats[ai.Seat] is not null)
            {
                return Fail(McpCodes.SeatTaken, $"座位 {ai.Seat} 已被占用。");
            }

            if (ai.Deck is { Count: > 0 } && _deckValidator?.Validate(ai.Deck, ai.DeckConfig) is { } invalid)
            {
                return invalid;
            }

            var slot = new SeatSlot
            {
                Seat = ai.Seat,
                Kind = ControllerKinds.Ai,
                DisplayName = string.IsNullOrWhiteSpace(ai.DisplayName) ? "AI" : ai.DisplayName,
                Deck = ai.Deck ?? Array.Empty<string>(),
                DeckConfig = ai.DeckConfig,
                SeatToken = Guid.NewGuid().ToString("N"),
                Connected = true,
            };

            _seats[ai.Seat] = slot;
            return Success("AI 控制器已生成。", SeatJson(ai.Seat, slot));
        }
    }

    /// <summary>开局（房主令牌；无房主座位时允许无令牌）。</summary>
    public async Task<McpToolResult> StartAsync(string? seatToken, CancellationToken ct = default)
    {
        RoomStartRequest request;
        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return Fail(McpCodes.RoomStarted, "房间已开局。");
            }

            if (Array.Exists(_seats, slot => slot is null))
            {
                return Fail(McpCodes.RoomNotReady, "仍有空座，无法开局。");
            }

            if (HostSlot() is { } host && host.SeatToken != seatToken)
            {
                return Fail(McpCodes.Unauthorized, "开局须由房主发起。");
            }

            if (_launcher is null)
            {
                return Fail(McpCodes.NotSupported, "宿主未提供对局装配面（无法开局）。");
            }

            _state = RoomState.Starting;
            request = new RoomStartRequest(
                RoomId,
                _seats.Select(slot => new RoomSeatStart(
                    slot!.Seat,
                    slot.DisplayName,
                    slot.Deck,
                    slot.DeckConfig,
                    slot.Kind)).ToArray(),
                PackFingerprint);
        }

        try
        {
            var result = await _launcher.StartAsync(request, ct).ConfigureAwait(false);

            lock (_sync)
            {
                _state = result.Ok ? RoomState.InMatch : RoomState.Lobby;
            }

            return result;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _state = RoomState.Lobby;
            }

            return Fail(McpCodes.Internal, $"开局失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>强制释放座位（房主）。</summary>
    public McpToolResult Kick(int seat, string? seatToken)
    {
        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return Fail(McpCodes.RoomStarted, "房间已开局，不能踢出座位。");
            }

            if (!InRange(seat) || _seats[seat] is null)
            {
                return Fail(McpCodes.InvalidArguments, $"座位 {seat} 当前为空。");
            }

            if (HostSlot() is { } host && host.SeatToken != seatToken)
            {
                return Fail(McpCodes.Unauthorized, "只有房主可以踢出座位。");
            }

            _seats[seat] = null;
            return Success("座位已释放。", new JsonObject { ["seat"] = seat });
        }
    }

    /// <summary>凭座位令牌重连（房间态与对局态均可）。</summary>
    public McpToolResult Resume(string seatToken)
    {
        lock (_sync)
        {
            for (var i = 0; i < _seats.Length; i++)
            {
                if (_seats[i] is { } slot && slot.SeatToken == seatToken)
                {
                    slot.Connected = true;
                    return Success("已重新绑定。", SeatJson(i, slot));
                }
            }

            return Fail(McpCodes.Unauthorized, "座位令牌无效。");
        }
    }

    /// <summary>标记座位断线（对局态保留座位；房间态由调用方决定是否释放）。</summary>
    public void MarkDisconnected(string seatToken)
    {
        lock (_sync)
        {
            for (var i = 0; i < _seats.Length; i++)
            {
                if (_seats[i] is { } slot && slot.SeatToken == seatToken)
                {
                    slot.Connected = false;
                    return;
                }
            }
        }
    }

    /// <summary>按座位标记断线（会话断开/心跳超时；不释放座位）。</summary>
    public void MarkDisconnected(int seat)
    {
        lock (_sync)
        {
            if (InRange(seat) && _seats[seat] is { } slot)
            {
                slot.Connected = false;
            }
        }
    }

    /// <summary>按座位标记恢复（重连成功）。</summary>
    public void MarkConnected(int seat)
    {
        lock (_sync)
        {
            if (InRange(seat) && _seats[seat] is { } slot)
            {
                slot.Connected = true;
            }
        }
    }

    /// <summary>直接清空座位（服务端内部用：房间态断线立即释放，无权限校验）。</summary>
    public void DropSeat(int seat)
    {
        lock (_sync)
        {
            if (InRange(seat) && _state == RoomState.Lobby)
            {
                _seats[seat] = null;
            }
        }
    }

    /// <summary>结束对局（终局后房间不可回到房间态）。</summary>
    public void MarkEnded()
    {
        lock (_sync)
        {
            _state = RoomState.Ended;
        }
    }

    /// <summary>取得某座位的控制器输入（开局装配用；不含令牌）。</summary>
    public RoomSeatStart? SeatAt(int seat)
    {
        lock (_sync)
        {
            return InRange(seat) && _seats[seat] is { } slot
                ? new RoomSeatStart(slot.Seat, slot.DisplayName, slot.Deck, slot.DeckConfig, slot.Kind)
                : null;
        }
    }

    private SeatSlot? HostSlot()
    {
        for (var i = 0; i < _seats.Length; i++)
        {
            if (_seats[i] is { Kind: ControllerKinds.Local } slot)
            {
                return slot;
            }
        }

        return null;
    }

    private bool CanControl(SeatSlot slot, string? token)
        => token is not null && (token == slot.SeatToken || HostSlot()?.SeatToken == token);

    private bool InRange(int seat) => seat >= 0 && seat < _seats.Length;

    private static string NormalizeKind(string? kind)
        => kind switch
        {
            ControllerKinds.Local => ControllerKinds.Local,
            ControllerKinds.Ai => ControllerKinds.Ai,
            _ => ControllerKinds.Remote,
        };

    private static string StateName(RoomState state) => state switch
    {
        RoomState.Lobby => RoomStates.Lobby,
        RoomState.InMatch or RoomState.Starting => RoomStates.InMatch,
        _ => RoomStates.Ended,
    };

    private static JsonObject SeatJson(int seat, SeatSlot slot) => new()
    {
        ["seat"] = seat,
        ["controllerKind"] = slot.Kind,
        ["displayName"] = slot.DisplayName,
        ["seatToken"] = slot.SeatToken,
    };

    private static McpToolResult Success(string message, JsonNode? details)
        => McpToolResult.Success(message, details: details);

    private static McpToolResult Fail(string code, string message)
        => McpToolResult.Failure(code, message);

    private sealed class SeatSlot
    {
        public required int Seat { get; init; }
        public required string Kind { get; init; }
        public required string DisplayName { get; init; }
        public required IReadOnlyList<string> Deck { get; init; }
        public RoomDeckConfig? DeckConfig { get; init; }
        public required string SeatToken { get; init; }
        public bool Connected { get; set; }
    }
}
