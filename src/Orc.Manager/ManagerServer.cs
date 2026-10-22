using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 管理器的**回环 TCP 服务**（JSON-RPC 2.0 over NDJSON）：常驻进程的唯一对外入口。
/// <para>
/// 为什么是 TCP 而不是 stdio：管理器的生命周期**长于**任何 agent——用户先启动管理器，再启动宿主，
/// 多个 agent 各自接入同一个管理器。stdio 会把"谁拉起谁"反过来（管理器变成 agent 的子进程），
/// 与 R1「常驻、并行托管多实例」直接冲突。
/// </para>
/// <para>
/// 绑定 <see cref="IPAddress.Loopback"/> 且不提供任何鉴权：管理权来源＝进程所有权，
/// 只有本机、且能连上这个回环端口的主体才算主人（与 R10 的"远程可调"口径一致——回环 IPC 不算远程协议）。
/// </para>
/// </summary>
internal sealed class ManagerServer : IAsyncDisposable
{
    private readonly IManager _manager;
    private readonly ManagerSessions _sessions = new();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    private ManagerServer(IManager manager, TcpListener listener)
    {
        _manager = manager;
        _listener = listener;
        _accept = Task.Run(AcceptLoopAsync);
    }

    /// <summary>实际监听端口（<c>0</c> 启动时由系统分配）。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>开始监听（立即返回；接受循环在后台）。</summary>
    public static ManagerServer Start(IManager manager, int port)
    {
        ArgumentNullException.ThrowIfNull(manager);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new ManagerServer(manager, listener);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeClientAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                await using var reader = new NdjsonReader(stream);
                await using var writer = new NdjsonWriter(stream);

                while (!_cts.IsCancellationRequested)
                {
                    JsonNode? node;
                    try
                    {
                        node = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        return; // 连接半开/对端断开：本连接结束，不影响其它连接。
                    }

                    if (node is null)
                    {
                        return;
                    }

                    var reply = await HandleAsync(node, _cts.Token).ConfigureAwait(false);
                    if (reply is null)
                    {
                        continue; // 通知（无 id）：不回执。
                    }

                    try
                    {
                        await writer.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 单连接故障隔离：不向接受循环冒泡。
        }
    }

    /// <summary>处理一条 JSON-RPC 帧；通知（无 <c>id</c>）返回 <c>null</c>。</summary>
    private async Task<JsonNode?> HandleAsync(JsonNode node, CancellationToken ct)
    {
        if (node is not JsonObject request)
        {
            return RpcError(null, -32600, "请求必须是 JSON 对象。");
        }

        var hasId = request.ContainsKey("id");
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"] as JsonObject;

        if (string.IsNullOrWhiteSpace(method))
        {
            return hasId ? RpcError(id, -32600, "缺少 method。") : null;
        }

        ManagerReply reply;
        try
        {
            reply = await DispatchAsync(method, parameters, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reply = ManagerReply.Fail(ManagerCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (!hasId)
        {
            return null; // 通知：执行即可，不回执。
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["ok"] = reply.Ok,
                ["code"] = reply.Code,
                ["message"] = reply.Message,
                ["payload"] = reply.Payload?.DeepClone(),
            },
        };
    }

    private async Task<ManagerReply> DispatchAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "manager.ping":
                return ManagerReply.Success(new JsonObject { ["version"] = 1 });

            case "manager.list":
            {
                var instances = new JsonArray();
                foreach (var info in _manager.List())
                {
                    instances.Add(InstanceJson(info));
                }

                return ManagerReply.Success(new JsonObject { ["instances"] = instances, ["count"] = instances.Count });
            }

            case "manager.spawn":
            {
                if (parameters is null)
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "manager.spawn 缺少 params。");
                }

                var instanceId = parameters["instanceId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "instanceId 必填（管理器内唯一）。");
                }

                return await _manager.SpawnAsync(ParseSpawn(instanceId, parameters), ct).ConfigureAwait(false);
            }

            case "manager.kill":
            {
                var instanceId = RequireInstanceId(parameters);
                return instanceId is null
                    ? ManagerReply.Fail(ManagerCodes.InvalidArguments, "instanceId 必填。")
                    : await _manager.KillAsync(instanceId, ct).ConfigureAwait(false);
            }

            case "manager.reload":
            {
                var instanceId = RequireInstanceId(parameters);
                return instanceId is null
                    ? ManagerReply.Fail(ManagerCodes.InvalidArguments, "instanceId 必填。")
                    : await _manager.ReloadAsync(instanceId, ct).ConfigureAwait(false);
            }

            case "manager.admin":
            {
                var instanceId = RequireInstanceId(parameters);
                if (instanceId is null)
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "instanceId 必填。");
                }

