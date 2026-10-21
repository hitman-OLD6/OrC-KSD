using System.Diagnostics;
using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 无头沙盒真进程用例（S8 用例 B 层）：就绪行、退出码、keep-alive、逐行 flush 与脚本入口。
/// <para>
/// 只断言进程层能负责的一半：CLI 字段、退出码、文件存在性；段连续性等引擎断言留在
/// <see cref="HeadlessTests"/>。全部标 <c>Category=Process</c>（可用 <c>--filter "Category!=Process"</c> 跳过）。
/// </para>
/// </summary>
[Trait("Category", "Process")]
public class ProcessHeadlessTests
{
    /// <summary>无头就绪行：单行 JSON ＋ 追加字段（<c>ai+ai</c> 省略 agent 字段）。</summary>
    [Fact]
    public async Task HeadlessReadyLine()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --mode play --seat0 ai --seat1 ai --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --wait-agent 10 --seed 42");

        var line = server.ReadyLine;
        Assert.True(line["ready"]!.GetValue<bool>());
        Assert.True(line["port"]!.GetValue<int>() > 0);
        Assert.True(line["headless"]!.GetValue<bool>());
        Assert.Equal("play", line["mode"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(line["runDir"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(line["eventsPath"]!.GetValue<string>()));
        Assert.DoesNotContain("agentSeat", line.Select(pair => pair.Key));
        Assert.DoesNotContain("agentTokenPath", line.Select(pair => pair.Key));
    }

    /// <summary>非无头模式：就绪行仍是 <c>{"ready":true,"port":N}</c>（无追加字段）。</summary>
    [Fact]
    public async Task NonHeadlessReadyLineUnchanged()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--port 0 --sandbox \"{Path.Combine(temp.Path, "sandbox")}\"");

        Assert.Equal(
            new[] { "ready", "port" },
            server.ReadyLine.Select(pair => pair.Key).ToArray());
    }

