using Orc.Game.Cards;
using Orc.Game.Decks;

namespace Orc.Server.Rooms;

/// <summary>
/// AI 卡组生成器（多人支持 S8b）：从已装载卡池随机组一副**合法**构筑。
/// <para>
/// 规则：随机主国 → 该主国 ＋ 中立卡 → 按稀有度同名上限填满。
/// <c>bestEffort=false</c>（缺省＝strict 口径）时卡种不足返回空，由调用方报明确错误；
/// <c>bestEffort=true</c>（无头 <c>lenient</c>）时返回**能凑到的最大**卡组，绝不返回空。
/// </para>
/// <para>
/// 确定性：同一 <see cref="Random"/> 实例与同一卡池 → 同一结果。无头链路用 <c>--seed</c> 构造
/// <see cref="Random"/>，因此"同种子 ＋ 同卡包"可复现（两副卡组张数允许不同）。
/// 不引入占位卡凑数（占位卡会污染对局语义）；下限＝1 张（满足 Match"卡组名单非空"契约）。
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

    /// <summary>生成卡组；无法满足张数时返回空列表（<paramref name="bestEffort"/>＝false 时）。</summary>
    /// <param name="size">目标张数（&lt;=0 且 <paramref name="bestEffort"/>＝false ＝不生成）。</param>
    /// <param name="bestEffort">是否尽力而为（返回最大可凑集合，绝不返回空）。</param>
    /// <param name="relaxCopyLimit">
    /// 是否放宽同名上限（无头 <c>lenient</c> 档）。默认 <c>false</c>＝按稀有度上限（strict 口径）。
    /// <para>
    /// 放宽后单卡最多可重复到目标张数——小卡包（如示例包 5 种卡）因此也能凑满 39 张，
    /// 避免长局因牌库过早抽空而触发引擎“空集合 Draw 被拒绝”。与 <c>DeckRules</c> 的 lenient 档同源。
    /// </para>
    /// </param>
    public IReadOnlyList<string> Generate(
        int size = DeckRules.DefaultRequiredSize,
        bool bestEffort = false,
        bool relaxCopyLimit = false)
    {
        if (_pool.Count == 0)
        {
            return [];
        }

        if (size <= 0 && !bestEffort)
        {
            return [];
        }

        var target = size > 0 ? size : int.MaxValue;

        var majors = _pool.Values
            .Select(definition => definition.Faction)
            .Distinct(StringComparer.Ordinal)
            .Where(FactionRules.IsMajorFaction)
            .OrderBy(_ => _random.Next())
            .ToArray();

        List<string>? best = null;

        foreach (var main in majors)
        {
            var allowed = _pool
                .Where(pair => string.Equals(pair.Value.Faction, main, StringComparison.Ordinal)
                    || string.Equals(pair.Value.Faction, Faction.Neutral, StringComparison.Ordinal))
                .OrderBy(_ => _random.Next())
                .ToArray();

            var deck = new List<string>(Math.Min(target, 64));
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            while (deck.Count < target)
            {
                var added = false;
                foreach (var (id, definition) in allowed)
                {
                    // 放宽同名上限时用目标张数作上限（而不是 int.MaxValue）——避免 size<=0 时无限填充。
                    var limit = relaxCopyLimit
                        ? (size > 0 ? size : 64)
                        : DeckRules.CopyLimit(definition.Rarity);
                    var count = used.GetValueOrDefault(id);
                    if (count >= limit)
                    {
                        continue;
                    }

                    used[id] = count + 1;
                    deck.Add(id);
                    added = true;
                    if (deck.Count >= target)
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

            if (bestEffort && (best is null || deck.Count > best.Count))
            {
                best = deck;
            }
        }

        if (bestEffort && best is { Count: > 0 })
        {
            return best;
        }

        return [];
    }
}
