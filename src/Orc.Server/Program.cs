using System.Text.Json.Nodes;
using Orc.Game.Decks;
using Orc.Server.Transport;

namespace Orc.Server;

/// <summary>
/// 服务端进程入口（多人支持 S8b；无头沙盒 S1 扩展）。
/// <para>
/// stdout **只写一行就绪报文**（非无头＝<c>{"ready":true,"port":N}</c>；无头＝同一行**追加**字段），
/// 其余日志一律走 stderr——与既有 stdio 适配层"stdout 只写协议"的约定一致，客户端据此解析实际端口。
/// </para>
/// <para>
/// 参数校验一律**早于任何目录创建**：无头专属参数在未开 <c>--headless</c> 时报参数错误并退出 <c>1</c>。
/// </para>
/// </summary>
internal static class Program
{
    private const string Usage =
        "用法：Orc.Server [--packs <dir;dir>] [--sandbox <dir>] [--port <n>] [--parent-pid <pid>]\n" +
        "                  [--headless] [--mode play|sandbox] [--run-dir <dir>] [--seat0 ai|agent] [--seat1 ai|agent]\n" +
        "                  [--seed <n>] [--deck-rules strict|lenient] [--wait-agent <秒>] [--exit-on-end|--keep-alive]\n" +
        "                  [--log-seat-views] [--allow-debug-writes] [--help]";

    private static async Task<int> Main(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed.ExitCode is { } immediate)
        {
            return immediate;
        }

        var options = parsed.Options!;

        try
        {
            Directory.CreateDirectory(options.SandboxRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败：无法创建沙盒目录 '{options.SandboxRoot}'：{ex.Message}");
            return 1;
        }

        ServerHost host;
        try
        {
            host = await ServerBootstrap.StartAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        await using (host)
        {
            // 就绪行：非无头只写既有两字段；无头为**增量**扩展（既有字段语义不变）。
            Console.Out.WriteLine(BuildReadyLine(host, options).ToJsonString());
            Console.Out.Flush();

            Console.Error.WriteLine($"Orc.Server 已就绪，监听端口 {host.Port}。");

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

            try
            {
                var headlessStop = host.Headless?.Stopped ?? Task.Delay(Timeout.Infinite);
                await Task.WhenAny(stop.Task, headlessStop);

                // 让会话写队列把最后一次响应送出（200ms 内未送出可放弃；不得先杀进程再写盘）。
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"运行期异常：{ex.GetType().Name}: {ex.Message}");
                return 2;
            }

            return host.Headless?.ExitCode ?? 0;
        }
    }

    /// <summary>构造 stdout 就绪行（字段顺序稳定；<c>ai+ai</c> 时省略 agent 相关字段而非写 <c>null</c>）。</summary>
    private static JsonObject BuildReadyLine(ServerHost host, ServerOptions options)
    {
        var ready = new JsonObject
        {
            ["ready"] = true,
            ["port"] = host.Port,
        };

        if (host.Headless is not { } headless)
        {
            return ready;
        }

        ready["headless"] = true;
        ready["mode"] = options.Mode == ServerMode.Sandbox ? "sandbox" : "play";
        ready["runDir"] = headless.RunDir;

        if (headless.AgentSeat is { } agentSeat)
        {
            ready["agentSeat"] = agentSeat;
            ready["agentTokenPath"] = headless.AgentTokenPath;
        }

        ready["eventsPath"] = headless.EventsPath;
        return ready;
    }

    // ---------------------------------------------------------------- 参数解析

    private sealed record ParseResult(ServerOptions? Options, int? ExitCode);

