using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Game.Decks;
using Orc.Protocol;
using Orc.Server.Admin;
using Orc.Server.Transport;

namespace Orc.Server;

/// <summary>
/// 服务端进程入口（S2/S5）。
/// <para>
/// stdout **只写**：①首行就绪报文 <c>{"ready":true,"port":N,…}</c>；②管理通道的回执与事件。
/// 其余日志一律 stderr——stdout 是父子私有通道的一半，混入日志会让管理器解析失败。
/// </para>
/// <para>
/// 参数校验一律**早于任何目录创建**。已退役的参数（<c>--headless</c>/<c>--run-dir</c>/<c>--seat0</c>/
/// <c>--seat1</c>/<c>--wait-agent</c>/<c>--exit-on-end</c>/<c>--keep-alive</c>/<c>--log-seat-views</c>）
/// **不保留"已弃用但可用"**，出现即参数错误（否则"服务器长存"会出现例外）。
/// </para>
/// </summary>
internal static class Program
{
    private const string Usage =
        "用法：Orc.Server [--packs <dir;dir>] [--sandbox <dir>] [--port <n>] [--parent-pid <pid>]\n" +
        "                  [--mode play|sandbox] [--seats <json>] [--seed <n>]\n" +
        "                  [--deck-rules strict|lenient|none] [--required-deck-size <n>]\n" +
        "                  [--disconnect-grace <秒>] [--heartbeat <秒>] [--target-timeout <秒>]\n" +
        "                  [--allow-debug-writes] [--label <text>] [--help]";

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
        await using (var channel = AdminChannel.Attach(Console.OpenStandardInput(), Console.OpenStandardOutput(), host))
        {
            channel.Start();

            // 就绪行：字段语义与既有约定一致（只**增量**追加 roomId/label）。
            Console.Out.WriteLine(BuildReadyLine(host, options).ToJsonString());
            Console.Out.Flush();

            Console.Error.WriteLine($"Orc.Server 已就绪（instance={options.Label ?? "-"}, port={host.Port}, room={host.Room.RoomId}）。");

            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.TrySetResult();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

            _ = channel.RunAsync(host.ShutdownToken);

            try
            {
                // 服务器**长存**：只有显式停止（Ctrl+C / 进程退出）才结束；
                // 管理通道 EOF 只是"没有管理面"，不影响业务协议继续服务。
                await stop.Task.ConfigureAwait(false);

                // 让会话写队列把最后一次响应送出（不得先杀进程再写盘）。
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"运行期异常：{ex.GetType().Name}: {ex.Message}");
                return 2;
            }

