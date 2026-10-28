namespace Orc.Game.Cards.Loading;

/// <summary>
/// 一条**实例装配阶段的组件失败**（游戏层口径；装载链送给宿主的原始形状）。
/// <para>
/// 为什么在游戏层单独有个类型：卡牌加载报告（<c>CardLoadReport</c>）住在 <c>Orc.Packages</c>，
/// 而依赖方向是 `Orc.Packages → Orc.Game`——游戏层**不能**引用报告模型，只能把事实交出去
/// （<c>Match.ComponentFailureSink</c>），由宿主映射成报告侧回填入参。
/// </para>
/// <para>字段与报告侧回填入参同形：应用层 ID／资产 GUID（游戏层不知道 GUID，通常为 null，按 ID 命中卡位）／字段问题。</para>
/// </summary>
/// <param name="AppId">卡库键（＝应用层 ID；牌堆条目形态，用于命中报告卡位）。</param>
/// <param name="AssetGuid">资产版本 GUID（游戏层通常为 null＝按 <paramref name="AppId"/> 命中）。</param>
/// <param name="Issues">字段问题（原因取 <see cref="CardIssueReason"/> 的冻结 token）。</param>
public sealed record CardComponentFailureData(
    string? AppId,
    Guid? AssetGuid,
    IReadOnlyList<CardFieldIssue> Issues);
