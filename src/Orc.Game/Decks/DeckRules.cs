using Orc.Game.Cards;

namespace Orc.Game.Decks;

/// <summary>一条卡组违规（可定位到卡 id 与违规类别）。</summary>
/// <param name="Kind">类别：<c>size</c>/<c>blank-id</c>/<c>unknown-card</c>/<c>copy-limit</c>/<c>faction-*</c>。</param>
/// <param name="CardId">违规卡 id（尺寸/配国类违规为 null）。</param>
/// <param name="Message">人读说明。</param>
public sealed record DeckViolation(string Kind, string? CardId, string Message);

/// <summary>
/// 卡组合法性规则（多人支持 S7）：尺寸、卡池存在性、同名上限（按稀有度分档）、主国/盟国配对。
/// <para>
/// **唯一实现**：MCP 建局（开局复检）与房间填充（即时校验）共用，不允许各写一套。
/// 不抛异常：全部违规以列表返回，由调用方决定错误码与呈现。
/// </para>
/// </summary>
public static class DeckRules
{
    /// <summary>默认卡组张数（KARDS 口径：39 张，总部随玩家自动生成）。</summary>
    public const int DefaultRequiredSize = 39;

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
    public static IReadOnlyList<DeckViolation> Validate(
        IReadOnlyList<string> deck,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredSize = DefaultRequiredSize)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(pool);

        var violations = new List<DeckViolation>();

        if (requiredSize > 0 && deck.Count != requiredSize)
        {
            violations.Add(new DeckViolation("size", null, $"卡组为 {deck.Count} 张，要求 {requiredSize} 张。"));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
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

    /// <summary>校验主国/盟国配对（未提供配置＝通过；配置成对由 <see cref="Match"/> 创建期另行校验）。</summary>
    public static DeckViolation? ValidateFaction(PlayerDeckConfiguration? configuration)
    {
        if (configuration is null)
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
