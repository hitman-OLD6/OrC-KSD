using System.Globalization;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Mcp.Bridge;

/// <summary>
/// 桥启动参数（S11 新链路）：连接 → <c>initialize</c> → <c>session.attach</c>（立即 pending）→ 等
/// <c>notify/admission</c>。
/// <para>
/// 不再有 <c>--no-resume</c>：调试面＝<c>--role debug</c>（仍须回环来源）；座位令牌由服务端批准时签发，
/// 桥可把它写进 <c>--token-out</c> 供断线重连（<c>--token</c> 读回）。<c>agent.token</c> 文件已随之退役。
/// </para>
/// </summary>
public sealed record BridgeOptions(
    string Host,
    int Port,
    int? Seat,
    string Role,
    IReadOnlyList<string> Deck,
    IReadOnlyList<PackageRef> Packages,
    string? Label)
{
    /// <summary>等待管理员批准的上限（超时＝退出码 3；不设服务端 attach 超时）。</summary>
    public TimeSpan AdmitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>断线重连令牌（来自上一次批准响应）。</summary>
    public string? Token { get; init; }

    /// <summary>批准后把座位令牌写到该文件（供下次 <c>--token</c> 使用）。</summary>
    public string? TokenOut { get; init; }

    /// <summary>解析命令行；失败返回 <c>null</c> 并给出原因。</summary>
    public static BridgeOptions? Parse(string[] args, out string? error)
    {
        error = null;
        string? connect = null;
        int? seat = null;
        var role = "controller";
        string? deckFile = null;
        string? deckInline = null;
        string? packagesFile = null;
        string? packagesInline = null;
        string? label = null;
        string? token = null;
        string? tokenOut = null;
        var admitTimeout = TimeSpan.FromMinutes(5);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connect" when i + 1 < args.Length:
                    connect = args[++i];
                    break;
                case "--seat" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedSeat):
                    seat = parsedSeat;
                    i++;
                    break;
                case "--role" when i + 1 < args.Length:
                    role = args[++i];
                    break;
                case "--deck-file" when i + 1 < args.Length:
                    deckFile = args[++i];
                    break;
                case "--deck" when i + 1 < args.Length:
                    deckInline = args[++i];
                    break;
                case "--packages-file" when i + 1 < args.Length:
                    packagesFile = args[++i];
                    break;
                case "--packages" when i + 1 < args.Length:
                    packagesInline = args[++i];
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--token-out" when i + 1 < args.Length:
                    tokenOut = args[++i];
                    break;
                case "--admit-timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    admitTimeout = TimeSpan.FromSeconds(seconds);
                    i++;
                    break;
                case "--help":
                    error = Usage;
                    return null;
                default:
                    error = $"未知参数 '{args[i]}'。\n{Usage}";
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(connect))
        {
            error = $"--connect 必填。\n{Usage}";
            return null;
        }

        if (role is not ("controller" or "viewer" or "both" or "omniscient" or "debug"))
        {
            error = $"--role 只接受 controller|viewer|both|omniscient|debug（收到 '{role}'）。";
            return null;
        }

        if (role is "controller" or "both" or "viewer")
        {
            if (seat is not (0 or 1))
            {
                error = $"--role {role} 需要 --seat 0|1（seat 绑定决定视角与输入归属）。";
                return null;
            }
        }

        if (role is "omniscient" or "debug" && seat is not null)
        {
            error = $"--role {role} 不绑 seat（请去掉 --seat）。";
            return null;
        }

        var deck = ReadDeck(deckFile, deckInline, out var deckError);
        if (deckError is not null)
        {
            error = deckError;
            return null;
        }

        var packages = ReadPackages(packagesFile, packagesInline, out var packageError);
        if (packageError is not null)
        {
            error = packageError;
            return null;
        }

        if (role is "controller" or "both")
        {
            if (deck.Count == 0)
            {
                error = $"--role {role} 需要卡组（--deck-file 或 --deck）。";
                return null;
            }

            if (packagesFile is null && packagesInline is null)
            {
                error = $"--role {role} 需要卡包声明（--packages-file 或 --packages）。";
                return null;
            }
        }

        var host = "127.0.0.1";
        var portText = connect;
        var colon = connect.LastIndexOf(':');
        if (colon >= 0)
        {
            host = connect[..colon];
            portText = connect[(colon + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            error = $"--connect 的端口非法：'{connect}'（应为 host:port 或纯端口）。";
            return null;
        }

        return new BridgeOptions(host, port, seat, role, deck, packages, label)
        {
            AdmitTimeout = admitTimeout,
            Token = token,
            TokenOut = tokenOut,
        };
    }

    /// <summary>转成 <c>session.attach</c> 参数（角色 ＋ 座位 ＋ 卡组 ＋ 卡包一条报文带齐）。</summary>
    public SessionAttachParams ToAttachParams()
    {
        var roles = Role switch
        {
            "controller" => new[] { SessionRoles.Controller },
            "viewer" => new[] { SessionRoles.Viewer },
            "both" => new[] { SessionRoles.Controller, SessionRoles.Viewer },
            "omniscient" => new[] { SessionRoles.Viewer },
            "debug" => new[] { SessionRoles.Debug },
            _ => new[] { SessionRoles.Viewer },
        };

        return new SessionAttachParams
        {
            Roles = roles,
            Seat = Seat,
            DisplayName = Label,
            Deck = Deck.Count > 0 ? Deck : null,
            Packages = Packages.Count > 0 ? Packages : null,
            Token = Token,
        };
    }

    private static IReadOnlyList<string> ReadDeck(string? file, string? inline, out string? error)
    {
        error = null;
        try
        {
            if (file is { Length: > 0 })
            {
                return ParseDeck(File.ReadAllText(file));
            }

            if (inline is { Length: > 0 })
            {
                return inline.Contains('[', StringComparison.Ordinal)
                    ? ParseDeck(inline)
                    : inline.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            error = $"卡组读取失败：{ex.Message}";
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseDeck(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            throw new System.Text.Json.JsonException("卡组文件应为 JSON 数组。");
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }

    private static IReadOnlyList<PackageRef> ReadPackages(string? file, string? inline, out string? error)
    {
        error = null;
        var json = file is { Length: > 0 } ? File.ReadAllText(file) : inline;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<PackageRef>();
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                throw new System.Text.Json.JsonException("卡包声明应为 JSON 数组。");
            }

            return array.Select(PackageRef.From).Where(p => p is not null).Select(p => p!).ToArray();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            error = $"卡包声明读取失败：{ex.Message}";
            return Array.Empty<PackageRef>();
        }
    }

    /// <summary>用法（只写 stderr）。</summary>
    public const string Usage =
        "用法：dotnet Orc.Mcp.Bridge.dll --connect <host:port|port> --role <controller|viewer|both|omniscient|debug> [--seat 0|1]\n"
        + "        [--deck-file <cards.json>|--deck a,b,c] [--packages-file <packages.json>|--packages <json>]\n"
        + "        [--label <text>] [--token <seatToken>] [--token-out <path>] [--admit-timeout <秒>]\n"
        + "批准由管理员执行：桥在 session.attach 后等 notify/admission（attached 立即返回 pending，不阻塞服务端）。";
}
