namespace Orc.Packages.Decks;

/// <summary>卡组定位结果状态。</summary>
public enum DeckLookupStatus
{
    /// <summary>唯一命中。</summary>
    Resolved = 0,

    /// <summary>不在可用集合。</summary>
    NotFound = 1,

    /// <summary>歧义（同一写法命中多份不同卡组）。</summary>
    Ambiguous = 2,
}

/// <summary>卡组定位结果（R6/D7/D65）。</summary>
/// <param name="Status">结果状态。</param>
/// <param name="AppId">命中的应用层 ID（未命中＝null）。</param>
/// <param name="Deck">命中的卡组对象（未命中／歧义＝null）。</param>
/// <param name="Candidates">歧义时的候选应用层 ID（其余情况＝空）。</param>
public sealed record DeckLookup(
    DeckLookupStatus Status,
    string? AppId,
    DeckDefinition? Deck,
    IReadOnlyList<string> Candidates)
{
    internal static DeckLookup NotFound() => new(DeckLookupStatus.NotFound, null, null, Array.Empty<string>());
}

/// <summary>
/// **卡组定位索引**（R6/D7/D65）：给「应用层 ID／同包裸局部 ID／展示名」三种写法提供同一入口。
/// <para>
/// 索引只装**lock 后可用集合**里的卡组（planner 实际排进本局计划、且 DTO 非空的那些）——
/// 这是 D8 的另一半：合法性判定看注册集合，名称解析看可用集合，两者不得混用。
/// </para>
/// <para>
/// 展示名键＝<c>Name.Trim()</c> 原样（不做大小写/全半角归一）：<c>示例先遣队</c> 与 <c>示例 先遣队</c> 是两个名字；
/// 重名报歧义本身是安全行为，静默择一才是坑。
/// </para>
/// </summary>
public sealed class DeckCatalog
{
    private readonly Dictionary<string, List<(string AppId, DeckDefinition Deck)>> _byName = new(StringComparer.Ordinal);
    private readonly List<(string AppId, DeckDefinition Deck)> _entries = new();

    private DeckCatalog()
    {
    }

    /// <summary>空索引（没有任何可用卡组）。</summary>
    public static DeckCatalog Empty { get; } = new();

    /// <summary>可用卡组（应用层 ID ＋ 定义；去重后按应用层 ID 稳定序）。</summary>
    public IReadOnlyList<(string AppId, DeckDefinition Deck)> Entries => _entries;

    /// <summary>构建索引（同 appId 重复＝后者不进，保持确定性）。</summary>
    public static DeckCatalog Build(IEnumerable<(string AppId, DeckDefinition Deck)> decks)
    {
        ArgumentNullException.ThrowIfNull(decks);

        var catalog = new DeckCatalog();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (appId, deck) in decks.OrderBy(item => item.AppId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(appId) || deck is null || !seen.Add(appId))
            {
                continue;
            }

            catalog._entries.Add((appId, deck));
            var name = deck.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                // D65：索引侧再判一次——将来若放宽「name 必填」，也不让空名悄悄进索引。
                continue;
            }

            if (!catalog._byName.TryGetValue(name, out var bucket))
            {
                bucket = new List<(string, DeckDefinition)>();
                catalog._byName.Add(name, bucket);
            }

            bucket.Add((appId, deck));
        }

        return catalog;
    }

    /// <summary>按应用层 ID 精确取（未命中＝null）。</summary>
    public DeckDefinition? FindByAppId(string appId)
        => _entries.FirstOrDefault(entry => string.Equals(entry.AppId, appId, StringComparison.Ordinal)).Deck;

    /// <summary>
    /// 解析座位给的标识（R6）：应用层 ID → 裸局部 ID → 展示名；重名＝歧义（附候选）。
    /// </summary>
    /// <param name="token">座位给的标识（空白＝未命中）。</param>
    public DeckLookup Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return DeckLookup.NotFound();
        }

        var trimmed = token.Trim();

        if (AssetAppId.IsAppId(trimmed))
        {
            var byId = _entries
                .Where(entry => string.Equals(entry.AppId, trimmed, StringComparison.Ordinal))
                .ToArray();
            return Choose(byId);
        }

        var byLocalId = _entries
            .Where(entry => AssetAppId.TrySplit(entry.AppId, out _, out var localId)
                            && string.Equals(localId, trimmed, StringComparison.Ordinal))
            .ToArray();
        if (byLocalId.Length > 0)
        {
            return Choose(byLocalId);
        }

        return _byName.TryGetValue(trimmed, out var named) ? Choose(named.ToArray()) : DeckLookup.NotFound();
    }

    private static DeckLookup Choose(IReadOnlyList<(string AppId, DeckDefinition Deck)> matches)
    {
        if (matches.Count == 0)
        {
            return DeckLookup.NotFound();
        }

        var distinct = matches
            .GroupBy(match => match.AppId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return distinct.Length == 1
            ? new DeckLookup(DeckLookupStatus.Resolved, distinct[0].AppId, distinct[0].Deck, Array.Empty<string>())
            : new DeckLookup(
                DeckLookupStatus.Ambiguous,
                null,
                null,
                distinct.Select(match => match.AppId).OrderBy(appId => appId, StringComparer.Ordinal).ToArray());
    }
}
