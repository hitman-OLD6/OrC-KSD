using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;

namespace Orc.Mcp.Bridge;

/// <summary>重连结果（服务端没了 vs 令牌失效必须可区分）。</summary>
public enum BridgeConnectionState
{
    /// <summary>已连接且已绑定座位。</summary>
    Ready,

    /// <summary>服务端退出/端口关闭（桥以 <c>0</c> 收尾）。</summary>
    NotConnected,

    /// <summary>令牌失效（实例已重启；桥以 <c>2</c> 退出）。</summary>
    TokenRejected,
}

/// <summary>
/// 桥对内的传输面（便于单测注入假传输；生产实现见 <see cref="OrcClientTransport"/>）。
/// <para>只做"连接 ＋ 工具调用 ＋ 推送接收"，不解析段内容做决策。</para>
/// </summary>
public interface IBridgeTransport : IAsyncDisposable
{
    /// <summary>当前连接是否可用。</summary>
    bool IsConnected { get; }

    /// <summary>服务端推送（<c>notify/*</c>）。</summary>
    event Action<RpcNotification>? NotificationReceived;

    /// <summary>服务端工具清单（原样透传，不增删）。</summary>
    Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>调用服务端工具。</summary>
    Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default);

    /// <summary>确保已连接并绑定座位（断线时读 <c>agent.token</c> 自动 <c>room.resume</c>）。</summary>
    Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="OrcClient"/> 实现：连接 → <c>initialize</c> → <c>room.resume</c>；断线自动重连并重新绑定。
/// </summary>
public sealed class OrcClientTransport : IBridgeTransport
{
    private readonly BridgeOptions _options;
    private OrcClient? _client;

    public OrcClientTransport(BridgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.IsConnected == true;

    /// <inheritdoc />
    public event Action<RpcNotification>? NotificationReceived;

    /// <inheritdoc />
    public async Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client is { IsConnected: true })
        {
            return BridgeConnectionState.Ready;
        }

        await DisposeClientAsync().ConfigureAwait(false);

        OrcClient client;
        try
        {
            client = await OrcClient.ConnectAsync(
                new ClientOptions
                {
                    Host = _options.Host,
                    Port = _options.Port,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] 连接失败：{ex.GetType().Name}: {ex.Message}");
            return BridgeConnectionState.NotConnected;
        }

        try
        {
            await client.InitializeAsync(new InitializeParams { ClientName = "orc-mcp-bridge" }, ct)
                .ConfigureAwait(false);

            if (_options.NoResume)
            {
                // 编排连接：只连不绑座。服务端据「回环 + 未绑定座位」放行调试写类工具
                // （card.write / card.load / effect.write / sandbox.* 等）。
                client.NotificationReceived += notification => NotificationReceived?.Invoke(notification);
                _client = client;
                return BridgeConnectionState.Ready;
            }

            var token = (await File.ReadAllTextAsync(_options.TokenFile!, ct).ConfigureAwait(false)).Trim();
            if (token.Length == 0)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                Console.Error.WriteLine($"[bridge] 令牌文件为空：{_options.TokenFile}");
                return BridgeConnectionState.TokenRejected;
            }

            var resume = await client.CallAsync("room.resume", new JsonObject { ["seatToken"] = token }, ct)
                .ConfigureAwait(false);

            if (!resume.Ok)
            {
                await client.DisposeAsync().ConfigureAwait(false);

                if (string.Equals(resume.Code, "unauthorized", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[bridge] 令牌失效（实例可能已重启）：请重新读取 agent.token 并重启桥。");
                    return BridgeConnectionState.TokenRejected;
                }

                Console.Error.WriteLine($"[bridge] room.resume 被拒：{resume.Code}: {resume.Message}");
                return BridgeConnectionState.NotConnected;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] 启动序列失败：{ex.GetType().Name}: {ex.Message}");
            await client.DisposeAsync().ConfigureAwait(false);
            return BridgeConnectionState.NotConnected;
        }

        client.NotificationReceived += notification => NotificationReceived?.Invoke(notification);
        _client = client;
        return BridgeConnectionState.Ready;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolDescriptorPayload>> ListToolsAsync(CancellationToken ct = default)
        => _client is null ? [] : await _client.ListToolsAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ToolResultPayload> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("桥尚未连接。");
        }

        return await _client.CallAsync(tool, arguments ?? new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisposeClientAsync().ConfigureAwait(false);

    private async Task DisposeClientAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        _client = null;
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭路径忽略。
        }
    }
}
