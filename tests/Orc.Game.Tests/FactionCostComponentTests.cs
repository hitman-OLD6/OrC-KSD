using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S10 功能点②验收（组件重构——「阵营〔国籍〕＋部署费」合并组件）：
/// ②1 组件就位与单一真源（合并组件全类别就位；TagData 保留〔稀有度＋开放 tag〕；旧花费组件退役；
/// 无第二读取真面——反射核验）；②2 写面与校验（国籍/部署费受控写面；合法修改即时可见；
/// 非法修改 fail-fast、不半改；部署费修改经管线传播〔链重跑＋集中触发〕）；
/// ②3 费用修饰链回归（基准→链→有效值行为不变——既有 DeployCostModifierTests 全绿保持；
/// 本文件补「基准修改 × 修饰链随动 × 真实消费」的写面场景——非回归、属写面新增组成部分）。
/// </summary>
public class FactionCostComponentTests
{
    private static UnitCard CreateUnitCard(LogicEngine engine, string faction = Faction.Germany, int deployCost = 3)
        => new(engine, new CardDefinition(
            "单位", deployCost, 1, 2, 4, CardCategory.Unit, faction: faction, rarity: Rarity.Standard));

    // ---------- ②1 组件就位与单一真源 ----------

    [Fact]
    public void Merged_Component_Is_Attached_For_All_Categories_With_Faction_And_DeployCost()
    {
        var library = new CardLibrary(new LogicEngine());
        library.Register("u1", new CardDefinition("单位", 2, 1, 3, 4, CardCategory.Unit, faction: Faction.Japan, rarity: Rarity.Standard));
        library.Register("c1", new CardDefinition("指令", 5, 0, 0, 0, CardCategory.Command, faction: Faction.Soviet, rarity: Rarity.Limited));
        library.Register("x1", new CardDefinition("反制", 1, 0, 0, 0, CardCategory.Counter, faction: Faction.USA, rarity: Rarity.Special));

        var unit = library.Instantiate("u1");
        var command = library.Instantiate("c1");
        var counter = library.Instantiate("x1");

        // 合并组件：国籍＋部署费入同一组件就位（全类别覆盖——单位 / 指令 / 反制）
        var unitFactionCost = unit.GetData<FactionCostData>();
        Assert.Equal(Faction.Japan, unitFactionCost.Faction);
        Assert.Equal(2, unitFactionCost.DeployCost);

        var commandFactionCost = command.GetData<FactionCostData>();
        Assert.Equal(Faction.Soviet, commandFactionCost.Faction);
        Assert.Equal(5, commandFactionCost.DeployCost);

        var counterFactionCost = counter.GetData<FactionCostData>();
        Assert.Equal(Faction.USA, counterFactionCost.Faction);
        Assert.Equal(1, counterFactionCost.DeployCost);

        // TagData 保留（稀有度＋开放 tag；装配属加载链——未加载实例无该组件，完整覆盖见 CardTagDataTests）；
        // 国籍不在 TagData（单一真源——「无第二读取真面」的反射核验见下用例）。
    }

    [Fact]
    public void Legacy_Types_Are_Retired_And_No_Second_Read_Face()
    {
        // 旧独立花费组件退役：类型不再存在（对齐「四合一退役」先例——同一数据无双真源）
        Assert.Null(Type.GetType("Orc.Game.Cards.CommandPointCostData, Orc.Game"));

        // 国籍不再经 TagData 承载（旧读面不保留兼容——迁移为一次性重构）
        Assert.Null(typeof(TagData).GetProperty("Faction"));
        Assert.Null(typeof(TagData).GetProperty("FactionSlot"));

        // 合并组件＝唯一国籍/部署费读面
        Assert.NotNull(typeof(FactionCostData).GetProperty("Faction"));
        Assert.NotNull(typeof(FactionCostData).GetProperty("DeployCost"));
    }

    // ---------- ②2 写面与校验（国籍） ----------

