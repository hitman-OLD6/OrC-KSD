using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2A 验收锚点③（卡牌基类骨架）：三类卡实例化（定义层类别 → 对应基类实例）、
/// 触发器声明位（对象在位、名称/类型可断言、事件装配调用成功）、
/// 加载模板（默认路径跑通＋card.load 广播；重建/装配扩展点存在可重写、默认空实现）。
/// </summary>
public class CardSkeletonTests
{
    private static CardLibrary CreateMixedLibrary(out LogicEngine engine)
    {
        engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("u1", new CardDefinition("单位甲", 1, 2, 3, 4, CardCategory.Unit, faction: Faction.Germany, rarity: Rarity.Standard));
        library.Register("c1", new CardDefinition("指令甲", 1, 1, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        library.Register("x1", new CardDefinition("反制甲", 1, 1, 0, 0, CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard));
        return library;
    }

    [Fact]
    public void Catalog_Produces_Three_Card_Classes_By_Category()
    {
        var library = CreateMixedLibrary(out _);

        Assert.IsType<UnitCard>(library.Instantiate("u1"));
        Assert.IsType<OrderCard>(library.Instantiate("c1"));
        Assert.IsType<CounterCard>(library.Instantiate("x1"));
    }

    [Fact]
    public void Catalog_Default_Category_Is_Unit_And_Invalid_Category_Is_Rejected()
    {
        // 既有构造形态（无类别参数）＝单位（兼容）；未定义类别在定义期被拒绝
        var legacyStyle = new CardDefinition("旧式", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard);
        Assert.Equal(CardCategory.Unit, legacyStyle.Category);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CardDefinition("坏类别", 1, 1, 1, 1, "未定义类别", faction: Faction.Germany, rarity: Rarity.Standard));
    }

    [Fact]
    public void Unit_Card_Declares_PrePlay_And_Play_Triggers_InPlace()
    {
        var library = CreateMixedLibrary(out _);
        var unit = Assert.IsType<UnitCard>(library.Instantiate("u1"));

        // 对象在位（非空）、名称/类型可断言
        Assert.NotNull(unit.PrePlayTrigger);
        Assert.NotNull(unit.PlayTrigger);
        Assert.Equal("预打出触发器", unit.PrePlayTrigger.Name);
        Assert.Equal("打出触发器", unit.PlayTrigger.Name);
        // 2B 迁移：运行时类型＝费用校验触发器（验证承载）；宽类型断言保持「CardTriggerView 触发器」语义（旧：IsType 精确断言）
        Assert.IsAssignableFrom<Trigger<CardTriggerView>>(unit.PrePlayTrigger);
        Assert.IsAssignableFrom<Trigger<CardTriggerView>>(unit.PlayTrigger);
    }

    [Fact]
    public void Command_Card_Declares_PrePlay_And_Play_Triggers_InPlace()
    {
        var library = CreateMixedLibrary(out _);
        var command = Assert.IsType<OrderCard>(library.Instantiate("c1"));

        Assert.NotNull(command.PrePlayTrigger);
        Assert.NotNull(command.PlayTrigger);
        Assert.Equal("预打出触发器", command.PrePlayTrigger.Name);
        Assert.Equal("打出触发器", command.PlayTrigger.Name);
        // 2B 迁移：运行时类型＝费用校验触发器（验证承载）；宽类型断言保持「CardTriggerView 触发器」语义（旧：IsType 精确断言）
        Assert.IsAssignableFrom<Trigger<CardTriggerView>>(command.PlayTrigger);
    }

    [Fact]
    public void Counter_Card_Declares_Single_Use_Counter_Trigger_InPlace()
    {
        var library = CreateMixedLibrary(out _);
        var counter = Assert.IsType<CounterCard>(library.Instantiate("x1"));

        // 反制＝使用反制的触发器（单个，唯一）
        Assert.NotNull(counter.UseCounterTrigger);
        Assert.Equal("使用反制的触发器", counter.UseCounterTrigger.Name);
        // 2B 迁移：运行时类型＝使用反制触发器（验证承载）；宽类型断言保持「CardTriggerView 触发器」语义（旧：IsType 精确断言）
        Assert.IsAssignableFrom<Trigger<CardTriggerView>>(counter.UseCounterTrigger);
    }

    [Fact]
    public async Task Declared_Triggers_Accept_Event_Assembly_And_Execute()
    {
        // 2B 迁移：打出触发器承载「外层验证（费用校验）」——执行需加载归属与足额点数；
        // 旧（无载荷空跑、断言 Normal）→ 新（对局内加载卡＋完整载荷执行、断言 Normal；装配断言不变）。
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0]; // 回合 1 先手（点数 1；c01 部署费 1）
        var unit = Assert.IsType<UnitCard>(match.CardLibrary.Instantiate("c01"));
        await unit.LoadAsync(player); // 2B：加载装配归属（打出链验证/扣费/离手依赖）

        // 事件装配：向声明位触发器注册一条占位事件（装配点就绪的验证）
        var registration = unit.PlayTrigger.Register("占位事件", (view, ctx, ct) => Task.CompletedTask);
        Assert.NotNull(registration);

        // 执行一次（完整载荷——2B 打出链：验证通过、链执行完成）
        var stream = await unit.PlayTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = unit,
                [GameUpdates.PayloadPlayer] = player,
                [GameUpdates.PayloadPosition] = match.Battlefield.PlayerASupportLine.GetInsertionGaps()[1],
            });
        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
    }

    [Fact]
    public async Task Load_Template_Runs_And_Emits_CardLoad()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        using var recorder = new UpdateRecorder(match.Engine);
        var player = match.Players[0];

        // 加载模板默认路径跑通：加载一张新实例 → 恰一条 card.load（载荷 {Card, Player}）
        var card = match.CardLibrary.Instantiate("c01");
        await card.LoadAsync(player);

        var update = Assert.Single(recorder.Updates);
        Assert.Equal(GameUpdates.CardLoad, update.Type);
        Assert.Same(card, update.Payload![GameUpdates.PayloadCard]);
        Assert.Same(player, update.Payload[GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task Load_Template_Invokes_Rebuild_Extension_And_Default_Is_Empty()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];

        // 默认路径（三类卡不重写扩展点）：加载不抛、全流程跑通
        var defaultCard = match.CardLibrary.Instantiate("c01");
        await defaultCard.LoadAsync(player);

        // 扩展点插桩语义已迁移：`RebuildFromPersistence` 随 P4b 删除，其能力由「扩展组件 loader」承载
        // （加载期第二段＝数据体声明序；等价验收见 CardDataBodyTests 的扩展组件用例）。
    }

    [Fact]
    public void Deck_Instance_Channel_Preserves_Id_Face_And_Keeps_Count_On_Attach()
    {
        var engine = new LogicEngine();
        var unit = new UnitCard(engine, new CardDefinition("单位", 1, 2, 3, 4, faction: Faction.Germany, rarity: Rarity.Standard));
        var deck = new CardList(new[] { "c01", "c02" });

        deck.AttachInstanceAt(0, unit); // 装配（加载通道）
        Assert.Equal(2, deck.Count); // 加载不改计数（仅装配）
        Assert.Equal(new[] { "c01", "c02" }, deck.ToArray()); // id 读面保持

        Assert.Throws<InvalidOperationException>(() => deck.AttachInstanceAt(0, unit)); // 重复装配被拒绝

        Assert.Same(unit, deck.DrawInstance()); // 实例通道取件（同一性原语：起手/抽牌所得即加载实例）
        Assert.Single(deck);
        Assert.Equal("c02", deck[0]);
        Assert.Throws<InvalidOperationException>(() => deck.DrawInstance()); // 首条未装配＝明确错误
    }

}
