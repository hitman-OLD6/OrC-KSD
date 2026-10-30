using Orc.Game;

namespace Orc.Packages.Decks;

/// <summary>
/// 卡组内的一条卡引用（R2/D46）：应用层 ID ＋ 张数聚合。
/// <para>
/// **不展开**成 N 条同 ID：保留聚合形态才能无损往返、诊断与 diff 才看得清「这张卡要 4 张」。
/// 展开只有 <see cref="DeckDefinition.ToCardIds"/> 一处实现。
/// </para>
/// </summary>
/// <param name="AppId">应用层 ID（<c>pack/{包id}/{局部ID}</c>；定义层恒为全名）。</param>
/// <param name="Count">张数（&gt; 0）。</param>
public sealed record DeckEntry(string AppId, int Count);

/// <summary>
/// 卡组定义（R2/R5/D46-D50）：注册面产出、开局消费的**合法卡组对象**。
/// <para>
/// 「全合法才生成」是卡牌同一哲学：非法卡组不产出本对象（注册面只留身份与诊断）。
/// 卡组**不产生卡牌实例、不占对局卡牌 ID 水位线**（Constraints）。
/// </para>
/// </summary>
public sealed class DeckDefinition
{
    /// <summary>创建卡组定义（调用方须已完成合法性判定——本类型不再复核业务规则）。</summary>
    /// <param name="id">包内局部 ID。</param>
    /// <param name="name">展示名（D64：非可空；空白与否由判定层负责，这里只拒绝 null）。</param>
    /// <param name="deckConfig">**推导后的最终构筑配置**（恒为完整两国，D16/D47）。</param>
    /// <param name="allyDerived">盟国是否由 R12 推导得出（仅展示用：不参与比对、不落盘）。</param>
    /// <param name="entries">卡引用（声明顺序；同 ID 不得重复）。</param>
    /// <exception cref="ArgumentException">id 空白；构筑配置缺主国/盟国；条目空白 ID／张数非正／重复。</exception>
    /// <exception cref="ArgumentNullException">name／deckConfig／entries 为 null。</exception>
    public DeckDefinition(
        string id,
        string name,
        PlayerDeckConfiguration deckConfig,
        bool allyDerived,
        IReadOnlyList<DeckEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(deckConfig);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(deckConfig.MainFaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(deckConfig.AllyFaction);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.AppId);

            if (entry.Count <= 0)
            {
                throw new ArgumentException(
                    $"卡组 '{id}' 的条目 '{entry.AppId}' 张数为 {entry.Count}（须为正整数）。", nameof(entries));
            }

            if (!seen.Add(entry.AppId))
            {
                throw new ArgumentException(
                    $"卡组 '{id}' 重复声明了 '{entry.AppId}'（聚合形式下同 ID 重复＝歧义）。", nameof(entries));
            }

            total += entry.Count;
        }

        Id = id;
        Name = name;
        DeckConfig = deckConfig;
        AllyDerived = allyDerived;
        Entries = entries.ToArray();
        CardCount = total;
    }

    /// <summary>包内局部 ID。</summary>
    public string Id { get; }

    /// <summary>展示名（R6 的第三种定位写法；参与跨包重名歧义判定）。</summary>
    public string Name { get; }

    /// <summary>
    /// 构筑配置的**最终值**（恒为完整两国）：文件省略 <c>allyFaction</c> 时这里就是推导结果。
    /// 座位显式给的配置只与此值比对（D47）——比对基准不是文件原文。
    /// </summary>
    public PlayerDeckConfiguration DeckConfig { get; }

    /// <summary>盟国是否由 R12 推导（UI/诊断展示用；**不参与任何比对、不落盘**）。</summary>
    public bool AllyDerived { get; }

    /// <summary>卡引用（数据体声明顺序——决定洗牌前初始序列，影响种子可复现性）。</summary>
    public IReadOnlyList<DeckEntry> Entries { get; }

    /// <summary>总张数（Σ Count）。</summary>
    public int CardCount { get; }

    /// <summary>
    /// **唯一展开实现**（D46）：按 <see cref="Entries"/> 声明顺序、每条重复 <see cref="DeckEntry.Count"/> 次。
    /// 不排序——排序会让同一份文件的展开结果与作者意图脱钩。
    /// </summary>
    public IReadOnlyList<string> ToCardIds()
    {
        var ids = new List<string>(CardCount);
        foreach (var entry in Entries)
        {
            for (var index = 0; index < entry.Count; index++)
            {
                ids.Add(entry.AppId);
            }
        }

        return ids;
    }
}
