using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Transport;

namespace Orc.Server.Admin;

/// <summary>
/// 父子私有通道（S2）：子进程 <c>stdin</c>（命令）/ <c>stdout</c>（回执 ＋ 事件）的 NDJSON 编解码。
/// <para>
/// 分流判据**单点**：有 <c>id</c> ＝应答、无 <c>id</c> ＝事件。未知 <c>op</c> 回 <c>ok:false</c> 且带可读码，
/// 不静默丢弃；stdout 不混入任何日志（日志一律 stderr）。
/// </para>
/// <para>
/// **禁止**把本通道做成回环端口或命名管道监听器——一旦"监听"就落在网络可达侧，
/// "远程可调"的判定口径立刻失效。
/// </para>
/// </summary>
public sealed class AdminChannel : IAsyncDisposable
{
    private readonly NdjsonReader _reader;
    private readonly NdjsonWriter _writer;
    private readonly AdminApi _api;
    private readonly ServerHost _host;
    private readonly CancellationTokenSource _shutdown = new();

    private AdminChannel(Stream stdin, Stream stdout, AdminApi api, ServerHost host)
    {
        _reader = new NdjsonReader(stdin);
        _writer = new NdjsonWriter(stdout);
        _api = api;
        _host = host;
    }

    /// <summary>在给定流上装配通道（标准入口＝进程的 stdin/stdout）。</summary>
    public static AdminChannel Attach(Stream stdin, Stream stdout, ServerHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new AdminChannel(stdin, stdout, host.Admin, host);
    }

    /// <summary>开始转发宿主事件（无 <c>id</c> 的报文）。</summary>
    public void Start()
    {
        _host.ChannelEvent += OnHostEvent;
    }

    /// <summary>命令循环：读到 EOF 即返回（**不因此停止业务协议**）。</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);

        while (!linked.IsCancellationRequested)
        {
            JsonNode? node;
            try
            {
                node = await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidDataException ex)
            {
                await WriteAsync(new JsonObject
                {
                    ["id"] = null,
                    ["ok"] = false,
                    ["code"] = ResultCodes.InvalidArguments,
                    ["message"] = $"单帧超长：{ex.Message}",
                }).ConfigureAwait(false);
                continue;
            }
            catch (System.Text.Json.JsonException)
            {
                await WriteAsync(new JsonObject
                {
                    ["id"] = null,
                    ["ok"] = false,
                    ["code"] = ResultCodes.InvalidArguments,
                    ["message"] = "报文不是合法 JSON。",
                }).ConfigureAwait(false);
                continue;
            }

            if (node is null)
            {
                return; // EOF：无管理面，但业务协议照常服务。
            }

            if (node is not JsonObject command)
            {
                await WriteAsync(new JsonObject
                {
                    ["id"] = null,
                    ["ok"] = false,
                    ["code"] = ResultCodes.InvalidArguments,
                    ["message"] = "管理报文必须是 JSON 对象。",
                }).ConfigureAwait(false);
                continue;
            }

            await HandleAsync(command, linked.Token).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(JsonObject command, CancellationToken ct)
    {
        var id = command["id"]?.DeepClone();
        var op = command["op"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        if (string.IsNullOrWhiteSpace(op))
        {
            await WriteAsync(new JsonObject
            {
                ["id"] = id,
                ["ok"] = false,
                ["code"] = ResultCodes.InvalidArguments,
                ["message"] = "管理命令缺少 'op'。",
            }).ConfigureAwait(false);
            return;
        }

        AdminOutcome outcome;
        try
        {
            outcome = await _api.InvokeAsync(op, command["args"] as JsonObject, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            outcome = AdminOutcome.Fail(ResultCodes.InvalidArguments, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            outcome = AdminOutcome.Fail(ResultCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }

        var reply = new JsonObject
        {
            ["id"] = id,
            ["ok"] = outcome.Ok,
            ["code"] = outcome.Code,
            ["message"] = outcome.Message,
        };

        if (outcome.Payload is not null)
        {
            reply["payload"] = outcome.Payload.DeepClone();
        }

        await WriteAsync(reply).ConfigureAwait(false);
    }

    private void OnHostEvent(JsonObject message) => _ = WriteAsync((JsonObject)message.DeepClone());

    private async Task WriteAsync(JsonObject node)
    {
        try
        {
            await _writer.WriteAsync(node, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 通道已关闭/对端消失：管理面不可用不影响业务协议。
        }
    }

    public async ValueTask DisposeAsync()
    {
        _host.ChannelEvent -= OnHostEvent;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _reader.DisposeAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