    [Fact]
    public void Faction_Write_Face_Is_Controlled_And_Silent()
    {
        var engine = new LogicEngine();
        var card = CreateUnitCard(engine, Faction.Germany, 3);
        using var recorder = new UpdateRecorder(engine);

        // 合法修改：受控写面可用、即时可见（单源直读——既有筛选/读取消费立即读到新值）
        card.SetFaction(Faction.Italy);
        Assert.Equal(Faction.Italy, card.GetData<FactionCostData>().Faction);

        // 静默变更（不发射/不通知——延续「元数据静默变更」先例）：零更新记录
        Assert.Empty(recorder.Updates);

        // 非法修改（未定义枚举值）：明确拒绝、不产生半改（值保持修改前）
        Assert.Throws<ArgumentOutOfRangeException>(() => card.SetFaction("未定义阵营"));
        Assert.Equal(Faction.Italy, card.GetData<FactionCostData>().Faction);
        Assert.Empty(recorder.Updates); // 拒绝路径零发射
    }

    // ---------- ②2 写面与校验（部署费——经管线传播） ----------

    [Fact]
    public async Task DeployCost_Base_Write_Propagates_Through_Pipeline_And_Chain_Follows()
    {
        var engine = new LogicEngine();
        var card = ModifierTestKit.CreateOrderCard(engine); // 部署费 2（基准）；独立构造——构造期常驻
        using var recorder = new UpdateRecorder(engine);

        // 先建立修饰机制初始基线（「装配完成点建立初始快照」先例——此后基准修改可被管线检测：
        // 「新 vs 上次」；无修饰、基准未变＝零变化、零发射）
        await card.Modifiers.RequestRerunAsync();
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder));

        // 合法修改基准：落值 → 经管线传播（链重跑＋集中触发）——有效值随动、恰一条 card.stat.changed
        await card.SetDeployCostBaseAsync(5);
        Assert.Equal(5, card.GetData<FactionCostData>().DeployCost);
        Assert.Equal(5, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var changed = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.DeployCost }, ModifierTestKit.ChangedFieldsOf(changed.Payload));

        // 链随动回归：既有修饰在基准修改后继续作用（基准 5＋修饰 +1 → 有效 6；基准改 7 → 有效 8）
        recorder.Clear();
        var source = new object();
        await card.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.DeployCost, 1, source));
        Assert.Equal(6, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Single(ModifierTestKit.StatChangedUpdates(recorder)); // 修饰挂载一条

        recorder.Clear();
        await card.SetDeployCostBaseAsync(7);
        Assert.Equal(8, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        var baseUpdate = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder)); // 基准修改一条（链随动）
        Assert.Equal(new[] { CardStatFields.DeployCost }, ModifierTestKit.ChangedFieldsOf(baseUpdate.Payload));

        // 无变更（设同值）：零发射（「有变更才发」管线语义）
        recorder.Clear();
        await card.SetDeployCostBaseAsync(7);
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder));

        // 非法修改（负）：拒绝、值不变（不产生半改——校验先于落值）
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => card.SetDeployCostBaseAsync(-1));
        Assert.Equal(7, card.GetData<FactionCostData>().DeployCost);
        Assert.Equal(8, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));

        // 0 合法（非负整数）：基准 0 → 有效 1（＋1 修饰）
        await card.SetDeployCostBaseAsync(0);
        Assert.Equal(0, card.GetData<FactionCostData>().DeployCost);
        Assert.Equal(1, card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
    }

    [Fact]
    public async Task DeployCost_Base_Write_Is_Consumed_By_Real_Play_Cost_Check()
    {
        // 写面场景（非回归）：基准修改后，既有消费点（预打出校验/扣费）行为不变——仍读「有效值」
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCostlyId); // 部署费 5

        // 未修改：点数 1 < 5 → 预打出拒绝（留手、零副作用）
        var rejected = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Failed, rejected.Status);
        Assert.Equal(PlayFailureReason.PrePlayPointShortage, rejected.FailureReason);

        // 基准修改为 1（受控写面）：消费点读有效值（基准→链→有效）→ 校验通过 → 扣费读 1
        await command.SetDeployCostBaseAsync(1);
        var played = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Success, played.Status);
        Assert.Equal(1, command.GetData<FactionCostData>().DeployCost);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(command, player.Hand);
    }
}
