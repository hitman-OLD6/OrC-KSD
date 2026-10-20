using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// 一次卡牌加载的**一个卡位引用**（牌堆卡位 → 应用层 ID）：批量＝多条清单，单卡＝一元清单（R5/D33）。
/// <para>
/// 「牌堆」不是本层的概念——本类型只承载「第几个玩家的第几个卡位引用了哪个应用层 ID」，
/// 局内新增卡（token/升级/老兵）同样以一元清单走同一入口。
/// </para>
/// </summary>
/// <param name="PlayerId">牌堆归属玩家标识（非空；由调用方给出，卡牌加载器不解释其语义）。</param>
/// <param name="Index">卡位序号（该玩家牌堆内的位置；报告用它定位「第几个卡位为什么空了」）。</param>
/// <param name="AppId">应用层 ID（含裸局部 ID——由中间层解析；解析失败＝该卡位留空）。</param>
public sealed record CardLoadItem(string PlayerId, int Index, string AppId);

/// <summary>
/// **卡牌引用**（D36）：加载产物的一条「应用层 ID → 已装载资产版本」条目（并入中间层的输入）。
/// 只对**已生成**的卡存在（合法／警报）；非法卡不生成，故无引用条目。
/// </summary>
/// <param name="AppId">应用层 ID。</param>
/// <param name="AssetGuid">资产版本 GUID（资产层 ID）。</param>
/// <param name="AssetName">资产名（<c>{种类}：{局部ID}@{包GUID8}#{版本}</c>）。</param>
/// <param name="Version">资产版本。</param>
/// <param name="Domain">资产域（正式／测试）。</param>
/// <param name="Tier">卡级档位（警报卡已按类型默认值补缺）。</param>
/// <param name="Definition">生成物（定义的唯一真源仍是注册面；此处为本次装载解析出的同一实例/补全实例）。</param>
public sealed record CardReference(
    string AppId,
    Guid AssetGuid,
    string AssetName,
    AssetVersion Version,
    AssetDomain Domain,
    CardLoadTier Tier,
    CardDefinition Definition);

/// <summary>
/// 一次卡牌加载的产出（R5/D33）：**报告 ＋ 卡牌引用列表**。
/// <para>报告不落盘（D43）——只经返回值与工具返回；本类型不含任何文件/序列化副作用。</para>
/// </summary>
/// <param name="Report">加载报告（按资产去重＋引用计数；每个卡位一条 slots）。</param>
/// <param name="Cards">卡牌引用列表（去重后的已生成卡，报告条目同序）。</param>
public sealed record CardLoadResult(CardLoadReport Report, IReadOnlyList<CardReference> Cards)
{
    /// <summary>本批标识（冗余自报告，便于日志关联）。</summary>
    public string BatchId => Report.BatchId;

    /// <summary>本批是否全部合法（无警报、无非法、无留空卡位）。</summary>
    public bool IsClean => Report.Items.All(item => item.Tier == CardLoadTier.Valid)
        && Report.Slots.All(slot => slot.Tier == CardLoadTier.Valid);
}