            return 0;
        }
    }

    /// <summary>构造 stdout 就绪行（字段顺序稳定）。</summary>
    private static JsonObject BuildReadyLine(ServerHost host, ServerOptions options)
    {
        var ready = new JsonObject
        {
            ["ready"] = true,
            ["port"] = host.Port,
            ["roomId"] = host.Room.RoomId,
            ["mode"] = options.Mode == ServerMode.Sandbox ? "sandbox" : "play",
            ["seatCount"] = host.Room.SeatCount,
        };

        if (options.Label is { Length: > 0 } label)
        {
            ready["label"] = label;
        }

        return ready;
    }

    // ---------------------------------------------------------------- 参数解析

    private sealed record ParseResult(ServerOptions? Options, int? ExitCode);

    private static ParseResult ParseArgs(string[] args)
    {
        var packs = new List<string>();
        var sandbox = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orc-server", Guid.NewGuid().ToString("N")));
        var port = 0;
        int? parentPid = null;

        var mode = ServerMode.Play;
        var seats = new List<SeatConfig> { new(), new() };
        int? seed = null;
        var deckRules = DeckRuleTier.Strict;
        var requiredDeckSize = DeckRules.DefaultRequiredSize;
        var disconnectGrace = TimeSpan.FromSeconds(60);
        var heartbeat = TimeSpan.FromSeconds(45);
        var targetTimeout = TimeSpan.FromMinutes(10);
        var allowDebugWrites = false;
        string? label = null;

        // 已退役参数：出现即参数错误（不保留"已弃用但可用"）。
        var retired = new HashSet<string>(StringComparer.Ordinal)
        {
            "--headless", "--run-dir", "--seat0", "--seat1", "--wait-agent",
            "--exit-on-end", "--keep-alive", "--log-seat-views",
        };

        for (var i = 0; i < args.Length; i++)
        {
            if (retired.Contains(args[i]))
            {
                return Fail($"参数错误：{args[i]} 已退役（服务器长存；座位由 --seats 配置决定，令牌只能由批准签发）。");
            }

            switch (args[i])
            {
                case "--packs" when i + 1 < args.Length:
                    packs.AddRange(args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--sandbox" when i + 1 < args.Length:
                    // 统一绝对化：沙盒写根的守卫只接受绝对路径。
                    sandbox = Path.GetFullPath(args[++i]);
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort):
                    port = parsedPort;
                    i++;
                    break;
                case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parent):
                    parentPid = parent;
                    i++;
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
                case "--seats" when i + 1 < args.Length:
                    if (!TryParseSeats(args[++i], seats, out var seatsError))
                    {
                        return Fail($"参数错误：--seats 非法（{seatsError}）。");
                    }

                    break;
                case "--seed" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSeed):
                    seed = parsedSeed;
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
                            deckRules = DeckRuleTier.None;
                            break;
                        default:
                            return Fail($"参数错误：--deck-rules 只接受 strict|lenient|none（收到 '{args[i]}'）。");
                    }

                    break;
                case "--required-deck-size" when i + 1 < args.Length && int.TryParse(args[i + 1], out var size):
                    requiredDeckSize = size;
                    i++;
                    break;
                case "--disconnect-grace" when i + 1 < args.Length && int.TryParse(args[i + 1], out var grace):
                    disconnectGrace = TimeSpan.FromSeconds(grace);
                    i++;
                    break;
                case "--heartbeat" when i + 1 < args.Length && int.TryParse(args[i + 1], out var heartbeatSeconds):
                    heartbeat = TimeSpan.FromSeconds(heartbeatSeconds);
                    i++;
                    break;
                case "--target-timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var targetSeconds):
                    targetTimeout = TimeSpan.FromSeconds(targetSeconds);
                    i++;
                    break;
                case "--allow-debug-writes":
                    allowDebugWrites = true;
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i];
                    break;
                case "--help":
                    Console.Error.WriteLine(Usage);
                    return new ParseResult(null, 0);

                // 取值型开关的"值缺失/非法"分支：不得静默忽略——参数错误一律在解析阶段退出 1。
                case "--packs" or "--sandbox" or "--port" or "--parent-pid"
                    or "--mode" or "--seats" or "--seed" or "--deck-rules"
                    or "--required-deck-size" or "--disconnect-grace" or "--heartbeat"
                    or "--target-timeout" or "--label":
                    return Fail($"参数错误：{args[i]} 缺少合法取值。");
            }
        }

        if (allowDebugWrites && mode != ServerMode.Sandbox)
        {
            return Fail("参数错误：--allow-debug-writes 只在 --mode sandbox 下有效。");
        }

        var options = new ServerOptions
        {
            PackDirectories = packs,
            SandboxRoot = sandbox,
            Port = port,
            ParentProcessId = parentPid,
            Mode = mode,
            Seats = seats,
            Seed = seed,
            DeckRules = deckRules,
            EnforceDeckRules = deckRules != DeckRuleTier.None,
            RequiredDeckSize = requiredDeckSize,
            DisconnectGrace = disconnectGrace,
            HeartbeatTimeout = heartbeat,
            TargetSelectionTimeout = targetTimeout,
            AllowDebugWrites = allowDebugWrites,
            Label = label,
        };

        return new ParseResult(options, null);
    }

    /// <summary>
    /// 解析 <c>--seats</c>：<c>[{"kind":"ai","packages":[],"deck":[…],"displayName":"…"},{"kind":"agent"}]</c>。
    /// 未给出/元素缺失＝<c>agent</c> 空座。
    /// </summary>
    private static bool TryParseSeats(string json, List<SeatConfig> seats, out string? error)
    {
        error = null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                error = "需要 JSON 数组";
                return false;
            }

            if (array.Count != 2)
            {
                error = $"seat 数恒为 2（收到 {array.Count}）";
                return false;
            }

            seats.Clear();
            foreach (var node in array)
            {
                var kindText = node?["kind"]?.GetValue<string>() ?? "agent";
                var kind = kindText switch
                {
                    "ai" => SeatKind.Ai,
                    "agent" or "human" => SeatKind.Agent,
                    _ => (SeatKind?)null,
                };

                if (kind is null)
                {
                    error = $"seat.kind 只接受 ai|agent（收到 '{kindText}'）";
                    return false;
                }

                var packages = node?["packages"] is JsonArray packageArray
                    ? packageArray.Select(PackageRef.From).Where(p => p is not null).Select(p => p!).ToArray()
                    : null;

                var deck = node?["deck"] is JsonArray deckArray
                    ? deckArray.Select(item => item?.GetValue<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray()
                    : null;

                DeckConfigPayload? deckConfig = null;
                if (node?["deckConfig"] is JsonObject config)
                {
                    deckConfig = new DeckConfigPayload(
                        config["mainFaction"]?.GetValue<string>(),
                        config["allyFaction"]?.GetValue<string>());
                }

                seats.Add(new SeatConfig(
                    kind.Value,
                    node?["displayName"]?.GetValue<string>(),
                    packages,
                    deck,
                    deckConfig));
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = ex.Message;
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
