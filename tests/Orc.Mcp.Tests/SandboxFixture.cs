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
        IReadOnlyList<int>? aiSeats = null)
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
            AiSeats = aiSeats ?? [],
        };

        // 进程级注册由宿主 boot 段完成（刻意变化第 10 条）；测试宿主须自己做一次。
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();

        Service = new McpService(Options, room);

        // 调试/沙盒用例默认以**调试面**视角读观察面（与旧 Authoring 摘要同形）。
        Service.Caller = new CallerView(DebugFace: true);
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

    /// <summary>切到某座位视角（读投影面板）；释放即恢复调试面视角。</summary>
    public IDisposable AsSeat(int seat)
        => new CallerScope(Service, new CallerView(Seat: seat));

    /// <summary>切到调试面视角（读全知摘要）。</summary>
    public IDisposable AsDebug()
        => new CallerScope(Service, new CallerView(DebugFace: true));

    private sealed class CallerScope : IDisposable
    {
        private readonly McpService _service;
        private readonly CallerView? _previous;

        public CallerScope(McpService service, CallerView caller)
        {
            _service = service;
            _previous = service.Caller;
            service.Caller = caller;
        }

        public void Dispose() => _service.Caller = _previous ?? CallerView.Anonymous;
    }

    /// <summary>在**消费者上下文**内调用工具（读引擎的只读面要求；与传输层同一前提）。</summary>
    public async Task<McpToolResult> InvokeInContextAsync(string tool, string argsJson = "{}")
    {
        using var _ = Orc.Mcp.Execution.ConsumerContext.Enter();
        return await Service.InvokeAsync(tool, Args(argsJson));
    }

    /// <summary>进程内建沙盒局（S11 退役了 <c>sandbox.create</c> 工具，能力仍在服务层）。</summary>
    public Task<McpToolResult> CreateSandboxMatchAsync() => Service.CreateSandboxMatchAsync();

    /// <summary>进程内销毁沙盒局。</summary>
    public Task<McpToolResult> DisposeSandboxMatchAsync() => Task.FromResult(Service.DisposeSandboxMatch());

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
