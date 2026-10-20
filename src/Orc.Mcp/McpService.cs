using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Mcp.Sandbox;

namespace Orc.Mcp;

/// <summary>
/// MCP 服务层（协议无关门面）。
/// <para>
/// 职责：①持有工具目录与统一返回外壳；②承载唯一活跃对局与对局执行器（单写者串行化）；
/// ③沙盒模式门控（非沙盒模式下"调试工具"根本不在目录里）；④把引擎异常/三态结果映射为统一结果码。
/// **不实现任何传输协议**（stdio/JSON-RPC 由宿主适配层负责；本工程零 MCP SDK 依赖）。
/// </para>
/// </summary>
public sealed partial class McpService
{
    private sealed record Entry(
        McpToolDescriptor Descriptor,
        bool SandboxOnly,
        bool ReadOnly,
        Func<McpToolCall, CancellationToken, Task<McpToolResult>> Handler);

    private readonly List<McpToolDescriptor> _descriptors = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _processStateNotes = new();

    public McpService(McpServiceOptions options)
        : this(options, roomControl: null)
    {
    }

    /// <summary>
    /// 创建服务层。
    /// </summary>
    /// <param name="options">运行选项。</param>
    /// <param name="roomControl">房间控制面（宿主注入；<c>null</c>＝无房间能力，<c>room.*</c> 工具不登记）。</param>
    public McpService(McpServiceOptions options, Orc.Mcp.Room.IRoomControl? roomControl)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        RoomControl = roomControl;
        Sandbox = new SandboxEnvironment(options);
        Bridge = new TargeterBridgeAdapter(options.TargetSelectionTimeout, KeyOf);
        Executor = new MatchExecutor(Bridge);
        Segments = new SegmentCache(options.SegmentCacheCapacity);
        Editor = CreateEditor();

