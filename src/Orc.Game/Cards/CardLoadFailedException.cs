using Orc.Game.Cards.Loading;

namespace Orc.Game.Cards;

/// <summary>
/// **卡牌加载失败**（卡级 fail-fast 的显式信号）：装载模板判定"这张卡加载不起来"时抛出，
/// 由装载链决定处置——卡组装载＝该卡不产生（其余卡照常）、运行期单卡创建＝上抛给调用方。
/// <para>
/// 与"单条效果隔离"的区别：隔离＝该效果不生效、卡照常可用；本异常＝**卡的能力已不可信**。
/// 触发口径（用户口径：「编译不过时，卡牌判加载失败」）：效果 csx 在 v4 承载视图下编译不过——
/// 该效果永远不可能生效（旧链已删、无回退可走）。
/// </para>
/// <para>
/// 携带报告面需要的定位信息：卡名／对局级卡 id／资产 id／**原因枚举**（<see cref="Orc.Game.Cards.Loading.CardIssueReason"/>
/// 的冻结取值，直接成为报告 <c>fields[].reason</c> 的 token）；卡库键（应用层 id）由装载链补上——
/// 卡牌实例自身不持有定义 id。
/// </para>
/// </summary>
public sealed class CardLoadFailedException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="cardName">卡名（数据体名称）。</param>
    /// <param name="matchCardId">对局级卡 id（加载时已分配＝非 null）。</param>
    /// <param name="assetId">出错的效果／组件资产 id。</param>
    /// <param name="reason">原因（报告 <c>fields[].reason</c> 的取值来源）。</param>
    /// <param name="detail">原始原因（含底层编译器原文）。</param>
    /// <param name="inner">内层异常。</param>
    public CardLoadFailedException(
        string cardName,
        int? matchCardId,
        string assetId,
        CardIssueReason reason,
        string detail,
        Exception inner)
        : base($"卡牌 '{cardName}' 加载失败（{reason}；资产 '{assetId}'）：{detail}", inner)
    {
        CardName = cardName;
        MatchCardId = matchCardId;
        AssetId = assetId;
        Reason = reason;
    }

    /// <summary>卡名（数据体名称）。</summary>
    public string CardName { get; }

    /// <summary>对局级卡 id（加载时已分配＝非 null）。</summary>
    public int? MatchCardId { get; }

    /// <summary>出错的效果／组件资产 id。</summary>
    public string AssetId { get; }

    /// <summary>失败原因（<see cref="Orc.Game.Cards.Loading.CardIssueReason"/> 的冻结取值）。</summary>
    public CardIssueReason Reason { get; }
}
