using Orc.Foundation.Actions;
using Orc.Mcp;
using Orc.Mcp.Sandbox;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Orc.Server.Transport;

namespace Orc.Server;

/// <summary>
/// 服务端启动链路（S5）：**boot 段不装任何卡池**，只做四步固定动作。
/// <list type="number">
///   <item><c>Sandbox.EnsureScaffold()</c>（幂等；manifest 自检失败**不阻断 boot**，由开局剔除降级兜住）；</item>
///   <item><c>BuiltInPlugin.RegisterProcessDefaults()</c> ＋ <c>FoundationActions.Register()</c>（进程级注册前移）；</item>
///   <item>建 lobby 房间 ＋ 2 个空 seat（AI seat 由 spawn 配置填充）；</item>
///   <item>起监听并回报就绪行。</item>
/// </list>
/// <para>
/// 判定分两个时点：**进程启动**只校验"卡包可索引"（不预装卡池）；**房间开局**才按并集装配本局卡池。
/// 因此不存在"启动装错、后续无法卸载"的路径。
/// </para>
/// </summary>
public static class ServerBootstrap
{
    /// <summary>启动服务端并返回宿主（调用方负责 Dispose）。</summary>
    /// <param name="options">启动选项。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ServerHost> StartAsync(ServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        // ① 沙盒脚手架（幂等）：沙盒包必须在本局装配前存在，不能等"第一次写卡"才惰性创建。
        EnsureScaffold(options);

        // ② 进程级注册前移（刻意变化第 10 条）：从每局的 CreateMatchAsync 迁到 boot 段，避免双真源。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();

        // ③＋④ 建 lobby 房间并起监听。
        var host = new ServerHost(options);
        await host.StartAsync(ct).ConfigureAwait(false);

        // 父进程兜底：管理器崩溃（无 FIN、无正常退出）时本进程随之退出。
        _ = ParentWatchdog.Start(options.ParentProcessId, ct: ct);

        host.NoteBootReady();
        return host;
    }

    /// <summary>
    /// 生成沙盒包脚手架**并补齐其元数据**（sidecar ＋ lock）；失败只记日志、不阻断 boot——
    /// 由开局"剔除沙盒包重试一次"的降级兜住。
    /// <para>
    /// 补元数据是必需的：装配链由 lock 驱动，只生成 manifest ＋ 占位卡会让沙盒包被判非法，
    /// 从而被降级剔除——那会静默地让"调试面造出的卡"永远进不了对局。
    /// </para>
    /// </summary>
    private static void EnsureScaffold(ServerOptions options)
    {
        try
        {
            var environment = new SandboxEnvironment(new McpServiceOptions
            {
                Mode = options.Mode == ServerMode.Sandbox ? McpServiceMode.Authoring : McpServiceMode.Player,
                SandboxRoot = options.SandboxRoot,
                AllowDebugWrites = options.Mode == ServerMode.Sandbox && options.AllowDebugWrites,
            });

            environment.EnsureScaffold();
            var metadata = environment.EnsurePackageMetadata();
            if (metadata.Obstacles.Count > 0)
            {
                Console.Error.WriteLine($"[boot] 沙盒包元数据存在障碍（不阻断启动）：{string.Join("；", metadata.Obstacles)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[boot] 沙盒脚手架生成失败（不阻断启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Validate(ServerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SandboxRoot);

        if (options.Seats.Count != 2)
        {
            throw new ArgumentException($"seat 数恒为 2（引擎 1v1）；收到 {options.Seats.Count}。", nameof(options));
        }

        if (options.AllowDebugWrites && options.Mode != ServerMode.Sandbox)
        {
            throw new ArgumentException("AllowDebugWrites 只在 ServerMode.Sandbox 下有效。", nameof(options));
        }

        // 进程启动只校验"卡包可索引"：目录存在 ＋ manifest 可解析。**不装卡池**。
        foreach (var directory in options.PackDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);

            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"卡包目录不存在（资产库未注册）：{directory}");
            }

            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException($"目录 '{directory}' 不是卡包（缺 manifest.json）：资产库不可索引。");
            }

            _ = PackageManifest.Parse(File.ReadAllText(manifestPath), out _);
        }
    }
}
