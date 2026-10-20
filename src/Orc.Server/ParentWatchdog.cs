using System.Diagnostics;

namespace Orc.Server;

/// <summary>
/// 父进程存活监视（多人支持 S8e）：父进程消失即退出本进程。
/// <para>
/// 为什么不用 Windows Job Object：P/Invoke 结构体在 x86/x64 下布局不同、且不可跨平台；
/// 父 PID 轮询语义等价（父死子亡），且可在任意平台测试。
/// 代价是检测延迟（默认 2 秒）与 PID 复用窗口——服务端进程存活期短，可接受。
/// </para>
/// </summary>
public static class ParentWatchdog
{
    /// <summary>启动监视（<paramref name="parentProcessId"/> 为 null 时不做任何事）。</summary>
    /// <param name="parentProcessId">父进程 PID。</param>
    /// <param name="onParentGone">父进程消失时的回调（默认退出进程）。</param>
    /// <param name="interval">轮询间隔。</param>
    /// <param name="ct">取消令牌。</param>
    public static Task? Start(
        int? parentProcessId,
        Action? onParentGone = null,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        if (parentProcessId is not { } pid || pid <= 0)
        {
            return null;
        }

        var delay = interval ?? TimeSpan.FromSeconds(2);
        var callback = onParentGone ?? (() => Environment.Exit(0));

        return Task.Run(
            async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!IsAlive(pid))
                    {
                        callback();
                        return;
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>进程是否存活（无权限/不存在均视为不存活）。</summary>
    public static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
