using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 桥真进程用例（S5/S8）：spawn <c>dotnet Orc.Mcp.Bridge.dll</c>，断言 stdout **每一行**都可解析，
/// 且工具清单区分"服务端工具"与"桥专属工具"。
/// <para>桥"打完整局"的能力由直连用例间接保证；这里只验协议面与 stdout 纯净性。</para>
/// </summary>
[Trait("Category", "Process")]
public class BridgeProcessTests
{
    [Fact]
    public async Task BridgeStdoutIsJsonPerLineAndListsBridgeTools()
    {
        using var temp = new TestSupport.TempDirectory("orc-bridge");
        var runDir = Path.Combine(temp.Path, "run");

        await using var server = await ServerProcessHarness.StartAsync(
            $"--headless --seat0 ai --seat1 agent --packs {TestSupport.SamplePackDirectory().Replace('\\', '/')} " +
            $"--run-dir \"{runDir}\" --wait-agent 30");

        var port = server.ReadyLine["port"]!.GetValue<int>();
        var tokenPath = server.ReadyLine["agentTokenPath"]!.GetValue<string>();
        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null),
            token => !string.IsNullOrWhiteSpace(token),
            TimeSpan.FromSeconds(10),
            throwOnTimeout: true);

        var bridgeDll = Path.Combine(
            ProcessHeadlessTests.FindRepositoryRoot(),
            "src", "Orc.Mcp.Bridge", "bin", "Debug", "net8.0", "Orc.Mcp.Bridge.dll");
        Assert.True(File.Exists(bridgeDll), $"未找到桥构建产物：{bridgeDll}");

        using var bridge = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{bridgeDll}\" --connect {port} --token-file \"{tokenPath}\"",
            WorkingDirectory = ProcessHeadlessTests.FindRepositoryRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        })!;

        try
        {
            var lines = new List<string>();

            await SendAsync(bridge, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            lines.Add(await ReadLineAsync(bridge));

            await SendAsync(bridge, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            lines.Add(await ReadLineAsync(bridge));

            await SendAsync(bridge, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"seat.state","arguments":{}}}""");
            lines.Add(await ReadLineAsync(bridge));

            foreach (var line in lines)
            {
                Assert.NotNull(JsonNode.Parse(line));
            }

            var tools = JsonNode.Parse(lines[1])!["result"]!["tools"]!.AsArray()
                .Select(node => node!["name"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("seat.state", tools);
            Assert.Contains("seat.segments", tools);
            Assert.Contains("room.info", tools);      // 服务端工具原样透传
            Assert.DoesNotContain("card.write", tools);

            var envelope = JsonNode.Parse(
                JsonNode.Parse(lines[2])!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.True(envelope["result"]!["ok"]!.GetValue<bool>(), envelope["result"]!.ToJsonString());
            Assert.NotNull(envelope["state"]);

            bridge.StandardInput.Close();
            Assert.True(bridge.WaitForExit(15000), "桥未在 stdin 关闭后退出。");
            Assert.Equal(0, bridge.ExitCode);
        }
        finally
        {
            if (!bridge.HasExited)
            {
                bridge.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task SendAsync(Process process, string frame)
    {
        await process.StandardInput.WriteLineAsync(frame);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<string> ReadLineAsync(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(string.IsNullOrWhiteSpace(line), "桥没有输出协议帧。");
        return line!;
    }
}
