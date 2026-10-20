using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Protocol;
using Orc.Server.Execution;

namespace Orc.Server.Transport;

/// <summary>
/// 服务端宿主（多人支持 S8）：TCP 监听 ＋ NDJSON 帧 ＋ JSON-RPC 分发 ＋ 串行执行队列 ＋ 推送。
/// <para>
/// 所有进入引擎的调用都经 <see cref="SerialWorkQueue"/> 单消费者执行；网络线程只负责收发。
/// 推送按会话的座位分别生成（非广播），并经每会话写队列发送——慢客户端不会拖住引擎。
/// </para>
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly McpService _service;
    private readonly ServerOptions _options;
    private readonly SerialWorkQueue _queue;
    private readonly Orc.Server.Rooms.Room? _room;
    private readonly bool _ownsQueue;
    private readonly Dictionary<int, Task> _aiDrivers = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _disconnectedSeats = new();
    private CancellationTokenSource? _aiCts;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly IDisposable _targetingSubscription;
    private readonly List<TcpClient> _clients = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _heartbeatLoop;

    public ServerHost(
        McpService service,
        ServerOptions options,
        SerialWorkQueue? queue = null,
        Orc.Server.Rooms.Room? room = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _queue = queue ?? new SerialWorkQueue();
        _room = room;
        _ownsQueue = queue is null;

        // 段由本宿主取用（服务层不再自行取段），取到后按座位脱敏分发。
        _service.SetSegmentConsumer(Orc.Mcp.Hosting.SegmentConsumer.Host);

        _targetingSubscription = _service.SubscribeTargetingRequests(OnTargetingRequest);
        _service.Bridge.PendingExpired += OnTargetingExpired;
        _service.MatchChanged += OnMatchChanged;
    }

    /// <summary>实际监听端口（<see cref="ServerOptions.Port"/> 为 0 时由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>当前已连接会话数（诊断/测试）。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>服务层门面（诊断/测试/宿主工具用）。</summary>
    public McpService Service => _service;

    /// <summary>启动监听。</summary>
    public Task<ServerHost> StartAsync(CancellationToken ct = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("服务端已启动。");
        }

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.FromResult(this);
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
            await using var reader = new NdjsonReader(stream);
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
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.Id, out _);
                if (session.Seat is { } disconnectedSeat)
                {
                    MarkSeatDisconnected(disconnectedSeat);
                }

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
            ProtocolMethods.Initialize => HandleInitialize(request),
            ProtocolMethods.Ping => RpcResponse.Success(request.Id, new JsonObject { ["pong"] = true }),
            ProtocolMethods.ToolsList => HandleToolsList(request),
            ProtocolMethods.ToolsCall => await HandleToolsCallAsync(request, session, ct).ConfigureAwait(false),
            _ => RpcResponse.Failure(request.Id, RpcErrorCodes.MethodNotFound, $"未知方法 '{request.Method}'。"),
        };
    }

    private RpcResponse HandleInitialize(RpcRequest request)
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

        var result = new InitializeResult
        {
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
            SchemaVersion = _service.Options.ContractSchemaVersion,
            Capabilities = ["room", "push", "targeting"],
        };

        return RpcResponse.Success(request.Id, Serialize(result));
    }

    private RpcResponse HandleToolsList(RpcRequest request)
    {
        var tools = _service.Tools
            .Select(tool => new ToolDescriptorPayload(tool.Name, tool.Group, tool.Description, tool.InputSchema))
            .ToArray();

        return RpcResponse.Success(request.Id, Serialize(tools));
    }

    private async Task<RpcResponse> HandleToolsCallAsync(RpcRequest request, ClientSession session, CancellationToken ct)
    {
        var name = request.Params?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidParams, "缺少工具名（params.name）。");
        }

        var arguments = ToElement(request.Params?["arguments"]);
        var priority = name.StartsWith("target.", StringComparison.Ordinal)
            || name.StartsWith("session.", StringComparison.Ordinal)
            ? WorkPriority.Interactive
            : WorkPriority.Command;

        McpToolResult result;
        try
        {
            result = await _queue.EnqueueAsync(
                priority,
                async token =>
                {
                    var outcome = await _service.InvokeAsync(name, arguments, token).ConfigureAwait(false);
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
            return RpcResponse.Success(request.Id, Serialize(ToolResultPayload.Failure(ResultCodes.Busy, "服务繁忙，请重试。")));
        }

        BindSeat(session, result);
        return RpcResponse.Success(request.Id, Serialize(Map(result)));
    }

    private static bool IsMatchAction(string tool)
        => tool.StartsWith("play.", StringComparison.Ordinal)
            || tool.StartsWith("command.", StringComparison.Ordinal)
            || tool.StartsWith("match.", StringComparison.Ordinal)
            || tool.StartsWith("mulligan.", StringComparison.Ordinal)
            || tool.StartsWith("target.", StringComparison.Ordinal);

    private void BindSeat(ClientSession session, McpToolResult result)
    {
        if (!result.Ok || result.Details?["seat"] is not { } seatNode)
        {
            return;
        }

        int seat;
        try
        {
            seat = seatNode.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return; // 非数值 seat：不是座位绑定结果。
        }

        session.Seat = seat;
        _disconnectedSeats.TryRemove(seat, out _);
        _room?.MarkConnected(seat);

        // 重连后若仍有待决选靶属于该座位，重推一次（R7）。
        if (_service.Bridge.CurrentPending is { } pending && pending.Seat == seat)
        {
            session.Send(Serialize(new RpcNotification
            {
                Method = ProtocolMethods.NotifyTargetRequest,
                Params = McpService.PendingJson(pending),
            }));
        }
    }

    /// <summary>座位断线：房间态立即释放（R9），对局态保留并记录宽限起点。</summary>
    private void MarkSeatDisconnected(int seat)
    {
        if (_room is { } room)
        {
            if (room.State == Orc.Server.Rooms.RoomState.Lobby)
            {
                room.DropSeat(seat);
                return;
            }

            room.MarkDisconnected(seat);
        }

        if (_service.Executor.Match is { State: Orc.Game.MatchState.InProgress })
        {
            _disconnectedSeats[seat] = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>宽限判负（R9）：轮到断线方且超过宽限 → 服务端代该座位认输。</summary>
    private void CheckDisconnectGrace()
    {
        if (_disconnectedSeats.IsEmpty || _service.Executor.Match is not { State: Orc.Game.MatchState.InProgress })
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

            // 只尝试一次：先移除再看是否轮到它，避免重复认输。
            if (!_disconnectedSeats.TryRemove(pair.Key, out _))
            {
                continue;
            }

            var seat = pair.Key;
            _ = _queue.EnqueueAsync(
                WorkPriority.Command,
                async token =>
                {
                    var view = _service.ProjectView(seat);
                    if (view.Payload is not JsonObject payload)
                    {
                        return false;
                    }

                    // 投影的 activePlayerSide 是相对 viewer 的：self＝轮到该座位。
                    if (payload["activePlayerSide"]?.GetValue<string>() != "self")
                    {
                        return false;
                    }

                    var outcome = await _service.ConcedeAsync(seat, token).ConfigureAwait(false);
                    return outcome.Status == Orc.Mcp.Match.ActionOutcome.StatusCompleted;
                },
                _shutdown.Token);
        }
    }

    // ---------------------------------------------------------------- 推送

    private async Task PushViewsAsync(CancellationToken ct)
    {
        PushSegments();

        foreach (var session in _sessions.Values)
        {
            if (session.Seat is not { } seat)
            {
                continue;
            }

            var outcome = _service.ProjectView(seat);
            if (outcome.Payload is JsonObject payload)
            {
                session.Send(Serialize(new RpcNotification
                {
                    Method = ProtocolMethods.NotifyView,
                    Params = payload,
                }));
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 取新增段并按座位脱敏分发（同一批段只取一次，再为每个座位各导出一份——
    /// 引擎的段取用是单消费者语义，不能各取各的）。
    /// </summary>
    private void PushSegments()
    {
        if (_service.Executor.Match is not { } match)
        {
            return;
        }

        var segments = match.Engine.TakeSegments();
        if (segments.Count == 0)
        {
            return;
        }

        // 宿主是取用者：喂回服务层缓存，保持观察面语义不变。
        _service.IngestSegments(segments);

        var visibility = new Orc.Game.Ui.MatchEventVisibility(match);

        foreach (var session in _sessions.Values)
        {
            if (session.Seat is not { } seat)
            {
                continue;
            }

            var payload = new JsonArray();
            foreach (var segment in segments)
            {
                var json = Orc.Output.EventSegmentJson.Serialize(segment, false, visibility, match.Players[seat]);
                payload.Add(JsonNode.Parse(json));
            }

            session.Send(Serialize(new RpcNotification
            {
                Method = ProtocolMethods.NotifySegment,
                Params = new JsonObject
                {
                    ["seat"] = seat,
                    ["segments"] = payload,
                },
            }));
        }
    }

    private void OnMatchChanged(object? sender, Orc.Mcp.Hosting.MatchLifecycleEventArgs args)
    {
        var created = args.Change == Orc.Mcp.Hosting.MatchLifecycleChange.Created;

        if (created)
        {
            StartAiDrivers(args.Match);
        }
        else
        {
            StopAiDrivers();
        }

        var method = created ? ProtocolMethods.NotifyMatchStarted : ProtocolMethods.NotifyMatchEnded;

        var notification = Serialize(new RpcNotification { Method = method, Params = new JsonObject() });
        foreach (var session in _sessions.Values)
        {
            session.Send(notification);
        }
    }

    /// <summary>
    /// 为 AI 座位启动驱动循环（R3）：AI 由服务端进程内驱动，不经过协议。
    /// 每一步经串行队列执行——AI 与远程动作共用同一门禁，不会并发进入引擎。
    /// </summary>
    private void StartAiDrivers(Orc.Game.Match? match)
    {
        if (_room is null || match is null)
        {
            return;
        }

        StopAiDrivers();
        _aiCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        for (var seat = 0; seat < 2; seat++)
        {
            var controller = _room.SeatAt(seat);
            if (controller is null
                || !string.Equals(controller.ControllerKind, Orc.Mcp.Room.ControllerKinds.Ai, StringComparison.Ordinal))
            {
                continue;
            }

            var provider = match.UiProjections.CreateProvider(null, _service.Options.MatchId, null);
            if (provider is null)
            {
                continue;
            }

            var pilot = new Orc.Ai.AiPilot(_service, provider, seat);
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
                    .EnqueueAsync(WorkPriority.Command, token => pilot.StepAsync(token), ct)
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
            catch (Exception)
            {
                // 单步异常不让驱动循环整体退出（下一步仍可能推进）。
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                continue;
            }

            if (match.State == Orc.Game.MatchState.Ended)
            {
                return;
            }

            // 有动作→短间隔（观感节奏）；无事可做（未轮到/无合法动作）→长间隔。
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

    private void OnTargetingExpired(PendingSelection pending)
    {
        var notification = Serialize(new RpcNotification
        {
            Method = ProtocolMethods.NotifyTargetExpired,
            Params = McpService.PendingJson(pending),
        });

        foreach (var session in _sessions.Values)
        {
            if (pending.Seat is { } seat && session.Seat != seat)
            {
                continue;
            }

            session.Send(notification);
        }
    }

    private void OnTargetingRequest(PendingSelection pending)
    {
        var notification = Serialize(new RpcNotification
        {
            Method = ProtocolMethods.NotifyTargetRequest,
            Params = McpService.PendingJson(pending),
        });

        foreach (var session in _sessions.Values)
        {
            // 有归属座位＝只投给该座位；无归属＝投给所有（兼容未绑定/单人场景）。
            if (pending.Seat is { } seat && session.Seat != seat)
            {
                continue;
            }

            session.Send(notification);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var timeout = _options.HeartbeatTimeout.TotalMilliseconds;

        // 检查间隔自适应：超时很小时也要能及时判定（默认 45s 超时 → 5s 检查）。
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

    // ---------------------------------------------------------------- 工具方法

    private static JsonNode? Serialize<T>(T value)
        => JsonSerializer.SerializeToNode(value, ProtocolJson.Options);

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

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        StopAiDrivers();
        _listener?.Stop();

        _targetingSubscription.Dispose();
        _service.Bridge.PendingExpired -= OnTargetingExpired;
        _service.MatchChanged -= OnMatchChanged;

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

    /// <summary>单个客户端会话：座位绑定、心跳时间戳、独立写队列（慢客户端不阻塞引擎）。</summary>
    private sealed class ClientSession : IAsyncDisposable
    {
        // 推送队列**有界**：慢客户端只丢最旧的推送（view 幂等、段可由下一帧 view 兜底），
        // 不会让服务端内存随客户端卡顿线性增长。
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
            _pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        }

        /// <summary>写响应（不排队；与推送共用 writer 的串行写锁，不会交错）。</summary>
        public Task SendResponseAsync(JsonNode? node)
            => node is null ? Task.CompletedTask : _writer.WriteAsync(node, _ct);

        public string Id { get; }

        public int? Seat { get; set; }

        public long LastSeen { get; private set; } = Environment.TickCount64;

        public void Touch() => LastSeen = Environment.TickCount64;

        public void Send(JsonNode? node) => _outbound.Writer.TryWrite(node);

        /// <summary>心跳超时：关闭连接（读循环随之退出并清理会话）。</summary>
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
