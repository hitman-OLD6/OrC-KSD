using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;

namespace Orc.Mcp.Bridge;

/// <summary>重连结果（服务端没了 vs 令牌失效 vs 未获批准必须可区分）。</summary>
public enum BridgeConnectionState
{
    /// <summary>已连接且角色已生效（admitted / viewer / omniscient / debug）。</summary>
    Ready,

    /// <summary>服务端退出/端口关闭（桥以 <c>0</c> 收尾）。</summary>
    NotConnected,

    /// <summary>令牌失效（实例已重启/令牌随房间终结；桥以 <c>2</c> 退出）。</summary>
    TokenRejected,

    /// <summary>等待管理员批准超时（桥以 <c>3</c> 退出）。</summary>
    AdmissionTimeout,
}

/// <summary>
/// 桥对内的传输面（便于单测注入假传输；生产实现见 <see cref="OrcClientTransport"/>）。
/// <para>只做"连接 ＋ 角色接入 ＋ 工具调用 ＋ 推送接收"，不解析段内容做决策。</para>
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

    /// <summary>确保已连接且角色生效（断线时用令牌自动恢复）。</summary>
    Task<BridgeConnectionState> EnsureConnectedAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="OrcClient"/> 实现：连接 → <c>initialize</c> → <c>session.attach</c>（立即 pending）→
/// 等 <c>notify/admission</c>。断线自动重连（带令牌则跳过 pending）。
/// </summary>
public sealed class OrcClientTransport : IBridgeTransport
{
    private readonly BridgeOptions _options;
    private OrcClient? _client;
    private string? _token;

    public OrcClientTransport(BridgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _token = options.Token;
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
            await client.InitializeAsync(new InitializeParams
            {
                ClientName = "orc-mcp-bridge",
                Label = _options.Label,
            }, ct).ConfigureAwait(false);

            var admission = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnNotification(RpcNotification notification)
            {
                if (string.Equals(notification.Method, ProtocolMethods.NotifyAdmission, StringComparison.Ordinal)
                    && notification.Params is JsonObject payload)
                {
                    admission.TrySetResult((JsonObject)payload.DeepClone());
                }

                NotificationReceived?.Invoke(notification);
            }

            client.NotificationReceived += OnNotification;

            var attachParams = _options.ToAttachParams();
            if (_token is { Length: > 0 } token)
            {
                attachParams = attachParams with { Token = token };
            }

            var attached = await client.AttachAsync(attachParams, ct).ConfigureAwait(false);

            if (!string.Equals(attached.Code, ResultCodes.Ok, StringComparison.Ordinal))
            {
                client.NotificationReceived -= OnNotification;
                await client.DisposeAsync().ConfigureAwait(false);

                if (string.Equals(attached.Code, ResultCodes.InvalidToken, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("[bridge] 座位令牌失效（实例可能已重启）：请重新申请并让管理员批准。");
                    return BridgeConnectionState.TokenRejected;
                }

                Console.Error.WriteLine($"[bridge] session.attach 被拒：{attached.Code}: {attached.Message}");
                return BridgeConnectionState.NotConnected;
            }

            if (attached.State is SessionStates.Debug or SessionStates.Admitted or SessionStates.Viewer
                or SessionStates.Omniscient)
            {
                _client = client;
                return BridgeConnectionState.Ready;
            }

            // 立即 pending：不为等批准挂起请求，改等管理员推送的 notify/admission。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.AdmitTimeout);

            JsonObject decision;
            try
            {
                decision = await admission.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                client.NotificationReceived -= OnNotification;
                await client.DisposeAsync().ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"[bridge] 等待管理员批准超时（{_options.AdmitTimeout.TotalSeconds:0} 秒）。请让管理员执行 conn.admit。");
                return BridgeConnectionState.AdmissionTimeout;
            }

            var verdict = decision["decision"]?.GetValue<string>();
            if (!string.Equals(verdict, "admitted", StringComparison.Ordinal))
            {
                client.NotificationReceived -= OnNotification;
                await client.DisposeAsync().ConfigureAwait(false);
                Console.Error.WriteLine(
                    $"[bridge] 接入许可未通过：{verdict}（{decision["reason"]?.GetValue<string>()}）。");
                return BridgeConnectionState.NotConnected;
            }

            if (decision["seatToken"]?.GetValue<string>() is { Length: > 0 } issued)
            {
                _token = issued;
                if (_options.TokenOut is { Length: > 0 } path)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                        await File.WriteAllTextAsync(path, issued, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"[bridge] 令牌落盘失败（不影响本次连接）：{ex.Message}");
                    }
                }
            }

            _client = client;
            return BridgeConnectionState.Ready;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] 启动序列失败：{ex.GetType().Name}: {ex.Message}");
            await client.DisposeAsync().ConfigureAwait(false);
            return BridgeConnectionState.NotConnected;
        }
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
