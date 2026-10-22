using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 一个被托管的服务器实例（**一实例＝一进程**，U2）：独立 stdin/stdout 对、独立端口、独立运行目录。
/// <para>
/// 通道分流与服务器侧同一条判据：有 <c>id</c> ＝应答、无 <c>id</c> ＝事件。
/// 子进程退出时**所有在途请求以失败结束**（绝不挂起）。
/// </para>
/// </summary>
public sealed class ManagedInstance : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NdjsonWriter _writer;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pending = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly List<string> _stderrTail = new();
    private int _nextId;
    private Exception? _closedReason;
    private Task? _stdoutLoop;
    private Task? _stderrLoop;

    private ManagedInstance(string instanceId, Process process, SpawnRequest request)
    {
        InstanceId = instanceId;
        _process = process;
        Request = request;
        RunDir = request.RunDir ?? Path.Combine(Path.GetTempPath(), "orc-manager", instanceId);
        _writer = new NdjsonWriter(process.StandardInput.BaseStream);
    }

    /// <summary>实例标识（稳定字符串）。</summary>
    public string InstanceId { get; }

    /// <summary>spawn 配置（重载时复用，实现"换进程式热更新"）。</summary>
    public SpawnRequest Request { get; }

    /// <summary>运行目录。</summary>
    public string RunDir { get; }

    /// <summary>业务端口（来自就绪行）。</summary>
    public int Port { get; private set; }

    /// <summary>首个 lobby 房间标识（来自就绪行；轮换后以事件为准）。</summary>
    public string RoomId { get; private set; } = string.Empty;

    /// <summary>进程标识。</summary>
    public int ProcessId => _process.Id;

    /// <summary>启动时刻。</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>子进程事件（<c>event</c> 名 ＋ payload）。</summary>
    public event Action<string, JsonNode?>? EventReceived;

    /// <summary>进程是否仍在运行。</summary>
    public bool Running
    {
        get
        {
            try
            {
                return !_process.HasExited && _closedReason is null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>启动子进程并等待首行就绪报文。</summary>
    /// <exception cref="ManagerBootException">进程起不来/未就绪/就绪行非法。</exception>
    public static async Task<ManagedInstance> StartAsync(
        SpawnRequest request,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // 父进程（管理器）死则实例退出；reload 时旧进程用 Kill(entireProcessTree) 回收。
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString());

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ManagerBootException("无法启动服务端进程（Process.Start 返回空）。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ManagerBootException($"无法启动服务端进程：{ex.Message}", ex);
        }

        var instance = new ManagedInstance(request.InstanceId, process, request);
        instance._stderrLoop = Task.Run(() => instance.PumpStderrAsync(), CancellationToken.None);

        try
        {
            instance.Port = await instance.WaitForReadyAsync(ct).ConfigureAwait(false);
            instance._stdoutLoop = Task.Run(() => instance.PumpStdoutAsync(), CancellationToken.None);
            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>单条管理命令的等待上限（超时＝失败，绝不挂起调用方）。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>把一条管理命令发给子进程并等待回执（子进程退出/超时 ⇒ 抛，不挂起）。</summary>
    public async Task<ManagerReply> AdminAsync(string op, JsonObject? args, CancellationToken ct = default)
    {
        if (_closedReason is { } reason)
        {
            throw new IOException("实例已退出（管理通道已关闭）。", reason);
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (_closedReason is { } closed)
            {
                throw new IOException("实例已退出（管理通道已关闭）。", closed);
            }

            _pending[id] = completion;
        }

        try
        {
            await _writer.WriteAsync(new JsonObject
            {
                ["id"] = id,
                ["op"] = op,
                ["args"] = args?.DeepClone(),
            }, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            var reply = await completion.Task.ConfigureAwait(false);

            var ok = reply["ok"]?.GetValue<bool>() ?? false;
            var code = reply["code"]?.GetValue<string>();
            var message = reply["message"]?.GetValue<string>();
            var payload = reply["payload"]?.DeepClone();

            return new ManagerReply(ok, ManagerCodes.Map(code), message, payload);
        }
        catch
        {
            Take(id);
            throw;
        }
    }

    private async Task<int> WaitForReadyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Request.StartupTimeout);

        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new ManagerBootException(
                        $"服务端进程在就绪前退出（exit={TryExitCode()}）。{Environment.NewLine}{StderrTail()}");
                }

                if (!line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node?["ready"]?.GetValue<bool>() == true && node["port"] is { } portNode)
                {
                    RoomId = node["roomId"]?.GetValue<string>() ?? string.Empty;
                    return portNode.GetValue<int>();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ManagerBootException(
                $"服务端在 {Request.StartupTimeout.TotalSeconds:0} 秒内未就绪。{Environment.NewLine}{StderrTail()}");
        }
    }

    /// <summary>读循环：就绪行之后的每一行要么是回执（有 id），要么是事件（无 id）。</summary>
    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node is not JsonObject message)
                {
                    continue;
                }

                if (message.TryGetPropertyValue("id", out var idNode) && idNode is not null)
                {
                    var id = idNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                        ? idNode.GetValue<string>()
                        : idNode.ToJsonString();

                    if (Take(id) is { } completion)
                    {
                        completion.TrySetResult(message);
                    }

                    continue;
                }

                var eventName = message["event"]?.GetValue<string>() ?? "unknown";
                EventReceived?.Invoke(eventName, message["payload"]?.DeepClone());
            }
        }
        catch (Exception)
        {
            // 进程退出/流关闭：在 finally 里统一收束在途请求。
        }
        finally
        {
            var closed = new IOException("实例进程已退出（管理通道关闭）。");
            _closedReason ??= closed;

            List<TaskCompletionSource<JsonNode>> pending;
            lock (_sync)
            {
                pending = _pending.Values.ToList();
                _pending.Clear();
            }

            foreach (var completion in pending)
            {
                completion.TrySetException(closed);
            }
        }
    }

    private TaskCompletionSource<JsonNode>? Take(string id)
    {
        lock (_sync)
        {
            return _pending.Remove(id, out var completion) ? completion : null;
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_sync)
                {
                    _stderrTail.Add(line);
                    if (_stderrTail.Count > 60)
                    {
                        _stderrTail.RemoveAt(0);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 进程退出。
        }
    }

    private string StderrTail()
    {
        lock (_sync)
        {
            return _stderrTail.Count == 0 ? "（无 stderr 输出）" : string.Join(Environment.NewLine, _stderrTail);
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

    /// <summary>
    /// 回收（R1）：先关管理通道（stdin EOF 让子进程正常收尾），再 <c>Kill(entireProcessTree: true)</c> ＋ 等待退出。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 写端必须先 flush 再关流：反过来会对已关闭的 FileStream 调 Flush。
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // 已被回收/管道断开。
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // 管道已断。
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 已退出/无权限。
        }

        foreach (var task in new[] { _stdoutLoop, _stderrLoop })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径忽略。
            }
        }

        _process.Dispose();
    }
}

/// <summary>实例起不来（boot 前置不满足）。</summary>
public sealed class ManagerBootException : Exception
{
    public ManagerBootException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
