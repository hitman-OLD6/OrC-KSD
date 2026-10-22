using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// 房间态卡组校验：包装 <see cref="DeckRules"/>（唯一实现），卡池由服务端启动链路注入。
/// <para>
/// 档位（无头沙盒 S2b）与 <see cref="DeckRules"/> 同源：<c>strict</c>＝生产口径；
/// <c>lenient</c>＝放宽张数/同名上限/配国但**保留**卡池校验；<c>none</c>＝既有关闭开关的映射。
/// </para>
/// </summary>
public sealed class DeckRulesValidator
{
    private readonly Func<IReadOnlyDictionary<string, CardDefinition>> _pool;
    private readonly DeckRuleTier _tier;
    private readonly int _requiredDeckSize;

    public DeckRulesValidator(
        Func<IReadOnlyDictionary<string, CardDefinition>> pool,
        bool enforce = true,
        int requiredDeckSize = DeckRules.DefaultRequiredSize,
        DeckRuleTier tier = DeckRuleTier.Strict)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _tier = enforce ? tier : DeckRuleTier.None;
        _requiredDeckSize = requiredDeckSize;
    }

    /// <summary>当前档位（诊断/守卫测试用）。</summary>
    public DeckRuleTier Tier => _tier;

    /// <summary>校验一副卡组（通过＝null；否则为可读失败结果）。</summary>
    public McpToolResult? Validate(IReadOnlyList<string> deck, RoomDeckConfig? config)
    {
        if (_tier == DeckRuleTier.None)
        {
            return deck.Count == 0
                ? McpToolResult.Failure(McpCodes.DeckInvalid, "卡组为空（卡组必须显式提供）。")
                : null;
        }

        // 张数口径来自 ServerOptions.RequiredDeckSize（与 AI 卡组生成同源），不得写死 39。
        var violations = new List<DeckViolation>(DeckRules.Validate(deck, _pool(), _requiredDeckSize, _tier));

        if (DeckRules.ValidateFaction(ToConfiguration(config), _tier) is { } factionViolation)
        {
            violations.Add(factionViolation);
        }

        return violations.Count == 0
            ? null
            : McpToolResult.Failure(
                McpCodes.DeckInvalid,
                "卡组不合法：" + string.Join("；", violations.Select(v => v.Message)));
    }

    private static PlayerDeckConfiguration? ToConfiguration(RoomDeckConfig? config)
        => config is null ? null : new PlayerDeckConfiguration(config.MainFaction, config.AllyFaction);
}
