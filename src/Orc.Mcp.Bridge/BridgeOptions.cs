using System.Globalization;

namespace Orc.Mcp.Bridge;

/// <summary>
/// 桥启动参数（无头沙盒 S5）：<c>--connect &lt;host:port&gt;|&lt;port&gt;</c> 与 <c>--token-file &lt;path&gt;</c>。
/// </summary>
public sealed record BridgeOptions(string Host, int Port, string TokenFile)
{
    /// <summary>解析命令行；失败返回 <c>null</c> 并给出原因。</summary>
    public static BridgeOptions? Parse(string[] args, out string? error)
    {
        error = null;
        string? connect = null;
        string? tokenFile = null;

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

        if (string.IsNullOrWhiteSpace(tokenFile))
        {
            error = $"--token-file 必填。\n{Usage}";
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

        return new BridgeOptions(host, port, tokenFile);
    }

    /// <summary>用法（只写 stderr）。</summary>
    public const string Usage =
        "用法：dotnet Orc.Mcp.Bridge.dll --connect <host:port|port> --token-file <agent.token>";
}
