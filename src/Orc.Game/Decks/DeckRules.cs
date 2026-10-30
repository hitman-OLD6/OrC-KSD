using Orc.Game.Cards;

namespace Orc.Game.Decks;

/// <summary>一条卡组违规（可定位到卡 id 与违规类别）。</summary>
/// <param name="Kind">
/// 类别（**稳定契约**，客户端按它分类展示）：<c>size</c>／<c>blank-id</c>／<c>unknown-card</c>／<c>copy-limit</c>／
/// <c>not-deckable</c>／<c>faction-main-missing</c>／<c>faction-main-invalid</c>／<c>faction-ally-invalid</c>／
/// <c>faction-foreign</c>／<c>ally-quota</c>／<c>name-missing</c>／<c>entry-invalid</c>／<c>duplicate-entry</c>。
/// 其中 <c>entry-invalid</c>＝某条卡引用的 <c>count</c> 非法（类型错／≤0），<c>duplicate-entry</c>＝同一张卡被重复声明；
/// 二者都是**条目自身**不成立，与 <c>size</c>（卡组总张数不符口径）不是一回事。
/// </param>
/// <param name="CardId">违规卡 id（尺寸/配国类违规为 null）。</param>
/// <param name="Message">人读说明。</param>
public sealed record DeckViolation(string Kind, string? CardId, string Message);

/// <summary>
/// 卡组规则档位（无头沙盒 S2b）：**分项放宽**，绝不"整体关闭"。
/// <para>
/// <c>Strict</c>＝与生产校验逐项等同；<c>Lenient</c>＝放宽张数/同名上限/配国，**保留** blank-id 与
/// unknown-card（卡池权威是硬边界）；<c>None</c>＝跳过全部校验（仅既有测试夹具）。
/// </para>
/// </summary>
public enum DeckRuleTier
{
    /// <summary>生产口径。</summary>
    Strict,

    /// <summary>放宽张数、同名上限、主国/盟国配对；保留空白 id 与卡池存在性。</summary>
    Lenient,

    /// <summary>全部跳过。</summary>
    None,
}

/// <summary>
/// 卡组合法性规则（多人支持 S7）：尺寸、卡池存在性、同名上限（按稀有度分档）、可构筑性、主国/盟国配对。
/// <para>
/// **唯一实现**：MCP 建局（开局复检）与房间填充（即时校验）共用，不允许各写一套。
/// 不抛异常：全部违规以列表返回，由调用方决定错误码与呈现。
/// </para>
/// </summary>
public static class DeckRules
{
    /// <summary>默认卡组张数（KARDS 口径：39 张，总部随玩家自动生成）。</summary>
    public const int DefaultRequiredSize = 39;

    /// <summary>盟国卡总张数上限（R10 第 2 条：≤12；neutral 不计入）。</summary>
    public const int AllyCopyLimit = 12;

    /// <summary>同名上限（按稀有度分档）：标准 4 / 限定 3 / 特殊 2 / 精英 1。</summary>
    public static int CopyLimit(string rarity) => rarity switch
    {
        Rarity.Elite => 1,
        Rarity.Special => 2,
        Rarity.Limited => 3,
        _ => 4,
    };

    /// <summary>
    /// 校验卡组结构与内容。
    /// </summary>
    /// <param name="deck">卡组名单（应用层 id；调用方应已解析为卡池键）。</param>
    /// <param name="pool">卡池（id → 定义）。</param>
    /// <param name="requiredSize">要求张数（&lt;=0＝不校验张数）。</param>
    /// <param name="tier">规则档位（默认 strict＝既有行为逐项不变）。</param>
    public static IReadOnlyList<DeckViolation> Validate(
        IReadOnlyList<string> deck,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredSize = DefaultRequiredSize,
        DeckRuleTier tier = DeckRuleTier.Strict)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(pool);

        var violations = new List<DeckViolation>();

        if (tier == DeckRuleTier.None)
        {
            return violations;
        }

        var lenient = tier == DeckRuleTier.Lenient;

