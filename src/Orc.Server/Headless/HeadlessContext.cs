using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orc.Game.Decks;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Output;
using Orc.Server.Execution;
using Orc.Server.Rooms;
using Orc.Server.Transport;

namespace Orc.Server.Headless;

/// <summary>
/// 无头编排上下文（无头沙盒 S1/S2/S4/S6）：运行目录、预置座位、自动开局、等待超时、日志与摘要。
/// <para>
/// **不变量**：一切进入引擎的调用（含自动开局）都经宿主 <see cref="SerialWorkQueue"/> 单消费者执行；
/// 座位生命周期＝进程生命周期（房间态断线不释放座位、不销毁令牌）。
/// </para>
/// </summary>
public sealed class HeadlessContext : IDisposable
{
    private readonly ServerOptions _options;
    private readonly object _sync = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonObject> _decks = new();
    private readonly CancellationTokenSource _lifecycle = new();

    private HeadlessSessionLog? _log;
    private ServerHost? _host;
    private bool _autoStartIssued;
    private bool _matchEnded;
    private bool _finalized;
    private int _errorCount;
    private int _failedCalls;

    internal HeadlessContext(ServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        RunDir = options.RunDir
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".orc-run", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        SummaryPath = Path.Combine(RunDir, "summary.json");

        for (var seat = 0; seat < options.SeatRoles.Count; seat++)
        {
            if (options.SeatRoles[seat] == SeatRole.Agent)
            {
                AgentSeat = seat;
                break;
            }
        }

        AgentTokenPath = AgentSeat is null ? null : Path.Combine(RunDir, "agent.token");
    }

    /// <summary>运行目录（日志、令牌、摘要的唯一落点）。</summary>
    public string RunDir { get; }

    /// <summary>agent 座位号（<c>ai+ai</c>＝<c>null</c>；就绪行据此省略相关字段）。</summary>
    public int? AgentSeat { get; }

    /// <summary>座位令牌文件路径（文件内容即令牌；文件名与座位号解耦）。</summary>
    public string? AgentTokenPath { get; }

    /// <summary>全知事件流路径。</summary>
    public string EventsPath => _log?.EventsPath ?? Path.Combine(RunDir, "events.jsonl");

    /// <summary>工具调用流水路径。</summary>
    public string CallsPath => _log?.CallsPath ?? Path.Combine(RunDir, "calls.jsonl");

    /// <summary>单局摘要路径。</summary>
    public string SummaryPath { get; }

    /// <summary>进程退出码（0 正常 / 1 启动失败 / 2 运行期异常 / 3 等待 agent 超时）。</summary>
    public int ExitCode { get; private set; }

    /// <summary>已请求停止（Program 据此收尾并退出）。</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>预置的座位卡组摘要（张数 ＋ 内容指纹；可复现锚点）。</summary>
    public IReadOnlyList<JsonObject> Decks => _decks;

    // ---------------------------------------------------------------- 启动期

    /// <summary>
    /// 预置两座位并落盘令牌（**先于监听**）：AI 座位由服务端驱动（<c>Connected=true</c>），
    /// agent 座位等待外部 <c>room.resume</c>（<c>Connected=false</c>，仅展示）。
    /// </summary>
    internal void Prepare(AiDeckGenerator generator, Room room)
    {
        Directory.CreateDirectory(RunDir);
        _log = new HeadlessSessionLog(RunDir, _options.LogSeatViews);

        for (var seat = 0; seat < _options.SeatRoles.Count; seat++)
        {
            var role = _options.SeatRoles[seat];
            var lenient = _options.DeckRules == DeckRuleTier.Lenient;
            var deck = generator.Generate(
                _options.RequiredDeckSize,
                bestEffort: lenient,
                relaxCopyLimit: lenient);

            if (deck.Count == 0)
            {
                throw new InvalidOperationException(
                    $"座位 {seat} 的卡组生成失败（卡池不足以生成非空卡组）：请检查 --packs 与 --deck-rules。");
            }

            _decks.Add(new JsonObject
            {
                ["seat"] = seat,
                ["count"] = deck.Count,
                ["fingerprint"] = DeckFingerprint(deck),
            });

            if (role == SeatRole.Ai)
            {
                var added = room.AddAi(new RoomAiSeat(seat, "AI", deck, DeckConfig: null));
                if (!added.Ok)
                {
                    throw new InvalidOperationException($"座位 {seat} 预置 AI 失败：{added.Message}");
                }

                continue;
            }

            var preset = room.PresetSeat(
                seat,
                ControllerKinds.Remote,
                "agent",
                deck,
                deckConfig: null,
                connected: false);

            if (!preset.Ok || preset.SeatToken is null)
            {
                throw new InvalidOperationException($"座位 {seat} 预置 agent 失败：{preset.Message}");
            }

            // 令牌只在进程内有效；文件名固定 agent.token（与座位号解耦），就绪行用 agentSeat 指明座位。
            File.WriteAllText(AgentTokenPath!, preset.SeatToken, new UTF8Encoding(false));
        }
    }

