using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Client;

/// <summary>
/// 协议客户端（多人支持 S8）：连接、握手、请求-响应、推送订阅。
/// <para>
/// **不含业务规则**：只做「报文 ↔ 强类型 DTO」与连接管理；合法性判断一律在服务端。
/// </para>
/// </summary>
public sealed class OrcClient : IAsyncDisposable
{
    private readonly Dictionary<string, TaskCompletionSource<RpcResponse>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pendingGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private ClientOptions? _options;
    private TcpClient? _tcp;
    private NdjsonReader? _reader;
    private NdjsonWriter? _writer;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private int _nextId;
    private bool _disposed;

    /// <summary>最近一次收到入站帧的时刻（心跳空闲计时器据此复位）。</summary>
    private long _lastInbound = Environment.TickCount64;

    /// <summary>
    /// 连接终止原因（读循环/心跳结束时写入）。后续请求直接失败——
    /// 否则会出现"读循环已退出、新请求仍被加入 pending"的竞态，调用方永久挂起。
    /// </summary>
    private Exception? _closedReason;

    /// <summary>服务端主动推送（<c>notify/*</c>）。</summary>
    public event Action<RpcNotification>? NotificationReceived;

    /// <summary>连接是否仍然可用（含"读循环已结束/心跳已判死"）。</summary>
    public bool IsConnected
        => _tcp?.Connected == true && !_shutdown.IsCancellationRequested && _closedReason is null;

    /// <summary>连接到服务端（不做握手；握手请调 <see cref="InitializeAsync"/>）。</summary>
    public static async Task<OrcClient> ConnectAsync(ClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var client = new OrcClient();
        try
        {
            await client.ConnectCoreAsync(options, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectCoreAsync(ClientOptions options, CancellationToken ct)
    {
        var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectTimeout);

        try
        {
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"连接 {options.Host}:{options.Port} 超时（{options.ConnectTimeout.TotalSeconds:0} 秒）。");
        }

        _tcp = tcp;
        _options = options;
        var stream = tcp.GetStream();
        _reader = new NdjsonReader(stream);
        _writer = new NdjsonWriter(stream);
        Volatile.Write(ref _lastInbound, Environment.TickCount64);
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);
        _heartbeatLoop = options.HeartbeatInterval > TimeSpan.Zero
            ? Task.Run(() => HeartbeatLoopAsync(_shutdown.Token), CancellationToken.None)
            : Task.CompletedTask;
    }

    /// <summary>握手：协商线协议版本与契约版本。</summary>
    public async Task<InitializeResult> InitializeAsync(InitializeParams parameters, CancellationToken ct = default)
    {
        var response = await CallRawAsync(
            ProtocolMethods.Initialize,
            JsonSerializer.SerializeToNode(parameters, ProtocolJson.Options),
            ct).ConfigureAwait(false);

        if (response.Error is { } error)
        {
            throw new InvalidOperationException($"握手失败：{error.Code} {error.Message}");
        }

        return response.Result?.Deserialize<InitializeResult>(ProtocolJson.Options)
            ?? throw new InvalidOperationException("握手响应缺少 result。");
    }

