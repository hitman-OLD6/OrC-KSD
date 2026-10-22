using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 管理器**代持**的玩家/调试面连接。
/// <para>
/// 为什么需要它：宿主的 MCP 列表在启动时固定，运行中新增不了连接。若玩家接入必须再配一条
/// MCP（指向实例端口），任何在会话里工作的 agent 都做不到"零代码开一局"——它改不了宿主配置。
/// 所以让管理器代持：agent 只连管理器一个 MCP，由管理器在内部建立并维持到实例的连接，
/// agent 用 <c>manager.connect</c> 拿 <c>sessionId</c>，再用 <c>manager.action</c> 转发工具调用。
/// </para>
/// <para>
/// 代持连接**由管理器自动批准**：能连上管理器回环端口的主体已经是进程主人（管理员），
/// 再让它"申请→批准"一次只是形式主义。外部真人连接仍走 <c>conn.admit</c>。
/// </para>
/// </summary>
internal sealed class ManagerSessions : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HeldSession> _sessions = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>建立一条到指定实例的连接（管理员面自动批准）。</summary>
    public async Task<ManagerReply> ConnectAsync(IManager manager, JsonObject args, CancellationToken ct)
    {
        var instanceId = args["instanceId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return ManagerReply.Fail(ManagerCodes.InvalidArguments, "instanceId 必填。");
        }

        var info = manager.List()
            .FirstOrDefault(candidate => string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal));
        if (info is null)
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"实例 '{instanceId}' 不存在。");
        }

        if (!info.Running)
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"实例 '{instanceId}' 未运行。");
        }

        var seat = args["seat"]?.GetValue<int>();
        var roles = ReadStrings(args["roles"]);
        if (roles.Count == 0)
        {
            // 缺省：给了 seat 就是兼任的玩家（否则收不到自己的棋盘），没给就是调试面。
            roles = seat is null
                ? [SessionRoles.Debug]
                : [SessionRoles.Controller, SessionRoles.Viewer];
        }

        var deck = ReadStrings(args["deck"]);
        var packages = ReadPackages(args["packages"]);
        var displayName = args["displayName"]?.GetValue<string>();

        OrcClient? client = null;
        var admission = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNotification(RpcNotification notification)
        {
            if (string.Equals(notification.Method, ProtocolMethods.NotifyAdmission, StringComparison.Ordinal))
            {
                admission.TrySetResult(notification.Params as JsonObject);
            }
        }

        try
        {
            client = await OrcClient.ConnectAsync(
                new ClientOptions { Host = "127.0.0.1", Port = info.Port }, ct).ConfigureAwait(false);

            client.NotificationReceived += OnNotification;
            var init = await client.InitializeAsync(
                new InitializeParams { ClientName = "orc-manager", Label = $"manager:{instanceId}" }, ct).ConfigureAwait(false);

            var attached = await client.AttachAsync(new SessionAttachParams
            {
                Roles = roles,
                Seat = seat,
                DisplayName = displayName,
                Deck = deck.Count > 0 ? deck : null,
                Packages = packages.Count > 0 ? packages : null,
            }, ct).ConfigureAwait(false);

            if (string.Equals(attached.Code, ResultCodes.Ok, StringComparison.Ordinal)
                && string.Equals(attached.State, SessionStates.Pending, StringComparison.Ordinal))
            {
                var approved = await manager.AdminAsync(
                    instanceId,
                    new ManagerAdminCall("conn.admit", new JsonObject { ["connectionId"] = attached.ConnectionId }),
                    ct).ConfigureAwait(false);

                if (!approved.Ok)
                {
                    return ManagerReply.Fail(
                        approved.Code,
                        approved.Message ?? "管理器自动批准失败。",
                        approved.Payload);
                }
            }
            else if (!string.Equals(attached.Code, ResultCodes.Ok, StringComparison.Ordinal))
            {
                return ManagerReply.Fail(attached.Code, attached.Message ?? "session.attach 被拒绝。");
            }

            // 调试面申请即生效；玩家面等 admission 通知。
            if (string.Equals(attached.State, SessionStates.Pending, StringComparison.Ordinal))
            {
                var payload = await admission.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (payload is null)
                {
                    return ManagerReply.Fail(ManagerCodes.Internal, "等待 admission 超时。");
                }

                if (!string.Equals(payload["decision"]?.GetValue<string>(), "admitted", StringComparison.Ordinal))
                {
                    return ManagerReply.Fail(
                        ManagerCodes.InvalidArguments,
                        $"接入被拒：{payload["decision"]?.GetValue<string>()}");
                }
            }

            var sessionId = "s" + Interlocked.Increment(ref _next);
            var session = new HeldSession(
                sessionId,
                instanceId,
                info.Port,
                init.ConnectionId,
                attached.Seat,
                attached.Roles.Count > 0 ? attached.Roles : roles,
                client);

            _sessions[sessionId] = session;

            // 把该连接**实际可用**的工具一并返回：agent 拿到 sessionId 就知道能调什么，
            // 不必再去猜或额外发一次 tools/list（这也是"零代码可读"的前提）。
            var tools = new JsonArray();
            try
            {
                foreach (var tool in await client.ListToolsAsync(ct).ConfigureAwait(false))
                {
                    tools.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                    });
                }
            }
            catch (Exception ex)
            {
                // 工具目录拿不到不影响连接本身：调用方仍可用 manager.action 试。
                tools.Add(new JsonObject { ["error"] = $"{ex.GetType().Name}: {ex.Message}" });
            }

            return ManagerReply.Success(new JsonObject
            {
                ["sessionId"] = sessionId,
                ["instanceId"] = instanceId,
                ["port"] = info.Port,
                ["connectionId"] = init.ConnectionId,
                ["seat"] = session.Seat,
                ["roles"] = new JsonArray(session.Roles.Select(role => (JsonNode)JsonValue.Create(role)!).ToArray()),
                ["tools"] = tools,
            }, "连接已建立并经管理员批准。");
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            return ManagerReply.Fail(ManagerCodes.Internal, $"建立连接失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (client is not null)
            {
                client.NotificationReceived -= OnNotification;
            }
        }
    }

    /// <summary>在已建立的连接上调用一个工具（透传 <c>tools/call</c>）。</summary>
    public async Task<ManagerReply> ActionAsync(
        string sessionId, string tool, JsonObject? arguments, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"会话 '{sessionId}' 不存在。");
        }

        try
        {
            var result = await session.Client.CallAsync(tool, arguments, ct).ConfigureAwait(false);
            return new ManagerReply(result.Ok, result.Code, result.Message, result.Details);
        }
        catch (Exception ex)
        {
            return ManagerReply.Fail(ManagerCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>列出代持会话。</summary>
    public ManagerReply List()
    {
        var items = new JsonArray();
        foreach (var session in _sessions.Values.OrderBy(item => item.SessionId, StringComparer.Ordinal))
        {
            items.Add(new JsonObject
            {
                ["sessionId"] = session.SessionId,
                ["instanceId"] = session.InstanceId,
                ["port"] = session.Port,
                ["connectionId"] = session.ConnectionId,
                ["seat"] = session.Seat,
                ["roles"] = new JsonArray(session.Roles.Select(role => (JsonNode)JsonValue.Create(role)!).ToArray()),
                ["connected"] = session.Client.IsConnected,
            });
        }

        return ManagerReply.Success(new JsonObject { ["sessions"] = items, ["count"] = items.Count });
    }

    /// <summary>断开并回收一条代持连接。</summary>
    public async Task<ManagerReply> DisconnectAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"会话 '{sessionId}' 不存在。");
        }

        await session.Client.DisposeAsync().ConfigureAwait(false);
        return ManagerReply.Success(new JsonObject { ["sessionId"] = sessionId }, "连接已断开。");
    }

    private static List<string> ReadStrings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToList()
            : [];

    private static List<PackageRef> ReadPackages(JsonNode? node)
    {
        var packages = new List<PackageRef>();
        if (node is not JsonArray array)
        {
            return packages;
        }

        foreach (var item in array)
        {
            if (item?["id"]?.GetValue<string>() is { Length: > 0 } id
                && item["version"]?.GetValue<string>() is { Length: > 0 } version
                && item["guid"]?.GetValue<string>() is { Length: > 0 } guid)
            {
                packages.Add(new PackageRef(id, version, guid));
            }
        }

        return packages;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.Client.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    private sealed record HeldSession(
        string SessionId,
        string InstanceId,
        int Port,
        string ConnectionId,
        int? Seat,
        IReadOnlyList<string> Roles,
        OrcClient Client);
}
