using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2A 验收锚点④（数据组件全集）：数据组件就位；拆分实施（指挥点花费＋对战三值；四合一退役、无双真源）；
/// S10 随改：指挥点花费并入「阵营〔国籍〕＋部署费」合并组件（FactionCostData——旧独立花费组件退役）。
/// 字段与基础读写可测（含实时值读写；初始值：位置 null／已毁 false／指挥 false·false／激活 false）。
/// 2C-A1 随改：词条面已迁出数据组件体系（三口合并替代＝词条组件化；寻址＝卡上词条面 <see cref="CardBase.Keywords"/>）——
/// 相关用例迁移至 KeywordComponentTests（见原用例处的逐条迁移说明）。
/// </summary>
public class CardDataComponentTests
{
    private static UnitCard CreateUnitCard(LogicEngine? engine = null)
        => new(engine ?? new LogicEngine(), new CardDefinition("单位", 1, 2, 3, 4, CardCategory.Unit, faction: Faction.Germany, rarity: Rarity.Standard));

    [Fact]
    public void Unit_Instantiate_Attaches_Both_Split_Components_From_Definition()
    {
        var card = CreateUnitCard();

        var factionCost = card.GetData<FactionCostData>(); // 阵营〔国籍〕＋部署费（合并组件——S10；全类别）
        Assert.Equal(1, factionCost.DeployCost);
        Assert.Equal(Faction.Germany, factionCost.Faction);
        var stats = card.GetData<BattleStatsData>(); // 对战三值（初始值；单位卡专属）
        Assert.Equal(2, stats.OperateCost);
        Assert.Equal(3, stats.Attack);
        Assert.Equal(4, stats.Defense);
    }

