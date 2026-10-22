using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 命令行入口（R1：**薄适配**——CLI 与进程内 API 共用同一个 <see cref="IManager"/>，逐字等价）。
/// <para>
/// stdout 只承载结果 JSON（与服务器的通道约定一致）；诊断一律 stderr。
/// </para>
/// <para>
/// `spawn` 默认**常驻**（管理器生命周期长于任何单场对局）：管理面是父子私有通道，
/// 只有创建者能写，因此"起实例 + 调管理面"必须在同一个进程里完成。
/// </para>
/// </summary>
public static class Cli
{
    private const string Usage =
        "用法：orc-manager <命令> [选项]\n" +
        "  spawn  --id <标识> [--packs <dir;dir>] [--seats <json>] [--mode play|sandbox]\n" +
        "         [--port <n>] [--seed <n>] [--deck-rules strict|lenient|none] [--required-deck-size <n>]\n" +
        "         [--disconnect-grace <秒>] [--allow-debug-writes] [--label <text>] [--serve-stdin]\n" +
        "  list\n" +
        "  kill   --id <标识>\n" +
        "  reload --id <标识>\n" +
        "  admin  --id <标识> --op <op> [--args <json>]   （仅在同一进程创建实例时可用）\n" +
        "  assets register --packs <dir;dir>\n" +
        "  assets deps\n" +
        "  assets diff --left <file.json> --right <file.json>\n" +
        "  assets plan --left <file.json> --right <file.json> [--sandbox <dir>]\n" +
        "\n常驻与远程接入：\n" +
        "  serve  [--port <n>] [--server <dll>]   常驻监听 127.0.0.1，stdout 写一行 {\"ready\":true,\"port\":N}\n" +
        "  mcp    --connect <host:port>           stdio MCP 前端：把 tools/call 转发给**已常驻**的管理器\n";

    /// <summary>执行一次 CLI 调用（返回进程退出码）。可注入管理器（测试用进程内实例驱动 CLI）。</summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<ServerManager>? managerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            stderr.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var options = ParseOptions(args.Skip(1).ToArray());

        // mcp 前端**不持有管理器**：它只是 stdio ↔ 回环 TCP 的转接，
        // 对端是用户先前启动的常驻 `serve`。放在创建管理器之前分流，避免前端进程空转一套管理器。
        if (string.Equals(args[0], "mcp", StringComparison.Ordinal))
        {
            return await RunMcpAsync(options, stderr, ct).ConfigureAwait(false);
        }

        await using var owned = managerFactory is null ? new ServerManager(Get(options, "server")) : null;
        var manager = managerFactory?.Invoke() ?? owned!;

