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

    public DeckRulesValidator(Func<IReadOnlyDictionary<string, CardDefinition>> pool, bool enforce = true)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _enforce = enforce;
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

        var violations = new List<DeckViolation>(DeckRules.Validate(deck, _pool()));

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
