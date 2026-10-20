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

    private TcpClient? _tcp;
    private NdjsonReader? _reader;
    private NdjsonWriter? _writer;
    private Task? _readLoop;
    private int _nextId;
    private bool _disposed;

    /// <summary>
    /// 连接终止原因（读循环结束时写入）。后续请求直接失败——
    /// 否则会出现"读循环已退出、新请求仍被加入 pending"的竞态，调用方永久挂起。
    /// </summary>
    private Exception? _closedReason;

    /// <summary>服务端主动推送（<c>notify/*</c>）。</summary>
    public event Action<RpcNotification>? NotificationReceived;

    /// <summary>连接是否仍然可用。</summary>
    public bool IsConnected => _tcp?.Connected == true && !_shutdown.IsCancellationRequested;

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
        var stream = tcp.GetStream();
        _reader = new NdjsonReader(stream);
        _writer = new NdjsonWriter(stream);
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), CancellationToken.None);
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

        if (_closedReason is { } reason)
        {
            throw new IOException("连接已关闭（服务端断开或网络中断）。", reason);
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _pendingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pending.Add(id, completion);
        }
        finally
        {
            _pendingGate.Release();
        }

        var request = new RpcRequest { Id = id, Method = method, Params = parameters };
        await _writer.WriteAsync(JsonSerializer.SerializeToNode(request, ProtocolJson.Options), ct).ConfigureAwait(false);

        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        return await completion.Task.ConfigureAwait(false);
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

                var response = node.AsObject();
                if (response.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                {
                    var id = idNode.GetValue<string>();
                    TaskCompletionSource<RpcResponse>? completion;
                    await _pendingGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        _pending.Remove(id, out completion);
                    }
                    finally
                    {
                        _pendingGate.Release();
                    }

                    completion?.TrySetResult(response.Deserialize<RpcResponse>(ProtocolJson.Options)!);
                    continue;
                }

                if (response.TryGetPropertyValue("method", out _))
                {
                    NotificationReceived?.Invoke(response.Deserialize<RpcNotification>(ProtocolJson.Options)!);
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

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
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