    /// <summary>工具目录。</summary>
    public async Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default)
    {
        var response = await CallRawAsync(ProtocolMethods.ToolsList, null, ct).ConfigureAwait(false);
        if (response.Error is { } error)
        {
            throw new InvalidOperationException($"tools/list 失败：{error.Code} {error.Message}");
        }

        return response.Result?.Deserialize<IReadOnlyList<ToolDescriptorPayload>>(ProtocolJson.Options)
            ?? [];
    }

    /// <summary>调用工具。</summary>
    public async Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        var parameters = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments ?? new JsonObject(),
        };

        var response = await CallRawAsync(ProtocolMethods.ToolsCall, parameters, ct).ConfigureAwait(false);

        if (response.Error is { } error)
        {
            throw new InvalidOperationException($"协议错误：{error.Code} {error.Message}");
        }

        return response.Result?.Deserialize<ToolResultPayload>(ProtocolJson.Options)
            ?? throw new InvalidOperationException("tools/call 响应缺少 result。");
    }

    /// <summary>发送一个请求并等待响应（协议层原语）。</summary>
    public async Task<RpcResponse> CallRawAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null)
        {
            throw new InvalidOperationException("尚未连接。");
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _pendingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 关闭检查必须与"登记"处在同一临界区内：否则会出现
            // "检查通过 → 读循环判死并清空 pending → 本请求登记" 的竞态，
            // 留下一个永远不会被完成的条目（F12 的漏点，只在并发压力下显现）。
            if (_closedReason is { } reason)
            {
                throw new IOException("连接已关闭（服务端断开或网络中断）。", reason);
            }

            _pending.Add(id, completion);
        }
        finally
        {
            _pendingGate.Release();
        }

        try
        {
            var request = new RpcRequest { Id = id, Method = method, Params = parameters };
            await _writer.WriteAsync(JsonSerializer.SerializeToNode(request, ProtocolJson.Options), ct).ConfigureAwait(false);

            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            // 写失败/调用方取消：pending 条目必须拆掉（否则字典随失败次数增长，且该 id 永不回收）。
            TakePending(id, out _);
            throw;
        }
    }

    /// <summary>取走并移除一个 pending 条目（同步；读循环与写失败清理共用）。</summary>
    private bool TakePending(string id, out TaskCompletionSource<RpcResponse>? completion)
    {
        completion = null;
        try
        {
            _pendingGate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            return _pending.Remove(id, out completion);
        }
        finally
        {
            try
            {
                _pendingGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 关闭中。
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader is not null)
            {
                var node = await _reader.ReadAsync(ct).ConfigureAwait(false);
                if (node is null)
                {
                    break;
                }

                // 任何入站帧都算"连接活着"的证据（心跳空闲计时器据此复位）。
                Volatile.Write(ref _lastInbound, Environment.TickCount64);

                if (node is not JsonObject response)
                {
                    continue; // 非对象帧：忽略，不杀读循环。
                }

                if (response.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                {
                    // 本端只发字符串 id：数字/其它形态的 id 无法匹配任何 pending → 忽略。
                    // （旧实现直接 GetValue<string>()，第三方客户端发数字 id 时会把读循环打挂。）
                    if (idNode.GetValueKind() != JsonValueKind.String
                        || !TakePending(idNode.GetValue<string>(), out var completion))
                    {
                        continue;
                    }

                    try
                    {
                        completion?.TrySetResult(response.Deserialize<RpcResponse>(ProtocolJson.Options)!);
                    }
                    catch (JsonException)
                    {
                        // 响应形状不符：忽略（不得让读循环退出）。
                    }

                    continue;
                }

                if (response.TryGetPropertyValue("method", out _))
                {
                    Dispatch(response.Deserialize<RpcNotification>(ProtocolJson.Options)!);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭中。
        }
        catch (Exception ex)
        {
            FailAllPending(ex);
        }
        finally
        {
            // 读循环结束（服务端关闭/连接断开）时，未完成的请求必须失败——
            // 否则调用方会永久挂起。已完成/已取消的 TCS 不受影响。
            if (!ct.IsCancellationRequested)
            {
                var closed = new IOException("连接已关闭（服务端断开或网络中断）。");
                _closedReason ??= closed;
                FailAllPending(closed);
            }
        }
    }

    /// <summary>逐订阅者隔离异常：一个 handler 抛异常不影响读循环，也不影响其余 handler。</summary>
    private void Dispatch(RpcNotification notification)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<RpcNotification>)handler)(notification);
            }
            catch (Exception)
            {
                // 订阅者故障不得让连接变成"假死"。
            }
        }
    }

    /// <summary>
    /// 心跳（F6）：静默 ≥ <see cref="ClientOptions.HeartbeatInterval"/> 发 <c>ping</c>；
    /// 发出后 <see cref="ClientOptions.HeartbeatTimeout"/> 内无任何入站帧 → 判死并失败所有挂起请求。
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var options = _options ?? throw new InvalidOperationException("尚未连接。");
        var interval = options.HeartbeatInterval;
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(interval.TotalMilliseconds / 4, 50, 1000));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(poll, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Environment.TickCount64 - Volatile.Read(ref _lastInbound) < (long)interval.TotalMilliseconds)
            {
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.HeartbeatTimeout);

            try
            {
                await CallRawAsync(ProtocolMethods.Ping, null, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Close(new IOException(
                    $"心跳超时：{options.HeartbeatTimeout.TotalSeconds:0.##} 秒内没有任何入站帧。"));
                return;
            }
            catch (Exception ex)
            {
                Close(ex as IOException ?? new IOException("心跳失败（连接已不可用）。", ex));
                return;
            }
        }
    }

    /// <summary>判死：记录原因、关闭 socket（读循环随之退出）并失败所有挂起请求。</summary>
    private void Close(Exception reason)
    {
        _closedReason ??= reason;

        try
        {
            _tcp?.Close();
        }
        catch (Exception)
        {
            // 已关闭。
        }

        FailAllPending(reason);
    }

    private void FailAllPending(Exception exception)
    {
        _pendingGate.Wait();
        try
        {
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(exception);
            }

            _pending.Clear();
        }
        finally
        {
            _pendingGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        _tcp?.Dispose();

        foreach (var loop in new[] { _readLoop, _heartbeatLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径忽略。
            }
        }

        FailAllPending(new ObjectDisposedException(nameof(OrcClient)));
        _pendingGate.Dispose();
        _shutdown.Dispose();
    }
}
