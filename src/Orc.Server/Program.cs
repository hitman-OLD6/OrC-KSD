namespace Orc.Server;

/// <summary>
/// 服务端进程入口（多人支持 S8b）。
/// <para>
/// stdout **只写一行就绪报文**（`{"ready":true,"port":N}`），其余日志一律走 stderr——
/// 与既有 stdio 适配层"stdout 只写协议"的约定一致，客户端据此解析实际端口。
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var packs = new List<string>();
        var sandbox = Path.Combine(Path.GetTempPath(), "orc-server", Guid.NewGuid().ToString("N"));
        var port = 0;
        int? parentPid = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--packs" when i + 1 < args.Length:
                    packs.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--sandbox" when i + 1 < args.Length:
                    sandbox = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                    port = parsed;
                    i++;
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parent):
                    parentPid = parent;
                    i++;
                    break;
                case "--help":
                    Console.Error.WriteLine("用法：Orc.Server [--packs <dir;dir>] [--sandbox <dir>] [--port <n>] [--parent-pid <pid>]");
                    return 0;
            }
        }

        try
        {
            Directory.CreateDirectory(sandbox);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败：无法创建沙盒目录 '{sandbox}'：{ex.Message}");
            return 1;
        }

        var options = new ServerOptions
        {
            PackDirectories = packs,
            SandboxRoot = sandbox,
            Port = port,
            ParentProcessId = parentPid,
        };

        try
        {
            await using var host = await ServerBootstrap.StartAsync(options, readyPort =>
            {
                Console.Out.WriteLine($"{{\"ready\":true,\"port\":{readyPort}}}");
                Console.Out.Flush();
            });

            Console.Error.WriteLine($"Orc.Server 已就绪，监听端口 {host.Port}。");

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

            await stop.Task;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