    /// <summary>绑定宿主（构造后、监听前）。</summary>
    internal void Attach(ServerHost host) => _host = host;

    /// <summary>监听就绪后启动编排：<c>ai+ai</c> 立即自动开局；有 agent 座位则开始等待计时。</summary>
    internal void Arm()
    {
        if (AgentSeat is null)
        {
            ScheduleAutoStart();
            return;
        }

        _ = WaitForAgentAsync(_lifecycle.Token);
    }

    // ---------------------------------------------------------------- 自动开局

    /// <summary>座位绑定成功后的回调（只认"会话确实绑定到 agent 座位"这一事实）。</summary>
    internal void OnSeatBound(int seat)
    {
        if (seat == AgentSeat)
        {
            ScheduleAutoStart();
        }
    }

    /// <summary>
    /// 判据＝房间态 ＋ 两座位已填充 ＋ agent 座位有绑定会话（**不含** <c>Connected</c>）。
    /// 入队一次 <see cref="WorkPriority.Command"/> 工作项，在消费者上下文里开局。
    /// </summary>
    private void ScheduleAutoStart()
    {
        if (_host is not { } host)
        {
            return;
        }

        lock (_sync)
        {
            if (_autoStartIssued || _finalized)
            {
                return;
            }

            if (!ReadyToAutoStart())
            {
                return;
            }

            _autoStartIssued = true;
        }

        try
        {
            _ = host.Queue.EnqueueAsync(
                WorkPriority.Command,
                async token =>
                {
                    try
                    {
                        // 执行期二次确认：桥"绑定后立刻断开重试"不得把对局开成无人在场的局。
                        if (!ReadyToAutoStart())
                        {
                            lock (_sync)
                            {
                                _autoStartIssued = false;
                            }

                            return false;
                        }

                        var result = await host.Room!.StartBySystemAsync(token).ConfigureAwait(false);
                        if (!result.Ok)
                        {
                            Console.Error.WriteLine($"[headless] 自动开局失败：{result.Code}: {result.Message}");
                            lock (_sync)
                            {
                                _autoStartIssued = false;
                            }
                        }

                        return result.Ok;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[headless] 自动开局异常：{ex.GetType().Name}: {ex.Message}");
                        lock (_sync)
                        {
                            _autoStartIssued = false;
                        }

                        throw;
                    }
                },
                host.ShutdownToken);
        }
        catch (Exception)
        {
            lock (_sync)
            {
                _autoStartIssued = false;
            }
        }
    }

    private bool ReadyToAutoStart()
    {
        if (_host?.Room is not { } room || room.State != RoomState.Lobby)
        {
            return false;
        }

        for (var seat = 0; seat < _options.SeatRoles.Count; seat++)
        {
            if (room.SeatAt(seat) is null)
            {
                return false;
            }
        }

        return AgentSeat is not { } agentSeat || _host.HasBoundSession(agentSeat);
    }

    // ---------------------------------------------------------------- 等待 agent 超时

