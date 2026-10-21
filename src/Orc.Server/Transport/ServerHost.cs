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
    private readonly Orc.Server.Headless.HeadlessContext? _headless;
    private readonly bool _ownsQueue;
    private readonly Dictionary<int, Task> _aiDrivers = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _disconnectedSeats = new();

    /// <summary>宽限判负的在途去重（同一座位在途期间不重复入队）。</summary>
    private readonly ConcurrentDictionary<int, byte> _graceInFlight = new();
    private CancellationTokenSource? _aiCts;
    private bool _matchEnded;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly IDisposable _targetingSubscription;
    private readonly List<TcpClient> _clients = new();

    private static readonly FrozenSet<string> SeatReleasingTools =
        new[] { "room.release", "room.kick", "room.add_ai" }
            .ToFrozenSet(StringComparer.Ordinal);

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _heartbeatLoop;

    public ServerHost(
        McpService service,
        ServerOptions options,
        SerialWorkQueue? queue = null,
        Orc.Server.Rooms.Room? room = null,
        Orc.Server.Headless.HeadlessContext? headless = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _queue = queue ?? new SerialWorkQueue();
        _room = room;
        _headless = headless;
        _ownsQueue = queue is null;

        // 段由本宿主取用（服务层不再自行取段），取到后按座位脱敏分发。
        _service.SetSegmentConsumer(Orc.Mcp.Hosting.SegmentConsumer.Host);

        _targetingSubscription = _service.SubscribeTargetingRequests(OnTargetingRequest);
        _service.Bridge.PendingExpired += OnTargetingExpired;
        _service.MatchChanged += OnMatchChanged;

        // S5③：选靶超时/取消把动作推进到完结时，完成点不在任何客户端调用栈上——
        // 订阅者只做"入队一次推送"（此刻不在消费者上下文，不能同步投影）。
        _service.Executor.Settled += OnActionSettled;
    }

    /// <summary>实际监听端口（<see cref="ServerOptions.Port"/> 为 0 时由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>当前已连接会话数（诊断/测试）。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>服务层门面（诊断/测试/宿主工具用）。</summary>
    public McpService Service => _service;

    /// <summary>无头编排上下文（未开启无头＝<c>null</c>）。</summary>
    public Orc.Server.Headless.HeadlessContext? Headless => _headless;

    /// <summary>房间状态机（无头编排与测试用；未注入＝<c>null</c>）。</summary>
    public Orc.Server.Rooms.Room? Room => _room;

    /// <summary>串行执行队列（无头编排据此入队自动开局；唯一写者语义不变）。</summary>
    public SerialWorkQueue Queue => _queue;

    /// <summary>宿主关闭令牌（入队工作项用）。</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>某座位当前是否有绑定会话（自动开局判据的唯一事实来源；<c>Connected</c> 只用于展示）。</summary>
    public bool HasBoundSession(int seat)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Seat == seat)
            {
                return true;
            }
        }

        return false;
    }

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
        catch (SocketException)
        {
            // 连接重置（RST）：与"对端断开"同义，不得冒泡成未观察异常。
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
            ProtocolMethods.ToolsList => HandleToolsList(request, session),
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

    /// <summary>
    /// 工具清单（S3：**按会话过滤**）。已绑定座位的会话不列出调试写类工具（含 agent 桥）；
    /// 可见性与门禁共用 <see cref="SeatScopeRegistry.IsVisibleToBoundSession"/>，避免两处判定漂移。
    /// </summary>
    private RpcResponse HandleToolsList(RpcRequest request, ClientSession session)
    {
        var bound = session.Seat is not null;

        var tools = _service.Tools
            .Where(tool => !bound || SeatScopeRegistry.IsVisibleToBoundSession(tool.Name))
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

        // 门禁顺序固定（不可调换）：分类 → 座位校验 → 入队。
        // 被拒绝的请求**不入队、不碰引擎**，也绝不改绑座位；但**要**记入 calls.jsonl（谁试图做什么被拒）。
        if (Gate(session, name, ref arguments) is { } denial)
        {
            _headless?.LogCall(
                session.Id,
                session.Seat,
                session.RemoteAddress?.ToString(),
                name,
                ok: false,
                code: ResultCodes.Unauthorized,
                durationMs: 0,
                queueMs: 0,
                segmentsFrom: null,
                segmentsTo: null,
                phase: "gate");

            return RpcResponse.Success(
                request.Id,
                Serialize(ToolResultPayload.Failure(ResultCodes.Unauthorized, denial)));
        }

        var priority = name.StartsWith("target.", StringComparison.Ordinal)
            || name.StartsWith("session.", StringComparison.Ordinal)
            ? WorkPriority.Interactive
            : WorkPriority.Command;

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var enqueuedAt = startedAt;
        long queueMs = 0;
        (long? From, long? To) range = (null, null);

        McpToolResult result;
        JsonObject? callExtra = null;
        try
        {
            result = await _queue.EnqueueAsync(
                priority,
                async token =>
                {
                    queueMs = ElapsedMs(enqueuedAt);

                    // R6：应答型动作的归因必须在 InvokeAsync **之前**采集——提交成功后 Bridge 立即清空待决，
                    // 事后只剩「已应答」这一事实，再也说不出应答的是哪个动作、选了哪个候选。
                    var answer = CaptureAnswerTarget(name, arguments);

                    var outcome = await _service.InvokeAsync(name, arguments, token).ConfigureAwait(false);
                    callExtra = BuildCallExtra(name, arguments, outcome, answer);

                    // S4：drain 固定在 work item 尾部，与动作成败无关（失败动作/静置期也能落盘）。
                    range = DrainSegments();
                    DetectMatchEnd();

                    if (outcome.Ok && IsMatchAction(name))
                    {
                        await PushViewsAsync(token).ConfigureAwait(false);
                    }

                    await MaybeFinalizeAsync(token).ConfigureAwait(false);
                    return outcome;
                },
                ct).ConfigureAwait(false);
        }
        catch (WorkQueueFullException)
        {
            return RpcResponse.Success(request.Id, Serialize(ToolResultPayload.Failure(ResultCodes.Busy, "服务繁忙，请重试。")));
        }

        ApplySeatEffects(session, name, result);

        if (result.Ok && IsRoomMutation(name))
        {
            BroadcastRoom();
        }

        _headless?.LogCall(
            session.Id,
            session.Seat,
            session.RemoteAddress?.ToString(),
            name,
            result.Ok,
            result.Code,
            ElapsedMs(startedAt),
            queueMs,
            range.From,
            range.To,
            phase: "exec",
            extra: callExtra);

        return RpcResponse.Success(request.Id, Serialize(Map(result)));
    }

    /// <summary>
    /// 应答型动作的归因快照（R6）：被应答的待决请求身份。
    /// 仅当待决 sessionId 与本次调用的 <c>sessionId</c> 参数一致时采纳——否则是**陈旧**待决，
    /// 把它认成本次应答会让 calls.jsonl 指错动作（比没有归因更糟）。
    /// </summary>
    private sealed record AnswerTarget(string SessionId, string Action, IReadOnlyList<PendingCandidate> Candidates);

    /// <summary>在调用进入引擎之前抓取待决请求（仅 <c>target.submit</c> / <c>target.cancel</c>；其他工具＝null）。</summary>
    private AnswerTarget? CaptureAnswerTarget(string tool, JsonElement arguments)
    {
        if (tool is not ("target.submit" or "target.cancel"))
        {
            return null;
        }

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sessionId", out var sessionNode)
            || sessionNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var sessionId = sessionNode.GetString();
        if (string.IsNullOrWhiteSpace(sessionId) || _service.Bridge.CurrentPending is not { } pending)
        {
            return null;
        }

        return string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal)
            ? new AnswerTarget(pending.SessionId, pending.Action, pending.Candidates)
            : null;
    }

    /// <summary>
    /// 构造调用记录的归因字段（R6）：
    /// 发起记录带 <c>pendingSessionId</c>；应答记录带 <c>answers</c>/<c>action</c>/<c>picks</c>/<c>pickedRefs</c>。
    /// 段区间仍留在记录顶层（段由本次调用真实产生）——不做回填，避免延迟写破坏按 id/at 顺序读的消费者。
    /// </summary>
    private static JsonObject? BuildCallExtra(
        string tool, JsonElement arguments, McpToolResult outcome, AnswerTarget? answer)
    {
        if (answer is not null)
        {
            var picks = new JsonArray();
            var refs = new JsonArray();
            var resolvable = true;

            if (arguments.TryGetProperty("picks", out var picksNode) && picksNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in picksNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index))
                    {
                        resolvable = false;
                        continue;
                    }

                    picks.Add(index);
                    if (index < 1 || index > answer.Candidates.Count)
                    {
                        // 越界序号由引擎判 InvalidSelection —— 记录原样保留 picks，但映射不出引用。
                        resolvable = false;
                        continue;
                    }

                    refs.Add(answer.Candidates[index - 1].ReferenceKey);
                }
            }

            var extra = new JsonObject
            {
                ["answers"] = answer.SessionId,
                ["action"] = answer.Action,
                ["picks"] = picks,
            };

            if (resolvable && tool == "target.submit")
            {
                extra["pickedRefs"] = refs;
            }

            return extra;
        }

        // 发起记录：动作类调用若停在 targeting，记下待决号，供与后续应答记录配对。
        try
        {
            if (outcome.Details?["pending"]?["sessionId"]?.GetValue<string>() is { } pendingSessionId
                && !string.IsNullOrWhiteSpace(pendingSessionId))
            {
                return new JsonObject { ["pendingSessionId"] = pendingSessionId };
            }
        }
        catch (Exception)
        {
            // 载荷形状异常＝无归因字段（宁可少报，不可误报）。
        }

        return null;
    }

    private static long ElapsedMs(long sinceTimestamp)
        => (long)System.Diagnostics.Stopwatch.GetElapsedTime(sinceTimestamp).TotalMilliseconds;

    /// <summary>房间态会变化的工具（成功即广播 <c>notify/room</c>）。</summary>
    private static bool IsRoomMutation(string tool)
        => tool is "room.claim" or "room.release" or "room.kick" or "room.add_ai";

    /// <summary>
    /// 向**所有会话**广播房间态（载荷＝<c>room.info</c> 的 details，同一个序列化函数）。
    /// <para>房间内容属产品上公开的信息（需求非目标已声明 <c>room.info</c> 不做访问控制）。</para>
    /// </summary>
    private void BroadcastRoom()
    {
        if (_room is not { } room)
        {
            return;
        }

        var notification = Serialize(new RpcNotification
        {
            Method = ProtocolMethods.NotifyRoom,
            Params = Orc.Mcp.Room.RoomSnapshotJson.ToNode(room.Snapshot()),
        });

        foreach (var session in _sessions.Values)
        {
            session.Send(notification);
        }
    }

    // ---------------------------------------------------------------- 授权门禁

    /// <summary>工具分类查询（诊断/守卫测试；只读，不暴露表本身）。</summary>
    public static bool IsToolClassified(string toolName) => SeatScopeRegistry.IsClassified(toolName);

    /// <summary>工具作用域名（未登记＝<c>null</c>；专门分支＝<c>"special"</c>；诊断/守卫测试用）。</summary>
    public static string? SeatScopeOf(string toolName) => SeatScopeRegistry.ScopeName(toolName);

    /// <summary>已登记的工具名（四类 ∪ 专门分支；反查死条目用）。</summary>
    public static IReadOnlyCollection<string> RegisteredToolNames => SeatScopeRegistry.RegisteredNames;

    /// <summary>
    /// 座位门禁：返回 <c>null</c>＝放行；否则为拒绝说明（统一映射为 <c>unauthorized</c>）。
    /// <para>
    /// 未登记工具一律拒绝（默认拒绝）；<c>Bind</c>/<c>Agnostic</c> 不看座位；
    /// <c>OwnerScoped</c> 校验 <c>owner</c>；<c>SeatRequired</c> 只要求已绑定。
    /// </para>
    /// </summary>
    private string? Gate(ClientSession session, string name, ref JsonElement arguments)
    {
        if (SeatScopeRegistry.SpecialCasedTools.Contains(name))
        {
            return GateSpecial(session, name, ref arguments);
        }

        if (!SeatScopeRegistry.ToolScopes.TryGetValue(name, out var scope))
        {
            return $"工具 '{name}' 未登记座位作用域（默认拒绝）。";
        }

        switch (scope)
        {
            case SeatScope.Bind:
                return GateBind(session, name, arguments);

            case SeatScope.Agnostic:
                return null;

            case SeatScope.OwnerScoped:
                if (session.Seat is not { } seat)
                {
                    return "会话未绑定座位（请先 room.claim / room.resume）。";
                }

                if (!TryReadInt(arguments, "owner", out var owner))
                {
                    return $"工具 '{name}' 需要整数参数 owner。";
                }

                return owner == seat
                    ? null
                    : $"owner={owner} 与会话座位 {seat} 不符。";

            case SeatScope.SeatRequired:
                return session.Seat is null
                    ? "会话未绑定座位（请先 room.claim / room.resume）。"
                    : null;

            case SeatScope.OrchestratorOnly:
                // 写类调试工具：只对"回环 ＋ 未绑定座位"的编排连接可见可用（明示不设防，只防误触）。
                if (session.Seat is { } boundSeat)
                {
                    return $"工具 '{name}' 是调试写类，已绑定座位 {boundSeat} 的会话不可用。";
                }

                return SeatScopePolicy.IsTrustedLocal(session.RemoteAddress)
                    ? null
                    : $"工具 '{name}' 只接受回环来源的编排连接（当前 {session.RemoteAddress}）。";

            default:
                return $"工具 '{name}' 的作用域未知（默认拒绝）。";
        }
    }

    /// <summary>
    /// Bind 类工具的附加门禁：<c>kind:"local"</c>（房主）只接受回环来源。
    /// </summary>
    private static string? GateBind(ClientSession session, string name, JsonElement arguments)
    {
        if (!string.Equals(name, "room.claim", StringComparison.Ordinal))
        {
            return null; // room.resume：凭令牌，不看来源。
        }

        var kind = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("kind", out var kindNode)
            && kindNode.ValueKind == JsonValueKind.String
                ? kindNode.GetString()
                : null;

        if (!string.Equals(kind, Orc.Mcp.Room.ControllerKinds.Local, StringComparison.Ordinal))
        {
            return null;
        }

        return SeatScopePolicy.IsTrustedLocal(session.RemoteAddress)
            ? null
            : $"kind:\"local\"（房主）只接受回环连接来源（当前 {session.RemoteAddress}）。";
    }

    /// <summary>
    /// 专门分支：<c>observe.view</c>（座位改写/拒绝）与 <c>target.*</c>（待决归属裁决）。
    /// </summary>
    private string? GateSpecial(ClientSession session, string name, ref JsonElement arguments)
    {
        switch (name)
        {
            case "observe.view":
            {
                if (session.Seat is not { } seat)
                {
                    // S3：分流依据是运行模式，不是"有没有绑定座位"。
                    // play＝无第三方只读连接（隐藏信息）；sandbox＝未绑定会话放行且**全知**（不强制 seat）。
                    if (_options.Mode == ServerMode.Play)
                    {
                        return "会话未绑定座位（play 模式的观察面只对已入座玩家开放）。";
                    }

                    return null;
                }

                if (TryReadInt(arguments, "seat", out var requested) && requested != seat)
                {
                    return $"seat={requested} 与会话座位 {seat} 不符（已绑定会话只能读自己的视图）。";
                }

                // 缺省/相同 → 改写为会话座位（"忽略"语义，响应回显也是会话座位）。
                arguments = WithSeat(arguments, seat);
                return null;
            }

            case "target.submit":
            case "target.cancel":
            {
                var pending = _service.Bridge.CurrentPending;
                if (pending is null)
                {
                    // 门禁不抢业务错误码：交由服务层回 invalid-arguments（pending 可能在入队后消失）。
                    return null;
                }

                if (session.Seat is not { } seat)
                {
                    return "会话未绑定座位，不能应答选靶请求。";
                }

                return pending.Seat is { } owner && owner != seat
                    ? $"待决请求归属座位 {owner}（会话座位 {seat}），无权应答。"
                    : null;
            }

            default:
                // target.claim：不推进引擎、只写认领表——本批不动协议面工具集合（已在接入指南标注退役）。
                return null;
        }
    }

    private static bool TryReadInt(JsonElement arguments, string property, out int value)
    {
        value = 0;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.Number
            && node.TryGetInt32(out value);
    }

    private static JsonElement WithSeat(JsonElement arguments, int seat)
    {
        var args = new JsonObject();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                args[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        args["seat"] = seat;
        using var document = JsonDocument.Parse(args.ToJsonString());
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------------------- 座位绑定与解绑

    private static bool IsMatchAction(string tool)
        => tool.StartsWith("play.", StringComparison.Ordinal)
            || tool.StartsWith("command.", StringComparison.Ordinal)
            || tool.StartsWith("match.", StringComparison.Ordinal)
            || tool.StartsWith("mulligan.", StringComparison.Ordinal)
            || tool.StartsWith("target.", StringComparison.Ordinal);

    /// <summary>
    /// 绑定/解绑的唯一入口（`session.Seat =` 只出现在本方法与两个解绑方法内）：
    /// 成功后按工具语义绑定座位，或按房间语义拆除座位上的悬空绑定。
    /// </summary>
    private void ApplySeatEffects(ClientSession session, string toolName, McpToolResult result)
    {
        if (!result.Ok)
        {
            return;
        }

        if (SeatScopeRegistry.ScopeOf(toolName) == SeatScope.Bind
            && result.Details is { } details
            && TryReadInt(details, "seat", out var seat))
        {
            BindSeat(session, seat);
            return;
        }

        // 座位消失的三条路径：释放座位 / 踢出 / 生成 AI（原本"只绑不拆"的漏洞面）。
        if (toolName is "room.release" or "room.kick" or "room.add_ai"
            && result.Details is { } seatDetails
            && TryReadInt(seatDetails, "seat", out var released))
        {
            UnbindSeat(released);
        }
    }

    private static bool TryReadInt(JsonNode? node, string property, out int value)
    {
        value = 0;
        if (node?[property] is not { } seatNode)
        {
            return false;
        }

        try
        {
            value = seatNode.GetValue<int>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 绑定座位（不变量：一个座位最多一个活动会话；一个会话最多一个座位）。
    /// </summary>
    private void BindSeat(ClientSession session, int seat)
    {
        UnbindSeat(seat, except: session); // ⑤ 同座位其它会话解绑（半开连接 + 新连接 resume）
        UnbindSeatOf(session);             // 一个会话最多一个座位（旧座位一并拆除）
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

        // S2：自动开局触发点（会话线程，不在消费者上下文）——编排侧负责入队，不在此直接开局。
        _headless?.OnSeatBound(seat);
    }

    /// <summary>座位消失：把绑到该座位的一切会话置空。</summary>
    private void UnbindSeat(int seat, ClientSession? except = null)
    {
        foreach (var session in _sessions.Values)
        {
            if (!ReferenceEquals(session, except) && session.Seat == seat)
            {
                session.Seat = null;
            }
        }
    }

    /// <summary>会话改绑前：拆掉它的旧座位。</summary>
    private void UnbindSeatOf(ClientSession session) => session.Seat = null;

    /// <summary>座位断线：房间态立即释放（含解绑），对局态保留并记录宽限起点。</summary>
    /// <para>
    /// S2 不变量：**无头模式下房间态断线不释放座位**（座位生命周期＝进程生命周期，
    /// 令牌始终有效，桥可反复重试 <c>room.resume</c>）；非无头模式保持既有房主开房语义。
    /// </para>
    private void MarkSeatDisconnected(int seat)
    {
        if (_room is { } room)
        {
            if (room.State == Orc.Server.Rooms.RoomState.Lobby)
            {
                if (_headless is not null)
                {
                    room.MarkDisconnected(seat);
                    BroadcastRoom();
                    return;
                }

                room.DropSeat(seat);
                UnbindSeat(seat); // ④ 断线释放座位后，其它绑到该座位的会话一并解绑
                BroadcastRoom();
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

            // 同一座位在途期间不重复入队（节流）；记录**不**在这里移除——只有认输成功才移除。
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
                            if (!IsItsTurn(seat))
                            {
                                return false; // 还没轮到它：记录保留，等下一次检查（F5）
                            }

                            var outcome = await _service.ConcedeAsync(seat, token).ConfigureAwait(false);
                            if (outcome.Status != ActionOutcome.StatusCompleted)
                            {
                                return false;
                            }

                            _disconnectedSeats.TryRemove(seat, out _); // 真正认输成功后才移除
                            DrainSegments();
                            DetectMatchEnd();
                            await PushViewsAsync(token).ConfigureAwait(false); // 客户端才看得到 phase:"over"
                            await MaybeFinalizeAsync(token).ConfigureAwait(false);
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
                // 队列满/已关闭：放弃本次尝试，下一次检查再试。
                _graceInFlight.TryRemove(seat, out _);
            }
        }
    }

    /// <summary>投影的 <c>activePlayerSide</c> 是相对 viewer 的：self＝轮到该座位。</summary>
    private bool IsItsTurn(int seat)
    {
        var view = _service.ProjectView(seat);
        return view.Payload is JsonObject payload
            && payload["activePlayerSide"]?.GetValue<string>() == "self";
    }

    // ---------------------------------------------------------------- 推送

    /// <summary>动作"交出去之后自行完结"（选靶超时/取消）→ 入队一次推送。</summary>
    private void OnActionSettled(Orc.Mcp.Match.ActionOutcome outcome) => EnqueuePush();

    /// <summary>
    /// 以 <see cref="WorkPriority.Command"/> 入队一次推送（与产生变更的动作同优先级，保证顺序）。
    /// <para>队列满/已关闭时静默丢弃：view 幂等，下一次动作的推送兜底。</para>
    /// </summary>
    private void EnqueuePush()
    {
        try
        {
            _ = _queue.EnqueueAsync(
                WorkPriority.Command,
                async token =>
                {
                    DrainSegments();
                    DetectMatchEnd();
                    await PushViewsAsync(token).ConfigureAwait(false);
                    await MaybeFinalizeAsync(token).ConfigureAwait(false);
                    return true;
                },
                _shutdown.Token);
        }
        catch (WorkQueueFullException)
        {
            // 静默丢弃。
        }
        catch (ObjectDisposedException)
        {
            // 关闭中。
        }
    }

    private async Task PushViewsAsync(CancellationToken ct)
    {
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
    /// 取新增段并落盘/分发（S4：**段队列的唯一取用者**）。返回本批段序号区间（无段＝<c>(null,null)</c>）。
    /// <para>
    /// 顺序固定：全知落盘 → 脱敏落盘 → 喂回观察面缓存 → 按座位脱敏分发 <c>notify/segment</c>。
    /// 无活跃对局或空队列时**零成本短路**（不构造可见性对象、不碰磁盘）。
    /// </para>
    /// </summary>
    private (long? From, long? To) DrainSegments()
    {
        if (_service.Executor.Match is not { } match)
        {
            return (null, null);
        }

        var segments = match.Engine.TakeSegments();
        if (segments.Count == 0)
        {
            return (null, null);
        }

        var range = _headless?.LogSegments(segments) ?? (null, null);

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

        return range;
    }

    /// <summary>终局收尾（S4/S6）：写摘要 → fsync → （默认）请求停止。幂等。</summary>
    private async Task MaybeFinalizeAsync(CancellationToken token)
    {
        if (_headless is null)
        {
            return;
        }

        await _headless.MaybeFinalizeAsync(token).ConfigureAwait(false);
    }

    private void OnMatchChanged(object? sender, Orc.Mcp.Hosting.MatchLifecycleEventArgs args)
    {
        var created = args.Change == Orc.Mcp.Hosting.MatchLifecycleChange.Created;

        if (created)
        {
            StartAiDrivers(args.Match);
            BroadcastNotification(ProtocolMethods.NotifyMatchStarted);
            return;
        }

        // 销毁路径：同样按终局收尾（幂等）。
        HandleMatchEnded();
    }

    /// <summary>对局终局（S4/S6）：只置标志 ＋ 停 AI ＋ 推 <c>notify/match.ended</c>，不做任何写盘与退出。</summary>
    /// <remarks>
    /// 引擎不在终局时抛事件（<c>MatchChanged</c> 只在 attach/dispose 时触发），因此终局由 work item 尾部
    /// <see cref="DetectMatchEnd"/> 读 <c>match.State</c> 发现——仍在消费者上下文内，不新增写路径。
    /// </remarks>
    private void HandleMatchEnded()
    {
        if (_matchEnded)
        {
            return;
        }

        _matchEnded = true;
        StopAiDrivers();

        // S6：把房间推进到 ended——MarkEnded() 此前全仓无调用者，keep-alive 会停在 InMatch。
        _room?.MarkEnded();
        _headless?.OnMatchEnded();

        BroadcastNotification(ProtocolMethods.NotifyMatchEnded);
    }

    /// <summary>向所有会话广播一条空载荷通知（对局开始/结束）。</summary>
    private void BroadcastNotification(string method)
    {
        var notification = Serialize(new RpcNotification { Method = method, Params = new JsonObject() });
        foreach (var session in _sessions.Values)
        {
            session.Send(notification);
        }
    }

    /// <summary>在 work item 尾部读对局状态（消费者上下文；无活跃对局＝零成本）。</summary>
    private void DetectMatchEnd()
    {
        if (!_matchEnded && _service.Executor.Match is { State: Orc.Game.MatchState.Ended })
        {
            HandleMatchEnded();
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
                    .EnqueueAsync(
                        WorkPriority.Command,
                        async token =>
                        {
                            try
                            {
                                var step = await pilot.StepAsync(token).ConfigureAwait(false);

                                // 未行动（Performed==false）不推——否则 AI 空转会挤掉真实 view。
                                if (step.Performed)
                                {
                                    // S5①：推送必须包在 AI 步自身的 work item 内（投影依赖只读执行面＝消费者上下文）。
                                    await PushViewsAsync(token).ConfigureAwait(false);
                                }

                                return step;
                            }
                            finally
                            {
                                // S4：drain 与收尾固定在 work item 尾部、与动作成败无关——
                                // 静置期推进的段不会滞留，终局（含 AI 步抛异常）也不会漏写摘要与退出。
                                DrainSegments();
                                DetectMatchEnd();
                                await MaybeFinalizeAsync(token).ConfigureAwait(false);
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
                // 单步异常不让驱动循环整体退出（下一步仍可能推进）；但必须留痕——
                // 静默吞异常会让"终局已发生却没写摘要"这类缺陷无从定位。
                Console.Error.WriteLine($"[headless] AI 座位 {pilot.Seat} 单步异常：{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// 序列化为 JsonNode（出站统一出口）。
    /// <para>
    /// **不得**用 <c>JsonSerializer.SerializeToNode</c>：载荷里的 <c>Details</c>/<c>Params</c> 常是
    /// 已挂在父节点上的 <see cref="JsonNode"/>（例如投影产出的 <c>observe.view</c>），
    /// <c>SerializeToNode</c> 会把它原样 attach 到新节点并抛 “The node already has a parent”。
    /// 走一轮字符串再解析＝强制深拷贝，是跨 JsonNode 边界唯一稳定的做法。
    /// </para>
    /// </summary>
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

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        StopAiDrivers();
        _listener?.Stop();

        _targetingSubscription.Dispose();
        _service.Bridge.PendingExpired -= OnTargetingExpired;
        _service.MatchChanged -= OnMatchChanged;
        _service.Executor.Settled -= OnActionSettled;

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

        _headless?.Dispose();
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
            RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            _pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        }

        /// <summary>写响应（不排队；与推送共用 writer 的串行写锁，不会交错）。</summary>
        public Task SendResponseAsync(JsonNode? node)
            => node is null ? Task.CompletedTask : _writer.WriteAsync(node, _ct);

        public string Id { get; }

        /// <summary>对端远端地址（房主来源约束判定用；已断开＝null）。</summary>
        public IPAddress? RemoteAddress { get; }

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
