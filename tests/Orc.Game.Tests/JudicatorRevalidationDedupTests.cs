using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K3 复验消重（C7+C8）游戏层测试：共享 leg 资格（move.leg.eligibility／attack.leg.eligibility）＋推进前置
/// （move.frontline-enemy）与可用性聚合、复验判定器的消重等价断言（同调矩阵）＋ moding 演示（leg 两分区条目＋C8 条目）＋
/// 契约与独立构造路径。
/// 矩阵口径（需求 §五·R5 1.1）：每个条件在其适用的全部调用点各 ≥1 例——
/// owner 非当前／owner 无效／已毁／费用不足＝各 4 例；行动标记 false＝按动作分（移动 2、攻击 2）；被压制＝4 例；
/// 位置非法（源非支援线）＝移动 2 例；前线有敌（C8）＝移动 2 例；攻击目标非法（C1 复用）＝攻击 2 例；
/// 正向＝每动作「可用性 Available＋复验 Valid」；优先级（多条件同时失败）＝每调用点 ≥1 例。
/// 断言层次：可用性侧经公开查询 GetCommandAvailability（取值级）；复验侧经执行前复验公开承载（触发器验证面）——
/// 可经真实指挥流程构造的格（费用不足／被压制／C8）另附真实流程拒绝/通过断言（BeginCommandAsync → 交互 → 执行前复验）。
/// moding 演示：改写 → 两调用点同步行为断言（含对侧动作不受影响的分区精确性）→ 注销回退 → 回退后恢复断言；
/// 复验侧经真实指挥流程（执行前复验拒绝/通过）；leg 演示取「条件豁免/局部改写」形态（如「移动无视被压制」）。
/// 链路口径：一律经判定器条目句柄／公开流程面——不直连判定器实例、不直调内部规则（单源）。
/// </summary>
public class JudicatorRevalidationDedupTests
{
    // ==================== 辅助 ====================

