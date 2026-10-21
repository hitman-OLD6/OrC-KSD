namespace Orc.Server;

/// <summary>
/// 服务端运行模式（无头沙盒 S1）：<c>play</c>＝真实玩家面（隐藏信息、无调试工具）；
/// <c>sandbox</c>＝服务层 Authoring（额外暴露调试类工具）。
/// <para>与 <see cref="ServerOptions.Headless"/> **正交**：模式只选服务层权限面，不隐含任何编排。</para>
/// </summary>
public enum ServerMode
{
    /// <summary>真实规则 ＋ 隐藏信息（生产协议面）。</summary>
    Play,

    /// <summary>服务层 Authoring：额外暴露调试/观察面。</summary>
    Sandbox,
}

/// <summary>
/// 无头模式的座位角色（无头沙盒 S2）：<c>ai</c>＝服务端内置 AI；<c>agent</c>＝外部 MCP agent。
/// </summary>
public enum SeatRole
{
    /// <summary>服务端驱动（<c>AiPilot</c>）。</summary>
    Ai,

    /// <summary>外部 agent（预置座位 ＋ 令牌文件 ＋ <c>room.resume</c>）。</summary>
    Agent,
}

/// <summary>
/// 服务端启动选项（多人支持 S1 骨架；S2/S8 起由宿主与传输层消费）。
/// 默认值取自需求与实现 grill 的确认结果。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>卡包目录列表（房主"开启房间"时指定；服务端按此装载卡池）。</summary>
    public required IReadOnlyList<string> PackDirectories { get; init; }

    /// <summary>沙盒写根（沿用服务层唯一可写根语义）。</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>日志目录（stderr 落盘目标；<c>null</c>＝不落盘）。</summary>
    public string? LogDirectory { get; init; }

    /// <summary>监听端口（0＝由系统分配，实际端口经 stdout 就绪行回报）。</summary>
    public int Port { get; init; }

    /// <summary>座位数（本阶段固定 1v1）。</summary>
    public int SeatCount { get; init; } = 2;

    /// <summary>对局态断线后，轮到断线方的宽限时长（超时由服务端代该座位认输）。</summary>
    public TimeSpan DisconnectGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>房间态断线后的座位保留时长（0＝立即释放）。</summary>
    public TimeSpan LobbySeatRetention { get; init; } = TimeSpan.Zero;

    /// <summary>卡组张数要求（房间态校验与 AI 生成共用）。</summary>
    public int RequiredDeckSize { get; init; } = 39;

    /// <summary>卡包内容指纹（两端对账用；<c>null</c>＝不提供）。</summary>
    public string? PackFingerprint { get; init; }

    /// <summary>心跳超时（超过即关闭连接；TCP 半开连接不会产生 FIN，必须靠应用层心跳）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 是否执行完整卡组规则（生产＝true）。仅端到端/诊断场景可关闭
    /// （示例卡包卡种不足时无法组成 39 张合法构筑）；协议面不暴露该开关。
    /// <para>无头链路改用 <see cref="DeckRuleTier"/>；两者同时给出非默认值即参数错误。</para>
    /// </summary>
    public bool EnforceDeckRules { get; init; } = true;

    /// <summary>
    /// 父进程 PID（客户端拉起时传入）。父进程消失则本进程退出——
    /// TCP 之外没有可靠的父子存活信号，这是"客户端崩溃后服务端不残留"的兜底。
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>选靶待决等待上限（超时按取消处理并推 <c>notify/target.expired</c>）。默认 10 分钟。</summary>
    public TimeSpan TargetSelectionTimeout { get; init; } = TimeSpan.FromMinutes(10);

    // ---------------------------------------------------------------- 无头沙盒（S1）

    /// <summary>
    /// 是否开启无头编排（预置座位、自动开局、事件流日志、令牌文件、<c>--exit-on-end</c>）。
    /// **唯一开关**，默认关闭；关闭时既有"房主开房"链路行为零变更。
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>服务层权限面（缺省 <see cref="ServerMode.Play"/>）；**不隐含** <see cref="Headless"/>。</summary>
    public ServerMode Mode { get; init; } = ServerMode.Play;

    /// <summary>无头运行目录（日志、令牌、摘要的唯一落点；缺省由入口派生）。</summary>
    public string? RunDir { get; init; }

    /// <summary>无头座位角色（索引＝座位号；缺省 seat0＝AI、seat1＝agent）。</summary>
    public IReadOnlyList<SeatRole> SeatRoles { get; init; } = [SeatRole.Ai, SeatRole.Agent];

    /// <summary>随机种子（唯一真值：直灌 <c>McpServiceOptions.Seed</c>；<c>null</c>＝引擎自生成）。</summary>
    public int? Seed { get; init; }

    /// <summary>卡组规则档位（无头缺省 <see cref="Orc.Game.Decks.DeckRuleTier.Lenient"/>；非无头恒为 strict/由 <see cref="EnforceDeckRules"/> 决定）。</summary>
    public Orc.Game.Decks.DeckRuleTier DeckRules { get; init; } = Orc.Game.Decks.DeckRuleTier.Strict;

    /// <summary>等待 agent 连接的上限（计时起点＝房间态预置完成）。超时退出码 <c>3</c>。</summary>
    public TimeSpan WaitAgent { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>终局后是否自动退出（无头缺省 true；<c>--keep-alive</c> 置 false）。</summary>
    public bool ExitOnEnd { get; init; } = true;

    /// <summary>是否额外产出按座位脱敏的 <c>seat0.jsonl</c>/<c>seat1.jsonl</c>。</summary>
    public bool LogSeatViews { get; init; }

    /// <summary>是否注册调试写类工具（仅 <see cref="ServerMode.Sandbox"/> 下有效）。</summary>
    public bool AllowDebugWrites { get; init; }
}