                var op = parameters!["op"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(op))
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "op 必填（服务器管理面操作名）。");
                }

                var args = parameters["args"] as JsonObject;
                return await _manager.AdminAsync(instanceId, new ManagerAdminCall(op, args), ct).ConfigureAwait(false);
            }

            // ---- 代持连接：让 agent 只连管理器一个 MCP，就能既当管理员又当玩家/调试面 ----

            case "manager.connect":
            {
                if (parameters is null)
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "manager.connect 缺少 params。");
                }

                return await _sessions.ConnectAsync(_manager, parameters, ct).ConfigureAwait(false);
            }

            case "manager.action":
            {
                var sessionId = parameters?["sessionId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "sessionId 必填。");
                }

                var tool = parameters["tool"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(tool))
                {
                    return ManagerReply.Fail(ManagerCodes.InvalidArguments, "tool 必填（实例工具名，如 match.end_turn）。");
                }

                var arguments = parameters["arguments"] as JsonObject;
                return await _sessions.ActionAsync(sessionId, tool, arguments, ct).ConfigureAwait(false);
            }

            case "manager.sessions":
                return _sessions.List();

            case "manager.disconnect":
            {
                var sessionId = parameters?["sessionId"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(sessionId)
                    ? ManagerReply.Fail(ManagerCodes.InvalidArguments, "sessionId 必填。")
                    : await _sessions.DisconnectAsync(sessionId).ConfigureAwait(false);
            }

            default:
                return ManagerReply.Fail(ManagerCodes.InvalidArguments, $"未知方法 '{method}'。");
        }
    }

    private static string? RequireInstanceId(JsonObject? parameters)
        => parameters?["instanceId"]?.GetValue<string>() is { Length: > 0 } id ? id : null;

    private static SpawnRequest ParseSpawn(string instanceId, JsonObject parameters)
    {
        var seats = new List<SeatSpawn>();
        if (parameters["seats"] is JsonArray seatArray)
        {
            foreach (var node in seatArray)
            {
                if (node is not JsonObject seat)
                {
                    continue;
                }

                var packages = new List<PackageRef>();
                if (seat["packages"] is JsonArray packageArray)
                {
                    foreach (var packageNode in packageArray)
                    {
                        if (packageNode?["id"]?.GetValue<string>() is { Length: > 0 } id
                            && packageNode["version"]?.GetValue<string>() is { Length: > 0 } version
                            && packageNode["guid"]?.GetValue<string>() is { Length: > 0 } guid)
                        {
                            packages.Add(new PackageRef(id, version, guid));
                        }
                    }
                }

                seats.Add(new SeatSpawn
                {
                    Kind = seat["kind"]?.GetValue<string>() is { Length: > 0 } kind ? kind : "agent",
                    DisplayName = seat["displayName"]?.GetValue<string>(),
                    Packages = packages,
                    Deck = (seat["deck"] as JsonArray)?.Select(item => item?.GetValue<string>() ?? string.Empty)
                        .Where(id => id.Length > 0)
                        .ToArray(),
                });
            }
        }

        return new SpawnRequest
        {
            InstanceId = instanceId,
            PackDirectories = (parameters["packDirectories"] as JsonArray)?
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(path => path.Length > 0)
                .ToArray() ?? [],
            Seats = seats.Count == 2 ? seats : [new SeatSpawn(), new SeatSpawn()],
            Mode = parameters["mode"]?.GetValue<string>() is { Length: > 0 } mode ? mode : "play",
            AllowDebugWrites = parameters["allowDebugWrites"]?.GetValue<bool>() ?? false,
            SandboxRoot = parameters["sandboxRoot"]?.GetValue<string>(),
            Port = parameters["port"]?.GetValue<int>() ?? 0,
            RunDir = parameters["runDir"]?.GetValue<string>(),
            Seed = parameters["seed"]?.GetValue<int>(),
            DeckRules = parameters["deckRules"]?.GetValue<string>() is { Length: > 0 } rules ? rules : "strict",
            DisconnectGraceSeconds = parameters["disconnectGraceSeconds"]?.GetValue<int>() ?? 60,
            Label = parameters["label"]?.GetValue<string>(),
            StartupTimeout = parameters["startupTimeoutSeconds"]?.GetValue<int>() is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(20),
        };
    }

    private static JsonObject InstanceJson(InstanceInfo info) => new()
    {
        ["instanceId"] = info.InstanceId,
        ["processId"] = info.ProcessId,
        ["port"] = info.Port,
        ["roomId"] = info.RoomId,
        ["mode"] = info.Mode,
        ["runDir"] = info.RunDir,
        ["label"] = info.Label,
        ["packDirectories"] = new JsonArray(info.PackDirectories
            .Select(path => (JsonNode)JsonValue.Create(path)!)
            .ToArray()),
        ["startedAt"] = info.StartedAt.ToString("O"),
        ["running"] = info.Running,
    };

    private static JsonObject RpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // 已停止。
        }

        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 接受循环已退出。
        }

        _cts.Dispose();
    }
}