    private async Task WaitForAgentAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.WaitAgent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_host?.Room is not { } room || room.State != RoomState.Lobby)
        {
            return;
        }

        if (AgentSeat is { } agentSeat && _host.HasBoundSession(agentSeat))
        {
            // 已绑定但尚未开局（例如开局失败并复位）：超时口径仍按"未开局"处理。
            Console.Error.WriteLine("[headless] 等待 agent 超时（已绑定但未开局）。");
        }
        else
        {
            Console.Error.WriteLine($"[headless] 等待 agent 连接超时（{_options.WaitAgent.TotalSeconds:0} 秒）。");
        }

        Abort("wait-agent-timeout", exitCode: 3);
    }

    // ---------------------------------------------------------------- 日志与摘要

    /// <summary>落盘一批段（全知流 ＋ 可选脱敏流），返回段序号区间。</summary>
    internal (long? From, long? To) LogSegments(IReadOnlyList<EventSegment> segments)
    {
        if (_log is null || _host?.Service.ActiveMatch is not { } match)
        {
            return (null, null);
        }

        return _log.WriteSegments(segments, match);
    }

    /// <summary>
    /// 记录一次工具调用（含门禁拒绝）。
    /// <para>
    /// <paramref name="ok"/>＝false 时按**错误口径**分列：基础设施错误计入 <c>errorCount</c>，
    /// 业务否定计入 <c>failedCalls</c>（R7——见 <see cref="IsInfrastructureError"/>）。
    /// </para>
    /// <para>
    /// <paramref name="extra"/>：调用专属的可归因字段（R6：<c>pendingSessionId</c> / <c>answers</c> / <c>action</c> /
    /// <c>picks</c> / <c>pickedRefs</c>）——合并进记录；用具名形参逐个加会让签名无限增长。
    /// </para>
    /// </summary>
    internal void LogCall(
        string sessionId,
        int? seat,
        string? remote,
        string tool,
        bool ok,
        string? code,
        long durationMs,
        long queueMs,
        long? segmentsFrom,
        long? segmentsTo,
        string phase,
        JsonObject? extra = null)
    {
        if (!ok)
        {
            // R7：错误口径收窄为「基础设施故障」——业务否定（pending-targeting / not-ready / NotYourTurn /
            // TargetSlotOccupied …）是**对局语义**的正常答复，混进 errorCount 会把「正常打了一手非法牌」
            // 报成「服务器坏了」，让 errorCount 失去告警价值。业务否定另计 failedCalls（只增字段、不改既有字段名）。
            if (IsInfrastructureError(code))
            {
                Interlocked.Increment(ref _errorCount);
            }
            else
            {
                Interlocked.Increment(ref _failedCalls);
            }
        }

        if (_log is null)
        {
            return;
        }

        var record = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _callSequence),
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sessionId"] = sessionId,
            ["seat"] = seat,
            ["remote"] = remote,
            ["tool"] = tool,
            ["ok"] = ok,
            ["code"] = code,
            ["durationMs"] = durationMs,
            ["queueMs"] = queueMs,
            ["phase"] = phase,
        };

        if (segmentsFrom is not null)
        {
            record["segmentsFrom"] = segmentsFrom;
        }

        if (segmentsTo is not null)
        {
            record["segmentsTo"] = segmentsTo;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                record[key] = value?.DeepClone();
            }
        }

        _log.WriteCall(record);
    }

    /// <summary>
    /// 是否基础设施错误（R7；白名单制）：传输断开 / 未授权 / 内部故障。
    /// <para>
    /// 用白名单而非黑名单：未知业务码应归入 <c>failedCalls</c>——把不认识的码默认当基础设施故障
    /// 会让 <c>errorCount</c> 再次被业务否定淹没（回到改动前的老问题）。
    /// </para>
    /// </summary>
    internal static bool IsInfrastructureError(string? code)
        => code is "not-connected"                // 桥侧字面量（BridgeServer 生成，不在 McpCodes 中）
            or McpCodes.Unauthorized
            or McpCodes.Internal;

    private long _callSequence;

    /// <summary>对局结束（只置标志；真正的收尾在当次 work item 尾部）。</summary>
    internal void OnMatchEnded()
    {
        _matchEnded = true;
        try
        {
            _lifecycle.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 关闭中。
        }
    }

    /// <summary>当次 work item 尾部调用：写摘要 → fsync → （默认）请求停止。幂等。</summary>
    /// <remarks>
    /// **收尾不可中断**：终局发现点（<c>DetectMatchEnd</c>）会顺手停掉 AI 驱动，而触发终局的那个 AI work item
    /// 正是带着 AI 驱动的取消令牌在跑——若用该令牌写盘，会抛 <see cref="OperationCanceledException"/> 并让
    /// <c>summary.json</c> 永久缺失（且 <see cref="_finalized"/> 已置位，后续 work item 不再重试）。
    /// 因此写盘一律用 <see cref="CancellationToken.None"/>；写失败则复位标志并记录原因，留给下一次 work item 重试。
    /// </remarks>
    internal async Task MaybeFinalizeAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_matchEnded || _finalized)
            {
                return;
            }

            _finalized = true;
        }

        try
        {
            await WriteSummaryAsync(aborted: false, reason: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[headless] 写终局摘要失败：{ex.GetType().Name}: {ex.Message}");

            lock (_sync)
            {
                _finalized = false; // 允许后续 work item 重试（否则摘要与退出会被永久跳过）
            }

            return;
        }

        if (_options.ExitOnEnd)
        {
            RequestStop(0);
        }
    }

    /// <summary>中止（等待超时等）：写 <c>aborted</c> 摘要并请求停止。</summary>
    internal void Abort(string reason, int exitCode)
    {
        lock (_sync)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
        }

        try
        {
            WriteSummaryAsync(aborted: true, reason: reason, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[headless] 写中止摘要失败：{ex.GetType().Name}: {ex.Message}");
        }

        RequestStop(exitCode);
    }

    private async Task WriteSummaryAsync(bool aborted, string? reason, CancellationToken ct)
    {
        await Task.CompletedTask;

        var match = _host?.Service.ActiveMatch;
        var decks = new JsonArray();
        foreach (var deck in _decks)
        {
            decks.Add(deck.DeepClone());
        }

        var summary = new JsonObject
        {
            ["aborted"] = aborted,
            ["reason"] = reason,
            ["mode"] = _options.Mode == ServerMode.Sandbox ? "sandbox" : "play",
            ["winner"] = match?.Winner?.Index,
            ["turns"] = match?.TurnNumber ?? 0,
            ["endReason"] = match?.EndReason?.ToString(),
            ["seed"] = match?.Seed ?? _options.Seed,
            ["packFingerprint"] = _host?.Room?.PackFingerprint,
            ["runDir"] = RunDir,
            ["eventsPath"] = EventsPath,
            ["callsPath"] = CallsPath,
            ["agentSeat"] = AgentSeat,
            // R7 口径变更：errorCount 只统计基础设施错误（传输/授权/内部）；业务否定计入 failedCalls。
            ["errorCount"] = Volatile.Read(ref _errorCount),
            ["failedCalls"] = Volatile.Read(ref _failedCalls),
            ["decks"] = decks,
        };

        await File.WriteAllTextAsync(SummaryPath, summary.ToJsonString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        _log?.Flush(toDisk: true);
    }

    private void RequestStop(int exitCode)
    {
        ExitCode = exitCode;
        _stopped.TrySetResult();
    }

    /// <summary>摘要用卡组指纹：排序后卡 id 列表的 SHA-256 前 16 位（张数不同不构成不可比）。</summary>
    internal static string DeckFingerprint(IReadOnlyList<string> deck)
    {
        var ordered = deck.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', ordered));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
    }

    internal CancellationToken LifecycleToken => _lifecycle.Token;

    /// <summary>宿主关闭时释放日志句柄与生命周期令牌（进程级资源；进程退出亦会回收）。</summary>
    public void Dispose()
    {
        _lifecycle.Cancel();
        _lifecycle.Dispose();
        _log?.Dispose();
        _log = null;
    }

    /// <summary>供测试读取当前摘要路径对应文件（不存在＝false）。</summary>
    public bool SummaryExists => File.Exists(SummaryPath);

    /// <summary>已落盘的调用条数（诊断/测试用；读 <c>calls.jsonl</c> 行数）。</summary>
    public int CallsCount
    {
        get
        {
            if (!File.Exists(CallsPath))
            {
                return 0;
            }

            using var stream = new FileStream(CallsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var count = 0;
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
        }
    }
}
