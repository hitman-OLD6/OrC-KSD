using Orc.Game.Decks;
using Orc.Protocol;

namespace Orc.Server;

/// <summary>
/// 服务端运行模式（S1；刻意变化第 1 条后**收窄为"是否注册调试类工具"**）。
/// <para>
/// 输出面判定（全知是否合法）已由拓扑（管理员批准）决定，**不再**读本开关。
/// </para>
/// </summary>
public enum ServerMode
{
    /// <summary>真实规则 ＋ 隐藏信息（不注册调试类工具）。</summary>
    Play,

    /// <summary>额外注册调试类工具（<c>Authoring</c> 面）。</summary>
    Sandbox,
}

/// <summary>座位形态（由管理器 spawn 配置决定；配置即视为"已批准"的只有 AI 座位）。</summary>
public enum SeatKind
{
    /// <summary>AI 座位：服务器进程内驱动，不占连接。</summary>
    Ai,

    /// <summary>人类 / agent 座位：留空等 <c>session.attach</c>。</summary>
    Agent,
}

/// <summary>
/// 座位启动配置（spawn 参数的一部分）：<c>seats: [{kind:"ai", packages:[], deck?}, {kind:"agent"}]</c>。
/// <para>
/// AI 座位**配置即视为已批准**（不需要再走申请）；其 <see cref="Packages"/> 缺省为空集
/// ＝并集判定的单位元（只使用人类一侧声明的包，不额外要求）。
/// </para>
/// </summary>
/// <param name="Kind">座位形态。</param>
/// <param name="DisplayName">展示名（缺省按座位号生成）。</param>
/// <param name="Packages">卡包声明（引用形态；AI 缺省空集）。</param>
/// <param name="Deck">卡组（缺省＝开局装配后由本局卡池生成）。</param>
/// <param name="DeckConfig">构筑配置（可空）。</param>
public sealed record SeatConfig(
    SeatKind Kind = SeatKind.Agent,
    string? DisplayName = null,
    IReadOnlyList<PackageRef>? Packages = null,
    IReadOnlyList<string>? Deck = null,
    DeckConfigPayload? DeckConfig = null,
    string? DeckId = null);

/// <summary>
/// 服务端启动选项（spawn 参数；由管理器传来，不在 CLI 里隐式传递能力开关）。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>卡包目录列表（管理器按资产库解析后下发；服务端只校验"可索引"）。</summary>
    public required IReadOnlyList<string> PackDirectories { get; init; }

    /// <summary>沙盒写根（沿用服务层唯一可写根语义）。</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>监听端口（0＝由系统分配，实际端口经 stdout 就绪行回报）。</summary>
    public int Port { get; init; }

    /// <summary>座位配置（恒 2 个；索引＝座位号）。</summary>
    public IReadOnlyList<SeatConfig> Seats { get; init; } = [new SeatConfig(), new SeatConfig()];

    /// <summary>座位数（恒 2——引擎是 1v1）。</summary>
    public int SeatCount => Seats.Count;

    /// <summary>对局态断线后，轮到断线方的宽限时长（超时由服务端代该座位认输）。</summary>
    public TimeSpan DisconnectGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>卡组张数要求（开局校验与 AI 生成共用）。</summary>
    public int RequiredDeckSize { get; init; } = 39;

    /// <summary>心跳超时（超过即关闭连接；判据＝无任何**入站帧**，含 <c>ping</c>）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 是否执行完整卡组规则（生产＝true）。仅端到端/诊断场景可关闭
    /// （示例卡包卡种不足时无法组成 39 张合法构筑）；协议面不暴露该开关。
    /// </summary>
    public bool EnforceDeckRules { get; init; } = true;

    /// <summary>卡组规则档位（缺省 strict＝生产口径）。</summary>
    public DeckRuleTier DeckRules { get; init; } = DeckRuleTier.Strict;

    /// <summary>父进程 PID（管理器拉起时传入）。父进程消失则本进程退出。</summary>
    public int? ParentProcessId { get; init; }

    /// <summary>选靶待决等待上限（超时按取消处理并推 <c>notify/target.expired</c>）。默认 10 分钟。</summary>
    public TimeSpan TargetSelectionTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>服务层权限面（缺省 <see cref="ServerMode.Play"/>）。</summary>
    public ServerMode Mode { get; init; } = ServerMode.Play;

    /// <summary>是否注册调试写类工具（仅 <see cref="ServerMode.Sandbox"/> 下有效）。</summary>
    public bool AllowDebugWrites { get; init; }

    /// <summary>随机种子（唯一真值：直灌 <c>McpServiceOptions.Seed</c>；<c>null</c>＝引擎自生成）。</summary>
    public int? Seed { get; init; }

    /// <summary>实例标签（仅日志/诊断）。</summary>
    public string? Label { get; init; }

    /// <summary>生效的卡组规则档位（关闭开关映射到 <see cref="DeckRuleTier.None"/>）。</summary>
    public DeckRuleTier EffectiveDeckTier => EnforceDeckRules ? DeckRules : DeckRuleTier.None;
}
