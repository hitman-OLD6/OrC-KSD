using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Game.Decks;
using Orc.Protocol;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 服务端测试公共夹具：样例卡包定位、卡组/卡包声明、真 socket 连接、临时目录、**进程内宿主**。
/// <para>只放"与断言无关的准备工作"，任何断言都必须留在用例体内。</para>
/// </summary>
internal static class TestSupport
{
    /// <summary>记录卡包的 id（示例卡包唯一兵种）。</summary>
    public const string SampleCardId = "ai.sample.infantry";

    public static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }

    /// <summary>示例卡包的引用形态声明（id/version/guid 从 manifest 读，避免与包内容漂移）。</summary>
    public static PackageRef SamplePackage()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(SamplePackDirectory(), "manifest.json")))!;
        return new PackageRef(
            manifest["id"]!.GetValue<string>(),
            manifest["version"]!.GetValue<string>(),
            manifest["guid"]!.GetValue<string>());
    }

    public static JsonArray Deck(int size = 39)
    {
        var deck = new JsonArray();
        for (var i = 0; i < size; i++)
        {
            deck.Add(SampleCardId);
        }

        return deck;
    }

    public static IReadOnlyList<string> DeckList(int size = 39)
        => Enumerable.Repeat(SampleCardId, size).ToArray();

    public static async Task<OrcClient> ConnectAsync(
        int port,
        string host = "127.0.0.1",
        TimeSpan? heartbeatInterval = null,
        TimeSpan? heartbeatTimeout = null)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = host,
            Port = port,
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30),
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(15),
        });
        await client.InitializeAsync(new InitializeParams { ClientName = "server-tests" });
        return client;
    }

    /// <summary>读取仍在写入中的日志文件（<c>FileShare.ReadWrite</c>）。</summary>
    public static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    /// <summary>等待条件成立（轮询）；超时返回最后值（由用例决定是否断言失败）。</summary>
    public static async Task<T> WaitUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        bool throwOnTimeout = false)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var last = default(T)!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"等待条件超时（最后值：{last}）。");
        }

        return last;
    }

    /// <summary>临时目录（用例结束删除；失败不影响结论）。</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix = "orc-server-tests")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }
}

/// <summary>
/// 进程内宿主夹具（S5）：真 TCP 监听 ＋ 真 boot 链路（脚手架 ＋ 进程级注册 ＋ lobby 房间）。
/// <para>管理面经 <see cref="ServerHost.Admin"/>（与父子私有通道同一个真身）。</para>
/// </summary>
internal sealed class HostFixture : IAsyncDisposable
{
    private HostFixture(ServerHost host, ServerOptions options, string root)
    {
        Host = host;
        Options = options;
        Root = root;
    }

    public ServerHost Host { get; }

    public ServerOptions Options { get; }

    public string Root { get; }

    public int Port => Host.Port;

    public static async Task<HostFixture> StartAsync(
        IReadOnlyList<SeatConfig>? seats = null,
        ServerMode mode = ServerMode.Play,
        bool allowDebugWrites = false,
        DeckRuleTier deckRules = DeckRuleTier.None,
        TimeSpan? disconnectGrace = null,
        IReadOnlyList<string>? packs = null,
        TimeSpan? targetSelectionTimeout = null,
        bool includeSandboxPackInPacks = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sandboxRoot = Path.Combine(root, "sandbox");
        var packDirectories = new List<string>(packs ?? [TestSupport.SamplePackDirectory()]);
        if (includeSandboxPackInPacks)
        {
            // 模拟「管理员把 <sandboxRoot>/pack 也写进了 spawn 列表」：启动不该因此失败，且它不进集合初值。
            packDirectories.Add(Path.Combine(sandboxRoot, "pack"));
        }

        var options = new ServerOptions
        {
            PackDirectories = packDirectories,
            SandboxRoot = sandboxRoot,
            Port = 0,
            Seats = seats ?? [new SeatConfig(), new SeatConfig()],
            Mode = mode,
            AllowDebugWrites = allowDebugWrites,
            DeckRules = deckRules,
            EnforceDeckRules = deckRules != DeckRuleTier.None,
            RequiredDeckSize = deckRules == DeckRuleTier.None ? 0 : DeckRules.DefaultRequiredSize,
            DisconnectGrace = disconnectGrace ?? TimeSpan.FromSeconds(60),
            HeartbeatTimeout = TimeSpan.FromSeconds(45),
            TargetSelectionTimeout = targetSelectionTimeout ?? TimeSpan.FromMinutes(10),
            Label = "tests",
        };

        Directory.CreateDirectory(options.SandboxRoot);
        var host = await ServerBootstrap.StartAsync(options);
        return new HostFixture(host, options, root);
    }

