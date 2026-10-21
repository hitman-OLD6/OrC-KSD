using System.Security.Cryptography;
using System.Text;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Mcp;
using Orc.Packages.Loading;
using Orc.Server.Headless;
using Orc.Server.Rooms;
using Orc.Server.Transport;

namespace Orc.Server;

/// <summary>
/// 服务端启动链路（多人支持 S8b）：装载卡池 → 组装房间/校验/装配器 → 创建服务层 → 启动监听。
/// <para>
/// 顺序要点：房间需要 launcher，而 launcher 需要服务层，服务层又需要房间控制面——
/// 用 lambda 延迟取服务实例打破这个构造环。
/// </para>
/// <para>
/// 无头沙盒（S1/S2）：<see cref="ServerOptions.Headless"/> 为真时，在任何外部连接之前预置两座位并落盘令牌；
/// 就绪回调之后才开始自动开局判定（<c>ai+ai</c> 立即开局，否则等待 agent 绑定）。
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

        // 两个开关语义不混：既有关闭开关（EnforceDeckRules=false）映射到 none，不得与三档同时给出。
        if (!options.EnforceDeckRules && options.DeckRules != DeckRuleTier.Strict)
        {
            throw new ArgumentException(
                "EnforceDeckRules=false 与 DeckRules 三档不能同时给出（前者映射到 none）。",
                nameof(options));
        }

        if (options.AllowDebugWrites && options.Mode != ServerMode.Sandbox)
        {
            throw new ArgumentException("AllowDebugWrites 只在 ServerMode.Sandbox 下有效。", nameof(options));
        }

        var tier = options.EnforceDeckRules ? options.DeckRules : DeckRuleTier.None;
        // lenient 放宽张数＝RequiredDeckSize 0（分项放宽的"张数"这一项）；strict 保持生产口径。
        var requiredDeckSize = tier == DeckRuleTier.Lenient ? 0 : options.RequiredDeckSize;

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

        // S2b：同一种子驱动卡组生成与建局，保证"同卡包 ＋ 同种子 ＋ 同参数"可复现。
        var generator = new AiDeckGenerator(pool, options.Seed is { } seed ? new Random(seed) : null);
        var launcher = new McpMatchLauncher(
            () => service ?? throw new InvalidOperationException("服务层尚未就绪。"),
            options.PackDirectories,
            () => generator.Generate(requiredDeckSize, bestEffort: tier == DeckRuleTier.Lenient),
            options.EnforceDeckRules,
            tier,
            requiredDeckSize,
            options.Seed);

        var room = new Room(
            Guid.NewGuid().ToString("N")[..8],
            options.SeatCount,
            deckValidator: new DeckRulesValidator(() => pool, options.EnforceDeckRules, requiredDeckSize, tier),
            launcher: launcher,
            addresses: () => LocalAddresses.Collect(listenerPort),
            packFingerprint: options.PackFingerprint ?? ComputePackFingerprint(options.PackDirectories));

        // S2：预置座位与令牌必须在任何外部连接之前完成（否则首个 resume 可能找不到 token）。
        var headless = options.Headless ? new HeadlessContext(options) : null;
        headless?.Prepare(generator, room);

        service = new McpService(
            new McpServiceOptions
            {
                Mode = options.Mode == ServerMode.Sandbox ? McpServiceMode.Authoring : McpServiceMode.Player,
                SandboxRoot = options.SandboxRoot,
                MatchId = room.RoomId,
                TargetSelectionTimeout = options.TargetSelectionTimeout,
                Seed = options.Seed,
                AgentSeat = headless?.AgentSeat,
                // S3：服务端 sandbox 默认零写面；显式 --allow-debug-writes 才注册写类。
                AllowDebugWrites = options.Mode == ServerMode.Sandbox && options.AllowDebugWrites,
            },
            new RoomControl(room));

        var host = await new ServerHost(service, options, queue: null, room: room, headless: headless)
            .StartAsync(ct)
            .ConfigureAwait(false);
        listenerPort = host.Port;
        headless?.Attach(host);

        // 父进程兜底：客户端崩溃（无 FIN、无正常退出）时本进程随之退出。
        _ = ParentWatchdog.Start(options.ParentProcessId, ct: ct);

        onReady?.Invoke(host.Port);

        // 监听就绪后启动编排：ai+ai 立即开局；有 agent 座位则开始等待计时。
        headless?.Arm();
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
