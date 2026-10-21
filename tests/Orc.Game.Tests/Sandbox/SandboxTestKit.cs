using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests.Sandbox;

/// <summary>
/// 沙盒原语测试基建：最小定义集（单位／指令／高费指令／单位价格）＋建局助手（跳过换牌、空起手＝局面干净）。
/// 断言口径：本套用例只经 <see cref="Match.Sandbox"/> 公开面与原语结果载体，不碰触发器内部。
/// </summary>
internal static class SandboxTestKit
{
    /// <summary>普通单位（部署费 1／行动费 1／攻 1／防 3；步兵）。</summary>
    public const string UnitId = "u_sbx_inf";

    /// <summary>轻指令（部署费 1／无效果）。</summary>
    public const string OrderId = "u_sbx_order";

    /// <summary>重指令（部署费 5——补足费用场景）。</summary>
    public const string HeavyOrderId = "u_sbx_heavy";

    /// <summary>第二单位（防 2——死亡链／移动场景用）。</summary>
    public const string ScoutId = "u_sbx_scout";

    /// <summary>定义集（4 枚；与卡组 id 对应）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(UnitId, new CardDefinition(
            "沙盒步兵", deployCost: 1, operateCost: 1, attack: 1, defense: 3,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ScoutId, new CardDefinition(
            "沙盒斥候", deployCost: 1, operateCost: 1, attack: 2, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(OrderId, new CardDefinition(
            "沙盒轻指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            category: CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(HeavyOrderId, new CardDefinition(
            "沙盒重指令", deployCost: 5, operateCost: 0, attack: 0, defense: 0,
            category: CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>
    /// 建局并初始化（跳过换牌＝直接"进行"态；起手 0 张＝手牌/场面由用例自行构造）。
    /// <paramref name="authorized"/> 决定 <see cref="MatchOptions.SandboxAuthorized"/>（缺省授权＝沙盒装配路径）。
    /// </summary>
    public static async Task<Match> CreateMatchAsync(bool authorized = true, int seed = 42, MatchOptions? options = null)
    {
        var resolved = options ?? new MatchOptions
        {
            SkipMulligan = true,
            OpeningHandSizeFirstPlayer = 0,
            OpeningHandSizeSecondPlayer = 0,
            SandboxAuthorized = authorized,
        };

        var match = new Match(
            new CardList(Enumerable.Repeat(UnitId, 10)),
            new CardList(Enumerable.Repeat(UnitId, 10)),
            CreateDefinitions(),
            seed: seed,
            options: resolved,
            packageActivator: TestHost.Activator);

        await match.Initialize();
        return match;
    }

    /// <summary>收集引擎更新信号名（订阅逻辑引擎 update 流；顺序＝发射序）。</summary>
    public static List<string> TrackUpdates(Match match)
    {
        var seen = new List<string>();
        match.Engine.Subscribe((kind, _, _) =>
        {
            seen.Add(kind);
            return Task.CompletedTask;
        });
        return seen;
    }

    /// <summary>解析引用键 → 引擎引用表对象（未命中＝null）。</summary>
    public static object? Resolve(Match match, string reference)
        => match.Engine.References.TryResolve(reference, out var value) ? value : null;

    /// <summary>玩家支援线指定索引的空槽（越界/占用＝断言失败）。</summary>
    public static Orc.Game.Board.Slot EmptySupportSlot(Match match, Player player, int index)
    {
        var slot = match.Battlefield.GetSupportLine(player)[index];
        Assert.True(slot.IsEmpty, $"支援线槽 {index} 应保持空槽（测试前置）。");
        return slot;
    }

    /// <summary>
    /// 对局状态快照（逐字段可比对文本）：双方点数/槽数/手牌数/卡组数、三条线的槽位占用者标识、回合数、引擎卡数。
    /// 用于「拒绝＝零状态变更」的逐字段断言。
    /// </summary>
    public static string Snapshot(Match match)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var player in match.Players)
        {
            builder.Append($"P{player.Index}{{points={player.Points};slots={player.PointSlots};")
                .Append($"hand={player.Hand.Count};deck={player.Deck.Count}}};");
        }

        AppendLine(builder, "A", match.Battlefield.PlayerASupportLine);
        AppendLine(builder, "F", match.Battlefield.FrontLine);
        AppendLine(builder, "B", match.Battlefield.PlayerBSupportLine);

        builder.Append($"turn={match.TurnNumber};cards={match.Engine.Cards.Count};");
        return builder.ToString();

        static void AppendLine(System.Text.StringBuilder target, string label, Orc.Game.Board.BattleLine line)
        {
            target.Append($"{label}[");
            for (var i = 0; i < line.Count; i++)
            {
                var occupant = line[i].Occupant;
                target.Append(occupant is null
                    ? "-"
                    : $"{occupant.GetType().Name}:{(occupant as Orc.Core.Entity)?.Name}");
                target.Append(i == line.Count - 1 ? ']' : ',');
            }

            target.Append(';');
        }
    }
}
