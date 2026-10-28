using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Protocol;
using Orc.Mcp.Room;
using Orc.Mcp.Tools;
using Orc.Server.Admin;
using Orc.Server.Execution;
using Orc.Server.Pool;
using Orc.Server.Rooms;

namespace Orc.Server.Transport;

/// <summary>连接的角色态（R6/R7；**一个连接对象 ＋ 一个角色状态机**，不拆成多种连接类型）。</summary>
public enum SessionState
{
    /// <summary>已握手、未申请角色：只有 initialize/ping/session.attach/room.info。</summary>
    Connected,

    /// <summary>已申请、未获批准：只能 ping/session.detach/room.info，且**零推送**。</summary>
    Pending,

    /// <summary>玩家面（绑 seat 的 controller；若同时持 viewer 角色，其 viewer 恒脱敏）。</summary>
    Admitted,

    /// <summary>绑 seat 的 viewer（脱敏）。</summary>
    Viewer,

    /// <summary>不绑 seat 的 viewer（全知）。</summary>
    Omniscient,

    /// <summary>调试面（不绑 seat 的 controller；跨房间长存）。</summary>
    DebugFace,

    /// <summary>许可被撤销：无任何能力，可退回 <see cref="Connected"/> 重新申请。</summary>
    Revoked,
}

