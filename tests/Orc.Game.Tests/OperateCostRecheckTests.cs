using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W3-2 G5 场景①（行动费复核——W2b 六处读取接改行为复核；本单对行动费预期零生产代码改动）。
/// 「-1/+3」两方向 × 三类读点（可用性/复验/扣费）同读有效值：
/// ①降费（不足→可用——移动/攻击可用性随有效值变化）＋扣费按有效值结算＋撤销回弹；
/// ②升费（可用→动作级置黑；交互期费用漂移→执行前复验拒绝、零副作用）＋复原后扣费按有效值；
/// ③攻击路径复验（漂移拒绝、零副作用）＋升费下复验通过并按有效值扣费。
/// 口径：直挂修饰（机制级最小行为证明）；驱动经真实指挥流程（BeginCommandAsync → 交互 → 执行/复验）。
/// </summary>
public class OperateCostRecheckTests
{
    // ---------- ① 降费方向：可用性解锁 ＋ 扣费读有效值 ----------

    [Fact]
    public async Task Decrease_Unlocks_Availability_And_Deduction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.CostlyId, 1); // 行动费 2
        CommandTestKit.Activate(unit);
        var source = new object();

        // 回合 1：点数 1。未修饰：行动费 2 → 移动/攻击均动作级置黑（PointShortage 读「基准」——费用判定先行）
        var before = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(before.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, before.Move.BlockReason);
        Assert.False(before.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, before.Attack.BlockReason);

        // 降费 -1：有效 1 → 移动可用（候选＝前线空槽）；攻击费用门槛消失（读点读「有效值」——
        // 因无目标止于候选空：BlockReason 从 PointShortage 让路为 NoCandidates）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, -1, source));
        Assert.Equal(1, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));
        var after = match.CommandManager.GetCommandAvailability(unit);
        Assert.True(after.Move.CanUse);
        Assert.False(after.Attack.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, after.Attack.BlockReason);

        // 真实移动：复验通过（有效 1 ≤ 1）→ 执行 → 扣费读有效值（1 点扣 1；读实时值 2 会扣成负）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var moveResult = await CommandTestKit.RunCommandAsync(
            match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);
        Assert.Equal(CommandResultStatus.Success, moveResult.Status);
        Assert.Equal(0, playerA.Points);

        // 撤销：有效回落 2（读点回弹——读取口径闭环）
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));
    }

    // ---------- ② 升费方向：动作级置黑 ＋ 交互期漂移触发复验拒绝（零副作用） ----------

    [Fact]
    public async Task Increase_Blocks_Availability_And_Revalidation()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 行动费 1
        var source = new object();

        await match.EndTurn();
        await match.EndTurn(); // 推进到 A 回合 3：点数 2
        CommandTestKit.Activate(unit);

        // 升费 +3：有效 4 → 移动动作级置黑（PointShortage 读「有效值」）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 3, source));
        var blocked = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(blocked.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, blocked.Move.BlockReason);

        // 撤销：复原可用（有效 1 ≤ 2——回弹闭环）
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.True(match.CommandManager.GetCommandAvailability(unit).Move.CanUse);

        // 交互期费用漂移（+3）：可用性检查时可用、执行前复验读「最新有效值」4 > 2 → 拒绝（零副作用）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var task = match.CommandManager.BeginCommandAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 3, source));
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref)));
        var revalidated = await task;

        Assert.Equal(CommandResultStatus.Failed, revalidated.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, revalidated.FailureReason);
        // 零副作用：未移动、未扣费、两 bool 未变
        Assert.Same(match.Battlefield.GetSupportLine(player), unit.GetData<UnitStateData>().Line); // 仍在原支援线（位置未变）
        Assert.Equal(2, player.Points);
        Assert.True(unit.GetData<CommandData>().CanMove);

        // 撤销漂移 → 有效回 1 → 真实移动成功（复验通过）→ 扣费读有效值（扣 1 → 点数 1）
        await unit.Modifiers.RemoveBySourceAsync(source);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var moved = await CommandTestKit.RunCommandAsync(
            match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);
        Assert.Equal(CommandResultStatus.Success, moved.Status);
        Assert.Equal(1, player.Points);
    }

    // ---------- ③ 攻击路径：复验拒绝（漂移）＋ 复验通过后扣费读有效值 ----------

    [Fact]
    public async Task Attack_Revalidation_And_Deduction_Read_Effective_Cost()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 行动费 1（攻 2）
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 跨线紧邻（支援线→敌前线）
        var probeSource = new object();
        var driftSource = new object();
        var stableSource = new object();

        await match.EndTurn();
        await match.EndTurn(); // 推进到 A 回合 3：点数 2
        CommandTestKit.Activate(attacker);

        // 攻击可用性读「有效值」：升费 +2（有效 3 > 2）→ 动作级置黑；撤销 → 复原可用（读点闭环）
        await attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 2, probeSource));
        var black = match.CommandManager.GetCommandAvailability(attacker);
        Assert.False(black.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, black.Attack.BlockReason);
        await attacker.Modifiers.RemoveBySourceAsync(probeSource);
        Assert.True(match.CommandManager.GetCommandAvailability(attacker).Attack.CanUse);

        // 交互期费用漂移（+2）：执行前复验读「最新有效值」3 > 2 → 拒绝（零副作用：不扣费、目标不受伤）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var task = match.CommandManager.BeginCommandAsync(attacker);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        await attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 2, driftSource));
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), target.Ref)));
        var rejected = await task;

        Assert.Equal(CommandResultStatus.Failed, rejected.Status);
        Assert.Equal(CommandFailureReason.ExecutionRejected, rejected.FailureReason);
        Assert.Equal(2, playerA.Points);
        Assert.Equal(5, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 未受伤
        await attacker.Modifiers.RemoveBySourceAsync(driftSource);

        // 升费 +1（有效 2 = 点数 2）：可用 → 真实攻击 → 复验通过 → 扣费读有效值（扣 2；读实时值 1 只扣 1）
        await attacker.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.OperateCost, 1, stableSource));
        Assert.True(match.CommandManager.GetCommandAvailability(attacker).Attack.CanUse);
        var attackResult = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, attackResult.Status);
        Assert.Equal(0, playerA.Points);
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 正常互伤（5 − 2）
    }
}
