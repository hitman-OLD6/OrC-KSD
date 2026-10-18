using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K4·B10 行动费统一（扣费写点与读取口径单源）游戏层测试：
/// ①同值口径＋恰一次（≥2 例）——同一修饰状态下「判定读取所用值＝实际扣取值」，每写点各 ≥1 例
/// （move＝FinalizeMove／attack＝FinalizeAttack）；两例均以「点数恰等有效值＝临界通过＋扣费后点数恰＝0」夹逼锁定
/// （判定读基础值/扣费读基础值/重复扣费均会使断言失败）。
/// ②「行动费条件豁免」moding 演示（move.leg.eligibility）——改写→行动费判定行为变化（可用性＋复验两点同步）→
/// 注销回退；含分区精确性（attack 侧不受影响）与局部精确性（豁免仅限费用——被压制仍拦截）。
/// 口径：驱动经真实指挥流程（BeginCommandAsync → 交互 → 执行）；链路口径＝判定器条目/公开流程面（不直调内部规则）。
/// 说明：演示不做「豁免后真实执行」——费用不足下豁免真实执行必然透支（扣费读有效值、无下限钳制——既有边界行为），
/// 超出本演示聚焦面；「leg 改写→真实流程成功→扣费按有效值」机制证据引用 K3 演示一/二（同机制不同条件维度，独立呈现见实现记录）。
/// </summary>
public class OperateCostUnificationTests
{
    // ==================== 辅助 ====================

    /// <summary>移动复验（执行前复验公开承载——触发器验证面）。</summary>
    private static bool MoveRecheck(Match match, UnitCard unit, Slot oldSlot, Slot newSlot)
        => match.CommandManager.UnitMoveTrigger.Validate(new[] { unit.Ref, oldSlot.Ref, newSlot.Ref });

    /// <summary>leg 失败结果组包（演示改写辅助）。</summary>
    private static object[] LegFailed(LegEligibilityFailure failure) => new object[] { failure };

    /// <summary>
    /// 演示用「局部豁免」替换逻辑（move leg——豁免「行动费不足」；复刻其余 leg 条件：
    /// moding＝纯替换语义，改写者自备本动作条件；对侧（attack）条件无需触碰——分区价值）。
    /// </summary>
    private static object[] MoveLegDefaultExceptCostShortage(Match match, object[]? args)
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

        if (SuppressRules.IsSuppressed(unit))
        {
            return LegFailed(LegEligibilityFailure.Suppressed);
        }

        // 「行动费不足」＝本演示的豁免项（跳过该检查——局部改写目标）。

        if (position is null || !match.Battlefield.GetSupportLine(owner).Contains(position))
        {
            return LegFailed(LegEligibilityFailure.PositionNotInSupportLine);
        }