/// <summary>
/// 服务端宿主（S5）：TCP 监听 ＋ NDJSON 帧 ＋ JSON-RPC 分发 ＋ 串行执行队列 ＋ 房间轮换 ＋ 推送。
/// <para>
/// **生命周期归属**（U7）：<see cref="SerialWorkQueue"/> 与宿主本身＝进程级（长存）；
/// <see cref="McpService"/> 与 <see cref="Room"/>＝房间级（轮换时销毁重建）。
/// 因此 `_service`/`_room` 是**可换引用**，而队列与连接表跨房间不变。
/// </para>
/// <para>
/// 推送按**角色态**分发（不是"有没有 seat"）：绑座 viewer 收单视角帧，全知 viewer 收两份视角打包帧，
/// <c>Pending</c>/<c>Connected</c>/调试面不收对局数据。
/// </para>
/// </summary>
public sealed partial class ServerHost : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly SerialWorkQueue _queue;
    private readonly bool _ownsQueue;
    private readonly object _stackSync = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Task> _aiDrivers = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _disconnectedSeats = new();
    private readonly ConcurrentDictionary<int, byte> _graceInFlight = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly List<TcpClient> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();

    private McpService _service = null!;
    private Room _room = null!;
    private int _epoch;
    private CancellationTokenSource? _aiCts;
    private bool _matchEnded;
    private volatile bool _draining;
    private int _rotationScheduled;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _heartbeatLoop;

    public ServerHost(ServerOptions options, SerialWorkQueue? queue = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _queue = queue ?? new SerialWorkQueue();
        _ownsQueue = queue is null;

        // 已注册包集合＝**进程级**（跨房间轮换存活；R1）。事件出口接本宿主的通道事件。
        Packs = new PackPool(options, _queue, () => IsDraining, PublishEvent);
        Admin = new AdminApi(this);
    }

    /// <summary>管理面（**不进工具表**；进程内 API 与父子私有通道共用同一真身）。</summary>
    public AdminApi Admin { get; }

    /// <summary>
    /// 已注册包集合（进程级；R1）。管理面 op、工具面与开局装配链共用**同一个**实例。
    /// </summary>
    public PackPool Packs { get; }

    /// <summary>通道事件（轮换结果、对局终局、seat 绑定变化）——由父子私有通道转发给管理器。</summary>
    public event Action<JsonObject>? ChannelEvent;

    /// <summary>实际监听端口（<see cref="ServerOptions.Port"/> 为 0 时由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>当前已连接会话数（诊断/测试）。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>当前房间的服务层门面（轮换后指向新栈）。</summary>
    public McpService Service
    {
        get
        {
            lock (_stackSync)
            {
                return _service;
            }
        }
    }

    /// <summary>当前房间（轮换后指向新房间；旧房间保持 <c>Ended</c>）。</summary>
    public Room Room => Volatile.Read(ref _room);

    /// <summary>串行执行队列（进程级；唯一写者语义不变）。</summary>
    public SerialWorkQueue Queue => _queue;

    /// <summary>宿主关闭令牌（入队工作项用）。</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>当前房间栈纪元（S8：轮换时与引用交换**同一个动作**里递增）。</summary>
    public int CurrentEpoch => Volatile.Read(ref _epoch);

    /// <summary>是否处于轮换 drain 窗口（期间拒绝新入座与新入队）。</summary>
    public bool IsDraining => _draining;

    /// <summary>某座位当前是否有 controller 绑定（诊断/测试）。</summary>
    public bool HasBoundSession(int seat)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Seat == seat && session.IsController)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- 启动 / 房间栈

    /// <summary>建第一个 lobby 房间并与监听一同启动（boot 段**不装任何卡池**）。</summary>
    public Task<ServerHost> StartAsync(CancellationToken ct = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("服务端已启动。");
        }

        InstallInitialStack();

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.FromResult(this);
    }

    private void InstallInitialStack()
    {
        var room = CreateRoom();
        var service = CreateService(room);
        lock (_stackSync)
        {
            _room = room;
            _service = service;
            _epoch = 0;
        }

        AttachService(service, room);
    }

    /// <summary>建一个 lobby 房间（seat 由 spawn 配置决定形态；AI 座位配置即已批准）。</summary>
    private Room CreateRoom()
    {
        var room = new Room(
            Guid.NewGuid().ToString("N")[..8],
            _options.Seats,
            () => LocalAddresses.Collect(Port));
        return room;
    }

    private McpService CreateService(Room room) => new(
        new McpServiceOptions
        {
            Mode = _options.Mode == ServerMode.Sandbox ? McpServiceMode.Authoring : McpServiceMode.Player,
            SandboxRoot = _options.SandboxRoot,
            MatchId = room.RoomId,
            TargetSelectionTimeout = _options.TargetSelectionTimeout,
            Seed = _options.Seed,
            AiSeats = _options.Seats
                .Select((seat, index) => (seat, index))
                .Where(pair => pair.seat.Kind == SeatKind.Ai)
                .Select(pair => pair.index)
                .ToArray(),
            AllowDebugWrites = _options.Mode == ServerMode.Sandbox && _options.AllowDebugWrites,
        },
        new RoomControl(room),
        Packs);

    private void AttachService(McpService service, Room room)
    {
        service.SetSegmentConsumer(Orc.Mcp.Hosting.SegmentConsumer.Host);
        room.SeatsChanged += OnSeatsChanged;
        lock (_subscriptions)
        {
            _subscriptions.Add(service.SubscribeTargetingRequests(OnTargetingRequest));
        }

        service.Bridge.PendingExpired += OnTargetingExpired;
        service.MatchChanged += OnMatchChanged;
        service.Executor.Settled += OnActionSettled;
    }

    private void DetachService(McpService service, Room room)
    {
        room.SeatsChanged -= OnSeatsChanged;
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        service.Bridge.PendingExpired -= OnTargetingExpired;
        service.MatchChanged -= OnMatchChanged;
        service.Executor.Settled -= OnActionSettled;
    }

    // ---------------------------------------------------------------- 接受连接

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            lock (_clients)
            {
                _clients.Add(client);
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        ClientSession? session = null;

        try
        {
            var stream = client.GetStream();
            await using var reader = new NdjsonReader(stream, maxLineLength: 4 << 20);
            await using var writer = new NdjsonWriter(stream);

            session = new ClientSession(sessionId, client, writer, ct);
            _sessions[sessionId] = session;
            while (!ct.IsCancellationRequested)
            {
                JsonNode? node;
                try
                {
                    node = await reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    await session.SendResponseAsync(Serialize(RpcResponse.Failure(null, RpcErrorCodes.InvalidRequest, ex.Message)))
                        .ConfigureAwait(false);
                    return;
                }
                catch (JsonException)
                {
                    await session.SendResponseAsync(Serialize(RpcResponse.Failure(null, RpcErrorCodes.ParseError, "报文不是合法 JSON。")))
                        .ConfigureAwait(false);
                    continue;
                }

                if (node is null)
                {
                    return;
                }

                // 心跳判据＝**无任何入站帧**（含 ping）：原"无请求"判据会把长期停在 Pending 的连接误判为死连接。
                session.Touch();

                RpcResponse? response;
                try
                {
                    response = await DispatchAsync(node, session, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    response = RpcResponse.Failure(null, RpcErrorCodes.InternalError, ex.Message);
                }

                if (response is not null)
                {
                    // 响应**不走**推送队列：队列满时会丢最旧项，丢响应会让客户端永久挂起。
                    await session.SendResponseAsync(Serialize(response)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭中。
        }
        catch (IOException)
        {
            // 对端断开。
        }
        catch (SocketException)
        {
            // 连接重置（RST）：与"对端断开"同义。
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.Id, out _);
                OnSessionClosed(session);
                await session.DisposeAsync().ConfigureAwait(false);
            }

            lock (_clients)
            {
                _clients.Remove(client);
            }

            client.Dispose();
        }
    }

    // ---------------------------------------------------------------- 分发

    private async Task<RpcResponse?> DispatchAsync(JsonNode node, ClientSession session, CancellationToken ct)
    {
        RpcRequest? request;
        try
        {
            request = node.Deserialize<RpcRequest>(ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return RpcResponse.Failure(null, RpcErrorCodes.InvalidRequest, "不是合法的 JSON-RPC 请求对象。");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
        {
            return RpcResponse.Failure(request?.Id, RpcErrorCodes.InvalidRequest, "缺少 method。");
        }

        return request.Method switch
        {
            ProtocolMethods.Initialize => HandleInitialize(request, session),
            ProtocolMethods.Ping => RpcResponse.Success(request.Id, new JsonObject { ["pong"] = true }),
            ProtocolMethods.SessionAttach => await HandleAttachAsync(request, session, ct).ConfigureAwait(false),
            ProtocolMethods.SessionDetach => HandleDetach(request, session),
            ProtocolMethods.ToolsList => HandleToolsList(request, session),
            ProtocolMethods.ToolsCall => await HandleToolsCallAsync(request, session, ct).ConfigureAwait(false),
            "cosmetics.get" or "cosmetics.set" or "cosmetics.asset" or "cosmetics.image" or "cosmetics.emote" => HandleCosmetics(request, session),
            _ => RpcResponse.Failure(request.Id, RpcErrorCodes.MethodNotFound, $"未知方法 '{request.Method}'。"),
        };
    }

    private RpcResponse HandleInitialize(RpcRequest request, ClientSession session)
    {
        InitializeParams? parameters = null;
        try
        {
            parameters = request.Params?.Deserialize<InitializeParams>(ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidParams, "initialize 参数非法。");
        }

        if (parameters?.ProtocolVersion is { Length: > 0 } version
            && !string.Equals(version, ProtocolInfo.ProtocolVersion, StringComparison.Ordinal))
        {
            return RpcResponse.Failure(
                request.Id,
                RpcErrorCodes.InvalidParams,
                $"线协议版本不匹配：客户端 {version}，服务端 {ProtocolInfo.ProtocolVersion}。");
        }

        session.Label = parameters?.Label;

        var result = new InitializeResult
        {
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
            SchemaVersion = Service.Options.ContractSchemaVersion,
            Capabilities = ["push", "targeting", "session", "admin-channel", "cosmetics-v1"],
            ConnectionId = session.Id,
            Label = session.Label,
            RoomId = Room.RoomId,
        };

        return RpcResponse.Success(request.Id, Serialize(result));
    }

    /// <summary>
    /// 工具清单（按角色过滤）：可见性＝"连接角色集合 ⊇ 工具要求"（与门禁共用同一判据）。
    /// </summary>
    private RpcResponse HandleToolsList(RpcRequest request, ClientSession session)
    {
        var tools = Service.Tools
            .Where(tool => string.Equals(tool.Name, "room.info", StringComparison.Ordinal)
                || session.Allows(tool.RequiredRole))
            .Select(tool => new ToolDescriptorPayload(tool.Name, tool.Group, tool.Description, tool.InputSchema))
            .ToArray();

        return RpcResponse.Success(request.Id, Serialize(tools));
    }

    private async Task<RpcResponse> HandleAttachAsync(RpcRequest request, ClientSession session, CancellationToken ct)
    {
        SessionAttachParams? parameters;
        try
        {
            parameters = new SessionAttachParams
            {
                Roles = ReadStringList(request.Params?["roles"]),
                Seat = request.Params?["seat"]?.GetValue<int>(),
                DisplayName = request.Params?["displayName"]?.GetValue<string>(),
                Deck = request.Params?["deck"] is JsonArray deck ? ReadStringList(deck) : null,
                DeckConfig = request.Params?["deckConfig"]?.Deserialize<DeckConfigPayload>(ProtocolJson.Options),
                Packages = ReadPackages(request.Params?["packages"]),
                Token = request.Params?["token"]?.GetValue<string>(),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidParams, "session.attach 参数非法。");
        }

        var reply = await AttachAsync(session, parameters).ConfigureAwait(false);
        return RpcResponse.Success(request.Id, Serialize(reply));
    }

    /// <summary>
    /// 申请角色（**申请—批准两段**）：调试面申请即生效（判据仍是回环 ＋ 未绑座）；
    /// 其余落 <c>Pending</c> 且**立即返回**（不为等批准挂起）。
    /// </summary>
    private async Task<SessionAttachResult> AttachAsync(ClientSession session, SessionAttachParams request)
    {
        var roles = request.Roles
            .Where(role => role is SessionRoles.Controller or SessionRoles.Viewer or SessionRoles.Debug)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (roles.Length == 0)
        {
            return AttachFailed(session, "session.attach 需要至少一个角色：controller / viewer / debug。");
        }

        var wantsController = roles.Contains(SessionRoles.Controller);
        var wantsViewer = roles.Contains(SessionRoles.Viewer);
        var wantsDebug = roles.Contains(SessionRoles.Debug);

        if (wantsDebug && (wantsController || wantsViewer))
        {
            return AttachFailed(session, "debug 不能与 controller/viewer 同时申请（调试面与玩家面互斥）。");
        }

        // 重连令牌：凭令牌恢复原角色与 seat 绑定（跳过 Pending）。
        if (request.Token is { Length: > 0 } token)
        {
            if (Room.TryResolveToken(token, out var tokenSeat))
            {
                session.Seat = tokenSeat;
                session.SeatToken = token;
                session.Binding = new SeatBinding(CurrentEpoch, tokenSeat, tokenSeat);
                // 令牌只签发给"占 seat 的 controller"；未显式声明 viewer 即按 controller 恢复。
                session.IsController = wantsController || !wantsViewer;
                session.IsViewer = wantsViewer;
                session.State = session.IsController ? SessionState.Admitted : SessionState.Viewer;
                session.Request = null;
                Room.MarkConnected(tokenSeat);
                EmitSeatEvent(tokenSeat, "admitted");
                PushToSession(session);
                return AttachSucceeded(session, "已凭令牌恢复绑定。");
            }

            return AttachFailed(session, "座位令牌无效或已随房间终结失效。", ResultCodes.InvalidToken);
        }

        if (wantsDebug)
        {
            if (!SeatScopePolicy.IsTrustedLocal(session.RemoteAddress))
            {
                return AttachFailed(session, "调试面只接受回环来源的编排连接（当前来源不是回环）。");
            }

            session.IsDebug = true;
            session.State = SessionState.DebugFace;
            session.Request = null;
            return AttachSucceeded(session, "调试面已生效（不占座位、跨房间长存）。");
        }

        // 玩家面/观察面：座位号与声明的合法性在**申请时**就地拒绝（可读原因），批准时再复核一次。
        if (wantsController && request.Seat is null)
        {
            return AttachFailed(session, "controller 必须绑定座位（seat=0/1）：引擎输入端按 seat 转发。");
        }

        if (request.Seat is { } seat && seat is not (0 or 1))
        {
            return AttachFailed(session, $"座位号 {seat} 越界（0/1）。");
        }

        if (wantsController && (request.Deck is null || request.Deck.Count == 0))
        {
            return AttachFailed(session, "controller 申请必须同时提交卡组（deck）：批准时必须能看到要打什么牌。");
        }

        if (wantsController && request.Packages is null)
        {
            return AttachFailed(session, "controller 申请必须同时提交卡包声明（packages）：它与卡组共同决定这一局能不能开。");
        }

        var deck = request.Deck ?? Array.Empty<string>();
        var packages = request.Packages ?? Array.Empty<PackageRef>();

        // 只有"要坐 controller 位"的申请才占座位；纯 viewer 不占（一个 seat 可挂 0..N 个 viewer）。
        if (wantsController && request.Seat is { } controllerSeat)
        {
            var roomState = Room.State;
            if (roomState != RoomState.Lobby && Room.IsOccupied(controllerSeat))
            {
                return AttachFailed(session, $"座位 {controllerSeat} 已被占用（局中不允许换人：只能原 controller 凭令牌重连）。", ResultCodes.SeatTaken);
            }

            var applied = Room.Apply(
                controllerSeat,
                new SeatApplication(session.Id, request.DisplayName, deck, request.DeckConfig, packages));
            if (!applied.Ok)
            {
                return AttachFailed(session, applied.Message ?? "座位申请被拒绝。", applied.Code);
            }

            EmitSeatEvent(controllerSeat, SeatStates.Pending);
        }

        session.IsController = wantsController;
        session.IsViewer = wantsViewer;
        session.Seat = request.Seat;
        session.SeatToken = null;
        session.Binding = null;
        session.Request = new PendingAttach(roles, request.Seat, request.DisplayName, deck, request.DeckConfig, packages);
        session.State = SessionState.Pending;

        // 通知管理器有新的待许可连接（事件，不是请求-应答）。
        EmitEvent("conn.pending", new JsonObject
        {
            ["connectionId"] = session.Id,
            ["roles"] = new JsonArray(roles.Select(role => (JsonNode)JsonValue.Create(role)!).ToArray()),
            ["seat"] = request.Seat,
            ["label"] = session.Label,
        });

        await Task.CompletedTask.ConfigureAwait(false);
        return AttachSucceeded(session, "申请已提交，等待管理员批准。");
    }

    private static SessionAttachResult AttachFailed(ClientSession session, string message, string code = ResultCodes.InvalidArguments)
        => new()
        {
            ConnectionId = session.Id,
            State = SessionStateNames.Of(session.State),
            Code = code,
            Message = message,
        };

    private static SessionAttachResult AttachSucceeded(ClientSession session, string message)
        => new()
        {
            ConnectionId = session.Id,
            State = SessionStateNames.Of(session.State),
            Code = ResultCodes.Ok,
            Seat = session.Seat,
            Roles = session.GrantedRoles(),
            Message = message,
        };

    private RpcResponse HandleDetach(RpcRequest request, ClientSession session)
    {
        if (Room.State == RoomState.InMatch
            && session.IsController
            && session.Seat is { } matchSeat)
        {
            // 对局态 detach＝意外断线的等价语义：**保留绑定**并起宽限（R11），不释放座位。
            session.Binding = null;
            Room.MarkDisconnected(matchSeat);
            EmitSeatEvent(matchSeat, SeatStates.Disconnected);
            if (Service.Executor.Match is { State: Orc.Game.MatchState.InProgress })
            {
                _disconnectedSeats[matchSeat] = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            ReleaseSession(session, notify: true);
        }

        return RpcResponse.Success(request.Id, Serialize(new JsonObject
        {
            ["connectionId"] = session.Id,
            ["state"] = SessionStateNames.Of(session.State),
        }));
    }

    // ---------------------------------------------------------------- 管理面协作（AdminApi 调用）

    /// <summary>按连接标识取会话（不存在＝null）。</summary>
    internal ClientSession? FindSession(string connectionId)
        => _sessions.TryGetValue(connectionId, out var session) ? session : null;

    /// <summary>全部会话快照（<c>connections.list</c>；纯读）。</summary>
    internal IReadOnlyList<JsonObject> ListConnections()
        => _sessions.Values
            .OrderBy(session => session.CreatedAtTicks)
            .Select(session => new JsonObject
            {
                ["connectionId"] = session.Id,
                ["label"] = session.Label,
                ["state"] = SessionStateNames.Of(session.State),
                ["roles"] = new JsonArray(session.GrantedRoles().Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["seat"] = session.Seat,
                ["remote"] = session.RemoteAddress?.ToString(),
                ["ageMs"] = (long)(DateTimeOffset.UtcNow - session.CreatedAt).TotalMilliseconds,
                ["hasDeclaration"] = session.Request is not null,
                ["deckSize"] = session.Request?.Deck.Count,
                ["packageCount"] = session.Request?.Packages.Count,
            })
            .ToArray();

    /// <summary>批准一个连接（许可 ＋ 绑定；令牌经**该连接自己的**批准响应下发）。</summary>
    internal AdminOutcome Admit(string connectionId, int? seat, IReadOnlyList<string>? roles)
    {
        if (FindSession(connectionId) is not { } session)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, $"连接 '{connectionId}' 不存在。");
        }

        if (session.State is SessionState.DebugFace)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, "该连接已是调试面（不需要座位批准）。");
        }

        if (session.Request is not { } request)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, "该连接没有待许可的申请（请先 session.attach）。");
        }

        var granted = (roles ?? request.Roles).ToArray();
        var wantsController = granted.Contains(SessionRoles.Controller);
        var wantsViewer = granted.Contains(SessionRoles.Viewer);
        var targetSeat = seat ?? request.Seat;

        if (wantsController && targetSeat is null)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, "controller 批准必须指定座位（seat=0/1）。");
        }

        if (wantsController && request.Deck.Count == 0)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, "该连接未提交卡组（deck）：不能批准为 controller。");
        }

        if (wantsController && targetSeat is { } controllerSeat)
        {
            var token = Room.Admit(controllerSeat, session.Id);
            if (token is null)
            {
                return AdminOutcome.Fail(ResultCodes.SeatTaken, $"座位 {controllerSeat} 已被占用或不再等待批准。");
            }

            session.SeatToken = token;
        }

        session.IsController = wantsController;
        session.IsViewer = wantsViewer;
        session.Seat = targetSeat;
        session.Request = null;

        if (targetSeat is { } bound)
        {
            session.Binding = new SeatBinding(CurrentEpoch, bound, bound);
        }

        session.State = wantsController
            ? SessionState.Admitted
            : targetSeat is not null ? SessionState.Viewer : SessionState.Omniscient;

        PushAdmission(session, "admitted", null);
        if (targetSeat is { } changedSeat)
        {
            EmitSeatEvent(changedSeat, SeatStates.Admitted);
        }

        // 绑定后重推 pending 与初始视图（它要读引擎状态，故本次批准在队列内执行）。
        PushToSession(session);

        var payload = new JsonObject
        {
            ["connectionId"] = connectionId,
            ["state"] = SessionStateNames.Of(session.State),
            ["seat"] = session.Seat,
            ["roles"] = new JsonArray(session.GrantedRoles().Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        };
        if (session.SeatToken is { } issued)
        {
            payload["seatToken"] = issued;
        }

        return AdminOutcome.Success(payload);
    }

    /// <summary>拒绝申请（回 <c>Connected</c>，让连接改声明重申请；不留在 Pending 挂 rejected 标记）。</summary>
    internal AdminOutcome Reject(string connectionId, string? reason)
    {
        if (FindSession(connectionId) is not { } session)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, $"连接 '{connectionId}' 不存在。");
        }

        var seat = session.Request?.Seat ?? session.Seat;
        if (seat is { } released && session.IsController)
        {
            Room.Release(released);
            EmitSeatEvent(released, SeatStates.Idle);
        }

        session.Request = null;
        session.IsController = false;
        session.IsViewer = false;
        session.Seat = null;
        session.Binding = null;
        session.SeatToken = null;
        session.State = SessionState.Connected;

        PushAdmission(session, "rejected", reason);
        return AdminOutcome.Success(new JsonObject { ["connectionId"] = connectionId, ["state"] = SessionStateNames.Of(session.State) });
    }

    /// <summary>
    /// 撤销许可（**立即生效、不给宽限**）：撤销 controller 在对局中＝该 seat 认输并结束对局；
    /// 房间态＝直接释放绑定；撤销 viewer 只断开该 viewer，对局不受影响。
    /// </summary>
    internal async Task<AdminOutcome> RevokeAsync(string connectionId, string? reason, CancellationToken ct)
    {
        if (FindSession(connectionId) is not { } session)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, $"连接 '{connectionId}' 不存在。");
        }

        var seat = session.Seat;
        var wasController = session.IsController;

        session.State = SessionState.Revoked;
        session.IsController = false;
        session.IsViewer = false;
        session.IsDebug = false;
        session.Seat = null;
        session.Binding = null;
        session.SeatToken = null;
        session.Request = null;

        if (seat is { } revokedSeat && wasController)
        {
            var match = Service.Executor.Match;
            if (match is { State: Orc.Game.MatchState.InProgress })
            {
                var outcome = await Service.ConcedeAsync(revokedSeat, ct).ConfigureAwait(false);
                _ = outcome;
                DrainSegments();
                DetectMatchEnd();
                await PushViewsAsync(ct).ConfigureAwait(false);
            }

            if (Room.State == RoomState.Lobby)
            {
                Room.Release(revokedSeat);
            }

            Room.Revoke(revokedSeat);
            EmitSeatEvent(revokedSeat, SeatStates.Revoked);
        }

        // 立即写（不经推送队列）：撤销通知必须先于断开到达。
        await PushAdmissionAsync(session, "revoked", reason ?? "admin-revoked", immediate: true).ConfigureAwait(false);
        session.Abort();

        return AdminOutcome.Success(new JsonObject
        {
            ["connectionId"] = connectionId,
            ["state"] = SessionStateNames.Of(session.State),
            ["seat"] = seat,
            ["revokedController"] = wasController,
        });
    }

    /// <summary>解绑（收回该连接的座位绑定；房间态直接释放座位）。</summary>
    internal AdminOutcome Unbind(string connectionId)
    {
        if (FindSession(connectionId) is not { } session)
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, $"连接 '{connectionId}' 不存在。");
        }

        var seat = session.Seat;
        ReleaseSession(session, notify: true);
        if (seat is { } released)
        {
            EmitSeatEvent(released, SeatStates.Idle);
        }

        return AdminOutcome.Success(new JsonObject
        {
            ["connectionId"] = connectionId,
            ["state"] = SessionStateNames.Of(session.State),
        });
    }

    /// <summary>会话断开/心跳超时的统一清理（房间态立即释放座位；对局态保留绑定并起宽限）。</summary>
    private void OnSessionClosed(ClientSession session)
    {
        if (session.Seat is not { } seat || !session.IsController)
        {
            // viewer 断开不影响对局、不影响他人（R5）；控制器断开才走宽限/释放。
            session.Seat = null;
            session.Binding = null;
            return;
        }

        if (Room.State == RoomState.Lobby)
        {
            Room.Release(seat);
            EmitSeatEvent(seat, SeatStates.Idle);
            session.Binding = null;
            return;
        }

        Room.MarkDisconnected(seat);
        EmitSeatEvent(seat, SeatStates.Disconnected);

        if (Service.Executor.Match is { State: Orc.Game.MatchState.InProgress })
        {
            _disconnectedSeats[seat] = DateTimeOffset.UtcNow;
        }
    }

    private void ReleaseSession(ClientSession session, bool notify)
    {
        var seat = session.Seat;
        if (seat is { } released && session.IsController)
        {
            Room.Release(released);
        }

        session.Seat = null;
        session.Binding = null;
        session.SeatToken = null;
        session.Request = null;
        session.IsController = false;
        session.IsViewer = false;
        session.IsDebug = false;
        session.State = SessionState.Connected;

        if (notify)
        {
            PushAdmission(session, "rejected", "detached");
        }
    }

    // ---------------------------------------------------------------- 工具调用

    private async Task<RpcResponse> HandleToolsCallAsync(RpcRequest request, ClientSession session, CancellationToken ct)
    {
        var name = request.Params?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidParams, "缺少工具名（params.name）。");
        }

        var arguments = ToElement(request.Params?["arguments"]);

        // drain 白名单只有 pool.list 一项：它直取快照、不入队（写类 tool 仍整体拒绝）。
        // 集合写工具在窗口内的说明与管理面 op **逐字一致**（S5/S6），其余工具维持原说明。
        if (_draining && !string.Equals(name, "pool.list", StringComparison.Ordinal))
        {
            var message = IsPoolWriteTool(name)
                ? PackPool.DrainMessage
                : "房间正在轮换，请稍后重试。";
            return ToolFailure(request.Id, ResultCodes.RoomRotating, message, new JsonObject { ["retryAfterMs"] = 200 });
        }

        // 门禁顺序固定（不可调换）：角色态 → 工具要求 → 参数归属；被拒绝的请求**不入队、不碰引擎**。
        if (Gate(session, name, ref arguments) is { } denial)
        {
            return ToolFailure(request.Id, denial.Code, denial.Message);
        }

        var priority = name.StartsWith("target.", StringComparison.Ordinal)
            || name.StartsWith("session.", StringComparison.Ordinal)
            ? WorkPriority.Interactive
            : WorkPriority.Command;

        McpToolResult result;
        (long? From, long? To) range = (null, null);

        // 纯只读查询在队列外（R10）：不碰引擎写面、不占单写者通道。
        if (IsQueueFreeRead(name))
        {
            result = await InvokeToolAsync(session, name, arguments, ct).ConfigureAwait(false);
            return RpcResponse.Success(request.Id, Serialize(Map(result)));
        }

        try
        {
            result = await _queue.EnqueueAsync(
                priority,
                async token =>
                {
                    var outcome = await InvokeToolAsync(session, name, arguments, token).ConfigureAwait(false);

                    // S4：drain 固定在 work item 尾部，与动作成败无关。
                    range = DrainSegments();
                    DetectMatchEnd();

                    if (outcome.Ok && IsMatchAction(name))
                    {
                        await PushViewsAsync(token).ConfigureAwait(false);
                    }

                    return outcome;
                },
                ct).ConfigureAwait(false);
        }
        catch (WorkQueueFullException)
        {
            return ToolFailure(request.Id, ResultCodes.Busy, "服务繁忙，请重试。");
        }
        catch (ObjectDisposedException)
        {
            return ToolFailure(request.Id, ResultCodes.RoomRotating, "房间正在轮换，请稍后重试。");
        }

        _ = range;
        return RpcResponse.Success(request.Id, Serialize(Map(result)));
    }

    /// <summary>在**消费者上下文内**调用工具：设置调用者视角 → 调服务层 → 复位视角。</summary>
    private async Task<McpToolResult> InvokeToolAsync(
        ClientSession session, string name, JsonElement arguments, CancellationToken ct)
    {
        var service = Service;
        service.Caller = new CallerView(
            session.Seat,
            session.State == SessionState.DebugFace,
            session.State == SessionState.Omniscient);

        try
        {
            return await service.InvokeAsync(name, arguments, ct).ConfigureAwait(false);
        }
        finally
        {
            service.Caller = CallerView.Anonymous;
        }
    }

    /// <summary>纯只读查询（不入队）：不产生引擎写面、不需要与动作互斥。</summary>
    private static bool IsQueueFreeRead(string name)
        => name is "room.info" or "service.info" or "service.tools"
            or "card.list" or "pack.info" or "file.list" or "file.read"
            or "docs.list" or "docs.read" or "pool.list";

    /// <summary>集合写工具（drain 窗口内的说明与管理面 op 逐字一致；不是白名单，仍被拒绝）。</summary>
    private static bool IsPoolWriteTool(string name)
        => name is AdminOps.PoolRegister or AdminOps.PoolUnregister;

    private RpcResponse ToolFailure(string? id, string code, string message, JsonNode? details = null)
        => RpcResponse.Success(id, Serialize(new ToolResultPayload { Ok = false, Code = code, Message = message, Details = details }));

    private static bool IsMatchAction(string tool)
        => tool.StartsWith("play.", StringComparison.Ordinal)
            || tool.StartsWith("command.", StringComparison.Ordinal)
            || tool.StartsWith("match.", StringComparison.Ordinal)
            || tool.StartsWith("mulligan.", StringComparison.Ordinal)
            || tool.StartsWith("target.", StringComparison.Ordinal);

    // ---------------------------------------------------------------- 门禁

    private readonly record struct Denial(string Code, string Message);

    /// <summary>
    /// 角色门禁（R6/R7/R10）：**能力由角色决定**，判定＝"连接角色集合 ⊇ 工具要求"。
    /// <para>
    /// <c>room.info</c> 是显式例外（<c>Connected</c> 与 <c>Pending</c> 都可调）；
    /// <c>Pending</c>/<c>Revoked</c> 调其它任何工具一律 <c>not-admitted</c>（不按工具区分）。
    /// </para>
    /// </summary>
    private Denial? Gate(ClientSession session, string name, ref JsonElement arguments)
    {
        if (session.State == SessionState.Revoked)
        {
            return new Denial(ResultCodes.NotAdmitted, "连接许可已被撤销（请重新 session.attach 申请）。");
        }

        if (string.Equals(name, "room.info", StringComparison.Ordinal))
        {
            return null;
        }

        if (session.State is SessionState.Connected or SessionState.Pending)
        {
            return new Denial(
                ResultCodes.NotAdmitted,
                session.State == SessionState.Pending
                    ? "申请尚未获批准：当前只能 ping / session.detach / room.info。"
                    : "尚未申请角色：请先 session.attach。");
        }

        var descriptor = Service.Tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return new Denial(ResultCodes.UnknownTool, $"未知工具 '{name}'。");
        }

        if (!session.Allows(descriptor.RequiredRole))
        {
            return new Denial(
                ResultCodes.Unauthorized,
                $"工具 '{name}' 需要 {descriptor.RequiredRole} 角色（当前 {SessionStateNames.Of(session.State)}）。");
        }

        // 现有调试判据（回环 ＋ 未绑座）完整保留：写类工具只接受回环来源。
        if (string.Equals(descriptor.RequiredRole, ToolRole.Debug, StringComparison.Ordinal)
            && DebugToolCatalog.WriteToolNames.Contains(name)
            && !SeatScopePolicy.IsTrustedLocal(session.RemoteAddress))
        {
            return new Denial(ResultCodes.Unauthorized, $"工具 '{name}' 只接受回环来源的编排连接（当前 {session.RemoteAddress}）。");
        }

        // owner 归属校验（绑定 seat 的 controller 只能操作自己那座）。
        if (SeatScopeRegistry.ScopeOf(name) == SeatScope.OwnerScoped)
        {
            if (session.Seat is not { } seat)
            {
                return new Denial(ResultCodes.Unauthorized, "会话未绑定座位。");
            }

            if (!TryReadInt(arguments, "owner", out var owner))
            {
                return new Denial(ResultCodes.InvalidArguments, $"工具 '{name}' 需要整数参数 owner。");
            }

            if (owner != seat)
            {
                return new Denial(ResultCodes.Unauthorized, $"owner={owner} 与会话座位 {seat} 不符。");
            }
        }

        if (name.StartsWith("target.", StringComparison.Ordinal)
            && (name is "target.submit" or "target.cancel"))
        {
            if (Service.Bridge.CurrentPending is { } pending && session.Seat is { } seat)
            {
                if (pending.Seat is { } owner && owner != seat)
                {
                    return new Denial(ResultCodes.Unauthorized, $"待决请求归属座位 {owner}（会话座位 {seat}），无权应答。");
                }
            }
        }

        return null;
    }

    private static bool TryReadInt(JsonElement arguments, string property, out int value)
    {
        value = 0;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.Number
            && node.TryGetInt32(out value);
    }

    // ---------------------------------------------------------------- 推送

    private void OnActionSettled(ActionOutcome outcome) => EnqueuePush();

    private void EnqueuePush()
    {
        if (_draining)
        {
            return; // 轮换窗口内不再推（旧局的帧不该落到新房间）。
        }

        try
        {
            _ = _queue.EnqueueAsync(
                WorkPriority.Command,
                async token =>
                {
                    DrainSegments();
                    DetectMatchEnd();
                    await PushViewsAsync(token).ConfigureAwait(false);
                    return true;
                },
                _shutdown.Token);
        }
        catch (Exception ex) when (ex is WorkQueueFullException or ObjectDisposedException)
        {
            // 静默丢弃：view 幂等，下一次动作的推送兜底。
        }
    }

    /// <summary>
    /// 按角色态分发视图帧（S7）：绑座 viewer＝单视角帧；全知 viewer＝两份视角打包帧；
    /// 其余角色（<c>Pending</c>/<c>Connected</c>/调试面/纯 controller）**不收**对局数据。
    /// <para>同一 seat 的多个 viewer 复用同一份计算结果（多一个全知 viewer 不会让玩家帧变大）。</para>
    /// </summary>
    private async Task PushViewsAsync(CancellationToken ct)
    {
        if (Service.Executor.Match is null)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        var seatFrames = new Dictionary<int, JsonObject?>();
        JsonObject? omniscientFrame = null;
        var omniscientComputed = false;

        foreach (var session in _sessions.Values)
        {
            if (session.State == SessionState.Omniscient)
            {
                if (!omniscientComputed)
                {
                    omniscientFrame = Service.ProjectOmniscientFrame().Payload as JsonObject;
                    omniscientComputed = true;
                }

                SendNotification(session, ProtocolMethods.NotifyView, omniscientFrame);
                continue;
            }

            if (!session.IsViewer || session.Seat is not { } seat)
            {
                continue;
            }

            if (!seatFrames.TryGetValue(seat, out var frame))
            {
                frame = Service.ProjectSeatFrame(seat).Payload as JsonObject;
                seatFrames[seat] = frame;
            }

            SendNotification(session, ProtocolMethods.NotifyView, frame);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>绑定/批准后重推该连接的初始视图与待决选靶请求。</summary>
    private void PushToSession(ClientSession session)
    {
        if (Service.Executor.Match is null)
        {
            return;
        }

        if (session.State == SessionState.Omniscient)
        {
            SendNotification(session, ProtocolMethods.NotifyView, Service.ProjectOmniscientFrame().Payload);
        }
        else if (session.IsViewer && session.Seat is { } seat)
        {
            SendNotification(session, ProtocolMethods.NotifyView, Service.ProjectSeatFrame(seat).Payload);
        }

        if (Service.Bridge.CurrentPending is { } pending && pending.Seat == session.Seat)
        {
            SendNotification(session, ProtocolMethods.NotifyTargetRequest, McpService.PendingJson(pending));
        }
    }

    private void PushAdmission(ClientSession session, string decision, string? reason)
        => _ = PushAdmissionAsync(session, decision, reason);

    /// <summary>
    /// 推接入许可决定。撤销路径必须 <paramref name="immediate"/>：连接马上要被关闭，
    /// 走推送队列会与关闭竞态，导致客户端"没收到 revoked 就被断开"。
    /// </summary>
    private async Task PushAdmissionAsync(ClientSession session, string decision, string? reason, bool immediate = false)
    {
        var payload = new AdmissionPayload
        {
            Decision = decision,
            Roles = session.GrantedRoles(),
            Seat = session.Seat,
            SeatToken = string.Equals(decision, "admitted", StringComparison.Ordinal) ? session.SeatToken : null,
            Reason = reason,
        };

        var node = Serialize(new RpcNotification { Method = ProtocolMethods.NotifyAdmission, Params = Serialize(payload) });
        if (node is null)
        {
            return;
        }

        if (immediate)
        {
            await session.SendNowAsync(node).ConfigureAwait(false);
            return;
        }

        session.Send(node);
    }

    private static void SendNotification(ClientSession session, string method, JsonNode? payload)
    {
        if (payload is null)
        {
            return;
        }

        session.Send(Serialize(new RpcNotification { Method = method, Params = payload }));
    }

    /// <summary>
    /// 取新增段并按角色分发（S7；段队列的唯一取用者）。返回本批段序号区间（无段＝<c>(null,null)</c>）。
    /// <para>顺序固定：喂回观察面缓存 → 绑座 viewer 脱敏段 → 全知 viewer 全量段。</para>
    /// </summary>
    private (long? From, long? To) DrainSegments()
    {
        if (Service.Executor.Match is not { } match)
        {
            return (null, null);
        }

        var segments = match.Engine.TakeSegments();
        if (segments.Count == 0)
        {
            return (null, null);
        }

        Service.IngestSegments(segments);

        var visibility = new Orc.Game.Ui.MatchEventVisibility(match);
        var seatPayloads = new Dictionary<int, JsonArray>();
        JsonArray? omniscientPayload = null;

        foreach (var session in _sessions.Values)
        {
            if (session.State == SessionState.Omniscient)
            {
                if (omniscientPayload is null)
                {
                    // 全知段＝**全量版**（不按座位脱敏）。
                    omniscientPayload = new JsonArray();
                    foreach (var segment in segments)
                    {
                        omniscientPayload.Add(JsonNode.Parse(
                            Orc.Output.EventSegmentJson.Serialize(segment, indented: false)));
                    }
                }

                session.Send(Serialize(new RpcNotification
                {
                    Method = ProtocolMethods.NotifySegment,
                    Params = new JsonObject
                    {
                        ["perspective"] = new JsonObject { ["mode"] = "omniscient" },
                        ["segments"] = omniscientPayload,
                    },
                }));
                continue;
            }

            if (!session.IsViewer || session.Seat is not { } seat)
            {
                continue;
            }

            if (!seatPayloads.TryGetValue(seat, out var payload))
            {
                payload = new JsonArray();
                foreach (var segment in segments)
                {
                    payload.Add(JsonNode.Parse(Orc.Output.EventSegmentJson.Serialize(
                        segment, indented: false, visibility, match.Players[seat])));
                }

                seatPayloads[seat] = payload;
            }

            session.Send(Serialize(new RpcNotification
            {
                Method = ProtocolMethods.NotifySegment,
                Params = new JsonObject
                {
                    ["perspective"] = new JsonObject { ["mode"] = "seat", ["seat"] = seat },
                    ["segments"] = payload,
                },
            }));
        }

        return (segments[0].Sequence, segments[^1].Sequence);
    }

    private void OnSeatsChanged()
    {
        var room = Room;
        var payload = new JsonObject
        {
            ["roomId"] = room.RoomId,
            ["seats"] = new JsonArray(room.Snapshot(Orc.Mcp.Room.RoomViewRequest.Anonymous).Seats
                .Select(seat => (JsonNode)Orc.Mcp.Room.RoomSnapshotJson.SeatToNode(seat))
                .ToArray()),
        };

        foreach (var session in _sessions.Values)
        {
            SendNotification(session, ProtocolMethods.NotifyRoomSeats, payload.DeepClone());
        }

        EmitEvent("room.seats", payload.DeepClone());
    }

    private void EmitSeatEvent(int seat, string state)
        => EmitEvent("seat.changed", new JsonObject
        {
            ["roomId"] = Room.RoomId,
            ["seat"] = seat,
            ["state"] = state,
        });

    private void EmitEvent(string name, JsonNode payload)
    {
        try
        {
            ChannelEvent?.Invoke(new JsonObject { ["event"] = name, ["payload"] = payload.DeepClone() });
        }
        catch (Exception)
        {
            // 事件订阅者故障不得影响房间/引擎路径。
        }
    }

    /// <summary>管理面终止对局后的统一收尾（推终局帧 → 标终局 → 安排轮换）。</summary>
    internal void AfterMatchAborted()
    {
        DrainSegments();
        PushViewsAsync(CancellationToken.None).GetAwaiter().GetResult();
        HandleMatchEnded();
    }

    /// <summary>管理面发包：事件外发（供 <see cref="AdminApi"/> 上报装配结果）。</summary>
    internal void PublishEvent(string name, JsonNode payload) => ChannelEvent?.Invoke(new JsonObject { ["event"] = name, ["payload"] = payload.DeepClone() });

    /// <summary>服务端启动选项（管理面读 spawn 配置）。</summary>
    internal ServerOptions Options => _options;

    // ---------------------------------------------------------------- 终局与轮换

    private void OnMatchChanged(object? sender, Orc.Mcp.Hosting.MatchLifecycleEventArgs args)
    {
        if (args.Change == Orc.Mcp.Hosting.MatchLifecycleChange.Created)
        {
            _matchEnded = false;
            StartAiDrivers(args.Match);
            return;
        }

        HandleMatchEnded();
    }

    /// <summary>在 work item 尾部读对局状态（消费者上下文；无活跃对局＝零成本）。</summary>
    private void DetectMatchEnd()
    {
        if (_matchEnded || Service.Executor.Match is not { State: Orc.Game.MatchState.Ended })
        {
            return;
        }

        // 终局帧必须**先**送达（客户端才看得到 phase:"over"），随后才安排轮换清队列。
        DrainSegments();
        PushViewsAsync(CancellationToken.None).GetAwaiter().GetResult();
        HandleMatchEnded();
    }

    /// <summary>终局（S5 第 1 步）：标 <c>Ended</c> ＋ 停 AI ＋ 停宽限计时 ＋ 安排轮换。</summary>
    private void HandleMatchEnded()
    {
        if (_matchEnded)
        {
            return;
        }

        _matchEnded = true;
        StopAiDrivers();
        _disconnectedSeats.Clear();
        Room.MarkEnded();

        EmitEvent("match.ended", new JsonObject
        {
            ["roomId"] = Room.RoomId,
            ["winner"] = Service.Executor.Match?.Winner?.Index,
            ["endReason"] = Service.Executor.Match?.EndReason?.ToString(),
        });

        ScheduleRotation();
    }

    /// <summary>安排轮换（幂等；在队列外推进，因为轮换需要等队列排空）。</summary>
    private void ScheduleRotation()
    {
        if (Interlocked.Exchange(ref _rotationScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RotateAsync);
    }

    /// <summary>
    /// 房间轮换（S5 十步；两条不可交换：drain 先于销毁、引用交换先于恢复队列）。
    /// <para>
    /// 进程级对象（传输宿主、串行队列、既有连接）**不重建**——连接保持存活，绑定 seat 的角色退回
    /// <c>Revoked</c> 等重新申请；调试面不受影响。
    /// </para>
    /// </summary>
    private async Task RotateAsync()
    {
        _draining = true;
        try
        {
            // 2. drain：拒绝新入队（调用方回 room-rotating），等既有工作项自然完成（不杀）。
            await _queue.WaitForIdleAsync(TimeSpan.FromSeconds(20), _shutdown.Token).ConfigureAwait(false);

            // 2 末：停 AI 与宽限计时（旧计时器不得对新房间 seat 触发认输）。
            StopAiDrivers();
            _disconnectedSeats.Clear();
            _graceInFlight.Clear();

            var oldService = Service;
            var oldRoom = Room;

            // 3/4. 销毁旧服务层（pending 选靶收束、段缓存丢弃）＋ 释放旧对局。
            oldService.Bridge.FailAll("room-rotated");
            DetachService(oldService, oldRoom);
            oldService.DisposeMatch();

            // 5/6/7. 建新房间与新服务层，**原子交换**引用并递增纪元。
            var room = CreateRoom();
            var service = CreateService(room);
            AttachService(service, room);

            lock (_stackSync)
            {
                Volatile.Write(ref _room, room);
                _service = service;
                _epoch++;
            }

            _matchEnded = false;

            // 8. 绑定 seat 的角色 → Revoked（**不含调试面**）；清待发帧后再推轮换通知。
            foreach (var session in _sessions.Values)
            {
                if (session.Seat is null && !session.IsController && !session.IsViewer)
                {
                    session.ClearOutbound();
                    continue;
                }

                session.Seat = null;
                session.Binding = null;
                session.SeatToken = null;
                session.Request = null;
                session.IsController = false;
                session.IsViewer = false;
                session.State = SessionState.Revoked;
                session.ClearOutbound();
            }

            var rotated = new JsonObject
            {
                ["roomId"] = room.RoomId,
                ["seatCount"] = room.SeatCount,
                ["epoch"] = CurrentEpoch,
            };

            foreach (var session in _sessions.Values)
            {
                SendNotification(session, ProtocolMethods.NotifyRoomRotated, rotated.DeepClone());
            }

            EmitEvent("room.rotated", rotated);
        }
        catch (Exception ex)
        {
            // 轮换失败不得让服务器死掉：保留旧栈继续服务，并留下可定位的事件。
            EmitEvent("room.rotate.failed", new JsonObject
            {
                ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
        finally
        {
            _draining = false;
            Interlocked.Exchange(ref _rotationScheduled, 0);
        }
    }

    // ---------------------------------------------------------------- AI 驱动

    private void StartAiDrivers(Orc.Game.Match? match)
    {
        if (match is null)
        {
            return;
        }

        StopAiDrivers();
        _aiCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        for (var seat = 0; seat < 2; seat++)
        {
            if (!Room.IsAiSeat(seat))
            {
                continue;
            }

            var provider = match.UiProjections.CreateProvider(null, Service.Options.MatchId, null);
            if (provider is null)
            {
                continue;
            }

            var pilot = new Orc.Ai.AiPilot(Service, provider, seat);
            var token = _aiCts.Token;
            _aiDrivers[seat] = Task.Run(() => DriveAiAsync(pilot, match, token), CancellationToken.None);
        }
    }

    private async Task DriveAiAsync(Orc.Ai.AiPilot pilot, Orc.Game.Match match, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (match.State == Orc.Game.MatchState.Ended)
            {
                return;
            }

            Orc.Ai.AiStepResult result;
            try
            {
                result = await _queue
                    .EnqueueAsync(
                        WorkPriority.Command,
                        async token =>
                        {
                            try
                            {
                                // 旧 AI 循环不得作用于新对局（S8）：换栈后本循环持有的 match 已 Ended。
                                if (match.State == Orc.Game.MatchState.Ended)
                                {
                                    return Orc.Ai.AiStepResult.Idle("room-rotated");
                                }

                                var step = await pilot.StepAsync(token).ConfigureAwait(false);
                                if (step.Performed)
                                {
                                    await PushViewsAsync(token).ConfigureAwait(false);
                                }

                                return step;
                            }
                            finally
                            {
                                DrainSegments();
                                DetectMatchEnd();
                            }
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WorkQueueFullException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ai] 座位 {pilot.Seat} 单步异常：{ex.GetType().Name}: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                continue;
            }

            if (match.State == Orc.Game.MatchState.Ended)
            {
                return;
            }

            var delay = result.Performed ? 150 : 300;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void StopAiDrivers()
    {
        try
        {
            _aiCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        _aiCts?.Dispose();
        _aiCts = null;
        _aiDrivers.Clear();
    }

    // ---------------------------------------------------------------- 选靶推送

    private void OnTargetingExpired(PendingSelection pending)
    {
        foreach (var session in _sessions.Values)
        {
            if (pending.Seat is { } seat && session.Seat != seat)
            {
                continue;
            }

            SendNotification(session, ProtocolMethods.NotifyTargetExpired, McpService.PendingJson(pending));
        }
    }

    private void OnTargetingRequest(PendingSelection pending)
    {
        foreach (var session in _sessions.Values)
        {
            // 有归属座位＝只投给该座位的 controller；无归属＝投给所有（兼容未绑定/单人场景）。
            if (pending.Seat is { } seat && (session.Seat != seat || !session.IsController))
            {
                continue;
            }

            SendNotification(session, ProtocolMethods.NotifyTargetRequest, McpService.PendingJson(pending));
        }
    }

    // ---------------------------------------------------------------- 心跳与宽限

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var timeout = _options.HeartbeatTimeout.TotalMilliseconds;
        var checkEvery = TimeSpan.FromSeconds(
            Math.Clamp(_options.HeartbeatTimeout.TotalSeconds / 2, 0.2, 5));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = Environment.TickCount64;
            foreach (var session in _sessions.Values)
            {
                if (now - session.LastSeen > timeout)
                {
                    session.Abort();
                }
            }

            CheckDisconnectGrace();
        }
    }

    /// <summary>宽限判负（R11）：**只在轮到该 seat 时计时**；到期由服务器代该座位认输。</summary>
    private void CheckDisconnectGrace()
    {
        if (_disconnectedSeats.IsEmpty || Service.Executor.Match is not { State: Orc.Game.MatchState.InProgress })
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _disconnectedSeats)
        {
            if (now - pair.Value < _options.DisconnectGrace)
            {
                continue;
            }

            if (!_graceInFlight.TryAdd(pair.Key, 0))
            {
                continue;
            }

            var seat = pair.Key;
            try
            {
                _ = _queue.EnqueueAsync(
                    WorkPriority.Command,
                    async token =>
                    {
                        try
                        {
                            // 计时器回调必须自检终局：取消与回调可能同时发生（S8）。
                            if (Service.Executor.Match is not { State: Orc.Game.MatchState.InProgress })
                            {
                                return false;
                            }

                            if (!IsItsTurn(seat))
                            {
                                return false; // 还没轮到它：记录保留，等下一次检查。
                            }

                            var outcome = await Service.ConcedeAsync(seat, token).ConfigureAwait(false);
                            if (outcome.Status != ActionOutcome.StatusCompleted)
                            {
                                return false;
                            }

                            _disconnectedSeats.TryRemove(seat, out _);
                            DrainSegments();
                            DetectMatchEnd();
                            await PushViewsAsync(token).ConfigureAwait(false);
                            return true;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                        finally
                        {
                            _graceInFlight.TryRemove(seat, out _);
                        }
                    },
                    _shutdown.Token);
            }
            catch (Exception)
            {
                _graceInFlight.TryRemove(seat, out _);
            }
        }
    }

    private bool IsItsTurn(int seat) => Service.Executor.Match?.CurrentPlayer?.Index == seat;

    // ---------------------------------------------------------------- 内部：启动后置

    /// <summary>boot 后的自检（脚手架生成、进程级注册等由 <see cref="ServerBootstrap"/> 负责）。</summary>
    internal void NoteBootReady()
        => EmitEvent("instance.ready", new JsonObject
        {
            ["port"] = Port,
            ["roomId"] = Room.RoomId,
            ["mode"] = _options.Mode == ServerMode.Sandbox ? "sandbox" : "play",
            ["seatCount"] = Room.SeatCount,
        });

    // ---------------------------------------------------------------- 工具方法

    private static JsonNode? Serialize<T>(T value)
        => JsonNode.Parse(JsonSerializer.Serialize(value, ProtocolJson.Options));

    private static ToolResultPayload Map(McpToolResult result) => new()
    {
        Ok = result.Ok,
        Code = result.Code,
        Message = result.Message,
        Details = result.Details ?? result.EngineResult,
    };

    private static JsonElement ToElement(JsonNode? node)
    {
        if (node is null)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<string> ReadStringList(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : Array.Empty<string>();

    private static IReadOnlyList<PackageRef>? ReadPackages(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        return array
            .Select(PackageRef.From)
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        StopAiDrivers();
        _listener?.Stop();

        DetachService(Service, Room);
        Service.DisposeMatch();

        foreach (var task in new[] { _acceptLoop, _heartbeatLoop })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径忽略。
            }
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }

        if (_ownsQueue)
        {
            await _queue.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    // ---------------------------------------------------------------- 会话

    /// <summary>待许可的申请（角色 ＋ 座位 ＋ 卡组 ＋ 卡包声明）。</summary>
    internal sealed record PendingAttach(
        IReadOnlyList<string> Roles,
        int? Seat,
        string? DisplayName,
        IReadOnlyList<string> Deck,
        DeckConfigPayload? DeckConfig,
        IReadOnlyList<PackageRef> Packages);

    /// <summary>
    /// 单个客户端连接（**输入与输出的唯一对象**，但角色态决定它"能做什么、能收什么"）。
    /// </summary>
    internal sealed class ClientSession : IAsyncDisposable
    {
        // 推送队列**有界**：慢客户端只丢最旧的推送（view 幂等），不会让服务端内存线性增长。
        private readonly Channel<JsonNode?> _outbound = Channel.CreateBounded<JsonNode?>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        private readonly TcpClient _client;
        private readonly NdjsonWriter _writer;
        private readonly CancellationToken _ct;
        private readonly Task _pump;

        public ClientSession(string id, TcpClient client, NdjsonWriter writer, CancellationToken ct)
        {
            Id = id;
            _client = client;
            _writer = writer;
            _ct = ct;
            RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            _pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        }

        public string Id { get; }

        public IPAddress? RemoteAddress { get; }

        public string? Label { get; set; }

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public long CreatedAtTicks => CreatedAt.UtcTicks;

        public SessionState State { get; set; } = SessionState.Connected;

        public bool IsController { get; set; }

        public bool IsViewer { get; set; }

        public bool IsDebug { get; set; }

        public int? Seat { get; set; }

        /// <summary>seat ↔ 设施的绑定（纪元 ＋ playerIndex；**不持有引擎活对象**）。</summary>
        public SeatBinding? Binding { get; set; }

        public string? SeatToken { get; set; }

        public PendingAttach? Request { get; set; }

        public long LastSeen { get; private set; } = Environment.TickCount64;

        /// <summary>已授予的角色集合（按状态；<c>Pending</c> 为空）。</summary>
        public IReadOnlyList<string> GrantedRoles()
        {
            var roles = new List<string>(2);
            if (IsDebug || State == SessionState.DebugFace)
            {
                roles.Add(SessionRoles.Debug);
            }

            if (IsController)
            {
                roles.Add(SessionRoles.Controller);
            }

            if (IsViewer)
            {
                roles.Add(SessionRoles.Viewer);
            }

            return roles;
        }

        /// <summary>角色集合是否满足工具要求（"连接角色集合 ⊇ 工具要求"）。</summary>
        public bool Allows(string requiredRole)
        {
            if (State == SessionState.Revoked)
            {
                return false;
            }

            if (State == SessionState.Connected || State == SessionState.Pending)
            {
                // 未批准＝零能力（room.info 在门禁里是显式例外，不经过本判定）。
                return false;
            }

            return requiredRole switch
            {
                ToolRole.None => true,
                ToolRole.Controller => IsController && Seat is not null,
                ToolRole.Viewer => (IsViewer && (Seat is not null || State == SessionState.Omniscient))
                    || State == SessionState.DebugFace,
                ToolRole.Debug => State == SessionState.DebugFace,
                _ => false,
            };
        }

        public void Touch() => LastSeen = Environment.TickCount64;

        /// <summary>写响应（不排队；与推送共用 writer 的串行写锁，不会交错）。</summary>
        public Task SendResponseAsync(JsonNode? node)
            => node is null ? Task.CompletedTask : _writer.WriteAsync(node, _ct);

        /// <summary>立即写一帧（不经推送队列；用于"必须先于断开到达"的报文）。</summary>
        public Task SendNowAsync(JsonNode? node)
            => node is null ? Task.CompletedTask : _writer.WriteAsync(node, _ct);

        public void Send(JsonNode? node) => _outbound.Writer.TryWrite(node);

        /// <summary>清空待发帧（轮换用：只保留终局帧与轮换通知，避免旧局 view 落在新房间之后）。</summary>
        public void ClearOutbound()
        {
            while (_outbound.Reader.TryRead(out _))
            {
                // 丢弃。
            }
        }

        /// <summary>心跳超时/撤销时关闭连接（读循环随之退出并清理会话）。</summary>
        public void Abort()
        {
            try
            {
                _client.Close();
            }
            catch (SocketException)
            {
                // 已关闭。
            }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var node in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (node is null)
                    {
                        return;
                    }

                    await _writer.WriteAsync(node, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 关闭中。
            }
            catch (IOException)
            {
                // 对端断开。
            }
            catch (ObjectDisposedException)
            {
                // 关闭中。
            }
        }

        public async ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete();
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径忽略。
            }
        }
    }
}

/// <summary>角色态 → 协议字符串（唯一映射）。</summary>
public static class SessionStateNames
{
    /// <summary>协议名。</summary>
    public static string Of(SessionState state) => state switch
    {
        SessionState.Pending => SessionStates.Pending,
        SessionState.Admitted => SessionStates.Admitted,
        SessionState.Viewer => SessionStates.Viewer,
        SessionState.Omniscient => SessionStates.Omniscient,
        SessionState.DebugFace => SessionStates.Debug,
        SessionState.Revoked => SessionStates.Revoked,
        _ => SessionStates.Connected,
    };
}
