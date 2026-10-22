using System.Net.Sockets;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 管理器的轻量客户端（MCP 前端用）：长连接 ＋ **串行调用**。
/// <para>
/// 串行是刻意的：管理器操作（spawn/kill/reload/admin）本就该按到达顺序执行，
/// 串行化同时省掉了"多请求 id 匹配"的复杂度。连接断开时自动重建一次。
/// </para>
/// </summary>
internal sealed class ManagerClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _client;
    private NdjsonReader? _reader;
    private NdjsonWriter? _writer;
    private int _nextId;

    public ManagerClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>调用一个管理器方法；传输层失败时抛 <see cref="IOException"/>（由调用方映射为 MCP 错误）。</summary>
    public async Task<ManagerReply> InvokeAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct).ConfigureAwait(false);
                    return await RoundTripAsync(method, parameters, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or SocketException or ObjectDisposedException)
                {
                    // 断线重连一次：管理器重启/空闲回收后，前端不该要求 agent 重开会话。
                    await ResetAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is { Connected: true } && _reader is not null && _writer is not null)
        {
            return;
        }

        await ResetAsync().ConfigureAwait(false);

        var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        _client = client;
        _reader = new NdjsonReader(stream);
        _writer = new NdjsonWriter(stream);
    }

    private async Task<ManagerReply> RoundTripAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            request["params"] = parameters.DeepClone();
        }

        await _writer!.WriteAsync(request, ct).ConfigureAwait(false);

        var response = await _reader!.ReadAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("管理器连接已关闭（未收到回执）。");

        if (response is not JsonObject reply)
        {
            throw new IOException("管理器回执不是 JSON 对象。");
        }

        if (reply["error"] is JsonObject error)
        {
            return ManagerReply.Fail(
                ManagerCodes.Internal,
                error["message"]?.GetValue<string>() ?? "管理器返回 JSON-RPC 错误。");
        }

        var result = reply["result"] as JsonObject
            ?? throw new IOException("管理器回执缺少 result。");

        return new ManagerReply(
            result["ok"]?.GetValue<bool>() ?? false,
            result["code"]?.GetValue<string>() ?? ManagerCodes.Internal,
            result["message"]?.GetValue<string>(),
            result["payload"]?.DeepClone());
    }

    private async Task ResetAsync()
    {
        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        _reader = null;
        _writer = null;
        _client?.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