        switch (args[0])
        {
            case "serve":
                return await RunServeAsync(manager, options, stdout, stderr, ct).ConfigureAwait(false);

            case "list":
            {
                var payload = new JsonArray(manager.List().Select(info => (JsonNode)new JsonObject
                {
                    ["instanceId"] = info.InstanceId,
                    ["processId"] = info.ProcessId,
                    ["port"] = info.Port,
                    ["roomId"] = info.RoomId,
                    ["mode"] = info.Mode,
                    ["runDir"] = info.RunDir,
                    ["label"] = info.Label,
                    ["running"] = info.Running,
                }).ToArray());

                await stdout.WriteLineAsync(new JsonObject { ["ok"] = true, ["instances"] = payload }.ToJsonString()).ConfigureAwait(false);
                return 0;
            }

            case "spawn":
            {
                var reply = await manager.SpawnAsync(ToSpawnRequest(options), ct).ConfigureAwait(false);
                await WriteAsync(stdout, reply).ConfigureAwait(false);
                if (!reply.Ok)
                {
                    return 1;
                }

                if (Has(options, "serve-stdin"))
                {
                    return await ServeStdinAsync(manager, Require(options, "id"), stdout, stderr, ct).ConfigureAwait(false);
                }

                // 无 --serve-stdin：保持管理面可用直到 Ctrl+C（否则实例会随父进程退出）。
                await stderr.WriteLineAsync("实例已启动；按 Ctrl+C 结束（管理面只在创建者进程内可用）。").ConfigureAwait(false);
                await WaitForStopAsync(ct).ConfigureAwait(false);
                await manager.KillAsync(Require(options, "id"), CancellationToken.None).ConfigureAwait(false);
                return 0;
            }

            case "kill":
            {
                var reply = await manager.KillAsync(Require(options, "id"), ct).ConfigureAwait(false);
                await WriteAsync(stdout, reply).ConfigureAwait(false);
                return reply.Ok ? 0 : 1;
            }

            case "reload":
            {
                var reply = await manager.ReloadAsync(Require(options, "id"), ct).ConfigureAwait(false);
                await WriteAsync(stdout, reply).ConfigureAwait(false);
                return reply.Ok ? 0 : 1;
            }

            case "admin":
            {
                var id = Require(options, "id");
                var op = Require(options, "op");
                var argsNode = Get(options, "args") is { Length: > 0 } json
                    ? JsonNode.Parse(json) as JsonObject
                    : null;

                var reply = await manager.AdminAsync(id, new ManagerAdminCall(op, argsNode), ct).ConfigureAwait(false);
                await WriteAsync(stdout, reply).ConfigureAwait(false);
                return reply.Ok ? 0 : 1;
            }

            case "assets":
                return await AssetsAsync(manager.Assets, args, options, stdout, stderr).ConfigureAwait(false);

            default:
                await stderr.WriteLineAsync($"未知命令 '{args[0]}'。{Environment.NewLine}{Usage}").ConfigureAwait(false);
                return 1;
        }
    }

    /// <summary>
    /// <c>serve</c>：常驻监听回环端口，作为**多个 agent 共用的唯一入口**。
    /// <para>就绪行与服务器进程同构（stdout 只写这一行 JSON），宿主/脚本据此拿到实际端口。</para>
    /// </summary>
    private static async Task<int> RunServeAsync(
        ServerManager manager,
        Dictionary<string, List<string>> options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var portText = Get(options, "port");
        var port = 0;
        if (!string.IsNullOrWhiteSpace(portText)
            && (!int.TryParse(portText, out port) || port is < 0 or > 65535))
        {
            await stderr.WriteLineAsync($"--port 非法：'{portText}'（0..65535）。").ConfigureAwait(false);
            return 1;
        }

        await using var server = ManagerServer.Start(manager, port);

        await stdout.WriteLineAsync(new JsonObject
        {
            ["ready"] = true,
            ["port"] = server.Port,
        }.ToJsonString()).ConfigureAwait(false);
        await stdout.FlushAsync(ct).ConfigureAwait(false);

        await stderr.WriteLineAsync($"Orc.Manager 已就绪，监听 127.0.0.1:{server.Port}（Ctrl+C 退出）。").ConfigureAwait(false);

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();

        await using var registration = ct.Register(() => stop.TrySetResult());
        await stop.Task.ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// <c>mcp</c>：stdio MCP 前端。宿主（WorkBuddy 等）以 <c>command + args</c> 方式拉起它，
    /// 它再把每条 <c>tools/call</c> 转发给常驻管理器。管理器不在线时如实失败并给出可操作提示。
    /// </summary>
    private static async Task<int> RunMcpAsync(
        Dictionary<string, List<string>> options,
        TextWriter stderr,
        CancellationToken ct)
    {
        var connect = Get(options, "connect");
        if (string.IsNullOrWhiteSpace(connect))
        {
            await stderr.WriteLineAsync("mcp 需要 --connect <host:port>（与 serve 的监听地址一致）。").ConfigureAwait(false);
            return 1;
        }

        if (!TryParseEndpoint(connect, out var host, out var port))
        {
            await stderr.WriteLineAsync($"--connect 非法：'{connect}'（应为 host:port 或纯端口）。").ConfigureAwait(false);
            return 1;
        }

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        await using var client = new ManagerClient(host, port);
        return await new ManagerMcpFront(client).RunAsync(input, output, ct).ConfigureAwait(false);
    }

    private static bool TryParseEndpoint(string value, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 0;

        var text = value.Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            var hostPart = text[..colon];
            text = text[(colon + 1)..];
            if (hostPart.Length > 0)
            {
                host = hostPart;
            }
        }

        return int.TryParse(text, out port) && port is > 0 and <= 65535;
    }

    private static async Task<int> AssetsAsync(
        AssetLibrary library, string[] args, Dictionary<string, List<string>> options, TextWriter stdout, TextWriter stderr)
    {
        var sub = args.Length > 1 ? args[1] : "deps";

        switch (sub)
        {
            case "register":
            {
                var result = library.Register(Split(Get(options, "packs")));
                await stdout.WriteLineAsync(result.ToJson().ToJsonString()).ConfigureAwait(false);
                return result.Registered.Count > 0 ? 0 : 1;
            }

            case "deps":
                await stdout.WriteLineAsync(library.AnalyzeDependencies().ToJsonString()).ConfigureAwait(false);
                return 0;

            case "diff":
            {
                var left = ReadDeclaration(Require(options, "left"));
                var right = ReadDeclaration(Require(options, "right"));
                await stdout.WriteLineAsync(library.DiffLocks(left, right).ToJsonString()).ConfigureAwait(false);
                return 0;
            }

            case "plan":
            {
                var left = ReadDeclaration(Require(options, "left"));
                var right = ReadDeclaration(Require(options, "right"));
                var outcome = library.Plan(left, right, Get(options, "sandbox"));

                var node = new JsonObject
                {
                    ["ok"] = outcome.Ok,
                    ["code"] = outcome.Code,
                    ["message"] = outcome.Message,
                    ["issues"] = outcome.Issues,
                    ["plan"] = outcome.Plan?.ToJson(),
                };

                await stdout.WriteLineAsync(node.ToJsonString()).ConfigureAwait(false);
                return outcome.Ok ? 0 : 1;
            }

            default:
                await stderr.WriteLineAsync($"未知 assets 子命令 '{sub}'。").ConfigureAwait(false);
                return 1;
        }
    }

    private static PackageDeclaration ReadDeclaration(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new ArgumentException($"声明文件 '{path}' 不是 JSON 对象。");

        var side = node["side"]?.GetValue<string>() ?? "seat0";
        var packages = node["packages"] is JsonArray array
            ? array.Select(PackageRef.From).Where(p => p is not null).Select(p => p!).ToArray()
            : Array.Empty<PackageRef>();

        return new PackageDeclaration(side, packages);
    }

    /// <summary>`--serve-stdin`：把管理器自己的 stdin 行转成实例的管理命令，回执/事件写 stdout。</summary>
    private static async Task<int> ServeStdinAsync(
        ServerManager manager, string instanceId, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        using var subscription = manager.Subscribe(instanceId, (name, payload) =>
        {
            _ = stdout.WriteLineAsync(new JsonObject
            {
                ["event"] = name,
                ["payload"] = payload?.DeepClone(),
            }.ToJsonString());
        });

        while (!ct.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject command;
            try
            {
                command = JsonNode.Parse(line) as JsonObject
                    ?? throw new JsonException("管理命令必须是 JSON 对象。");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                await stdout.WriteLineAsync(new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = ManagerCodes.InvalidArguments,
                    ["message"] = ex.Message,
                }.ToJsonString()).ConfigureAwait(false);
                continue;
            }

            var id = command["id"]?.DeepClone();
            var op = command["op"]?.GetValue<string>();
            var argsNode = command["args"] as JsonObject;

            var reply = op is null
                ? ManagerReply.Fail(ManagerCodes.InvalidArguments, "管理命令缺少 'op'。")
                : await manager.AdminAsync(instanceId, new ManagerAdminCall(op, argsNode), ct).ConfigureAwait(false);

            await stdout.WriteLineAsync(new JsonObject
            {
                ["id"] = id,
                ["ok"] = reply.Ok,
                ["code"] = reply.Code,
                ["message"] = reply.Message,
                ["payload"] = reply.Payload?.DeepClone(),
            }.ToJsonString()).ConfigureAwait(false);
        }

        await stderr.WriteLineAsync("管理通道已关闭（stdin EOF）；继续托管实例直到 Ctrl+C。").ConfigureAwait(false);
        await WaitForStopAsync(ct).ConfigureAwait(false);
        await manager.KillAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task WaitForStopAsync(CancellationToken ct)
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.TrySetResult();
        };

        using var registration = ct.Register(() => stop.TrySetResult());
        await stop.Task.ConfigureAwait(false);
    }

    private static SpawnRequest ToSpawnRequest(Dictionary<string, List<string>> options)
    {
        var seats = Get(options, "seats") is { Length: > 0 } json
            ? ParseSeats(json)
            : new[] { new SeatSpawn(), new SeatSpawn() };

        return new SpawnRequest
        {
            InstanceId = Require(options, "id"),
            PackDirectories = Split(Get(options, "packs")),
            Seats = seats,
            Mode = Get(options, "mode") ?? "play",
            AllowDebugWrites = Has(options, "allow-debug-writes"),
            SandboxRoot = Get(options, "sandbox"),
            Port = int.TryParse(Get(options, "port"), out var port) ? port : 0,
            RunDir = Get(options, "run-dir"),
            Seed = int.TryParse(Get(options, "seed"), out var seed) ? seed : null,
            DeckRules = Get(options, "deck-rules") ?? "strict",
            DisconnectGraceSeconds = int.TryParse(Get(options, "disconnect-grace"), out var grace) ? grace : 60,
            Label = Get(options, "label"),
        };
    }

    private static IReadOnlyList<SeatSpawn> ParseSeats(string json)
    {
        var node = JsonNode.Parse(json) as JsonArray
            ?? throw new ArgumentException("--seats 需要 JSON 数组。");

        return node.Select(item =>
        {
            var packages = item?["packages"] is JsonArray array
                ? array.Select(PackageRef.From).Where(p => p is not null).Select(p => p!).ToArray()
                : Array.Empty<PackageRef>();

            var deck = item?["deck"] is JsonArray deckArray
                ? deckArray.Select(id => id?.GetValue<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray()
                : null;

            return new SeatSpawn
            {
                Kind = item?["kind"]?.GetValue<string>() ?? "agent",
                DisplayName = item?["displayName"]?.GetValue<string>(),
                Packages = packages,
                Deck = deck,
            };
        }).ToArray();
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";

            if (!options.TryGetValue(key, out var list))
            {
                list = new List<string>();
                options[key] = list;
            }

            list.Add(value);
        }

        return options;
    }

    private static string? Get(Dictionary<string, List<string>> options, string key)
        => options.TryGetValue(key, out var values) ? values[^1] : null;

    private static bool Has(Dictionary<string, List<string>> options, string key)
        => options.ContainsKey(key);

    private static string Require(Dictionary<string, List<string>> options, string key)
        => Get(options, key) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少必填参数 --{key}。");

    private static IReadOnlyList<string> Split(string? value)
        => value is null
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task WriteAsync(TextWriter stdout, ManagerReply reply)
        => await stdout.WriteLineAsync(new JsonObject
        {
            ["ok"] = reply.Ok,
            ["code"] = reply.Code,
            ["message"] = reply.Message,
            ["payload"] = reply.Payload?.DeepClone(),
        }.ToJsonString()).ConfigureAwait(false);
}
