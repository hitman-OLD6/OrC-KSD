using System.Text.Json;
using Orc.Mcp;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tests;

/// <summary>
/// 测试夹具：临时沙盒根 + 服务层实例（每个测试独立目录，避免相互污染）。
/// </summary>
internal sealed class SandboxFixture : IDisposable
{
    public SandboxFixture(
        McpServiceMode mode = McpServiceMode.Authoring,
        bool promote = false,
        bool csx = false,
        Orc.Mcp.Room.IRoomControl? room = null,
        int? agentSeat = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "orc-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Options = new McpServiceOptions
        {
            Mode = mode,
            SandboxRoot = Root,
            PromoteEnabled = promote,
            CsxEnabled = csx,
            Seed = 1234,
            SegmentCacheCapacity = 32,
            AgentSeat = agentSeat,
        };

        Service = new McpService(Options, room);
    }

    public string Root { get; }

    public McpServiceOptions Options { get; }

    public McpService Service { get; }

    public static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public Task<McpToolResult> InvokeAsync(string tool, string argsJson = "{}")
        => Service.InvokeAsync(tool, Args(argsJson));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
