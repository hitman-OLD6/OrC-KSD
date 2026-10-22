using System.Text;

namespace Orc.Mcp.Bridge;

/// <summary>
/// stdio MCP 桥进程入口（无头沙盒 S5）。
/// <para>
/// **stdout 只写 JSON-RPC 帧**（诊断一律 stderr）；启动序列＝连接 → <c>initialize</c> → 读令牌 →
/// <c>room.resume</c>，任一步失败以非零码退出（不半死不活地挂着）。
/// </para>
/// <para>
/// 启动形态固定为 <c>dotnet &lt;绝对路径&gt;/Orc.Mcp.Bridge.dll</c>——**禁止 <c>dotnet run</c>**：
/// 其还原/构建输出会落到 stdout，MCP 客户端读到第一行不是 JSON-RPC 帧就崩。
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = BridgeOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        await using var transport = new OrcClientTransport(options);

        var state = await transport.EnsureConnectedAsync().ConfigureAwait(false);
        switch (state)
        {
            case BridgeConnectionState.TokenRejected:
                return 2;
            case BridgeConnectionState.AdmissionTimeout:
                return 3;
            case BridgeConnectionState.NotConnected:
                return 1;
        }

        var server = new BridgeServer(transport);

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        return await server.RunAsync(input, output).ConfigureAwait(false);
    }
}