        return new object[] { null! }; // 通过
    }

    // ==================== ① 同值口径＋恰一次（移动·FinalizeMove） ====================

    [Fact]
    public async Task Move_Deduction_Matches_Judged_Effective_Value_Exactly_Once()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 1); // 行动费 2
        CommandTestKit.Activate(unit);
        var source = new object();

        // 修饰 +1：基础 2 → 有效 3（构造「基础值 ≠ 有效值」的区分场景）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 1, source));
        Assert.Equal(3, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

        // 点数 2：有效 3 > 2 → PointShortage——判定读「有效值 3」
        // （若判定读基础值 2，则 2 ≥ 2 会通过——以拒绝锁定判定读取值＝有效值）。
        await match.ResourceManager.AddPointsAsync(playerA, 1); // 回合 1 结算后 1 → 2
        var blocked = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(blocked.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, blocked.Move.BlockReason);

        // 点数 3（＝有效值）：临界通过——判定读取值恰为 3。
        await match.ResourceManager.AddPointsAsync(playerA, 1); // 2 → 3
        var available = match.CommandManager.GetCommandAvailability(unit);
        Assert.True(available.Move.CanUse);

        // 真实移动：复验通过（3 ≤ 3）→ 执行 → 扣费恰一次＝有效值 3
        // （3 − 3 ＝ 0：重复扣费＝−3、按基础值扣＝1——断言 0 同时锁定「同值」与「恰一次」）。
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var moved = await CommandTestKit.RunCommandAsync(
            match, bridge, unit, match.Battlefield.FrontLine[0].Ref);
        Assert.Equal(CommandResultStatus.Success, moved.Status);
        Assert.Equal(0, playerA.Points);
        Assert.Same(match.Battlefield.FrontLine[0], unit.GetData<UnitStateData>().Position); // 推进成立
    }

    // ==================== ① 同值口径＋恰一次（攻击·FinalizeAttack） ====================

    [Fact]
    public async Task Attack_Deduction_Matches_Judged_Effective_Value_Exactly_Once()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 行动费 1
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 跨线紧邻（敌前线）
        CommandTestKit.Activate(attacker);
        var source = new object();

        // 修饰 +2：基础 1 → 有效 3（构造「基础值 ≠ 有效值」的区分场景）
        await attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 2, source));
        Assert.Equal(3, attacker.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

        // 点数 2：有效 3 > 2 → PointShortage——判定读「有效值 3」
        // （若判定读基础值 1，则 2 ≥ 1 会通过——以拒绝锁定判定读取值＝有效值）。
        await match.ResourceManager.AddPointsAsync(playerA, 1); // 1 → 2
        var blocked = match.CommandManager.GetCommandAvailability(attacker);
        Assert.False(blocked.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, blocked.Attack.BlockReason);

        // 点数 3（＝有效值）：临界通过——判定读取值恰为 3。
        await match.ResourceManager.AddPointsAsync(playerA, 1); // 2 → 3
        var available = match.CommandManager.GetCommandAvailability(attacker);
        Assert.True(available.Attack.CanUse);

        // 真实攻击：复验通过 → 执行 → 扣费恰一次＝有效值 3（3 − 3 ＝ 0；重复扣费＝−3、按基础值扣＝2）。
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var attacked = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, attacked.Status);
        Assert.Equal(0, playerA.Points);
    }

    // ==================== ② 行动费条件豁免（moding 演示·move leg 条目） ====================

    [Fact]
    public async Task Move_Leg_Moding_Exempts_Cost_Shortage_Syncs_Both_CallPoints()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 1); // 行动费 2
        var suppressed = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 2);
        CommandTestKit.Activate(unit);
        CommandTestKit.Activate(suppressed);
        var supportSlot = match.Battlefield.GetSupportLine(playerA)[1];
        var frontSlot = match.Battlefield.FrontLine[0];

        // ---------- 改写前（基线）：费用不足（回合 1 点数 1 ＜ 行动费 2）＝ 移动不可用 ＋ 移动复验拒绝 ----------
        var baselineAvailability = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(baselineAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, baselineAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, unit, supportSlot, frontSlot));

        // ---------- 改写：move leg → 「行动费条件豁免」（局部豁免——其余条件复刻） ----------
        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.MoveLegEligibility),
            args => MoveLegDefaultExceptCostShortage(match, args));
        Assert.NotNull(moding);

        // 两点同步（调用点一：可用性公开查询——费用阻断消除）。
        var modedAvailability = match.CommandManager.GetCommandAvailability(unit);
        Assert.True(modedAvailability.Move.CanUse);
        Assert.NotEmpty(modedAvailability.Move.Candidates);

        // 两点同步（调用点二：移动复验——同一费用不足状态豁免通过）。
        Assert.True(MoveRecheck(match, unit, supportSlot, frontSlot));

        // 分区精确性（定义性质）：对侧动作（攻击）不受 move 条目改写影响——费用不足仍拦截。
        var partitionAvailability = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(partitionAvailability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, partitionAvailability.Attack.BlockReason);

        // 局部精确性（补充）：豁免仅限「行动费」——被压制仍拦截（suppressed 条件仍在链上、顺序不变）。
        Assert.True(await SuppressRules.ApplyAsync(suppressed));
        var suppressedAvailability = match.CommandManager.GetCommandAvailability(suppressed);
        Assert.False(suppressedAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, suppressedAvailability.Move.BlockReason);

        // ---------- 注销回退 ----------
        Assert.True(match.Judicators.UnregisterWrapper(moding!));

        // 回退后恢复原行为（可用性＋复验）：
        var restoredAvailability = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(restoredAvailability.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, restoredAvailability.Move.BlockReason);
        Assert.False(MoveRecheck(match, unit, supportSlot, frontSlot));
    }
}