    private static ParseResult ParseArgs(string[] args)
    {
        var packs = new List<string>();
        var sandbox = Path.Combine(Path.GetTempPath(), "orc-server", Guid.NewGuid().ToString("N"));
        var sandboxGiven = false;
        var port = 0;
        int? parentPid = null;

        var headless = false;
        var mode = ServerMode.Play;
        string? runDir = null;
        var seat0 = SeatRole.Ai;
        var seat1 = SeatRole.Agent;
        int? seed = null;
        var deckRules = DeckRuleTier.Lenient;
        var deckRulesGiven = false;
        var waitAgent = TimeSpan.FromSeconds(300);
        var exitOnEnd = true;
        var exitOnEndGiven = false;
        var keepAliveGiven = false;
        var logSeatViews = false;
        var allowDebugWrites = false;

        // 无头专属参数在未开 --headless 时必须报错（不得静默忽略）。
        var headlessOnly = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--packs" when i + 1 < args.Length:
                    packs.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--sandbox" when i + 1 < args.Length:
                    // 统一绝对化：沙盒写根的守卫只接受绝对路径，且 <run-dir>/sandbox 的派生
                    // 在相对路径下会拼出混合分隔符并被拒绝（PathViolationException）。
                    sandbox = Path.GetFullPath(args[++i]);
                    sandboxGiven = true; // 显式给出以显式值为准（无头下不被 <run-dir>/sandbox 覆盖）
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort):
                    port = parsedPort;
                    i++;
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parent):
                    parentPid = parent;
                    i++;
                    break;

                case "--headless":
                    headless = true;
                    break;
                case "--mode" when i + 1 < args.Length:
                    switch (args[++i])
                    {
                        case "play":
                            mode = ServerMode.Play;
                            break;
                        case "sandbox":
                            mode = ServerMode.Sandbox;
                            break;
                        default:
                            return Fail($"参数错误：--mode 只接受 play|sandbox（收到 '{args[i]}'）。");
                    }

                    break;
                case "--run-dir" when i + 1 < args.Length:
                    runDir = Path.GetFullPath(args[++i]);
                    headlessOnly.Add("--run-dir");
                    break;
                case "--seat0" when i + 1 < args.Length:
                    if (!TryParseRole(args[++i], out seat0))
                    {
                        return Fail($"参数错误：--seat0 只接受 ai|agent（收到 '{args[i]}'）。");
                    }

                    headlessOnly.Add("--seat0");
                    break;
                case "--seat1" when i + 1 < args.Length:
                    if (!TryParseRole(args[++i], out seat1))
                    {
                        return Fail($"参数错误：--seat1 只接受 ai|agent（收到 '{args[i]}'）。");
                    }

                    headlessOnly.Add("--seat1");
                    break;
                case "--seed" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSeed):
                    seed = parsedSeed;
                    headlessOnly.Add("--seed");
                    i++;
                    break;
                case "--deck-rules" when i + 1 < args.Length:
                    switch (args[++i])
                    {
                        case "strict":
                            deckRules = DeckRuleTier.Strict;
                            break;
                        case "lenient":
                            deckRules = DeckRuleTier.Lenient;
                            break;
                        case "none":
                            return Fail("参数错误：无头 --deck-rules 不接受 none（只允许 strict|lenient）。");
                        default:
                            return Fail($"参数错误：--deck-rules 只接受 strict|lenient（收到 '{args[i]}'）。");
                    }

                    deckRulesGiven = true;
                    headlessOnly.Add("--deck-rules");
                    break;
                case "--wait-agent" when i + 1 < args.Length && int.TryParse(args[i + 1], out var waitSeconds):
                    waitAgent = TimeSpan.FromSeconds(waitSeconds);
                    headlessOnly.Add("--wait-agent");
                    i++;
                    break;
                case "--exit-on-end":
                    exitOnEnd = true;
                    exitOnEndGiven = true;
                    headlessOnly.Add("--exit-on-end");
                    break;
                case "--keep-alive":
                    exitOnEnd = false;
                    keepAliveGiven = true;
                    headlessOnly.Add("--keep-alive");
                    break;
                case "--log-seat-views":
                    logSeatViews = true;
                    headlessOnly.Add("--log-seat-views");
                    break;
                case "--allow-debug-writes":
                    allowDebugWrites = true;
                    break;
                case "--help":
                    Console.Error.WriteLine(Usage);
                    return new ParseResult(null, 0);

                // 取值型开关的"值缺失/非法"分支（带 when 守卫的 case 不匹配时落到这里）：
                // 不得静默忽略——参数错误一律在解析阶段退出 1。
                case "--packs" or "--sandbox" or "--port" or "--parent-pid"
                    or "--mode" or "--run-dir" or "--seat0" or "--seat1"
                    or "--seed" or "--deck-rules" or "--wait-agent":
                    return Fail($"参数错误：{args[i]} 缺少合法取值。");
            }
        }

        if (!headless && headlessOnly.Count > 0)
        {
            return Fail(
                "参数错误：无头专属参数需要 --headless：" + string.Join(", ", headlessOnly.Distinct(StringComparer.Ordinal)) + "。");
        }

        if (headless && exitOnEndGiven && keepAliveGiven)
        {
            return Fail("参数错误：--exit-on-end 与 --keep-alive 互斥。");
        }

        if (allowDebugWrites && mode != ServerMode.Sandbox)
        {
            return Fail("参数错误：--allow-debug-writes 只在 --mode sandbox 下有效。");
        }

        if (headless && seat0 == SeatRole.Agent && seat1 == SeatRole.Agent)
        {
            return Fail("参数错误：无头运行需要至少一个 ai 座位（1v1 只有一个 agent 令牌）。");
        }

        if (headless && runDir is null)
        {
            runDir = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".orc-run",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        }

        // 运行目录与沙盒写根语义不混：日志一律落 --run-dir；沙盒写根未显式给出时派生 <run-dir>/sandbox。
        if (headless && !sandboxGiven && runDir is not null)
        {
            sandbox = Path.Combine(runDir, "sandbox");
        }

        if (headless)
        {
            // 无头专属语义：lenient 默认放宽张数（与 DeckRules 三档同源）；显式 strict 保持生产口径。
            if (!deckRulesGiven)
            {
                deckRules = DeckRuleTier.Lenient;
            }
        }
        else
        {
            deckRules = DeckRuleTier.Strict;
        }

        var roles = new[] { seat0, seat1 };
        var options = new ServerOptions
        {
            PackDirectories = packs,
            SandboxRoot = sandbox,
            Port = port,
            ParentProcessId = parentPid,
            Headless = headless,
            Mode = mode,
            RunDir = runDir,
            SeatRoles = roles,
            Seed = seed,
            DeckRules = deckRules,
            WaitAgent = waitAgent,
            ExitOnEnd = headless && exitOnEnd,
            LogSeatViews = logSeatViews,
            AllowDebugWrites = allowDebugWrites,
        };

        return new ParseResult(options, null);
    }

    private static bool TryParseRole(string value, out SeatRole role)
    {
        switch (value)
        {
            case "ai":
                role = SeatRole.Ai;
                return true;
            case "agent":
                role = SeatRole.Agent;
                return true;
            default:
                role = SeatRole.Ai;
                return false;
        }
    }

    private static ParseResult Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return new ParseResult(null, 1);
    }
}