    /// <summary>无头专属参数在非无头下报参数错误退出 <c>1</c>，且不创建任何目录。</summary>
    [Fact]
    public async Task HeadlessOnlyArgsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "should-not-exist");

        var result = await ServerProcessHarness.RunToExitAsync(
            $"--port 0 --run-dir \"{runDir}\"");

        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(runDir));
        Assert.Contains("--headless", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>等待 agent 超时：退出码 <c>3</c> ＋ <c>summary.json{aborted:true,reason:"wait-agent-timeout"}</c>。</summary>
    [Fact]
    public async Task WaitAgentTimeout()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --seat0 ai --seat1 agent --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --wait-agent 1");

        var exitCode = await server.WaitForExitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(3, exitCode);

        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!;
        Assert.True(summary["aborted"]!.GetValue<bool>());
        Assert.Equal("wait-agent-timeout", summary["reason"]!.GetValue<string>());
    }

    /// <summary>用例 B：ai+agent 真进程打一局（agent 认输收尾）→ 退出码 <c>0</c> ＋ 摘要与事件流存在。</summary>
    [Fact]
    public async Task ExitCodeZeroWithSummary()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --seat0 ai --seat1 agent --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --wait-agent 30 --seed 7");

        var seat = server.ReadyLine["agentSeat"]!.GetValue<int>();
        var tokenPath = server.ReadyLine["agentTokenPath"]!.GetValue<string>();
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null),
            token => !string.IsNullOrWhiteSpace(token),
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        await using var agent = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = server.ReadyLine["port"]!.GetValue<int>(),
        });
        await agent.InitializeAsync(new InitializeParams { ClientName = "process-tests" });

        var resume = await agent.CallAsync(
            "room.resume",
            new JsonObject { ["seatToken"] = File.ReadAllText(tokenPath).Trim() });
        Assert.True(resume.Ok, resume.Message);

        // 换牌确认 + 等待 play，然后认输（终结对局 → 进程自退）。
        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await agent.CallAsync("observe.view");
                if (!view.Ok)
                {
                    return false;
                }

                var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
                if (phase == "mulligan")
                {
                    await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                    return false;
                }

                return phase == "play";
            },
            ready => ready,
            TimeSpan.FromSeconds(30),
            throwOnTimeout: true);

        var concede = await agent.CallAsync("match.concede", new JsonObject { ["owner"] = seat });
        Assert.True(concede.Ok, concede.Message);

        var exitCode = await server.WaitForExitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, exitCode);

        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json")))!;
        Assert.False(summary["aborted"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(summary["endReason"]!.GetValue<string>()));
        Assert.True(summary["winner"]!.GetValue<int>() == 0);
        Assert.True(File.Exists(Path.Combine(runDir, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "calls.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "agent.token")));
    }

    /// <summary><c>--keep-alive</c>：终局后进程存活，<c>room.start</c> 被明确拒绝（不支持再来一局）。</summary>
    [Fact]
    public async Task KeepAliveRejectsStart()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --keep-alive --seat0 ai --seat1 agent --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --wait-agent 30");

        var seat = server.ReadyLine["agentSeat"]!.GetValue<int>();
        var tokenPath = server.ReadyLine["agentTokenPath"]!.GetValue<string>();

        await using var agent = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = server.ReadyLine["port"]!.GetValue<int>(),
        });
        await agent.InitializeAsync(new InitializeParams { ClientName = "process-tests" });

        var token = (await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null),
            value => !string.IsNullOrWhiteSpace(value),
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true))!;

        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = token })).Ok);

        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await agent.CallAsync("observe.view");
                if (!view.Ok)
                {
                    return false;
                }

                var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
                if (phase == "mulligan")
                {
                    await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                    return false;
                }

                return phase == "play";
            },
            ready => ready,
            TimeSpan.FromSeconds(30),
            throwOnTimeout: true);

        Assert.True((await agent.CallAsync("match.concede", new JsonObject { ["owner"] = seat })).Ok);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(Path.Combine(runDir, "summary.json"))),
            exists => exists,
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);

        Assert.False(server.HasExited);

        var restart = await agent.CallAsync("room.start", new JsonObject { ["seatToken"] = token });
        Assert.False(restart.Ok);
        Assert.Equal("room-started", restart.Code);

        var info = await agent.CallAsync("room.info");
        Assert.Equal("ended", info.Details!["state"]!.GetValue<string>());
    }

    /// <summary>强杀后已产生的段仍可读（逐行 flush）——每行都能解析。</summary>
    [Fact]
    public async Task AbruptKillKeepsFlushedLines()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --seat0 ai --seat1 ai --packs {PackArg()} --run-dir \"{runDir}\" --seed 3");

        var eventsPath = Path.Combine(runDir, "events.jsonl");
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(eventsPath) ? TestSupport.ReadAllLinesShared(eventsPath).Length : 0),
            lines => lines >= 5,
            TimeSpan.FromSeconds(30),
            throwOnTimeout: true);

        server.Kill();

        var lines = TestSupport.ReadAllLinesShared(eventsPath);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.NotNull(JsonNode.Parse(line));
        }
    }

    /// <summary>运行目录结构稳定：<c>agent.token</c>/<c>events.jsonl</c>/<c>calls.jsonl</c>/<c>summary.json</c>。</summary>
    [Fact]
    public async Task RunDirLayout()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --seat0 ai --seat1 agent --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --log-seat-views --wait-agent 30");

        Assert.True(File.Exists(Path.Combine(runDir, "agent.token")));
        Assert.True(File.Exists(Path.Combine(runDir, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "calls.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "seat0.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDir, "seat1.jsonl")));
    }

    /// <summary>
    /// 无头下**显式** <c>--sandbox</c> 以显式值为准（不被 <c>&lt;run-dir&gt;/sandbox</c> 派生值覆盖）；
    /// 未显式给出时才派生。
    /// </summary>
    [Fact]
    public async Task ExplicitSandboxRootWins()
    {
        using var temp = new TestSupport.TempDirectory("orc-process");
        var runDir = Path.Combine(temp.Path, "run");
        var explicitRoot = Path.Combine(temp.Path, "explicit-sandbox");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --mode sandbox --keep-alive --seat0 ai --seat1 ai --packs {PackArg()} " +
            $"--run-dir \"{runDir}\" --sandbox \"{explicitRoot}\" --seed 3");

        Assert.True(Directory.Exists(explicitRoot));
        Assert.False(Directory.Exists(Path.Combine(runDir, "sandbox")));
    }

    /// <summary>脚本入口可用：<c>bash scripts/headless.sh --help</c> 打印参数。</summary>
    [Fact]
    public async Task ScriptHelp()
    {
        var repoRoot = FindRepositoryRoot();
        var script = Path.Combine(repoRoot, "scripts", "headless.sh");
        Assert.True(File.Exists(script), $"未找到脚本：{script}");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"\"{script}\" --help",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("--run-dir", stdout, StringComparison.Ordinal);
        Assert.Contains("--games", stdout, StringComparison.Ordinal);
        Assert.Contains("--allow-debug-writes", stdout, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static string PackArg()
        => TestSupport.SamplePackDirectory().Replace('\\', '/');

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到仓库根（OrcEngine.slnx）。");
    }
}

/// <summary>真进程夹具：启动 <c>dotnet Orc.Server.dll</c>、读就绪行、回收进程树。</summary>
internal sealed class ServerProcessHarness : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<string> _stderr = new();
    private readonly Task _stderrPump;

    private ServerProcessHarness(Process process, JsonObject readyLine)
    {
        _process = process;
        ReadyLine = readyLine;
        _stderrPump = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Add(line);
                }
            }
        });
    }

    public JsonObject ReadyLine { get; }

    public bool HasExited => _process.HasExited;

    public static async Task<ServerProcessHarness> StartAsync(string arguments)
    {
        var dll = Path.Combine(ProcessHeadlessTests.FindRepositoryRoot(), "src", "Orc.Server", "bin", "Debug", "net8.0", "Orc.Server.dll");
        Assert.True(File.Exists(dll), $"未找到服务端构建产物：{dll}");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" {arguments}",
            WorkingDirectory = ProcessHeadlessTests.FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(string.IsNullOrWhiteSpace(line), "服务端未输出就绪行。");

        return new ServerProcessHarness(process, (JsonObject)JsonNode.Parse(line!)!);
    }

    /// <summary>跑到退出（不读就绪行；用于参数错误类用例）。</summary>
    public static async Task<(int ExitCode, string Stderr)> RunToExitAsync(string arguments)
    {
        var dll = Path.Combine(ProcessHeadlessTests.FindRepositoryRoot(), "src", "Orc.Server", "bin", "Debug", "net8.0", "Orc.Server.dll");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" {arguments}",
            WorkingDirectory = ProcessHeadlessTests.FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stderr);
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        await _process.WaitForExitAsync().WaitAsync(timeout);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // 已退出。
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // 关闭路径忽略。
        }

        _process.Dispose();
    }
}
