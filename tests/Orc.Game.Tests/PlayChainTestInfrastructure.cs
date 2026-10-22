using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Tests;

/// <summary>
/// 打出链测试套件数据与工具（2B）：定制定义集对局（费用/类别可控）、加载卡取用、全槽位候选脚本。
/// 卡组＝轻单位 x10（起手 4/5 与推回合用例可复现）；六个定制定义覆盖费用不足/足额与三大类别。
/// </summary>
internal static class PlayChainTestKit
{
    /// <summary>轻单位（部署费 1 / 行动费 1 / 攻 2 / 防 3）。</summary>
    public const string UnitCheapId = "pu1";

    /// <summary>重单位（部署费 5 / 行动费 1 / 攻 4 / 防 5）。</summary>
    public const string UnitCostlyId = "pu5";

    /// <summary>轻指令（部署费 1）。</summary>
    public const string CommandCheapId = "pc1";

    /// <summary>重指令（部署费 5）。</summary>
    public const string CommandCostlyId = "pc5";

    /// <summary>轻反制（部署费 1）。</summary>
    public const string CounterCheapId = "px1";

    /// <summary>重反制（部署费 3）。</summary>
    public const string CounterCostlyId = "px3";

    /// <summary>定制定义集（六个；费用/类别入测试断言对照）。W1-1 随改：必填槽位（国籍/稀有度）统一补 Germany / Standard（无特定语义卡取合理值）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(UnitCheapId, new CardDefinition("轻单位", deployCost: 1, operateCost: 1, attack: 2, defense: 3, CardCategory.Unit, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(UnitCostlyId, new CardDefinition("重单位", deployCost: 5, operateCost: 1, attack: 4, defense: 5, CardCategory.Unit, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CommandCheapId, new CardDefinition("轻指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CommandCostlyId, new CardDefinition("重指令", deployCost: 5, operateCost: 0, attack: 0, defense: 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CounterCheapId, new CardDefinition("轻反制", deployCost: 1, operateCost: 0, attack: 0, defense: 0, CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CounterCostlyId, new CardDefinition("重反制", deployCost: 3, operateCost: 0, attack: 0, defense: 0, CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>创建打出链测试对局（双方轻单位 x10 卡组＋定制定义集；可选目标选择桥接）。</summary>
    public static Match CreatePlayMatch(MockTargeterBridge? bridge = null, int seed = 42, bool skipMulligan = true)
        => new(
            new CardList(Enumerable.Repeat(UnitCheapId, 10)),
            new CardList(Enumerable.Repeat(UnitCheapId, 10)),
            CreateDefinitions(),
            seed,
            firstPlayerIndex: null,
            options: new MatchOptions { SkipMulligan = skipMulligan },
            targeterBridge: bridge,
            packageActivator: TestHost.Activator);

    /// <summary>实例化＋加载一张定制卡（归属＝player；可选放入手牌——离手/取消断言需要）。</summary>
    public static async Task<T> InstantiateLoadedAsync<T>(Match match, Player player, string id = UnitCheapId, bool toHand = true)
        where T : CardBase
    {
        var card = (T)match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        if (toHand)
        {
            player.Hand.Add(card);
        }

        return card;
    }

    /// <summary>候选收集脚本（提交全战场槽位引用——超集；后端粗筛收敛到候选面）。</summary>
    public static Func<object, Task<IReadOnlyList<object?>>> AllSlotsCandidatesScript(Match match)
        => _ => Task.FromResult<IReadOnlyList<object?>>(AllSlotsOf(match).Select(slot => (object?)slot.Ref).ToArray());

    /// <summary>全战场槽位（三线顺序枚举）。</summary>
    public static IEnumerable<Orc.Cards.Card> AllSlotsOf(Match match)
        => match.Battlefield.PlayerASupportLine.Members
            .Concat(match.Battlefield.FrontLine.Members)
            .Concat(match.Battlefield.PlayerBSupportLine.Members);
}
