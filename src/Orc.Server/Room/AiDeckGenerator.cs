using Orc.Game.Cards;
using Orc.Game.Decks;

namespace Orc.Server.Rooms;

/// <summary>
/// AI 卡组生成器（多人支持 S8b）：从已装载卡池随机组一副**合法**构筑。
/// <para>
/// 规则：随机主国 → 该主国 ＋ 中立卡 → 按稀有度同名上限填满；卡种不足时返回空（由调用方报明确错误）。
/// 生成的卡组仍会经 <see cref="DeckRules"/> 复检。
/// </para>
/// </summary>
public sealed class AiDeckGenerator
{
    private readonly IReadOnlyDictionary<string, CardDefinition> _pool;
    private readonly Random _random;

    public AiDeckGenerator(IReadOnlyDictionary<string, CardDefinition> pool, Random? random = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _random = random ?? new Random();
    }

    /// <summary>生成卡组；无法满足张数时返回空列表。</summary>
    public IReadOnlyList<string> Generate(int size = DeckRules.DefaultRequiredSize)
    {
        if (size <= 0 || _pool.Count == 0)
        {
            return [];
        }

        var majors = _pool.Values
            .Select(definition => definition.Faction)
            .Distinct(StringComparer.Ordinal)
            .Where(FactionRules.IsMajorFaction)
            .OrderBy(_ => _random.Next())
            .ToArray();

        foreach (var main in majors)
        {
            var allowed = _pool
                .Where(pair => string.Equals(pair.Value.Faction, main, StringComparison.Ordinal)
                    || string.Equals(pair.Value.Faction, Faction.Neutral, StringComparison.Ordinal))
                .OrderBy(_ => _random.Next())
                .ToArray();

            var deck = new List<string>(size);
            while (deck.Count < size)
            {
                var added = false;
                foreach (var (id, definition) in allowed)
                {
                    var limit = DeckRules.CopyLimit(definition.Rarity);
                    var used = 0;
                    foreach (var entry in deck)
                    {
                        if (string.Equals(entry, id, StringComparison.Ordinal))
                        {
                            used++;
                        }
                    }

                    if (used >= limit)
                    {
                        continue;
                    }

                    deck.Add(id);
                    added = true;
                    if (deck.Count >= size)
                    {
                        break;
                    }
                }

                if (!added)
                {
                    break;
                }
            }

            if (deck.Count == size)
            {
                return deck;
            }
        }

        return [];
    }
}
