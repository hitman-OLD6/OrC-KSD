using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S9（G6+G13 生成·复制·转换）测试数据与工具：
/// ①专用定义集（发射源〔效果宿主〕、轻步兵〔衍生卡/生成目标/转换目标〕、精锐兵〔转换目标〕、钳击兵〔离场通知验证〕）；
/// ②更新记录器扩展；③场景对局构造与准备助手。
/// <para>
/// **场景效果桩已删除**（§28）：它继承 <c>ActiveEffect&lt;TView&gt;</c>（随 <c>Effect</c> 家族删除）。
/// v4 的施放由卡上组件边（<c>engine#card.played</c>）承载，<c>CardCastActions.Of(card)</c> 恒返回空——
/// 以其为驱动的那批服务场景用例（CardServiceTests）一并删除，需在测试改造阶段按 v4 组件重写。
/// </para>
/// </summary>
internal static class S9Kit
{
    /// <summary>发射源（效果宿主——指令卡；不参与战斗）。</summary>
    public const string EmitterId = "u_s9_emit";

    /// <summary>轻步兵（生成目标/衍生卡——「构筑外」验证用；攻 1 / 防 2）。</summary>
    public const string LightInfantryId = "u_s9_light";

    /// <summary>精锐兵（转换目标——定义 X；坦克类型；攻 3 / 防 4）。</summary>
    public const string EliteId = "u_s9_elite";

    /// <summary>钳击兵（离场通知接入验证；攻 2 / 防 3）。</summary>
    public const string PincerUnitId = "u_s9_pincer";

    /// <summary>场景效果标识（声明到发射源）。</summary>
    public const string ProbeEffectId = "effect.s9.probe";

    /// <summary>专用定义集（4 枚；装配期注册进卡库——「衍生卡定义随装配期注册」）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(EmitterId, new CardDefinition(
            "发射源", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(LightInfantryId, new CardDefinition(
            "轻步兵", deployCost: 1, operateCost: 1, attack: 1, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(EliteId, new CardDefinition(
            "精锐兵", deployCost: 2, operateCost: 1, attack: 3, defense: 4,
            unitTypes: new[] { UnitType.Tank }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(PincerUnitId, new CardDefinition(
            "钳击兵", deployCost: 1, operateCost: 1, attack: 2, defense: 3,
            unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Pincer) },
            faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>创建场景对局（双方步兵 x10 卡组＋S9 专用定义集；可选桥接/效果注册表）。</summary>
    public static Match CreateSceneMatch(
        MockTargeterBridge? bridge = null,
        CardEffectRegistry? effectRegistry = null,
        int seed = 42)
        => CommandTestKit.CreateCommandMatch(
            bridge, seed, effectRegistry, extraDefinitions: CreateDefinitions());

    /// <summary>攻击力有效值（测试断言辅助）。</summary>
    public static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    /// <summary>攻击力有效值（测试断言辅助）。</summary>
    public static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    /// <summary>指定玩家的存活在场单位计数（测试断言辅助——「场上计数一致」）。</summary>
    public static int CountAliveUnitsOf(Match match, Player player)
        => CommandTestKit.AllUnitsOf(match)
            .Count(unit => ReferenceEquals(unit.Owner, player)
                && !unit.GetData<UnitStateData>().IsDestroyed);
}

/// <summary>
/// 更新记录器扩展（S9 测试辅助；复用既有 <see cref="UpdateRecorder"/>〔TestInfrastructure〕——
/// 补充按类型计数/取载荷/总数三个只读断言辅助，不动既有类）。
/// </summary>
internal static class UpdateRecorderExtensions
{
    /// <summary>指定类型的记录数。</summary>
    public static int CountOf(this UpdateRecorder recorder, string type)
        => recorder.Types.Count(item => string.Equals(item, type, StringComparison.Ordinal));

    /// <summary>总记录数。</summary>
    public static int TotalCount(this UpdateRecorder recorder) => recorder.Updates.Count;

    /// <summary>指定类型的首条载荷（无＝null）。</summary>
    public static IReadOnlyDictionary<string, object?>? PayloadOf(this UpdateRecorder recorder, string type)
        => recorder.Updates
            .FirstOrDefault(item => string.Equals(item.Type, type, StringComparison.Ordinal))
            .Payload;
}
