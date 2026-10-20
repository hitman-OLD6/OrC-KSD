using System.Globalization;
using Orc.Game.Triggers;

namespace Orc.Game.Cards.Loading;

/// <summary>
/// **牌堆卡位装载请求**（S7；对局接线面的输入）：第几个玩家的第几个卡位引用了哪个应用层 ID。
/// <para>
/// <see cref="PlayerId"/> 由对局层给出、加载器**不解释其语义**（只用于报告定位）；局内新增卡同样以单条清单元数据走同一入口。
/// </para>
/// </summary>
/// <param name="PlayerId">牌堆归属玩家标识（非空）。</param>
/// <param name="Index">卡位序号（该玩家牌堆内的位置）。</param>
/// <param name="Reference">应用层 ID 或裸局部 ID（解析经中间层；解析失败＝该卡位留空）。</param>
public sealed record MatchCardSlot(string PlayerId, int Index, string Reference)
{
    /// <summary>
    /// 按玩家索引构造卡位（对局层的唯一装配约定：<see cref="PlayerId"/> ＝ 玩家索引的十进制串）。
    /// </summary>
    /// <param name="playerIndex">玩家索引（0／1）。</param>
    /// <param name="index">卡位序号。</param>
    /// <param name="reference">应用层 ID 或裸局部 ID。</param>
    /// <exception cref="ArgumentOutOfRangeException">playerIndex 为负。</exception>
    public static MatchCardSlot ForPlayer(int playerIndex, int index, string reference)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playerIndex);
        return new MatchCardSlot(playerIndex.ToString(CultureInfo.InvariantCulture), index, reference);
    }
}

/// <summary>卡位装载结局（S7）：<see cref="Loaded"/>＝可实例化；<see cref="Empty"/>＝卡位留空（开局继续）。</summary>
public enum CardSlotStatus
{
    /// <summary>已产出可玩定义（合法或警报——警报卡已按类型默认值补缺）。</summary>
    Loaded = 0,

    /// <summary>卡位留空（未注册／非法／未入 lock）——牌堆该位不产生卡。</summary>
    Empty = 1,
}

/// <summary>一个卡位的装载结局（应用层 ID 为**解析后**的规范形态；留空＝null）。</summary>
/// <param name="Status">结局。</param>
/// <param name="AppId">解析后的应用层 ID（留空＝null）。</param>
public sealed record CardSlotOutcome(CardSlotStatus Status, string? AppId)
{
    /// <summary>卡位留空。</summary>
    public static CardSlotOutcome Empty { get; } = new(CardSlotStatus.Empty, null);

    /// <summary>是否装载成功。</summary>
    public bool IsLoaded => Status == CardSlotStatus.Loaded;
}

/// <summary>
/// 一次装载的结局（S7）：<see cref="Slots"/> 与输入**同序**、同长；<see cref="BatchId"/> 用于日志关联。
/// <para>本类型**不含卡实例**：装载只做解析／校验／定义生成，实例化仍由对局在
/// <c>LoadDecksAsync</c> 内原位进行（ID 水位线语义挂在这条时序上）。</para>
/// </summary>
/// <param name="BatchId">批次标识（同一次加载的多条日志关联用，不含时间戳语义）。</param>
/// <param name="Slots">卡位结局（与输入同序）。</param>
public sealed record CardLoadOutcome(string BatchId, IReadOnlyList<CardSlotOutcome> Slots)
{
    /// <summary>装载成功的卡位数。</summary>
    public int LoadedCount => Slots.Count(slot => slot.IsLoaded);

    /// <summary>是否全部卡位装载成功。</summary>
    public bool AllLoaded => Slots.All(slot => slot.IsLoaded);
}

/// <summary>
/// **对局卡牌加载器端口**（S7；依赖倒置）：对局层只声明「整批装载这些卡位」，
/// 实现由宿主提供（`Orc.Packages` 的卡组装载：资产加载器 → 卡牌加载器 → 卡库）。
/// <para>
/// 契约要点：
/// <list type="bullet">
///   <item>**整批一次调用**（双方牌堆合并为一次）；单卡＝一元清单——同一入口，不设第二条装载路径。</item>
///   <item>**不分配实例 ID**：只做解析／校验／定义生成与卡库落地；实例化仍在对局的原位时序上进行。</item>
///   <item>卡库键＝**应用层 ID**（本端口不向对局层暴露 GUID）；GUID → 定义的解析全部留在实现侧。</item>
///   <item>牌堆中的非法／未注册卡位＝<see cref="CardSlotStatus.Empty"/>（开局继续），不阻断整批。</item>
/// </list>
/// </para>
/// </summary>
public interface IMatchCardLoader
{
    /// <summary>
    /// 对局装配期：把本局卡库接到装载链（资产加载器产出分层列表 → 卡牌加载器整批 → 合法卡进卡库，含资产域）。
    /// </summary>
    /// <param name="library">本局卡库（装载落地目标）。</param>
    /// <param name="triggers">本局触发器注册表（加载器登记进 LowLevel 审计面——仅记账）。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidOperationException">重复接入；或装载链结构性失败。</exception>
    void Attach(CardLibrary library, TriggerRegistry triggers);

    /// <summary>
    /// 整批装载（双方牌堆＝一次调用；局内新增卡＝一元清单）：返回与输入同序的卡位结局。
    /// </summary>
    /// <param name="slots">卡位清单（可为空＝空批次）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">slots 为 null。</exception>
    /// <exception cref="InvalidOperationException">尚未 <see cref="Attach"/>（装配时序错误）。</exception>
    Task<CardLoadOutcome> LoadBatchAsync(IReadOnlyList<MatchCardSlot> slots, CancellationToken ct = default);
}
