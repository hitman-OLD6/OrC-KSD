using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 管理器实现（R1/R2）：常驻、并行托管多个服务器实例（一实例＝一进程）。
/// <para>
/// 管理器自身状态（资产库、依赖分析结果、实例清单）在实例更替后保留；
/// **不持有**任何 <c>Match</c>/<c>Player</c>/<c>Room</c> 活对象。
/// </para>
/// <para>
/// 热更新＝换进程：销毁旧子进程 → 用新 dll 起新子进程；管理器不重启（D1 手动触发）。
/// </para>
/// </summary>
public sealed class ServerManager : IManager
{
    private readonly ConcurrentDictionary<string, ManagedInstance> _instances = new(StringComparer.Ordinal);
    private readonly string _serverAssembly;
    private readonly string _workingDirectory;

    /// <summary>创建管理器。</summary>
    /// <param name="serverAssembly">服务端 dll 的绝对路径（缺省＝与本程序同目录的 <c>Orc.Server.dll</c>）。</param>
    /// <param name="workingDirectory">实例工作目录（沙盒/日志落点基准）。</param>
    public ServerManager(string? serverAssembly = null, string? workingDirectory = null)
    {
        _serverAssembly = serverAssembly ?? Path.Combine(AppContext.BaseDirectory, "Orc.Server.dll");
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public AssetLibrary Assets { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<InstanceInfo> List()
        => _instances.Values
            .Select(instance => new InstanceInfo(
                instance.InstanceId,
                instance.ProcessId,
                instance.Port,
                instance.RoomId,
                instance.Request.Mode,
                instance.RunDir,
                instance.Request.Label,
                instance.Request.PackDirectories,
                instance.StartedAt,
                instance.Running))
            .OrderBy(info => info.StartedAt)
            .ToArray();

    /// <inheritdoc />
    public async Task<ManagerReply> SpawnAsync(SpawnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);

        if (_instances.ContainsKey(request.InstanceId))
        {
            return ManagerReply.Fail(ManagerCodes.InvalidArguments, $"实例标识 '{request.InstanceId}' 已存在。");
        }

        try
        {
            var instance = await StartInstanceAsync(request, ct).ConfigureAwait(false);
            if (!_instances.TryAdd(request.InstanceId, instance))
            {
                await instance.DisposeAsync().ConfigureAwait(false);
                return ManagerReply.Fail(ManagerCodes.InvalidArguments, $"实例标识 '{request.InstanceId}' 已存在。");
            }

            return ManagerReply.Success(Describe(instance), "实例已启动。");
        }
        catch (ManagerBootException ex)
        {
            return ManagerReply.Fail(ManagerCodes.InstanceBootFailed, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ManagerReply> KillAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryRemove(instanceId, out var instance))
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"实例 '{instanceId}' 不存在。");
        }

        var port = instance.Port;
        await instance.DisposeAsync().ConfigureAwait(false);

        return ManagerReply.Success(new JsonObject
        {
            ["instanceId"] = instanceId,
            ["port"] = port,
            ["released"] = true,
        }, "实例已销毁。");
    }

