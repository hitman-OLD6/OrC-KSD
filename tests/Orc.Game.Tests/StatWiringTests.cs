using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2b G3 接线——读取面与机制接线测试：
/// ①期限两型（turn.end／下个友方回合开始〔含真实回合循环≥1 例与机制级模拟〕）；
/// ②设值型（常量／引用含随动——自动随动、无需显式请求、经集中触发正常发射）；
/// ③读取面行为证明（攻/防/行动费各 ≥1 例：修饰改变实际结算读数；含 KeywordSystem 伏击读点）。
/// 机制级用例遵循「首轮基线」约定：直接构造卡在首次变更前先显式跑链一次（装配完成点等价；零发射）。
/// </summary>
public class StatWiringTests
{
    // ---------- ① 期限两型 ----------

    [Fact]
    public async Task Expiry_TurnEnd_Through_Real_Turn_Cycle()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 攻 2
        using var recorder = new UpdateRecorder(match.Engine);

        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Attack, 2, new object(), new ModifierExpiry(GameUpdates.TurnEnd)));
        Assert.Equal(4, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack)); // +2 生效

        // 真实回合循环：turn.end 到达（A 回合结束）→ 到期自注销 → 有效值回落（恰一条集中触发）
        recorder.Clear();
        await match.EndTurn();
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));
    }

    [Fact]
    public async Task Expiry_NextFriendlyTurnStart_Mechanism_Simulation_With_Usage_Site_Filter()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);
        var ownerMarker = new object(); // 「友方归属过滤」＝使用处自建（机制不内建归属语义）
        var otherMarker = new object();

        // 核心证明＝机制级模拟：总线发相位信号（turn.start.after）→ 订阅/到期/注销
        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Attack, 2, new object(),
            new ModifierExpiry(
                GameUpdates.TurnStartAfter,
                payload => payload is not null && ReferenceEquals(payload[GameUpdates.PayloadPlayer], ownerMarker))));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 敌方回合开始（过滤未命中）：不撤、保持挂载
        recorder.Clear();
        await engine.Emit(
            GameUpdates.TurnStartAfter,
            new Dictionary<string, object?> { [GameUpdates.PayloadPlayer] = otherMarker });
        Assert.Single(unit.Modifiers.All);
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder));

        // 己方回合开始（命中）：自注销 → 回落（恰一条集中触发）
        await engine.Emit(
            GameUpdates.TurnStartAfter,
            new Dictionary<string, object?> { [GameUpdates.PayloadPlayer] = ownerMarker });
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));
    }

    [Fact]
    public async Task Expiry_NextFriendlyTurnStart_Through_Real_Turn_Cycle()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2
        using var recorder = new UpdateRecorder(match.Engine);

        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Attack, 2, new object(),
            new ModifierExpiry(
                GameUpdates.TurnStartAfter,
                payload => payload is not null && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))));

        // 真实循环：敌方（B）回合开始——锚点 turn.start.after 与真实序列一致、过滤未命中 → 保持挂载
        await match.EndTurn();
        Assert.Single(unit.Modifiers.All);
        Assert.Equal(4, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 下个友方（A）回合开始：命中 → 自注销 → 回落（无需显式请求）
        recorder.Clear();
        await match.EndTurn();
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));
    }

    // ---------- ② 设值型 ----------

    [Fact]
    public async Task SetValue_Constant_Sets_Effective_Value_And_Chain_Continues()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        await unit.Modifiers.RequestRerunAsync();
        var setSource = new object();

        // 常量设值（如「攻击力为 0」）：有效值＝常量
        await unit.Modifiers.AddModifierAsync(new SetModifier(CardStatFields.Attack, 0, setSource));
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 设值不终止链：其后修饰器继续按挂载序应用（0 + 3 = 3）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, new object()));
        Assert.Equal(3, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 按来源撤销设值 → 回到 5 + 3 = 8
        await unit.Modifiers.RemoveBySourceAsync(setSource);
        Assert.Equal(8, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
    }

    [Fact]
    public async Task Reference_Follows_Defense_Changes_Automatically_Without_Explicit_Rerun()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 2, defense: 5);
        using var recorder = new UpdateRecorder(engine);
        await unit.Modifiers.RequestRerunAsync(); // 基线（零发射）

        // 引用型设值（结构位启用——W2b 接线）：「攻击力＝防御力当前有效值」（读取时合成/现算）
        // W3-3 随改：修饰器宿主类型泛化至 Card——本处还原卡侧强类型读取（该场景宿主恒为单位卡 CardBase）。
        await unit.Modifiers.AddModifierAsync(new ReferenceSetModifier(
            CardStatFields.Attack,
            card => ((CardBase)card).Modifiers.ComputeEffectiveValue(CardStatFields.Defense),
            new object()));
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 防御变化（经门户→跑链）→ 攻击有效值自动随动（同一轮内、无需显式请求）＋随动经集中触发正常发射
        recorder.Clear();
        await unit.ApplyDefenseDamageAsync(3);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack)); // 5 − 3 = 2（自动随动）
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder)); // E1-33：另有一条 card.damaged
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Equal(
            new[] { CardStatFields.Attack, CardStatFields.Defense },
            ModifierTestKit.ChangedFieldsOf(update.Payload));
    }

    // ---------- ③ 读取面行为证明（攻／防／行动费） ----------

    [Fact]
    public async Task Attack_Modifier_Changes_Actual_Damage_Through_Basic_Combat()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 防 5（支援线 HQ 之后）
        await attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 1, new object())); // 攻 3
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 行为证明（攻读点）：修饰后攻击有效值 3 参与伤害结算 → 目标 5−3=2（若读未修饰实时值会错误地 5−2=3）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task Defense_Modifier_Changes_Damage_And_Survival_Reading()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FighterId, 1); // 攻 3
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0); // 防 2
        await target.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, new object())); // 防 4
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 行为证明（防读点）：修饰使有效防御 4 参与扣减与死亡判定 → 4−3=1 存活（未修饰时 2−3=−1 将致死）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(1, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.False(target.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task OperateCost_Modifier_Changes_Availability_And_Deduction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：1 点
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 行动费 1
        CommandTestKit.Activate(unit);
        var source = new object();

        // 直挂修饰（机制级最小证明）：有效行动费 1 → 2
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 1, source));

        // 可用性读点：1 点 < 有效 2 → 动作级置黑（PointShortage）
        var report = match.CommandManager.GetCommandAvailability(unit);
        Assert.Equal(CommandBlockReason.PointShortage, report.Move.BlockReason);
        Assert.False(report.Move.CanUse);

        // 推进到 A 回合 3（点数 2）→ 可用；真实移动 → 扣费读有效值（2 点扣 2）
        await match.EndTurn();
        await match.EndTurn();
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var moveResult = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);
        Assert.Equal(CommandResultStatus.Success, moveResult.Status);
        Assert.Equal(0, player.Points); // 2 − 2：扣费读有效行动费

        // 撤销修饰：有效回落 1（读点行为闭环）
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(1, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));
    }

    [Fact]
    public async Task Ambush_Condition_Reads_Effective_Values()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6/防 7
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5/防 6（伏击）
        await ambusher.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, new object())); // 有效攻 8
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        // 伏击读点（KeywordSystem）行为证明：条件读「有效值」——8 ＞ 7（攻击者防御有效值）→ 改写成立
        // （攻击者死亡、伏击者不受伤）；对照：未修饰时 5 ＞ 7 不成立＝正常互伤。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.False(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusher.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 不受伤
    }
}
