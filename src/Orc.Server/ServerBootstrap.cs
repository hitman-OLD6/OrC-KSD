using System.Security.Cryptography;
using System.Text;
using Orc.Game.Cards;
using Orc.Mcp;
using Orc.Packages.Loading;
using Orc.Server.Rooms;
using Orc.Server.Transport;

namespace Orc.Server;

/// <summary>
/// 服务端启动链路（多人支持 S8b）：装载卡池 → 组装房间/校验/装配器 → 创建服务层 → 启动监听。
/// <para>
/// 顺序要点：房间需要 launcher，而 launcher 需要服务层，服务层又需要房间控制面——
/// 用 lambda 延迟取服务实例打破这个构造环。
/// </para>
/// </summary>
public static class ServerBootstrap
{
    /// <summary>启动服务端并返回宿主（调用方负责 Dispose）。</summary>
    /// <param name="options">启动选项。</param>
    /// <param name="onReady">监听就绪回调（参数＝实际端口；用于写 stdout 就绪行）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ServerHost> StartAsync(
        ServerOptions options,
        Action<int>? onReady = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyDictionary<string, CardDefinition> pool =
            new Dictionary<string, CardDefinition>(StringComparer.Ordinal);

        if (options.PackDirectories.Count > 0)
        {
            var assembly = MatchCardAssembly.Register(options.PackDirectories);
            pool = assembly.Definitions.ToDictionary(
                entry => entry.Id,
                entry => entry.Definition,
                StringComparer.Ordinal);
        }

        var listenerPort = 0;
        McpService? service = null;

        var generator = new AiDeckGenerator(pool);
        var launcher = new McpMatchLauncher(
            () => service ?? throw new InvalidOperationException("服务层尚未就绪。"),
            options.PackDirectories,
            () => generator.Generate(options.RequiredDeckSize),
            options.EnforceDeckRules);

        var room = new Room(
            Guid.NewGuid().ToString("N")[..8],
            options.SeatCount,
            deckValidator: new DeckRulesValidator(() => pool, options.EnforceDeckRules),
            launcher: launcher,
            addresses: () => LocalAddresses.Collect(listenerPort),
            packFingerprint: options.PackFingerprint ?? ComputePackFingerprint(options.PackDirectories));

        service = new McpService(
            new McpServiceOptions
            {
                Mode = McpServiceMode.Player,
                SandboxRoot = options.SandboxRoot,
                MatchId = room.RoomId,
                TargetSelectionTimeout = options.TargetSelectionTimeout,
            },
            new RoomControl(room));

        var host = await new ServerHost(service, options, queue: null, room: room).StartAsync(ct).ConfigureAwait(false);
        listenerPort = host.Port;

        // 父进程兜底：客户端崩溃（无 FIN、无正常退出）时本进程随之退出。
        _ = ParentWatchdog.Start(options.ParentProcessId, ct: ct);

        onReady?.Invoke(host.Port);
        return host;
    }

    /// <summary>
    /// 卡包内容指纹（两端对账用）：对卡包目录下全部文件按路径排序后做 SHA-256，取前 16 位。
    /// <para>只用于"双方是否同一份卡包"的对账，不构成安全边界（R12：不承诺防作弊）。</para>
    /// </summary>
    public static string? ComputePackFingerprint(IReadOnlyList<string> packDirectories)
    {
        if (packDirectories.Count == 0)
        {
            return null;
        }

        using var sha = SHA256.Create();

        foreach (var directory in packDirectories.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                var nameBytes = Encoding.UTF8.GetBytes(relative);
                sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);

                var content = File.ReadAllBytes(file);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant()[..16];
    }
}
