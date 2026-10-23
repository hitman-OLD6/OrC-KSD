using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Mcp.Sandbox;

namespace Orc.Mcp;

/// <summary>
/// 调用发起者视角（宿主填充；只承载"结果按谁裁剪"所需的最小信息）。
/// </summary>
/// <param name="Seat">绑定的座位（未绑＝null）。</param>
/// <param name="DebugFace">是否调试面（可见全量声明与全知摘要）。</param>
/// <param name="Omniscient">是否全知 viewer（可见双方视图，但**看不到**构筑与卡包声明）。</param>
public sealed record CallerView(int? Seat = null, bool DebugFace = false, bool Omniscient = false)
{
    /// <summary>未绑定视角。</summary>
    public static readonly CallerView Anonymous = new();

    /// <summary>转成房间快照的视角请求（全知 viewer 不是"全量声明"的授权来源——R12 明确禁止）。</summary>
    public Orc.Mcp.Room.RoomViewRequest ToRoomView() => new(Seat, DebugFace);
}

/// <summary>
/// 本局卡池装配结果（S10）：成功时给出包集合与资产数；回退时说明"用了上一次成功的包集合"。
/// </summary>
/// <param name="Packages">本次装配的包目录（人读摘要）。</param>
/// <param name="Definitions">装到的卡定义数。</param>
/// <param name="Fallback">是否由"最后一次成功的包集合"回退装配。</param>
/// <param name="Failure">失败原因（成功＝null）。</param>
public sealed record MatchPoolReport(
    IReadOnlyList<string> Packages,
    int Definitions,
    bool Fallback = false,
    string? Failure = null)
{
    /// <summary>JSON 形态（<c>sandbox.state.pool</c>）。</summary>
    public JsonNode ToJson() => new JsonObject
    {
        ["packages"] = new JsonArray(Packages.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        ["definitions"] = Definitions,
        ["fallback"] = Fallback,
        ["failure"] = Failure,
    };
}

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
        : this(options, roomControl: null, poolControl: null)
    {
    }

    /// <summary>
    /// 创建服务层。
    /// </summary>
    /// <param name="options">运行选项。</param>
    /// <param name="roomControl">房间控制面（宿主注入；<c>null</c>＝无房间能力，<c>room.*</c> 工具不登记）。</param>
    public McpService(McpServiceOptions options, Orc.Mcp.Room.IRoomControl? roomControl)
        : this(options, roomControl, poolControl: null)
    {
    }

    /// <summary>
    /// 创建服务层（含卡包集合控制面）。
    /// </summary>
    /// <param name="options">运行选项。</param>
    /// <param name="roomControl">房间控制面（宿主注入；<c>null</c>＝无房间能力，<c>room.*</c> 工具不登记）。</param>
    /// <param name="poolControl">
    /// 卡包集合控制面（宿主注入；与 <paramref name="roomControl"/> 同一条注入路径）。
    /// <c>null</c>＝无集合能力，<c>pool.*</c> 工具不登记。
    /// </param>
    public McpService(McpServiceOptions options, Orc.Mcp.Room.IRoomControl? roomControl, Orc.Mcp.Pool.IPoolControl? poolControl)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        RoomControl = roomControl;
        PoolControl = poolControl;
        Sandbox = new SandboxEnvironment(options);
        Bridge = new TargeterBridgeAdapter(options.TargetSelectionTimeout, KeyOf);
        Executor = new MatchExecutor(Bridge);
        Segments = new SegmentCache(options.SegmentCacheCapacity);
        Editor = CreateEditor();

        Orc.Mcp.Tools.ToolRegistration.RegisterAll(this);
    }

    /// <summary>运行选项。</summary>
    public McpServiceOptions Options { get; }

    /// <summary>
    /// 当前调用的发起者视角（由宿主在**同一串行工作项内**设置并清除；服务层不感知会话模型）。
    /// <para>只在工具需要"按角色构造结果"时读取（<c>room.info</c> / <c>observe.view</c>）。</para>
    /// </summary>
    public CallerView Caller { get; set; } = CallerView.Anonymous;

    /// <summary>房间控制面（未注入＝<c>null</c>，房间工具不登记）。</summary>
    public Orc.Mcp.Room.IRoomControl? RoomControl { get; }

    /// <summary>
    /// 卡包集合控制面（未注入＝<c>null</c>，<c>pool.*</c> 工具不登记）。
    /// **进程级对象**：宿主注入同一实例，房间轮换不重建（集合是实例状态，R1）。
    /// </summary>
    public Orc.Mcp.Pool.IPoolControl? PoolControl { get; }

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
    /// <param name="readOnly">是否只读（忙时仍可调用；写类默认不注册，见 <see cref="McpServiceOptions.AllowDebugWrites"/>）。</param>
    /// <param name="requiredRole">角色要求（缺省＝按工具类推断，见 <see cref="InferRole"/>）。</param>
    public void AddTool(
        string name,
        string group,
        string description,
        string inputSchema,
        Func<McpToolCall, CancellationToken, Task<McpToolResult>> handler,
        bool sandboxOnly = false,
        bool readOnly = false,
        string? requiredRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        // R0a：沙盒专属工具在非沙盒模式下**不登记**（不暴露）——工具清单里根本不存在调试类能力。
        if (sandboxOnly && Options.Mode != McpServiceMode.Authoring)
        {
            return;
        }

        // S3：写类调试工具默认不注册（默认零写面）；显式开启后才登记，且仍只对编排连接可见。
        if (sandboxOnly && !readOnly && !Options.AllowDebugWrites)
        {
            return;
        }

        if (_entries.ContainsKey(name))
        {
            throw new InvalidOperationException($"工具名 '{name}' 已登记（重复登记被拒绝）。");
        }

        var descriptor = new McpToolDescriptor(
            name,
            group,
            description,
            inputSchema,
            requiredRole ?? InferRole(name, sandboxOnly));
        _entries.Add(name, new Entry(descriptor, sandboxOnly, readOnly, handler));
        _descriptors.Add(descriptor);
    }

    /// <summary>
    /// 角色推断（**单一来源**）：不按角色划分的只读面＝<see cref="ToolRole.None"/>；
    /// 调试类（含沙盒专属）＝<see cref="ToolRole.Debug"/>；
    /// 引擎输入面（<c>play.*</c>/<c>command.*</c>/<c>match.*</c>/<c>mulligan.*</c>/<c>target.*</c>）＝<see cref="ToolRole.Controller"/>；
    /// <c>observe.view</c>＝<see cref="ToolRole.Viewer"/>。
    /// <para>角色是"态"的一部分，因此这是登记期的一次静态分类，不在调用期逐条猜。</para>
    /// </summary>
    public static string InferRole(string name, bool sandboxOnly)
    {
        if (string.Equals(name, "observe.view", StringComparison.Ordinal))
        {
            return ToolRole.Viewer;
        }

        if (sandboxOnly || Orc.Mcp.Tools.DebugToolCatalog.IsDebugTool(name))
        {
            return ToolRole.Debug;
        }

        if (name.StartsWith("play.", StringComparison.Ordinal)
            || name.StartsWith("command.", StringComparison.Ordinal)
            || name.StartsWith("match.", StringComparison.Ordinal)
            || name.StartsWith("mulligan.", StringComparison.Ordinal)
            || name.StartsWith("target.", StringComparison.Ordinal))
        {
            return ToolRole.Controller;
        }

        return ToolRole.None;
    }

    /// <summary>记录一条进程级变更留痕（工具返回值与 <c>sandbox.state</c> 均会呈现）。</summary>
    public void NoteProcessStateChange(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        _processStateNotes.Add(note);
    }

    /// <summary>
    /// 本局卡池装配结果（S10 可观测面）：**不进 <c>room.info</c> 快照**（避免两处表达同一事实），
    /// 只经 <c>sandbox.state</c> 与通道事件上报。
    /// </summary>
    public MatchPoolReport? Pool { get; set; }

    /// <summary>卡池装配结果的 JSON 形态（未装配＝null）。</summary>
    public JsonNode? PoolJson() => Pool?.ToJson();

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

        // 挂起期门禁（R2c）：待决期间只允许 target.*、只读工具，以及「会话（重新）绑定」。
        // room.resume 必须放行：否则「应答待决须先连上、连上须先过忙门禁」构成死锁——
        // 桥一旦在待决期间断开（进程重启/网络抖动）就再也回不来（本局实测：room.resume 被 pending-targeting 拒）。
        // 放行是安全的：绑定座位不改变对局状态；绑定后 BindSeat 会把该座位的待决请求重推一次。
        if (Executor.IsBusy
            && !entry.ReadOnly
            && !call.Name.StartsWith("target.", StringComparison.Ordinal)
            && !string.Equals(call.Name, "room.resume", StringComparison.Ordinal))
        {
            // 若"忙"来自选靶会话异常：先收束该动作并回 engine-rejected（不得永久 busy）。
            if (Executor.TryFailFaultedAction(out var faulted))
            {
                return McpToolResult.Failure(
                    faulted!.FailureCode ?? McpCodes.EngineRejected,
                    faulted.FailureMessage ?? "动作失败（选靶会话异常）。");
            }

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
            ["action"] = pending.Action,
            ["candidates"] = candidates,
            ["identifiers"] = identifiers,
        };
    }
}