    /// <summary>移动复验（执行前复验公开承载——触发器验证面）。</summary>
    private static bool MoveRecheck(Match match, UnitCard unit, Slot oldSlot, Slot newSlot)
        => match.CommandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref });

    /// <summary>攻击复验（执行前复验公开承载——触发器验证面）。</summary>
    private static bool AttackRecheck(Match match, UnitCard attacker, Ref<Entity> targetRef)
        => match.CommandManager.UnitAttackTrigger.Validate(new[] { attacker.Ref, targetRef });

    /// <summary>真实指挥流程驱动（含交互期插入动作——执行前复验拒绝/通过的行为观测面）。</summary>
    private static async Task<CommandResult> RunCommandWithInterludeAsync(
        Match match, MockTargeterBridge bridge, UnitCard unit, Ref<Entity> targetRef, Func<Task> interlude)
    {
        var task = match.CommandManager.BeginCommandAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        await interlude();
        var accepted = responder.Complete(
            description.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, targetRef));
        Assert.True(accepted, "交互完成被拒绝（目标不在候选面）。");
        return await task;
    }

    /// <summary>leg 失败结果组包（演示改写辅助）。</summary>
    private static object[] LegFailed(LegEligibilityFailure failure) => new object[] { failure };

    /// <summary>
    /// 演示用「局部豁免」替换逻辑（move leg——豁免「被压制」；复刻其余 leg 条件：
    /// moding＝纯替换语义，改写者自备本动作条件；对侧（attack）条件无需触碰——分区价值）。
    /// </summary>
    private static object[] MoveLegDefaultExceptSuppressed(Match match, object[]? args)
    {
        var unit = (UnitCard)args![0]!;
        var position = (Slot?)args[1];

        var current = match.CurrentPlayer;
        if (current is null)
        {
            return LegFailed(LegEligibilityFailure.NonOwnerTurn);
        }

        var owner = unit.Owner;
        if (owner is null)
        {
            return LegFailed(LegEligibilityFailure.OwnerInvalid);
        }

        if (!ReferenceEquals(owner, current))
        {
            return LegFailed(LegEligibilityFailure.NonOwnerTurn);
        }

        if (unit.GetData<UnitStateData>().IsDestroyed)
        {
            return LegFailed(LegEligibilityFailure.UnitDead);
        }

        if (!unit.GetData<CommandData>().CanMove)
        {
            return LegFailed(LegEligibilityFailure.FlagFalse);
        }

        // 「被压制」＝本演示的豁免项（跳过该检查——局部改写目标）。

        if (owner.Points < unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost))
        {
            return LegFailed(LegEligibilityFailure.PointShortage);
        }

        if (position is null || !match.Battlefield.GetSupportLine(owner).Contains(position))
        {
            return LegFailed(LegEligibilityFailure.PositionNotInSupportLine);
        }

        return new object[] { null! }; // 通过
    }

    /// <summary>演示用「局部豁免」替换逻辑（attack leg——豁免「被压制」；复刻其余 leg 条件）。</summary>
    private static object[] AttackLegDefaultExceptSuppressed(Match match, object[]? args)
    {
        var unit = (UnitCard)args![0]!;

        var current = match.CurrentPlayer;
        if (current is null)
        {
            return LegFailed(LegEligibilityFailure.NonOwnerTurn);
        }

        var owner = unit.Owner;
        if (owner is null)
        {
            return LegFailed(LegEligibilityFailure.OwnerInvalid);
        }

        if (!ReferenceEquals(owner, current))
        {
            return LegFailed(LegEligibilityFailure.NonOwnerTurn);
        }

        if (unit.GetData<UnitStateData>().IsDestroyed)
        {
            return LegFailed(LegEligibilityFailure.UnitDead);
        }

        if (!unit.GetData<CommandData>().CanAttack)
        {
            return LegFailed(LegEligibilityFailure.FlagFalse);
        }

        // 「被压制」＝本演示的豁免项（跳过该检查——局部改写目标）。

        if (owner.Points < unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost))
        {
            return LegFailed(LegEligibilityFailure.PointShortage);
        }

        return new object[] { null! }; // 通过
    }

    // ==================== 矩阵（条件×调用点） ====================

    // ---------- 矩阵格：owner 非当前（4 例） ----------

    [Fact]
    public async Task Owner_Not_Current_All_Four_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var ownUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(ownUnit);
        CommandTestKit.Activate(enemyUnit);

        // 移动/攻击可用性（公开查询——取值级）：非己方回合 → 流程级同因阻断。
        var availability = match.CommandManager.GetCommandAvailability(enemyUnit);
        Assert.Equal(CommandBlockReason.NonOwnerTurn, availability.IneligibleReason);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.NonOwnerTurn, availability.Move.BlockReason);
        Assert.False(availability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.NonOwnerTurn, availability.Attack.BlockReason);

        // 移动/攻击复验（执行前复验公开承载）：
        var oldSlot = match.Battlefield.GetSupportLine(playerB)[1];
        var newSlot = match.Battlefield.FrontLine[0];
        Assert.False(MoveRecheck(match, enemyUnit, oldSlot, newSlot));
        Assert.False(AttackRecheck(match, enemyUnit, ownUnit.Ref));
    }

    // ---------- 矩阵格：owner 无效（4 例） ----------

    [Fact]
    public async Task Owner_Invalid_All_Four_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var other = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(other);

        // 未加载（未设归属）单位经加入路径上场：已单位化、Owner＝null（加入路径不要求归属）。
        var orphan = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var joined = await match.PlayManager.JoinUnitAsync(orphan, match.Battlefield.GetSupportLine(playerA)[1]);
        Assert.Equal(PlayResultStatus.Success, joined.Status);
        Assert.Null(orphan.Owner);

        // 移动/攻击可用性：归属无效 → 流程级同因阻断。
        var availability = match.CommandManager.GetCommandAvailability(orphan);
        Assert.Equal(CommandBlockReason.OwnerInvalid, availability.IneligibleReason);
        Assert.Equal(CommandBlockReason.OwnerInvalid, availability.Move.BlockReason);
        Assert.Equal(CommandBlockReason.OwnerInvalid, availability.Attack.BlockReason);

        // 移动/攻击复验：
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var newSlot = match.Battlefield.FrontLine[0];
        Assert.False(MoveRecheck(match, orphan, oldSlot, newSlot));
        Assert.False(AttackRecheck(match, orphan, other.Ref));
    }

    // ---------- 矩阵格：已毁（4 例） ----------

    [Fact]
    public async Task Destroyed_All_Four_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var doomed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var other = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(doomed);
        CommandTestKit.Activate(other);
        var oldSlot = (Slot)doomed.GetData<UnitStateData>().Position!;

        // 防御归零（修饰器驱动）→ 统一死亡链 → 已毁。
        await doomed.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, -99, new object()));
        Assert.True(doomed.GetData<UnitStateData>().IsDestroyed);

        // 移动/攻击可用性：已毁 → 流程级同因阻断。
        var availability = match.CommandManager.GetCommandAvailability(doomed);
        Assert.Equal(CommandBlockReason.UnitDead, availability.IneligibleReason);
        Assert.Equal(CommandBlockReason.UnitDead, availability.Move.BlockReason);
        Assert.Equal(CommandBlockReason.UnitDead, availability.Attack.BlockReason);

        // 移动/攻击复验：
        var newSlot = match.Battlefield.FrontLine[0];
        Assert.False(MoveRecheck(match, doomed, oldSlot, newSlot));
        Assert.False(AttackRecheck(match, doomed, other.Ref));
    }

    // ---------- 矩阵格：费用不足（4 例；复验 2 例经真实流程拒绝） ----------

    [Fact]
    public async Task Point_Shortage_All_Four_CallPoints()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 可用性 2 例：重费单位（行动费 2）＋回合 1 点数 1 → PointShortage。
        var costly = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 1);
        CommandTestKit.Activate(costly);
        var costlyAvailability = match.CommandManager.GetCommandAvailability(costly);
        Assert.False(costlyAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, costlyAvailability.Move.BlockReason);
        Assert.False(costlyAvailability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, costlyAvailability.Attack.BlockReason);

        // 复验 2 例（真实指挥流程——交互期费用漂移 → 执行前复验拒绝、零副作用）。
        await match.ResourceManager.AddPointsAsync(playerA, 2); // 点数 3
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(mover);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var costSource = new object();
        var moveRun = await RunCommandWithInterludeAsync(
            match, bridge, mover, match.Battlefield.FrontLine[0].Ref,
            () => mover.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 5, costSource)));
        Assert.Equal(CommandResultStatus.Failed, moveRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, moveRun.FailureReason);
        Assert.Same(match.Battlefield.GetSupportLine(playerA)[4], mover.GetData<UnitStateData>().Position);
        Assert.Equal(3, playerA.Points); // 零副作用：未扣费

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        var victim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var attackCostSource = new object();
        var attackRun = await RunCommandWithInterludeAsync(
            match, bridge, attacker, victim.Ref,
            () => attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 5, attackCostSource)));
        Assert.Equal(CommandResultStatus.Failed, attackRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, attackRun.FailureReason);
        Assert.Equal(5, victim.GetData<UnitStateData>().Defense); // 零副作用：未结算伤害
        Assert.Equal(5, attacker.GetData<UnitStateData>().Defense); // 零副作用：未受伤
    }

    // ---------- 矩阵格：行动标记 false（移动 2 例＋攻击 2 例） ----------

    [Fact]
    public async Task Action_Flag_False_Move_And_Attack()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var newSlot = match.Battlefield.FrontLine[0];

        // 移动侧：CanMove＝false。
        CommandTestKit.Activate(unit, canMove: false, canAttack: true);
        var moveBlocked = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(moveBlocked.Move.CanUse);
        Assert.Equal(CommandBlockReason.FlagFalse, moveBlocked.Move.BlockReason);
        Assert.False(MoveRecheck(match, unit, oldSlot, newSlot));

        // 攻击侧：CanAttack＝false。
        CommandTestKit.Activate(unit, canMove: true, canAttack: false);
        var attackBlocked = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(attackBlocked.Attack.CanUse);
        Assert.Equal(CommandBlockReason.FlagFalse, attackBlocked.Attack.BlockReason);
        Assert.False(AttackRecheck(match, unit, unit.Ref));
    }

    // ---------- 矩阵格：被压制（4 例；复验 2 例经真实流程拒绝） ----------

    [Fact]
    public async Task Suppressed_All_Four_CallPoints()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 可用性 2 例：压制态。
        var pressed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(pressed);
        Assert.True(await SuppressRules.ApplyAsync(pressed));
        var availability = match.CommandManager.GetCommandAvailability(pressed);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Move.BlockReason);
        Assert.False(availability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Attack.BlockReason);

        // 复验 2 例（真实指挥流程——交互期压制 → 执行前复验拒绝、零副作用）。
        await match.ResourceManager.AddPointsAsync(playerA, 1); // 点数 2
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(mover);
        var moveRun = await RunCommandWithInterludeAsync(
            match, bridge, mover, match.Battlefield.FrontLine[0].Ref,
            () => SuppressRules.ApplyAsync(mover));
        Assert.Equal(CommandResultStatus.Failed, moveRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, moveRun.FailureReason);
        Assert.Same(match.Battlefield.GetSupportLine(playerA)[4], mover.GetData<UnitStateData>().Position);
        Assert.True(mover.GetData<CommandData>().CanMove); // 零副作用：两 bool 未变
        Assert.Equal(2, playerA.Points); // 零副作用：未扣费

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        var victim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var attackRun = await RunCommandWithInterludeAsync(
            match, bridge, attacker, victim.Ref,
            () => SuppressRules.ApplyAsync(attacker));
        Assert.Equal(CommandResultStatus.Failed, attackRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, attackRun.FailureReason);
        Assert.Equal(5, victim.GetData<UnitStateData>().Defense); // 零副作用：未结算伤害
        Assert.Equal(2, playerA.Points);

        // 压制态复验（公开承载面补充——与可用性同调）：
        var pressedOldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var pressedNewSlot = match.Battlefield.FrontLine[1];
        Assert.False(MoveRecheck(match, pressed, pressedOldSlot, pressedNewSlot));
        Assert.False(AttackRecheck(match, pressed, victim.Ref));
    }

    // ---------- 矩阵格：位置非法（源非支援线——移动 2 例） ----------

    [Fact]
    public async Task Position_Not_In_Support_Line_Move_Both_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var frontUnit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(frontUnit);
        var oldSlot = (Slot)frontUnit.GetData<UnitStateData>().Position!;

        // 移动可用性：源位置（前线）不在支援线 → NoCandidates（其余条件均满足：可动/无压制/费够/C8 无敌人/候选非空）。
        var availability = match.CommandManager.GetCommandAvailability(frontUnit);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, availability.Move.BlockReason);

        // 移动复验（位置＝原槽位——共享谓词「源位置∈支援线」经 leg 条目）：
        Assert.False(MoveRecheck(match, frontUnit, oldSlot, match.Battlefield.FrontLine[1]));
    }

    // ---------- 矩阵格：前线有敌（C8——移动 2 例） ----------

    [Fact]
    public async Task Frontline_Enemy_Move_Both_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);

        // 移动可用性：前线存在存活敌方单位 → NoCandidates（其余条件均满足）。
        var availability = match.CommandManager.GetCommandAvailability(mover);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, availability.Move.BlockReason);

        // 移动复验（推进前置经 move.frontline-enemy 条目）：
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var newSlot = match.Battlefield.FrontLine[1];
        Assert.False(MoveRecheck(match, mover, oldSlot, newSlot));
    }

    // ---------- 矩阵格：攻击目标非法（C1 复用——攻击 2 例） ----------

    [Fact]
    public async Task Attack_Target_Illegal_Both_CallPoints()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        var farTarget = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);

        // 攻击可用性：目标筛选（范围矩阵——步/坦仅跨线相邻；敌支援线跨两线＝非法）后无合法候选 → NoCandidates。
        var availability = match.CommandManager.GetCommandAvailability(attacker);
        Assert.False(availability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, availability.Attack.BlockReason);

        // 攻击复验（C1 复用——combat.target.legal 判定非法目标）：
        Assert.False(AttackRecheck(match, attacker, farTarget.Ref));
    }

    // ---------- 矩阵格：正向（每动作「可用性 Available＋复验 Valid」） ----------

    [Fact]
    public async Task Positive_Each_Action_Available_And_Valid()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];

        // 移动正向（无敌人：C8 通过；候选非空；全链通过）。
        var moveAvailability = match.CommandManager.GetCommandAvailability(mover);
        Assert.True(moveAvailability.Move.CanUse);
        Assert.NotEmpty(moveAvailability.Move.Candidates);
        Assert.True(MoveRecheck(match, mover, oldSlot, match.Battlefield.FrontLine[0]));

        // 攻击正向（敌方前线单位——跨线相邻合法目标；全链通过）。
        var victim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var attackAvailability = match.CommandManager.GetCommandAvailability(mover);
        Assert.True(attackAvailability.Attack.CanUse);
        Assert.Contains(victim.Ref, attackAvailability.Attack.Candidates);
        Assert.True(AttackRecheck(match, mover, victim.Ref));
    }

    // ---------- 矩阵格：优先级（多条件同时失败——每调用点 ≥1 例） ----------

    [Fact]
    public async Task Priority_Multi_Condition_Failure_Per_CallPoint()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 1); // 行动费 2 > 点数 1
        CommandTestKit.Activate(unit);
        Assert.True(await SuppressRules.ApplyAsync(unit));

        // 「被压制」与「费用不足」同时满足 → 可用性按现状优先级报 Suppressed（suppressed 先于 cost——
        // 短路顺序逐字保真的值级证据；两侧状态均不改变——R4 1.1 口径）。
        var availability = match.CommandManager.GetCommandAvailability(unit);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Move.BlockReason);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Attack.BlockReason);

        // 复验侧（bool 语义——多条件同时失败仍拒绝；布尔二值不可观察具体顺序，由同调断言覆盖）：
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        Assert.False(MoveRecheck(match, unit, oldSlot, match.Battlefield.FrontLine[0]));
        Assert.False(AttackRecheck(match, unit, unit.Ref));
    }

    // ==================== moding 演示 ====================

    // ---------- 演示一：move leg 分区条目（「移动无视被压制」局部改写——两点同步） ----------

    [Fact]
    public async Task Move_Leg_Moding_Exempts_Suppressed_Syncs_Both_CallPoints()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var probed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var rejected = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var succeeded = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyAnchor = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(probed);
        CommandTestKit.Activate(rejected);
        CommandTestKit.Activate(succeeded);
        await match.ResourceManager.AddPointsAsync(playerA, 5);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var probedSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var rejectedSlot = match.Battlefield.GetSupportLine(playerA)[4];

        // ---------- 改写前（基线）：压制态 = 移动不可用 + 移动复验拒绝 ----------
        Assert.True(await SuppressRules.ApplyAsync(probed));
        var baselineAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(baselineAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, baselineAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // 真实指挥流程拒绝（执行前复验）：发起（未压制）→ 交互期压制 → 执行前复验拒绝、零副作用。
        var baselineRun = await RunCommandWithInterludeAsync(
            match, bridge, rejected, match.Battlefield.FrontLine[0].Ref,
            () => SuppressRules.ApplyAsync(rejected));
        Assert.Equal(CommandResultStatus.Failed, baselineRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, baselineRun.FailureReason);
        Assert.Same(rejectedSlot, rejected.GetData<UnitStateData>().Position);

        // ---------- 改写：move leg → 「移动无视被压制」（局部豁免——其余条件复刻） ----------
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.MoveLegEligibility),
            args => MoveLegDefaultExceptSuppressed(match, args));
        Assert.NotNull(moding);

        // 两点同步（调用点一：可用性公开查询——同一压制态豁免）。
        var modedAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.True(modedAvailability.Move.CanUse);
        Assert.NotEmpty(modedAvailability.Move.Candidates);

        // 两点同步（调用点二：移动复验——同一压制态通过）。
        Assert.True(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // 真实指挥流程通过（执行前复验）：发起（未压制）→ 交互期压制 → 复验豁免 → 执行成功（推进＋扣费＋置位）。
        var pointsBefore = playerA.Points;
        var modedRun = await RunCommandWithInterludeAsync(
            match, bridge, succeeded, match.Battlefield.FrontLine[0].Ref,
            () => SuppressRules.ApplyAsync(succeeded));
        Assert.Equal(CommandResultStatus.Success, modedRun.Status);
        Assert.Same(match.Battlefield.FrontLine[0], succeeded.GetData<UnitStateData>().Position);
        Assert.Equal(pointsBefore - 1, playerA.Points); // 扣费（行动费 1）
        Assert.False(succeeded.GetData<CommandData>().CanMove); // 收尾置位

        // 分区精确性（定义性质）：对侧动作（攻击）不受 move 条目改写影响——保持被压制拦截。
        var partitionAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(partitionAvailability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, partitionAvailability.Attack.BlockReason);
        Assert.False(AttackRecheck(match, probed, enemyAnchor.Ref));

        // 局部精确性（补充）：豁免仅限「被压制」——费用不足仍被拦截（cost 条件仍在链上、顺序不变）。
        var costlyProbe = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.CostlyId, 1); // 行动费 2
        CommandTestKit.Activate(costlyProbe);
        Assert.True(await SuppressRules.ApplyAsync(costlyProbe));
        await costlyProbe.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 10, new object()));
        var preciseAvailability = match.CommandManager.GetCommandAvailability(costlyProbe);
        Assert.Equal(CommandBlockReason.PointShortage, preciseAvailability.Move.BlockReason);

        // ---------- 注销回退 ----------
        Assert.True(match.Judicators.UnregisterWrapper(moding!));

        // 回退后恢复原行为（可用性＋复验）：
        var restoredAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(restoredAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, restoredAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // 回退后恢复原行为（真实指挥流程拒绝）：槽位复用（succeeded 已离开槽 3；目标槽避开前线占用者）。
        var restoredRunUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(restoredRunUnit);
        var restoredRun = await RunCommandWithInterludeAsync(
            match, bridge, restoredRunUnit, match.Battlefield.FrontLine[2].Ref,
            () => SuppressRules.ApplyAsync(restoredRunUnit));
        Assert.Equal(CommandResultStatus.Failed, restoredRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, restoredRun.FailureReason);
    }

    // ---------- 演示二：attack leg 分区条目（「攻击无视被压制」局部改写——两点同步） ----------

    [Fact]
    public async Task Attack_Leg_Moding_Exempts_Suppressed_Syncs_Both_CallPoints()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var probed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var rejected = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var succeeded = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var victim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var rearTarget = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2); // 后排目标（回退阶用）
        CommandTestKit.Activate(probed);
        CommandTestKit.Activate(rejected);
        CommandTestKit.Activate(succeeded);
        await match.ResourceManager.AddPointsAsync(playerA, 5);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var probedSlot = match.Battlefield.GetSupportLine(playerA)[1];

        // ---------- 改写前（基线）：压制态 = 攻击不可用 + 攻击复验拒绝 ----------
        Assert.True(await SuppressRules.ApplyAsync(probed));
        var baselineAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(baselineAvailability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, baselineAvailability.Attack.BlockReason);
        Assert.False(AttackRecheck(match, probed, victim.Ref));

        // 真实指挥流程拒绝（执行前复验）：发起（未压制）→ 交互期压制 → 执行前复验拒绝、零副作用。
        var baselineRun = await RunCommandWithInterludeAsync(
            match, bridge, rejected, victim.Ref,
            () => SuppressRules.ApplyAsync(rejected));
        Assert.Equal(CommandResultStatus.Failed, baselineRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, baselineRun.FailureReason);
        Assert.Equal(5, victim.GetData<UnitStateData>().Defense); // 零副作用：未结算伤害

        // ---------- 改写：attack leg → 「攻击无视被压制」（局部豁免——其余条件复刻） ----------
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.AttackLegEligibility),
            args => AttackLegDefaultExceptSuppressed(match, args));
        Assert.NotNull(moding);

        // 两点同步（调用点一：可用性公开查询——同一压制态豁免）。
        var modedAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.True(modedAvailability.Attack.CanUse);
        Assert.NotEmpty(modedAvailability.Attack.Candidates);

        // 两点同步（调用点二：攻击复验——同一压制态通过）。
        Assert.True(AttackRecheck(match, probed, victim.Ref));

        // 真实指挥流程通过（执行前复验）：发起（未压制）→ 交互期压制 → 复验豁免 → 执行成功（伤害结算）。
        var modedRun = await RunCommandWithInterludeAsync(
            match, bridge, succeeded, victim.Ref,
            () => SuppressRules.ApplyAsync(succeeded));
        Assert.Equal(CommandResultStatus.Success, modedRun.Status);
        Assert.Equal(3, victim.GetData<UnitStateData>().Defense); // 5-2＝3（伤害结算发生）
        Assert.False(succeeded.GetData<CommandData>().CanAttack); // 收尾置位

        // 分区精确性（定义性质）：对侧动作（移动）不受 attack 条目改写影响——保持被压制拦截。
        var partitionAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(partitionAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, partitionAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // ---------- 注销回退 ----------
        Assert.True(match.Judicators.UnregisterWrapper(moding!));

        // 回退后恢复原行为（可用性＋复验）：
        var restoredAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(restoredAvailability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, restoredAvailability.Attack.BlockReason);
        Assert.False(AttackRecheck(match, probed, victim.Ref));

        // 回退后恢复原行为（真实指挥流程拒绝）：单位经前线发起（攻击无位置条件；跨线相邻目标）。
        var restoredRunUnit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(restoredRunUnit);
        var restoredRun = await RunCommandWithInterludeAsync(
            match, bridge, restoredRunUnit, rearTarget.Ref,
            () => SuppressRules.ApplyAsync(restoredRunUnit));
        Assert.Equal(CommandResultStatus.Failed, restoredRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, restoredRun.FailureReason);
        Assert.Equal(5, rearTarget.GetData<UnitStateData>().Defense); // 零副作用
    }

    // ---------- 演示三：C8 推进前置条目（三态对照——可用性与复验两点同步） ----------

    [Fact]
    public async Task Frontline_Enemy_Moding_Three_State_Contrast()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var probed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var rejected = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var succeeded = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(probed);
        CommandTestKit.Activate(rejected);
        CommandTestKit.Activate(succeeded);
        await match.ResourceManager.AddPointsAsync(playerA, 5);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var probedSlot = match.Battlefield.GetSupportLine(playerA)[1];

        // 同盘面构造：先发起（当时前线无敌人）——交互期布置敌方前线单位（同一盘面三态观察的起点）。
        var pending = match.CommandManager.BeginCommandAsync(rejected);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);

        // ---------- 态一（改写前）：不可通过 ----------
        var blockedAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(blockedAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, blockedAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // 真实指挥流程拒绝（执行前复验——C8 拦截；零副作用）。
        Assert.True(responder.Complete(description.RequestId,
            TargeterTestKit.Selection(TargetSlot.DefaultName, match.Battlefield.FrontLine[1].Ref)));
        var blockedRun = await pending;
        Assert.Equal(CommandResultStatus.Failed, blockedRun.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, blockedRun.FailureReason);
        Assert.Same(match.Battlefield.GetSupportLine(playerA)[4], rejected.GetData<UnitStateData>().Position);

        // ---------- 改写：move.frontline-enemy → 恒假（「前线无敌人」——C8 豁免） ----------
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.MoveFrontlineEnemy),
            _ => new object[] { false });
        Assert.NotNull(moding);

        // ---------- 态二（改写后）：通过 ----------
        var passedAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.True(passedAvailability.Move.CanUse);
        Assert.NotEmpty(passedAvailability.Move.Candidates);
        Assert.True(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[1]));

        // 真实指挥流程通过（执行前复验——C8 豁免；推进成功）。
        var passedRun = await RunCommandWithInterludeAsync(
            match, bridge, succeeded, match.Battlefield.FrontLine[1].Ref, () => Task.CompletedTask);
        Assert.Equal(CommandResultStatus.Success, passedRun.Status);
        Assert.Same(match.Battlefield.FrontLine[1], succeeded.GetData<UnitStateData>().Position);

        // ---------- 注销回退 ----------
        Assert.True(match.Judicators.UnregisterWrapper(moding!));

        // ---------- 态三（回退后）：恢复不可通过（换用空槽位观察——succeeded 已占前线槽 1） ----------
        var restoredAvailability = match.CommandManager.GetCommandAvailability(probed);
        Assert.False(restoredAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, restoredAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, probed, probedSlot, match.Battlefield.FrontLine[2]));
    }

    // ==================== 契约与独立构造路径 ====================

    // ---------- 条目契约：可解析、直接可调用、载荷 fail-fast 边界 ----------

    [Fact]
    public async Task Entries_Resolvable_And_Contract_Boundaries()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        var position = (Slot)unit.GetData<UnitStateData>().Position!;

        // 默认名恒可解析（固定内置注册段——无条件可用；moding 寻址前提）。
        var moveLeg = match.Judicators.Resolve(JudicatorNames.MoveLegEligibility);
        var attackLeg = match.Judicators.Resolve(JudicatorNames.AttackLegEligibility);
        var frontlineEnemy = match.Judicators.Resolve(JudicatorNames.MoveFrontlineEnemy);
        Assert.NotNull(moveLeg);
        Assert.NotNull(attackLeg);
        Assert.NotNull(frontlineEnemy);

        // 直接调用（统一面）：通过标记＝null；失败＝结构化原因（LegEligibilityFailure）。
        Assert.Equal(new object[] { null! }, moveLeg.Invoke(new object[] { unit, position }));
        Assert.Equal(new object[] { null! }, attackLeg.Invoke(new object[] { unit, null! }));
        await SuppressRules.ApplyAsync(unit);
        Assert.Equal(new object[] { LegEligibilityFailure.Suppressed }, moveLeg.Invoke(new object[] { unit, position }));
        Assert.Equal(new object[] { LegEligibilityFailure.Suppressed }, attackLeg.Invoke(new object[] { unit, null! }));

        // C8 条目（bool 输出）：
        Assert.Equal(new object[] { false }, frontlineEnemy.Invoke(new object[] { playerA }));
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.Equal(new object[] { true }, frontlineEnemy.Invoke(new object[] { playerA }));
        Assert.Equal(new object[] { false }, frontlineEnemy.Invoke(new object[] { playerB })); // B 视角：A 前线无单位

        // 载荷契约（fail-fast 族——与既有判定器口径一致）：缺失/类型不符＝ArgumentException。
        Assert.Throws<ArgumentException>(() => moveLeg.Invoke(null));
        Assert.Throws<ArgumentException>(() => moveLeg.Invoke(new object[] { unit })); // 缺 position 槽位（统一载荷两元素）
        Assert.Throws<ArgumentException>(() => moveLeg.Invoke(new object[] { "非法载荷", position }));
        Assert.Throws<ArgumentException>(() => attackLeg.Invoke(null));
        Assert.Throws<ArgumentException>(() => attackLeg.Invoke(new object[] { "非法载荷", null! }));
        Assert.Throws<ArgumentException>(() => frontlineEnemy.Invoke(null));
        Assert.Throws<ArgumentException>(() => frontlineEnemy.Invoke(new object[] { "非法载荷" }));
    }

    // ---------- 独立构造路径（无注册表）：内置默认通道构造即可用、行为与注册路径一致 ----------

    [Fact]
    public async Task Standalone_CommandManager_BuiltIn_Leg_And_Frontline_Defaults_Consistent()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        var oldSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var newSlot = match.Battlefield.FrontLine[0];

        // 独立构造（不经 Match 装配链/注册表）：leg/推进前置内置默认通道构造即可用（零配置）。
        var standalone = new CommandManager(
            match.Engine,
            match.Battlefield,
            match.TargeterManager,
            match.Players,
            () => match.CurrentPlayer);

        // 复验（执行前复验公开承载——独立构造路径；与注册路径同组合同结果——默认逻辑真实求值）。
        Assert.True(standalone.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref }));
        Assert.True(match.CommandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref })); // 对照：注册路径

        // 可用性（独立构造路径公开面——与注册路径同结果）。
        Assert.True(standalone.GetCommandAvailability(unit).Move.CanUse);
        Assert.True(match.CommandManager.GetCommandAvailability(unit).Move.CanUse);

        // 行动标记 false（同组合同结果——非恒真恒假）：
        CommandTestKit.Activate(unit, canMove: false);
        Assert.False(standalone.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref }));
        Assert.Equal(CommandBlockReason.FlagFalse, standalone.GetCommandAvailability(unit).Move.BlockReason);
        Assert.Equal(CommandBlockReason.FlagFalse, match.CommandManager.GetCommandAvailability(unit).Move.BlockReason);

        // 被压制（同组合同结果）：
        CommandTestKit.Activate(unit, canMove: true);
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.False(standalone.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref }));
        Assert.Equal(CommandBlockReason.Suppressed, standalone.GetCommandAvailability(unit).Move.BlockReason);
        Assert.Equal(CommandBlockReason.Suppressed, match.CommandManager.GetCommandAvailability(unit).Move.BlockReason);
    }
}