    /// <inheritdoc />
    public async Task<ManagerReply> ReloadAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"实例 '{instanceId}' 不存在。");
        }

        // 换进程不换接入面：把旧实例实际端口钉进重载请求（否则 port=0 会换个随机端口，客户端要改配置）。
        var fixedPort = instance.Port;
        var request = instance.Request with { Port = fixedPort > 0 ? fixedPort : instance.Request.Port };

        // drain（可选）→ Kill(entireProcessTree) → WaitForExit → 用新 dll 起新进程 → 等就绪 → 端口复用断言。
        _instances.TryRemove(instanceId, out _);
        await instance.DisposeAsync().ConfigureAwait(false);

        // 端口复用断言：旧监听必须已释放（在起新进程**之前**探测——新进程起来后就又占住了）。
        var reusable = fixedPort <= 0 || IsPortFree(fixedPort);

        try
        {
            var replacement = await StartInstanceAsync(request, ct).ConfigureAwait(false);
            _instances[instanceId] = replacement;

            return ManagerReply.Success(new JsonObject
            {
                ["instanceId"] = instanceId,
                ["port"] = replacement.Port,
                ["portReusable"] = reusable,
                ["processId"] = replacement.ProcessId,
            }, "实例已重载（换进程）。");
        }
        catch (ManagerBootException ex)
        {
            return ManagerReply.Fail(ManagerCodes.InstanceBootFailed, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ManagerReply> AdminAsync(string instanceId, ManagerAdminCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return ManagerReply.Fail(ManagerCodes.UnknownInstance, $"实例 '{instanceId}' 不存在。");
        }

        try
        {
            return await instance.AdminAsync(call.Op, call.Args, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return ManagerReply.Fail(ManagerCodes.Internal, ex.Message);
        }
    }

    /// <summary>订阅实例事件（轮换/装配结果/座位变化回到管理器）。</summary>
    public IDisposable Subscribe(string instanceId, Action<string, JsonNode?> handler)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            throw new ArgumentException($"实例 '{instanceId}' 不存在。", nameof(instanceId));
        }

        instance.EventReceived += handler;
        return new Subscription(() => instance.EventReceived -= handler);
    }

    private async Task<ManagedInstance> StartInstanceAsync(SpawnRequest request, CancellationToken ct)
    {
        var runDir = request.RunDir ?? Path.Combine(_workingDirectory, ".orc-manager", "instances", request.InstanceId);
        Directory.CreateDirectory(runDir);

        var sandbox = request.SandboxRoot ?? Path.Combine(runDir, "sandbox");
        Directory.CreateDirectory(sandbox);

        var arguments = new List<string> { _serverAssembly };

        if (request.PackDirectories.Count > 0)
        {
            arguments.Add("--packs");
            arguments.Add(string.Join(';', request.PackDirectories));
        }

        arguments.Add("--sandbox");
        arguments.Add(sandbox);
        arguments.Add("--port");
        arguments.Add(request.Port.ToString());
        arguments.Add("--mode");
        arguments.Add(request.Mode);
        arguments.Add("--seats");
        arguments.Add(SeatsJson(request.Seats).ToJsonString());
        arguments.Add("--deck-rules");
        arguments.Add(request.DeckRules);
        arguments.Add("--disconnect-grace");
        arguments.Add(request.DisconnectGraceSeconds.ToString());

        if (request.Seed is { } seed)
        {
            arguments.Add("--seed");
            arguments.Add(seed.ToString());
        }

        if (request.AllowDebugWrites)
        {
            arguments.Add("--allow-debug-writes");
        }

        if (request.Label is { Length: > 0 } label)
        {
            arguments.Add("--label");
            arguments.Add(label);
        }

        return await ManagedInstance
            .StartAsync(request with { RunDir = runDir, SandboxRoot = sandbox }, "dotnet", arguments, ct)
            .ConfigureAwait(false);
    }

    private static JsonArray SeatsJson(IReadOnlyList<SeatSpawn> seats)
    {
        var array = new JsonArray();
        foreach (var seat in seats)
        {
            var node = new JsonObject { ["kind"] = seat.Kind };
            if (seat.DisplayName is { Length: > 0 } name)
            {
                node["displayName"] = name;
            }

            if (seat.Packages.Count > 0)
            {
                node["packages"] = new JsonArray(seat.Packages
                    .Select(package => (JsonNode)package.ToJson())
                    .ToArray());
            }

            if (seat.Deck is { Count: > 0 } deck)
            {
                node["deck"] = new JsonArray(deck
                    .Select(id => (JsonNode)JsonValue.Create(id)!)
                    .ToArray());
            }

            array.Add(node);
        }

        return array;
    }

    private static JsonObject Describe(ManagedInstance instance) => new()
    {
        ["instanceId"] = instance.InstanceId,
        ["processId"] = instance.ProcessId,
        ["port"] = instance.Port,
        ["roomId"] = instance.RoomId,
        ["runDir"] = instance.RunDir,
        ["mode"] = instance.Request.Mode,
        ["label"] = instance.Request.Label,
    };

    private static bool IsPortFree(int port)
    {
        if (port <= 0)
        {
            return true;
        }

        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>关闭管理器：先回收全部实例（避免残留子进程）。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var instanceId in _instances.Keys.ToArray())
        {
            if (_instances.TryRemove(instanceId, out var instance))
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _dispose();
            }
        }
    }
}
