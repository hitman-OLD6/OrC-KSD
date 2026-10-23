using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// J2 合法性验证替换（判定器承载）游戏层迁移测试：
/// ①费用验证（对局路径默认行为——通过/拒绝两分支；moding 全局生效〔4 实例共享结构证明〕＋注销回退）；
/// ②反制使用（同源预检一致性——触发器验证与入口预检同步受改写影响；缺类别降级为一般性失败原因）；
/// ③复验粒度独立改写（移动改、攻击不改——互不牵连）；
/// ④独立构造路径默认可用（不经注册表、构造即可用——费用/反制/复验）；
/// ⑤默认名恒可解析 vs 自定义名未注册＝装配期 fail-fast；自定义名经装配段注册可解析。
/// 对应验收：③⑩⑪⑫⑬⑭⑮⑰⑱（⑨⑲由内核测试覆盖；④既有行为保持由全量回归覆盖）。
/// </summary>
public class JudicatorValidationTests
{
    // ---------- ① 费用验证（对局路径默认行为） ----------

    [Fact]
    public async Task Cost_Default_Validation_On_Match_Path_Accept_And_Reject()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1

        var empty = Array.Empty<Ref<Entity>>();
        var cheap = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId, toHand: false);   // 费 1
        var costly = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCostlyId, toHand: false); // 费 5

        Assert.True(cheap.PrePlayTrigger.Validate(empty));    // 费用 1 ≤ 1：通过（预打出）
        Assert.True(cheap.PlayTrigger.Validate(empty));       //                通过（打出）
        Assert.False(costly.PrePlayTrigger.Validate(empty));  // 费用 5 > 1：拒绝（预打出）
        Assert.False(costly.PlayTrigger.Validate(empty));     //                拒绝（打出）
        Assert.True(cheap.PrePlayTrigger.EvaluateValidation(empty).IsValid); // 取数形态一致
    }

    // ---------- ① 费用验证：moding 全局生效（4 实例共享结构证明）＋注销回退 ----------

    [Fact]
    public async Task Cost_Moding_Affects_All_Four_Instances_And_Falls_Back()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];

        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCostlyId, toHand: false);       // 费 5
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCostlyId, toHand: false); // 费 5
        var instances = new[] { unit.PrePlayTrigger, unit.PlayTrigger, command.PrePlayTrigger, command.PlayTrigger };
        var empty = Array.Empty<Ref<Entity>>();

        // 基线：4 个实例（单位预打出/打出＋指令预打出/打出）全部拒绝（费用不足——默认费用逻辑在跑）。
        Assert.All(instances, trigger => Assert.False(trigger.Validate(empty)));

        // moding 恒真改写＝费用验证改写（其全部引用点生效——单源绑定 4 实例的一致性证明）。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CostCheck),
            _ => new object[] { ValidationVerdict.Valid });
        Assert.NotNull(moding);
        Assert.All(instances, trigger => Assert.True(trigger.Validate(empty)));

        // 注销回退：4 个实例全部回退默认逻辑（拒绝）。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.All(instances, trigger => Assert.False(trigger.Validate(empty)));
    }

    // ---------- ② 反制使用：同源预检一致性＋缺类别降级 ----------

    [Fact]
    public async Task Counter_Precheck_And_Trigger_Share_Source_Under_Moding()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1 己方回合：点数 1、费 1
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(match, player, PlayChainTestKit.CounterCheapId, toHand: false);
        var empty = Array.Empty<Ref<Entity>>();

        // 基线：触发器验证通过（同一判定源——默认反制使用逻辑在跑）。
        Assert.True(counter.UseCounterTrigger.Validate(empty));

        // moding 恒假（缺类别）：触发器验证拒绝；入口预检同步拒绝并降级为一般性失败原因（规范内缺省——不伪造具体类别）。
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.CounterUse),
            _ => new object[] { ValidationVerdict.Invalid() });
        Assert.NotNull(moding);

        Assert.False(counter.UseCounterTrigger.Validate(empty)); // 触发器侧：同步受改写影响
        var rejected = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Failed, rejected.Status);
        Assert.Equal(PlayFailureReason.CounterRejected, rejected.FailureReason); // 一般性失败原因（降级）
        Assert.False(counter.GetData<CounterActivationData>().IsActive);         // 零副作用（未翻转）

        // 注销回退：两侧恢复默认（入口预检经判定器通过 → 激活成功）。
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.True(counter.UseCounterTrigger.Validate(empty));
        var accepted = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, accepted.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
    }

    // ---------- ③ 复验粒度：移动改、攻击不改（互不牵连） ----------

    [Fact]
    public async Task Move_And_Attack_Recheck_Are_Independently_Modable()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1、行动费 1
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);

        var moveTrigger = match.CommandManager.UnitMoveTrigger;
        var attackTrigger = match.CommandManager.UnitAttackTrigger;
        var empty = Array.Empty<Ref<Entity>>();
        // 载荷口径：OldPosition＝已成事实的位置（BoardPosition：线 + 元素位置序号）、NewPosition＝请求落位（FormationGap）。
        var supportLine = match.Battlefield.GetSupportLine(player);
        var oldSlot = new Orc.Game.Board.BoardPosition(supportLine, supportLine.IndexOf(unit));
        var newSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        // 基线：移动合法输入通过（默认复验逻辑真实求值）；空输入双侧拒绝。
        Assert.True(moveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref }));
        Assert.False(moveTrigger.Validate(empty));
        Assert.False(attackTrigger.Validate(empty));

        // ① 改写移动复验（恒真）：移动（空输入）通过＝改写真实生效；攻击保持拒绝＝互不牵连。
        var moveModing = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.MoveRecheck),
            _ => new object[] { ValidationVerdict.Valid });
        Assert.NotNull(moveModing);
        Assert.True(moveTrigger.Validate(empty));
        Assert.False(attackTrigger.Validate(empty));
        Assert.True(match.Judicators.UnregisterWrapper(moveModing!));
        Assert.False(moveTrigger.Validate(empty)); // 注销回退

        // ② 改写攻击复验（恒真）：攻击（空输入）通过＝改写真实生效；移动保持拒绝＝互不牵连。
        var attackModing = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.AttackRecheck),
            _ => new object[] { ValidationVerdict.Valid });
        Assert.NotNull(attackModing);
        Assert.True(attackTrigger.Validate(empty));
        Assert.False(moveTrigger.Validate(empty));
        Assert.True(match.Judicators.UnregisterWrapper(attackModing!));
        Assert.False(attackTrigger.Validate(empty)); // 注销回退
    }

    // ---------- ④ 独立构造路径默认可用（不经注册表、构造即可用） ----------

    [Fact]
    public async Task Standalone_Cards_Bind_BuiltIn_Defaults_Without_Registry()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 点数 1
        var empty = Array.Empty<Ref<Entity>>();

        // 独立构造（不经 CardLibrary/注册表）＋加载：费用/反制默认判定器内置可绑定（构造期完成绑定）。
        var cheap = new OrderCard(match.Engine, new CardDefinition(
            "独立轻指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        await cheap.LoadAsync(player);
        Assert.True(cheap.PrePlayTrigger.Validate(empty)); // 费用 1 ≤ 1：通过（默认费用逻辑在跑）

        var costly = new OrderCard(match.Engine, new CardDefinition(
            "独立重指令", deployCost: 5, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        await costly.LoadAsync(player);
        Assert.False(costly.PrePlayTrigger.Validate(empty)); // 费用 5 > 1：拒绝

        // 对照：未绑定验证的触发器＝恒合法（区分「默认绑定在跑」与「未绑定恒合法」）。
        var plain = new Trigger<CardTriggerView>("未绑定");
        Assert.True(plain.Validate(empty));

        // 反制（独立构造）：无回合上下文 → 默认反制逻辑拒绝（非恒合法——默认绑定生效的强证明）。
        var counter = new CounterCard(match.Engine, new CardDefinition(
            "独立反制", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard));
        await counter.LoadAsync(player);
        Assert.False(counter.UseCounterTrigger.Validate(empty));
        Assert.False(counter.UseCounterTrigger.EvaluateValidation(empty).IsValid);
    }

    [Fact]
    public async Task Standalone_CommandManager_Binds_BuiltIn_Recheck_Defaults()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);

        // 独立构造（不经 Match 装配链/注册表）：复验判定器内置可绑定、构造即可用
        //（以同对局引擎/战场/玩家为共享数据面——Player 类无公开构造；本用例仅只读验证、零副作用）。
        var commandManager = new CommandManager(
            match.Engine,
            match.Battlefield,
            match.TargeterManager,
            match.Players,
            () => match.CurrentPlayer);

        // 载荷口径同前：OldPosition＝BoardPosition、NewPosition＝前线插入空隙。
        var supportLine = match.Battlefield.GetSupportLine(player);
        var oldSlot = new Orc.Game.Board.BoardPosition(supportLine, supportLine.IndexOf(unit));
        var newSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        Assert.True(commandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref })); // 默认复验逻辑在跑（合法场景通过）

        CommandTestKit.Activate(unit, canMove: false);
        Assert.False(commandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref })); // 不可动：拒绝（非恒合法）
    }

    // ---------- ⑤ 默认名恒可解析 vs 自定义名未注册＝fail-fast／自定义名经装配段注册可解析 ----------

    [Fact]
    public async Task BuiltIn_Names_Resolvable_And_Custom_Unregistered_Fails_Fast()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();

        // 默认名恒可解析（内置注册段——无条件执行、不依赖外部装配段）。
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CostCheck));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.CounterUse));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.MoveRecheck));
        Assert.NotNull(match.Judicators.Resolve(JudicatorNames.AttackRecheck));

        // 自定义名未注册：绑定动作（按名解析）＝装配期 fail-fast（显性失败、不被吞）。
        var trigger = new Trigger<CardTriggerView>("自定义验证点");
        Assert.Throws<KeyNotFoundException>(() =>
            trigger.BindValidation(JudicatorBinding.FromRegistration(match.Judicators.Resolve("test.validation.custom"))));
    }

    [Fact]
    public async Task Custom_Name_Registered_Through_Assembly_Segment_Resolves_And_Binds()
    {
        var match = new Match(
            GameTestData.CreateDeck(),
            GameTestData.CreateDeck(),
            GameTestData.CreateDefinitions(),
            seed: 42,
            judicatorAssembly: registry => registry.Register("test.validation.custom", new AlwaysInvalidJudicator()),
            packageActivator: TestHost.Activator);
        await match.Initialize();

        // 自定义名经装配段（追加/定制通道）注册 → 可解析、可绑定、行为生效；改写经注册表 moding 同步生效。
        var trigger = new Trigger<CardTriggerView>("自定义验证点");
        trigger.BindValidation(JudicatorBinding.FromRegistration(match.Judicators.Resolve("test.validation.custom")));
        Assert.False(trigger.Validate(Array.Empty<Ref<Entity>>()));

        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve("test.validation.custom"),
            _ => new object[] { ValidationVerdict.Valid });
        Assert.NotNull(moding);
        Assert.True(trigger.Validate(Array.Empty<Ref<Entity>>()));
    }

    // ---------- 测试辅助 ----------

    /// <summary>恒假验证判定器（测试夹具：自定义验证点名绑定演示）。</summary>
    private sealed class AlwaysInvalidJudicator : ValidationJudicator
    {
        protected override ValidationVerdict ValidateLogic(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject)
            => ValidationVerdict.Invalid();
    }
}