    [Fact]
    public void Category_Specific_Assembly_Commands_And_Counters_Carry_No_Battle_Stats()
    {
        var library = new CardLibrary(new LogicEngine());
        library.Register("c1", new CardDefinition("指令", 5, 0, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        library.Register("x1", new CardDefinition("反制", 2, 0, 0, 0, CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard));

        // 指令＝合并组件（无其他数据组件）
        var command = library.Instantiate("c1");
        Assert.Equal(5, command.GetData<FactionCostData>().DeployCost);
        Assert.Throws<KeyNotFoundException>(() => command.GetData<BattleStatsData>());

        // 反制＝合并组件＋激活状态组件（2B 挂载落实：实例化路径装配；旧「挂载后置（测试内构造挂载）」→ 新「组件在位、初始未激活」）
        var counter = library.Instantiate("x1");
        Assert.Equal(2, counter.GetData<FactionCostData>().DeployCost);
        Assert.Throws<KeyNotFoundException>(() => counter.GetData<BattleStatsData>());
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
    }

    [Fact]
    public async Task UnitStateData_Defaults_And_ReadWrite()
    {
        var state = new UnitStateData();

        // 初始值：位置 null／已毁 false／类型列表空（0 个合法）／实时值 0／损伤量 0／在场回合数 0
        // （手构空组件默认 0——入场经 CreateInitial 置 1，见下用例；G14补 S10 随注）
        Assert.Null(state.Line);
        Assert.False(state.IsDestroyed);
        Assert.Empty(state.UnitTypes);
        Assert.Equal(0, state.OperateCost);
        Assert.Equal(0, state.Attack);
        Assert.Equal(0, state.Defense);
        Assert.Equal(0, state.DefenseLoss);
        Assert.Equal(0, state.TurnsInPlay);

        // 基础读写：位置（阵线引用）、类型列表（0 个/多个均合法）
        // 三值/损伤量写面已收窄（W2b）：装配期填充经 CreateInitial（见下用例）、运行期变更经门户操作面（StatPortalTests）。
        // S9 随改（旧→新：state.UnitTypes.Add 直写×2 → 经受控入口 UnitCard.AddUnitTypeAsync×2）：类型列表写面已收窄
        // （受控入口＝唯一合规增补路径）；「多个合法＋登记序」的验证经受控入口完成——信号副作用不涉本断言
        // （独立引擎无订阅者；受控入口完整行为——去重/信号/实例级——见 CardServiceTests 专项）。
        var line = new BattleLine(4);
        state.Line = line;
        state.IsDestroyed = true;
        var host = CreateUnitCard();
        host.AddData(state);
        await host.AddUnitTypeAsync(UnitType.Infantry);
        await host.AddUnitTypeAsync(UnitType.Tank);

        Assert.Same(line, state.Line); // 位置＝阵线引用本身（顺序即位置，线上不存"槽位实体"）
        Assert.True(state.IsDestroyed);
        Assert.Equal(new[] { UnitType.Infantry, UnitType.Tank }, state.UnitTypes);
    }

    [Fact]
    public void UnitStateData_CreateInitial_Copies_From_BattleStats_And_Benchmark_Stays_Unchanged()
    {
        var stats = new BattleStatsData(2, 4, 6);

        var state = UnitStateData.CreateInitial(stats);
        // 初始＝对战组件值（一次性复制契约；损伤量清零；入场即第 1 回合——G14补 S10）
        Assert.Equal(2, state.OperateCost);
        Assert.Equal(4, state.Attack);
        Assert.Equal(6, state.Defense);
        Assert.Equal(0, state.DefenseLoss);
        Assert.Equal(1, state.TurnsInPlay);

        // 复制为一次性、只读基准独立：运行期变更（经门户——伤害/修复/修饰）不回写基准。
        // 「运行期变更不写基准」的行为证明见 StatPortalTests（伤害后 stats.Defense 保持原值）。
        Assert.Equal(6, stats.Defense);
        Assert.Equal(4, stats.Attack);
    }

    [Fact]
    public void CommandData_Defaults_And_ReadWrite()
    {
        var command = new CommandData();
        Assert.False(command.CanMove); // 初始 false/false
        Assert.False(command.CanAttack);

        command.ResetQuotas(1, 1);
        Assert.True(command.CanMove);
        Assert.True(command.CanAttack);
    }

    // 〔2C-A1 随改：旧→新〕原 KeywordData_Add_Remove_Contains_Are_Idempotent_And_Ordered（登记/幂等/登记序/null 宽容查询）
    // 与 KeywordData_Rejects_Blank_Identifiers（空白标识拒绝）两条用例随「三口合并替代（词条组件化）」迁移至
    // KeywordComponentTests.Manager_Query_Semantics_Mirrors_Legacy_Registration_Coverage
    // （等价覆盖：授予/移除幂等、登记序、null/空白宽容查询、空白标识拒绝——不得以删除代替迁移）。

    [Fact]
    public void CounterActivationData_Defaults_And_ReadWrite()
    {
        var state = new CounterActivationData();
        Assert.False(state.IsActive); // 初始 false

        state.IsActive = true;
        Assert.True(state.IsActive);
    }

    [Fact]
    public void Component_Set_Attaches_Via_Existing_System_And_Keyword_Face_Is_Addressable()
    {
        var card = CreateUnitCard();

        // 单位卡实例化路径已装配两件（阵营〔国籍〕＋部署费合并组件〔S10〕／对战）
        Assert.NotNull(card.GetData<FactionCostData>());
        Assert.NotNull(card.GetData<BattleStatsData>());

        // 其余三件（单位/指挥/反制）：类定义就绪＋测试内构造挂载（实际挂载时机属后续批次）
        card.AddData(new UnitStateData());
        card.AddData(new CommandData());
        card.AddData(new CounterActivationData());
        Assert.NotNull(card.GetData<UnitStateData>());
        Assert.NotNull(card.GetData<CommandData>());
        Assert.NotNull(card.GetData<CounterActivationData>());

        // 2C-A1 随改（旧「All_Six_Components_Attach...」→ 本用例）：词条面＝组件化体系（卡上伴生管理组件、非数据组件——
        // GetData<KeywordData> 契约随三口合并替代移除）；含无词条卡可寻址、不抛、空内容。
        Assert.Empty(card.Keywords.Components);
        Assert.False(card.Keywords.Has("词条"));
    }

    [Fact]
    public void Missing_Component_Access_Throws_Along_Existing_Contract()
    {
        var card = CreateUnitCard();
        // 未挂载＝沿用既有 GetData 契约（缺失抛明确异常）
        Assert.Throws<KeyNotFoundException>(() => card.GetData<UnitStateData>());
    }
}
