using System.Globalization;

namespace Orc.Mcp.Bridge;

/// <summary>
/// 桥启动参数（无头沙盒 S5）：<c>--connect &lt;host:port&gt;|&lt;port&gt;</c> 与 <c>--token-file &lt;path&gt;</c>。
/// <para>
/// <c>--no-resume</c>：编排放行形态——只连接、不 <c>room.resume</c>（因此不绑定座位）。
/// 服务端把调试写类工具（<c>card.write</c>/<c>card.load</c>/<c>effect.write</c>/<c>sandbox.*</c> 等）
/// 登记为 <c>OrchestratorOnly</c>，只对「回环 ＋ 未绑定座位」的连接可见可用；
/// 座位桥与编排桥并存是同一次对局既打牌又造卡的唯一合法组合。
/// </para>
/// </summary>
public sealed record BridgeOptions(string Host, int Port, string? TokenFile)
{
    /// <summary>只连不绑座（编排连接）；此形态下 <see cref="TokenFile"/> 可缺省。</summary>
    public bool NoResume { get; init; }

    /// <summary>解析命令行；失败返回 <c>null</c> 并给出原因。</summary>
    public static BridgeOptions? Parse(string[] args, out string? error)
    {
        error = null;
        string? connect = null;
        string? tokenFile = null;
        var noResume = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connect" when i + 1 < args.Length:
                    connect = args[++i];
                    break;
                case "--token-file" when i + 1 < args.Length:
                    tokenFile = args[++i];
                    break;
                case "--no-resume":
                    noResume = true;
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

        if (!noResume && string.IsNullOrWhiteSpace(tokenFile))
        {
            error = $"--token-file 必填（或显式 --no-resume 起编排连接）。\n{Usage}";
            return null;
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

        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        return new BridgeOptions(host, port, tokenFile) { NoResume = noResume };
    }

    /// <summary>用法（只写 stderr）。</summary>
    public const string Usage =
        "用法：dotnet Orc.Mcp.Bridge.dll --connect <host:port|port> --token-file <agent.token>\n"
        + "      dotnet Orc.Mcp.Bridge.dll --connect <host:port|port> --no-resume   （编排连接：不绑座位）";
}
