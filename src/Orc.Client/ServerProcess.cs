using System.Diagnostics;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Client;

/// <summary>服务端子进程的启动选项。</summary>
public sealed class ServerProcessOptions
{
    /// <summary>可执行文件（<c>Orc.Server</c> 或 <c>dotnet</c>）。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>命令行参数（如 <c>--packs</c>、<c>--port 0</c>）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>工作目录（可空）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>启动超时（等待 stdout 就绪行）。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>stderr 落盘路径（<c>null</c>＝只保留尾部用于错误提示）。</summary>
    public string? LogPath { get; init; }
}

/// <summary>
/// 服务端子进程管理（多人支持 S8e）：启动、读取就绪行、stderr 落盘、退出回收。
/// <para>
/// 就绪约定：stdout 首行 `{"ready":true,"port":N}`；其余输出一律 stderr。
/// </para>
/// </summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ServerProcessOptions _options;
    private readonly List<string> _stderrTail = new();
    private readonly object _stderrSync = new();

    private Task? _stderrPump;
    private bool _disposed;

    private ServerProcess(Process process, ServerProcessOptions options)
    {
        _process = process;
        _options = options;
    }

    /// <summary>实际监听端口（来自就绪行）。</summary>
    public int Port { get; private set; }

    /// <summary>子进程是否已退出（已释放时视为已退出）。</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>启动子进程并等待就绪行。</summary>
    /// <exception cref="TimeoutException">超过 <see cref="ServerProcessOptions.StartupTimeout"/> 未就绪。</exception>
    /// <exception cref="InvalidOperationException">进程提前退出或就绪行非法。</exception>
    public static async Task<ServerProcess> StartAsync(ServerProcessOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var info = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in options.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // 默认把本进程记为父进程：客户端崩溃时服务端随之退出（调用方已显式指定则不覆盖）。
        if (!options.Arguments.Contains("--parent-pid"))
        {
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
        }

        if (options.WorkingDirectory is { Length: > 0 } workingDirectory)
        {
            info.WorkingDirectory = workingDirectory;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动服务端进程。");
        var instance = new ServerProcess(process, options);
        instance._stderrPump = Task.Run(() => instance.PumpStderrAsync(), CancellationToken.None);

        try
        {
            instance.Port = await instance.WaitForReadyAsync(ct).ConfigureAwait(false);
            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> WaitForReadyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException(
                        $"服务端进程在就绪前退出（exit={TryExitCode()}）。{Environment.NewLine}{StderrTail()}");
                }

                if (!line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                var node = JsonNode.Parse(line);
                if (node?["ready"]?.GetValue<bool>() == true && node["port"] is { } portNode)
                {
                    return portNode.GetValue<int>();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"服务端在 {_options.StartupTimeout.TotalSeconds:0} 秒内未就绪。{Environment.NewLine}{StderrTail()}");
        }
    }

    private async Task PumpStderrAsync()
    {
        StreamWriter? log = null;
        try
        {
            if (_options.LogPath is { Length: > 0 } logPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            }

            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrSync)
                {
                    _stderrTail.Add(line);
                    if (_stderrTail.Count > 40)
                    {
                        _stderrTail.RemoveAt(0);
                    }
                }

                if (log is not null)
                {
                    await log.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // 进程退出/流关闭：诊断信息已尽力收集。
        }
        finally
        {
            if (log is not null)
            {
                await log.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string StderrTail()
    {
        lock (_stderrSync)
        {
            return _stderrTail.Count == 0 ? "(无 stderr 输出)" : string.Join(Environment.NewLine, _stderrTail);
        }
    }

    private string TryExitCode()
    {
        try
        {
            return _process.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>等待子进程退出并返回退出码。</summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // 已退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 无权限/已退出。
        }

        if (_stderrPump is not null)
        {
            try
            {
                await _stderrPump.ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径忽略。
            }
        }

        _process.Dispose();
    }
}
