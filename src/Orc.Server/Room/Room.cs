using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Protocol;

namespace Orc.Server.Rooms;

/// <summary>
/// 房间状态机（R4/R5/R12）：**seat 属于房间**，房间是 seat 的容器；seat 数恒为 2（引擎 1v1）。
/// <para>
/// 一场对局对应一个房间；终局后房间保持 <see cref="RoomState.Ended"/> 不可回退，服务器经**房间轮换**
/// 产出下一个 lobby。跨房间不共享 seat（下一局必须重新入座、重新提供卡组）。
/// </para>
/// <para>
/// 房间**不持有任何引擎活对象**（<c>Match</c>/<c>Player</c>/<c>Card</c>）：它只有座位、声明与令牌。
/// 对局设施的归属走 <see cref="SeatBinding"/>（纪元 ＋ playerIndex）。
/// </para>
/// </summary>
public sealed class Room
{
    private readonly object _sync = new();
    private readonly SeatSlot?[] _seats;
    private readonly Func<IReadOnlyList<RoomListenAddress>> _addresses;

    private RoomState _state = RoomState.Lobby;

    public Room(
        string roomId,
        IReadOnlyList<SeatConfig> seatConfigs,
        Func<IReadOnlyList<RoomListenAddress>>? addresses = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNull(seatConfigs);
        if (seatConfigs.Count != 2)
        {
            throw new ArgumentException($"seat 数恒为 2（引擎 1v1）；收到 {seatConfigs.Count}。", nameof(seatConfigs));
        }

        RoomId = roomId;
        _seats = new SeatSlot?[seatConfigs.Count];
        _addresses = addresses ?? (() => Array.Empty<RoomListenAddress>());

        // AI 座位：**配置即视为已批准**（不需要连接、不需要管理员再批一次）。
        for (var seat = 0; seat < seatConfigs.Count; seat++)
        {
            var config = seatConfigs[seat];
            if (config.Kind != SeatKind.Ai)
            {
                continue;
            }

            _seats[seat] = new SeatSlot
            {
                Seat = seat,
                Kind = ControllerKinds.Ai,
                State = SeatState.Admitted,
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? "AI" : config.DisplayName,
                Deck = config.Deck ?? Array.Empty<string>(),
                DeckId = string.IsNullOrWhiteSpace(config.DeckId) ? null : config.DeckId.Trim(),
                DeckConfig = config.DeckConfig is { } deckConfig
                    ? new RoomDeckConfig(deckConfig.MainFaction, deckConfig.AllyFaction)
                    : null,
                Packages = config.Packages ?? Array.Empty<PackageRef>(),
                SeatToken = NewToken(),
                SessionId = null,
            };
        }
    }

    public string RoomId { get; }

    /// <summary>座位数（恒 2）。</summary>
    public int SeatCount => _seats.Length;

    /// <summary>座位状态迁移通知（宿主据此推 <c>notify/room.seats</c>；载荷不含卡组/卡包）。</summary>
    public event Action? SeatsChanged;

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

    /// <summary>是否全部座位已批准（开局前置之一）。</summary>
    public bool AllAdmitted
    {
        get
        {
            lock (_sync)
            {
                return Array.TrueForAll(_seats, slot => slot is { State: SeatState.Admitted or SeatState.Disconnected });
            }
        }
    }

    /// <summary>是否仍有空座（开局拒绝原因之一）。</summary>
    public bool HasEmptySeat
    {
        get
        {
            lock (_sync)
            {
                return Array.Exists(_seats, slot => slot is null || slot.State != SeatState.Admitted);
            }
        }
    }

    /// <summary>
    /// 房间快照（**按调用者角色构造**，R12）：自己的卡组/卡包只回给自己的座位；
    /// 调试面可见全量；对手的永远不可见（全知 viewer 亦不可）。
    /// </summary>
    public RoomSnapshot Snapshot(RoomViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RoomSeatSnapshot[] seats;
        string state;

        lock (_sync)
        {
            seats = new RoomSeatSnapshot[_seats.Length];
            for (var i = 0; i < _seats.Length; i++)
            {
                var slot = _seats[i];
                if (slot is null)
                {
                    seats[i] = new RoomSeatSnapshot(i, false, SeatStates.Idle, null);
                    continue;
                }

                var own = request.SeeAllDeclarations || request.Seat == i;
                // 声明形态回显（D28）：给了卡组标识就只回 DeckId，Deck 保持 null——不展开成 39 条卡 id。
                seats[i] = new RoomSeatSnapshot(
                    i,
                    true,
                    StateName(slot.State),
                    slot.DisplayName,
                    slot.Kind,
                    own && string.IsNullOrWhiteSpace(slot.DeckId) ? slot.Deck : null,
                    own ? ToPackageSnapshots(slot.Packages) : null)
                {
                    DeckId = own ? slot.DeckId : null,
                };
            }

            state = RoomStateName(_state);
        }

        return new RoomSnapshot(RoomId, state, _seats.Length, seats, _addresses());
    }