    /// <summary>连接并握手。</summary>
    public Task<OrcClient> ConnectAsync(string? label = null)
    {
        var client = OrcClient.ConnectAsync(new ClientOptions { Host = "127.0.0.1", Port = Port });
        return ConnectCoreAsync(client, label);
    }

    private static async Task<OrcClient> ConnectCoreAsync(Task<OrcClient> pending, string? label)
    {
        var client = await pending;
        await client.InitializeAsync(new InitializeParams { ClientName = "server-tests", Label = label });
        return client;
    }

    /// <summary>调一条管理命令（与通道共用同一实现）。</summary>
    public Task<AdminCommandResult> AdminAsync(string op, JsonObject? args = null)
        => AdminCommandResult.InvokeAsync(Host, op, args);

    /// <summary>申请一个 controller 座位（立即返回 pending）。</summary>
    public static Task<SessionAttachResult> AttachControllerAsync(
        OrcClient client, int seat, int deckSize = 39, PackageRef? package = null, string? token = null)
        => client.AttachAsync(new SessionAttachParams
        {
            Roles = [SessionRoles.Controller],
            Seat = seat,
            DisplayName = $"seat{seat}",
            Deck = TestSupport.DeckList(deckSize),
            Packages = [package ?? TestSupport.SamplePackage()],
            Token = token,
        });

    /// <summary>申请一个 viewer（seat 为 null ＝ 全知）。</summary>
    public static Task<SessionAttachResult> AttachViewerAsync(OrcClient client, int? seat)
        => client.AttachAsync(new SessionAttachParams
        {
            Roles = [SessionRoles.Viewer],
            Seat = seat,
            DisplayName = seat is null ? "omniscient" : $"viewer{seat}",
        });

    /// <summary>申请调试面（不绑 seat；仍须回环来源）。</summary>
    public static Task<SessionAttachResult> AttachDebugAsync(OrcClient client)
        => client.AttachAsync(new SessionAttachParams { Roles = [SessionRoles.Debug] });

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响结论。
        }
    }
}

/// <summary>管理面回执（测试侧读取 <c>ok/code/payload</c>）。</summary>
internal sealed record AdminCommandResult(bool Ok, string Code, string? Message, JsonObject? Payload)
{
    public static async Task<AdminCommandResult> InvokeAsync(ServerHost host, string op, JsonObject? args)
    {
        var outcome = await host.Admin.InvokeAsync(op, args ?? new JsonObject());
        return new AdminCommandResult(
            outcome.Ok,
            outcome.Code,
            outcome.Message,
            outcome.Payload as JsonObject);
    }
}

/// <summary>推送收集器（按方法名等待，超时给可读失败）。</summary>
internal sealed class NotificationCollector : IAsyncDisposable
{
    private readonly OrcClient _client;
    private readonly ConcurrentQueue<RpcNotification> _received = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public NotificationCollector(OrcClient client)
    {
        _client = client;
        _client.NotificationReceived += OnNotification;
    }

    public IReadOnlyList<RpcNotification> Received => _received.ToArray();

    public async Task<RpcNotification> WaitAsync(string method, Func<RpcNotification, bool>? predicate = null, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var notification in _received)
            {
                if (string.Equals(notification.Method, method, StringComparison.Ordinal)
                    && (predicate is null || predicate(notification)))
                {
                    return notification;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await _signal.WaitAsync(remaining);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException(
            $"未收到推送 '{method}'（已收到：{string.Join(",", _received.Select(n => n.Method).Distinct())}）。");
    }

    public bool Has(string method)
        => _received.Any(n => string.Equals(n.Method, method, StringComparison.Ordinal));

    private void OnNotification(RpcNotification notification)
    {
        _received.Enqueue(notification);
        _signal.Release();
    }

    public ValueTask DisposeAsync()
    {
        _client.NotificationReceived -= OnNotification;
        _signal.Dispose();
        return ValueTask.CompletedTask;
    }
}