        Orc.Mcp.Tools.ToolRegistration.RegisterAll(this);
    }

    /// <summary>运行选项。</summary>
    public McpServiceOptions Options { get; }

    /// <summary>房间控制面（未注入＝<c>null</c>，房间工具不登记）。</summary>
    public Orc.Mcp.Room.IRoomControl? RoomControl { get; }

    /// <summary>沙盒环境（目录/脚手架/路径守卫）。</summary>
    public SandboxEnvironment Sandbox { get; }

    /// <summary>目标选择桥接（装配进对局）。</summary>
    public TargeterBridgeAdapter Bridge { get; }

    /// <summary>对局执行器（唯一活跃对局 + 串行化）。</summary>
    public MatchExecutor Executor { get; }

    /// <summary>事件流段缓存。</summary>
    public SegmentCache Segments { get; }

    /// <summary>沙盒对局使用的效果工厂注册表（建局时创建；供运行期注册新卡携带效果声明）。</summary>
    public Orc.Game.Cards.CardEffectRegistry? EffectRegistry { get; internal set; }

    /// <summary>工具目录（顺序＝注册顺序；稳定）。</summary>
    public IReadOnlyList<McpToolDescriptor> Tools => _descriptors;

    /// <summary>本进程已发生的**进程级**变更留痕（词条注册面、卡包目录等；R0c：不回滚但必须可见）。</summary>
    public IReadOnlyList<string> ProcessStateNotes => _processStateNotes;

    /// <summary>登记一个工具。</summary>
    /// <param name="name">工具名。</param>
    /// <param name="group">分组。</param>
    /// <param name="description">说明。</param>
    /// <param name="inputSchema">输入 JSON Schema（手写常量）。</param>
    /// <param name="handler">执行器。</param>
    /// <param name="sandboxOnly">是否沙盒专属（非沙盒模式不暴露）。</param>
    /// <param name="readOnly">是否只读（忙时仍可调用）。</param>
    public void AddTool(
        string name,
        string group,
        string description,
        string inputSchema,
        Func<McpToolCall, CancellationToken, Task<McpToolResult>> handler,
        bool sandboxOnly = false,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        // R0a：沙盒专属工具在非沙盒模式下**不登记**（不暴露）——工具清单里根本不存在调试类能力。
        if (sandboxOnly && Options.Mode != McpServiceMode.Authoring)
        {
            return;
        }

        if (_entries.ContainsKey(name))
        {
            throw new InvalidOperationException($"工具名 '{name}' 已登记（重复登记被拒绝）。");
        }

        var descriptor = new McpToolDescriptor(name, group, description, inputSchema);
        _entries.Add(name, new Entry(descriptor, sandboxOnly, readOnly, handler));
        _descriptors.Add(descriptor);
    }

    /// <summary>记录一条进程级变更留痕（工具返回值与 <c>sandbox.state</c> 均会呈现）。</summary>
    public void NoteProcessStateChange(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        _processStateNotes.Add(note);
    }

    /// <summary>
    /// 解析 <see cref="Ref{T}"/> 的引擎引用键：
    /// 实体（<c>Entity.Id</c>）直接用既有 GUID；无 GUID 对象经引擎引用表**懒分配**（同一对局内稳定）。
    /// </summary>
    public string KeyOf(Ref<Entity> reference)
    {
        if (!reference.IsAlive)
        {
            return string.Empty;
        }

        var value = reference.Value;
        if (value is Entity entity)
        {
            return entity.Id.ToString("N");
        }

        var engine = Executor.Match?.Engine;
        return engine?.References.Assign(value) ?? string.Empty;
    }

    /// <summary>按名调用工具（协议适配层的唯一入口）。</summary>
    public Task<McpToolResult> InvokeAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
        => InvokeAsync(new McpToolCall(toolName, arguments), ct);

    /// <summary>按名调用工具。</summary>
    public async Task<McpToolResult> InvokeAsync(McpToolCall call, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(call.Name, out var entry))
        {
            return McpToolResult.Failure(McpCodes.UnknownTool, $"未知工具 '{call.Name}'。");
        }

        // 模式门禁：沙盒专属工具在非沙盒模式下**不存在**（不暴露）。
        if (entry.SandboxOnly && Options.Mode != McpServiceMode.Authoring)
        {
            return McpToolResult.ModeDenied(
                $"工具 '{call.Name}' 只在沙盒模式（--mode authoring）下可用。");
        }

        // 挂起期门禁（R2c）：待决期间只允许 target.* 与只读工具。
        if (Executor.IsBusy && !entry.ReadOnly && !call.Name.StartsWith("target.", StringComparison.Ordinal))
        {
            var pending = Bridge.CurrentPending;
            return McpToolResult.Failure(
                McpCodes.PendingTargeting,
                "存在未完成的选择请求：请先调用 target.submit 或 target.cancel 应答。",
                PendingJson(pending));
        }

        try
        {
            return await entry.Handler(call, ct).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, ex.Message);
        }
        catch (SandboxPathViolationException ex)
        {
            return McpToolResult.Failure(McpCodes.PathDenied, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return McpToolResult.Failure(McpCodes.Cancelled, "调用被取消（取消令牌已触发）。");
        }
        catch (Exception ex)
        {
            return McpToolResult.Failure(
                McpCodes.Internal,
                $"工具自身故障：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>待决请求 → JSON（工具返回与结果细节共用）。</summary>
    public static JsonNode? PendingJson(PendingSelection? pending)
    {
        if (pending is null)
        {
            return null;
        }

        var candidates = new JsonArray();
        foreach (var candidate in pending.Candidates)
        {
            candidates.Add(new JsonObject
            {
                ["index"] = candidate.Index,
                ["kind"] = candidate.Kind,
                ["ref"] = candidate.ReferenceKey,
                ["name"] = candidate.DisplayName,
                ["alive"] = candidate.IsAlive,
            });
        }

        var identifiers = new JsonArray();
        foreach (var identifier in pending.Identifiers)
        {
            identifiers.Add(identifier);
        }

        return new JsonObject
        {
            ["sessionId"] = pending.SessionId,
            ["selector"] = pending.SelectorName,
            ["mode"] = pending.Mode,
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            ["seat"] = pending.Seat,
            ["candidates"] = candidates,
            ["identifiers"] = identifiers,
        };
    }
}