    /// <summary>申请座位（空闲 → 待许可）。**同步拒绝**已被占用的座位（不进 <c>Pending</c>）。</summary>
    public SeatApplyOutcome Apply(int seat, SeatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        bool changed;
        SeatApplyOutcome outcome;

        lock (_sync)
        {
            changed = false;
            if (_state != RoomState.Lobby)
            {
                return SeatApplyOutcome.Reject(McpCodes.RoomStarted, "房间已开局，不能再申请座位。");
            }

            if (!InRange(seat))
            {
                return SeatApplyOutcome.Reject(McpCodes.InvalidArguments, $"座位号 {seat} 越界（0..{_seats.Length - 1}）。");
            }

            if (_seats[seat] is { } existing)
            {
                // 同一连接重复申请＝幂等（把声明更新为最新一份）；其它连接＝占用冲突。
                if (!string.Equals(existing.SessionId, application.SessionId, StringComparison.Ordinal))
                {
                    return SeatApplyOutcome.Reject(McpCodes.SeatTaken, $"座位 {seat} 已被占用。");
                }

                existing.Deck = application.Deck;
                existing.DeckId = application.DeckId;
                existing.DeckConfig = ToDeckConfig(application.DeckConfig);
                existing.Packages = application.Packages;
                existing.DisplayName = DisplayName(application.DisplayName, seat);
                outcome = SeatApplyOutcome.Accepted;
            }
            else
            {
                _seats[seat] = new SeatSlot
                {
                    Seat = seat,
                    Kind = ControllerKinds.Remote,
                    State = SeatState.Pending,
                    DisplayName = DisplayName(application.DisplayName, seat),
                    Deck = application.Deck,
                    DeckId = application.DeckId,
                    DeckConfig = ToDeckConfig(application.DeckConfig),
                    Packages = application.Packages,
                    SeatToken = NewToken(),
                    SessionId = application.SessionId,
                };
                outcome = SeatApplyOutcome.Accepted;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }

        return outcome;
    }

    /// <summary>批准某个待许可座位（pending → admitted）；返回该座位的令牌（**批准那一刻签发**）。</summary>
    public string? Admit(int seat, string sessionId)
    {
        string? token = null;
        var changed = false;

        lock (_sync)
        {
            if (!InRange(seat) || _seats[seat] is not { } slot)
            {
                return null;
            }

            if (slot.State == SeatState.Admitted
                || slot.State == SeatState.Disconnected)
            {
                // 幂等：已批准时同连接再次批准返回同一令牌（令牌不因重复批准而变）。
                return string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal) ? slot.SeatToken : null;
            }

            if (slot.SessionId is { } owner && !string.Equals(owner, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            slot.SessionId = sessionId;
            slot.State = SeatState.Admitted;
            slot.SeatToken = NewToken();
            token = slot.SeatToken;
            changed = true;
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }

        return token;
    }

    /// <summary>拒绝申请（回到空闲；申请被拒后连接退回 <c>Connected</c> 改声明重申请）。</summary>
    public bool Release(int seat)
    {
        var changed = false;

        lock (_sync)
        {
            if (!InRange(seat) || _seats[seat] is not { } slot)
            {
                return false;
            }

            if (_state != RoomState.Lobby)
            {
                return false; // 对局态不允许释放座位（R11：局中不释放绑定）
            }

            slot.State = SeatState.Idle;
            _seats[seat] = null;
            changed = true;
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }

        return true;
    }

    /// <summary>撤销某座位的批准（→ revoked；对局态也允许，用于"管理员收回"）。</summary>
    public bool Revoke(int seat)
    {
        var changed = false;

        lock (_sync)
        {
            if (!InRange(seat) || _seats[seat] is not { } slot)
            {
                return false;
            }

            slot.State = SeatState.Revoked;
            slot.SessionId = null;
            slot.SeatToken = NewToken(); // 旧令牌立即失效
            changed = true;
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }

        return true;
    }

    /// <summary>标记座位断线（**保留绑定**＝对局态宽限的来源）。</summary>
    public void MarkDisconnected(int seat)
    {
        var changed = false;

        lock (_sync)
        {
            if (InRange(seat) && _seats[seat] is { State: SeatState.Admitted } slot)
            {
                slot.State = SeatState.Disconnected;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }
    }

    /// <summary>标记座位恢复（重连成功）。</summary>
    public void MarkConnected(int seat)
    {
        var changed = false;

        lock (_sync)
        {
            if (InRange(seat) && _seats[seat] is { State: SeatState.Disconnected } slot)
            {
                slot.State = SeatState.Admitted;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseSeatsChanged();
        }
    }

    /// <summary>房间进入对局态（开局成功）。</summary>
    public void MarkInMatch()
    {
        lock (_sync)
        {
            _state = RoomState.InMatch;
        }
    }

    /// <summary>开局失败：回到 lobby（进程存活、不留半装载态）。</summary>
    public void MarkLobby()
    {
        lock (_sync)
        {
            if (_state == RoomState.Starting)
            {
                _state = RoomState.Lobby;
            }
        }
    }

    /// <summary>标记房间进入开局中（防并发开局）。</summary>
    public bool TryBeginStart()
    {
        lock (_sync)
        {
            if (_state != RoomState.Lobby)
            {
                return false;
            }

            _state = RoomState.Starting;
            return true;
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

    /// <summary>取得某座位的开局输入（不含令牌）。</summary>
    public RoomSeatStart? SeatAt(int seat)
    {
        lock (_sync)
        {
            return InRange(seat) && _seats[seat] is { } slot
                ? new RoomSeatStart(
                    slot.Seat,
                    slot.DisplayName,
                    slot.Deck,
                    slot.DeckConfig,
                    slot.Kind,
                    slot.Packages,
                    slot.DeckId)
                : null;
        }
    }

    /// <summary>按令牌定位座位（重连用）；不存在＝false。</summary>
    public bool TryResolveToken(string token, out int seat)
    {
        seat = -1;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_sync)
        {
            for (var i = 0; i < _seats.Length; i++)
            {
                if (_seats[i] is { } slot
                    && string.Equals(slot.SeatToken, token, StringComparison.Ordinal)
                    && slot.State is SeatState.Admitted or SeatState.Disconnected)
                {
                    seat = i;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>座位是否被占用（诊断/守卫测试）。</summary>
    public bool IsOccupied(int seat)
    {
        lock (_sync)
        {
            return InRange(seat) && _seats[seat] is not null;
        }
    }

    /// <summary>该座位是否 AI（轮换重建与 AI 驱动判据）。</summary>
    public bool IsAiSeat(int seat)
    {
        lock (_sync)
        {
            return InRange(seat) && _seats[seat] is { Kind: ControllerKinds.Ai };
        }
    }

    /// <summary>座位的卡包声明（AI 卡组的并集单位元）。</summary>
    public IReadOnlyList<PackageRef> PackagesOf(int seat)
    {
        lock (_sync)
        {
            return InRange(seat) && _seats[seat] is { } slot ? slot.Packages : Array.Empty<PackageRef>();
        }
    }

    /// <summary>更新某座位的卡组（AI 开局生成后回写，供快照与日志）。</summary>
    public void SetDeck(int seat, IReadOnlyList<string> deck)
    {
        lock (_sync)
        {
            if (InRange(seat) && _seats[seat] is { } slot)
            {
                slot.Deck = deck;
            }
        }
    }

    private void RaiseSeatsChanged()
    {
        try
        {
            SeatsChanged?.Invoke();
        }
        catch (Exception)
        {
            // 订阅者故障不得影响房间状态机。
        }
    }

    private bool InRange(int seat) => seat >= 0 && seat < _seats.Length;

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static string DisplayName(string? name, int seat)
        => string.IsNullOrWhiteSpace(name) ? $"座位{seat}" : name;

    private static RoomDeckConfig? ToDeckConfig(DeckConfigPayload? config)
        => config is null ? null : new RoomDeckConfig(config.MainFaction, config.AllyFaction);

    private static IReadOnlyList<RoomPackageSnapshot> ToPackageSnapshots(IReadOnlyList<PackageRef> packages)
        => packages.Select(p => new RoomPackageSnapshot(p.PackageId, p.Version, p.Guid)).ToArray();

    private static string StateName(SeatState state) => state switch
    {
        SeatState.Pending => SeatStates.Pending,
        SeatState.Admitted => SeatStates.Admitted,
        SeatState.Disconnected => SeatStates.Disconnected,
        SeatState.Revoked => SeatStates.Revoked,
        _ => SeatStates.Idle,
    };

    private static string RoomStateName(RoomState state) => state switch
    {
        RoomState.Lobby => RoomStates.Lobby,
        RoomState.InMatch or RoomState.Starting => RoomStates.InMatch,
        _ => RoomStates.Ended,
    };

    private sealed class SeatSlot
    {
        public required int Seat { get; init; }
        public required string Kind { get; init; }
        public required string DisplayName { get; set; }
        public required IReadOnlyList<string> Deck { get; set; }
        public string? DeckId { get; set; }
        public RoomDeckConfig? DeckConfig { get; set; }
        public required IReadOnlyList<PackageRef> Packages { get; set; }
        public required string SeatToken { get; set; }
        public string? SessionId { get; set; }
        public SeatState State { get; set; }
    }
}