        if (!lenient && requiredSize > 0 && deck.Count != requiredSize)
        {
            violations.Add(new DeckViolation("size", null, $"卡组为 {deck.Count} 张，要求 {requiredSize} 张。"));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var reportedNotDeckable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in deck)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                violations.Add(new DeckViolation("blank-id", id, "卡组含空白卡 id。"));
                continue;
            }

            if (!pool.TryGetValue(id, out var definition))
            {
                violations.Add(new DeckViolation("unknown-card", id, $"卡 id '{id}' 不在已装载卡池中。"));
                continue;
            }

            var count = counts.GetValueOrDefault(id) + 1;
            counts[id] = count;

            // 不可构筑卡（R9/D12）：与 unknown-card 同侧——lenient **保留**、none 已在上方跳过。
            // 判定只读 CardDefinition.Deckable（老兵内建规则已在建定义时合并进去，这里不再看 VeteranOf）。
            if (!definition.Deckable && reportedNotDeckable.Add(id))
            {
                violations.Add(new DeckViolation(
                    "not-deckable",
                    id,
                    $"'{definition.Name}'（{id}）不可构筑（衍生卡或老兵卡），不得进卡组。"));
            }

            if (lenient)
            {
                continue;
            }

            var limit = CopyLimit(definition.Rarity);
            if (count == limit + 1)
            {
                // 只在首次越界时报一条，避免同卡 39 张刷屏。
                violations.Add(new DeckViolation(
                    "copy-limit",
                    id,
                    $"'{definition.Name}' 超过同名上限（稀有度 {definition.Rarity}：最多 {limit} 张，实际 {deck.Count(x => x == id)} 张）。"));
            }
        }

        return violations;
    }

    /// <summary>
    /// 校验卡组展示名（R2/D64/D66）：缺失或空白 ⇒ <c>name-missing</c>。
    /// <para>
    /// 属「卡组自身组」，判定顺序上早于逐卡检查——先确认这是一份合法声明，再逐张查卡。
    /// </para>
    /// </summary>
    public static DeckViolation? ValidateName(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? new DeckViolation("name-missing", null, "卡组缺少展示名 name（展示名参与按名定位，不得为空）。")
            : null;

    /// <summary>
    /// 校验阵营归属与盟国配额（R10 第 1／2 条）：卡阵营 ∉ {主国, 盟国, neutral} ⇒ <c>faction-foreign</c>；
    /// 盟国卡合计 &gt; 12 ⇒ <c>ally-quota</c>。
    /// <para>
    /// 档位归位（D12）：两项与「配国」同侧——strict 查、lenient 放宽、none 跳过。
    /// 未提供构筑配置＝归属无从判，直接通过（配置本身的问题由 <see cref="ValidateFaction"/> 报）。
    /// </para>
    /// </summary>
    /// <param name="deck">卡组名单（应用层 id，可含重复张）。</param>
    /// <param name="pool">卡池（id → 定义）。</param>
    /// <param name="configuration">构筑配置（**最终值**：盟国已含 R12 推导结果）。</param>
    /// <param name="tier">规则档位。</param>
    public static IReadOnlyList<DeckViolation> ValidateFactionComposition(
        IReadOnlyList<string> deck,
        IReadOnlyDictionary<string, CardDefinition> pool,
        PlayerDeckConfiguration? configuration,
        DeckRuleTier tier = DeckRuleTier.Strict)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(pool);

        var violations = new List<DeckViolation>();
        if (tier != DeckRuleTier.Strict
            || configuration?.MainFaction is not { Length: > 0 } main)
        {
            return violations;
        }

        var ally = configuration.AllyFaction;
        var allyCount = 0;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in deck)
        {
            if (string.IsNullOrWhiteSpace(id) || !pool.TryGetValue(id, out var definition))
            {
                continue; // 空白/未知卡已在 Validate 报过，不重复
            }

            var faction = definition.Faction;
            if (string.Equals(faction, Faction.Neutral, StringComparison.Ordinal)
                || string.Equals(faction, main, StringComparison.Ordinal))
            {
                continue;
            }

            if (ally is { Length: > 0 } && string.Equals(faction, ally, StringComparison.Ordinal))
            {
                allyCount++;
                continue;
            }

            // 同一张卡在名单里出现多次：只报一条（按卡点名，不刷屏）。
            if (reported.Add(id))
            {
                violations.Add(new DeckViolation(
                    "faction-foreign",
                    id,
                    $"'{definition.Name}'（{id}）的阵营 {faction} 既非主国 {main}、也非盟国 "
                    + $"{(ally is { Length: > 0 } ? ally : "<无>")} 或 neutral——不允许三国混编。"));
            }
        }

        if (allyCount > AllyCopyLimit)
        {
            violations.Add(new DeckViolation(
                "ally-quota",
                null,
                $"盟国 {ally} 的卡合计 {allyCount} 张，超过上限 {AllyCopyLimit} 张（neutral 不计入）。"));
        }

        return violations;
    }

    /// <summary>校验主国/盟国配对（未提供配置＝通过；配置成对由 <see cref="Match"/> 创建期另行校验）。</summary>
    public static DeckViolation? ValidateFaction(
        PlayerDeckConfiguration? configuration,
        DeckRuleTier tier = DeckRuleTier.Strict)
    {
        if (configuration is null || tier != DeckRuleTier.Strict)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuration.MainFaction))
        {
            return new DeckViolation("faction-main-missing", null, "未指定主国（构筑配置须提供主国）。");
        }

        if (!FactionRules.IsMajorFaction(configuration.MainFaction))
        {
            return new DeckViolation(
                "faction-main-invalid",
                null,
                $"主国 '{configuration.MainFaction}' 不是五大主国之一。");
        }

        if (configuration.AllyFaction is { Length: > 0 } ally
            && !FactionRules.IsValidAlly(configuration.MainFaction, ally))
        {
            return new DeckViolation(
                "faction-ally-invalid",
                null,
                $"盟国 '{ally}' 非法（不得等于主国，也不得为中立）。");
        }

        return null;
    }
}
