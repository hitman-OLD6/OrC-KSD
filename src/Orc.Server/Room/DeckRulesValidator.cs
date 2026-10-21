using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// 房间态卡组校验：包装 <see cref="DeckRules"/>（唯一实现），卡池由服务端启动链路注入。
/// </summary>
public sealed class DeckRulesValidator : IDeckValidator
{
    private readonly Func<IReadOnlyDictionary<string, CardDefinition>> _pool;
    private readonly bool _enforce;
    private readonly int _requiredDeckSize;

    public DeckRulesValidator(
        Func<IReadOnlyDictionary<string, CardDefinition>> pool,
        bool enforce = true,
        int requiredDeckSize = DeckRules.DefaultRequiredSize)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _enforce = enforce;
        _requiredDeckSize = requiredDeckSize;
    }

    /// <inheritdoc />
    public McpToolResult? Validate(IReadOnlyList<string> deck, RoomDeckConfig? config)
    {
        if (!_enforce)
        {
            return deck.Count == 0
                ? McpToolResult.Failure(McpCodes.DeckInvalid, "卡组为空（卡组必须显式提供）。")
                : null;
        }

        // 张数口径来自 ServerOptions.RequiredDeckSize（与 AI 卡组生成同源），不得写死 39。
        var violations = new List<DeckViolation>(DeckRules.Validate(deck, _pool(), _requiredDeckSize));

        if (DeckRules.ValidateFaction(ToConfiguration(config)) is { } factionViolation)
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
