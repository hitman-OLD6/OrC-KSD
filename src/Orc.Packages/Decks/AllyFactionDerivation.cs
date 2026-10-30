using Orc.Game.Cards;

namespace Orc.Packages.Decks;

/// <summary>
/// 盟国自动推导（R12/D16）：结果**必定存在**，因此「无盟国」这个形态在卡组资产里不存在——
/// 卡组可以一张盟国卡都不带，但盟国标识恒有值。
/// <para>
/// 推导**先于判定**、结果落到 <see cref="DeckDefinition.DeckConfig"/>：开局不重算，否则卡池或卡定义变化会让
/// 同一份卡组在不同时间点解出不同盟国（同一份文件两种含义）。
/// </para>
/// </summary>
public static class AllyFactionDerivation
{
    /// <summary>
    /// 推导盟国：① 按各阵营张数（排除主国、排除 neutral）取最多者；② 并列取 <see cref="Faction.All"/> 登记序在前者；
    /// ③ 除主国与 neutral 外无卡 ⇒ 取 <see cref="Faction.All"/> 中第一个满足 <c>IsValidAlly</c> 者。
    /// </summary>
    /// <param name="mainFaction">主国（须为已定义主国；非法值由调用方先行拒绝，这里不重复判定）。</param>
    /// <param name="entries">卡引用（未知卡跳过——它们另有 <c>unknown-card</c> 违规，不应影响推导）。</param>
    /// <param name="pool">卡池（已注册且通过卡牌判定的卡）。</param>
    /// <exception cref="InvalidOperationException">兜底也找不到合法盟国（主国非法时的防御分支）。</exception>
    public static string Derive(
        string mainFaction,
        IReadOnlyList<DeckEntry> entries,
        IReadOnlyDictionary<string, CardDefinition> pool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainFaction);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(pool);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!pool.TryGetValue(entry.AppId, out var definition))
            {
                continue;
            }

            var faction = definition.Faction;
            if (IsMainOrNeutral(faction, mainFaction))
            {
                continue;
            }

            counts[faction] = counts.GetValueOrDefault(faction) + entry.Count;
        }

        // 按登记序遍历 + 严格大于 ⇒ 并列时自然取登记序在前者（自定义阵营不在 All 里，不参与推导）。
        string? best = null;
        var bestCount = 0;
        foreach (var faction in Faction.All)
        {
            if (IsMainOrNeutral(faction, mainFaction)
                || !counts.TryGetValue(faction, out var count)
                || count <= bestCount)
            {
                continue;
            }

            best = faction;
            bestCount = count;
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var faction in Faction.All)
        {
            if (!string.Equals(faction, Faction.Neutral, StringComparison.Ordinal)
                && FactionRules.IsValidAlly(mainFaction, faction))
            {
                return faction;
            }
        }

        throw new InvalidOperationException(
            $"主国 '{mainFaction}' 在内置阵营清单中没有合法盟国（兜底不成立）。");
    }

    private static bool IsMainOrNeutral(string faction, string mainFaction)
        => string.Equals(faction, mainFaction, StringComparison.Ordinal)
           || string.Equals(faction, Faction.Neutral, StringComparison.Ordinal);
}
