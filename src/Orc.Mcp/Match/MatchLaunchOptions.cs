namespace Orc.Mcp.Match;

/// <summary>
/// 座位装配输入：一个座位对应一份**显式卡组**（由控制器给出；不做自动构筑）。
/// </summary>
/// <param name="Seat">座位号（0＝玩家A、1＝玩家B）。</param>
/// <param name="Deck">卡组名单（卡 id 序列；可含包内原 id 或已前缀化 id）。</param>
/// <param name="Name">展示名（缺省＝按座位号生成）。</param>
/// <param name="DeckConfiguration">构筑配置（主国/盟国；提供即由 <c>Match</c> 创建期校验并注入）。</param>
public sealed record MatchSeatConfig(
    int Seat,
    IReadOnlyList<string> Deck,
    string? Name = null,
    Orc.Game.PlayerDeckConfiguration? DeckConfiguration = null);

/// <summary>
/// 真实对局创建输入（进程内；非沙盒）：卡包目录 ＋ 两个座位装配输入。
/// </summary>
public sealed class MatchLaunchOptions
{
    /// <summary>
    /// 卡包目录列表（按顺序注册与装载；**加载严格按元数据**——每个目录须含 <c>manifest.json</c>）。
    /// S7 起目录链（无 manifest 的裸卡目录）已退役：无元数据即无资产身份，不能进入中间层与 lock 定版。
    /// </summary>
    public required IReadOnlyList<string> PackDirectories { get; init; }

    /// <summary>0 号座位。</summary>
    public required MatchSeatConfig SeatA { get; init; }

    /// <summary>1 号座位。</summary>
    public required MatchSeatConfig SeatB { get; init; }

    /// <summary>随机种子（缺省＝引擎自动生成；指定以复现）。</summary>
    public int? Seed { get; init; }

    /// <summary>跳过换牌相位（缺省 false＝真实对局须经换牌）。</summary>
    public bool SkipMulligan { get; init; }

    /// <summary>先手起手张数（默认 4）。</summary>
    public int OpeningHandSizeFirstPlayer { get; init; } = Orc.Game.MatchOptions.DefaultOpeningHandSizeFirstPlayer;

    /// <summary>后手起手张数（默认 5）。</summary>
    public int OpeningHandSizeSecondPlayer { get; init; } = Orc.Game.MatchOptions.DefaultOpeningHandSizeSecondPlayer;

    /// <summary>
    /// 卡组张数要求（默认 39——KARDS 默认构筑口径：39 张卡组，总部随玩家自动生成）。
    /// 不满足＝创建失败。
    /// </summary>
    public int RequiredDeckSize { get; init; } = 39;

    /// <summary>
    /// 是否执行完整卡组规则（张数/卡池/同名上限/配国）。
    /// **协议面（房间开局）必须为 true**；仅诊断/测试场景可关闭（示例卡包卡种不足时无法组成 39 张合法卡组）。
    /// </summary>
    public bool EnforceDeckRules { get; init; } = true;
}
