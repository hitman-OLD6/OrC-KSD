using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 后置项 C（前线阻挡）：①推进前置——进入前线需「前线不存在存活敌方单位」（空前线或己方已占）；
/// 候选面剔除（移动候选为空、攻击候选不受影响）＋执行兜底复验（分派后、执行前；拒绝＝零副作用）＋
/// 敌方清空后实时恢复；②轰炸机拦截——轰炸机攻击目标筛选：目标所在战线存在存活敌方战斗机时该战线
/// 非战斗机目标置黑（含 HQ）；跨战线其他目标不受影响；仅轰炸机；候选面＋确认面全程；多条/存活限定。
/// </summary>
public class CommandFrontLineTests
{
    [Fact]
    public async Task Advance_Is_Blocked_While_Enemy_Occupies_Front_And_Recovers_After_Clear()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var beast = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BeastId, 1); // 攻 6 / 防 7
        var blocker = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0); // 攻 1 / 防 2（敌占前线）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn(); // → B 回合
        await match.EndTurn(); // → A 回合 3（2 点；两 bool 恢复）
        CommandTestKit.Activate(mover);
        CommandTestKit.Activate(beast);
        var front = match.Battlefield.FrontLine;

        // 敌占前线：推进候选剔除（移动不可用、候选为空——前线物理空槽不作为移动候选）；
        // 攻击候选不受影响（前线敌单位仍可达）。
        var blocked = match.CommandManager.GetCommandAvailability(mover);
        Assert.False(blocked.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, blocked.Move.BlockReason);
        Assert.Empty(blocked.Move.Candidates);
        Assert.True(blocked.Attack.CanUse);
        Assert.Contains(blocker.Ref, blocked.Attack.Candidates);

        // 清空前线（杀死敌占单位）→ 下一次候选查询即恢复（实时计算，无需额外动作）。
        var kill = await CommandTestKit.RunCommandAsync(match, bridge, beast, blocker.Ref);
        Assert.Equal(CommandResultStatus.Success, kill.Status);
        Assert.True(blocker.GetData<UnitStateData>().IsDestroyed);
        Assert.DoesNotContain(blocker, front.Members);

        var recovered = match.CommandManager.GetCommandAvailability(mover);
        Assert.True(recovered.Move.CanUse);
        Assert.Equal(new[] { front[0].Ref, front[1].Ref, front[2].Ref, front[3].Ref, front[4].Ref },
            recovered.Move.Candidates);
    }

    [Fact]
    public async Task Advance_Remains_Available_When_Front_Holds_Only_Friendly_Units()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        // 己方已占前线（无敌人）＝前置满足：推进候选＝前线空槽（被占槽除外）。
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        var front = match.Battlefield.FrontLine;

        var report = match.CommandManager.GetCommandAvailability(mover);

        Assert.True(report.Move.CanUse);
        Assert.Equal(new[] { front[1].Ref, front[2].Ref, front[3].Ref, front[4].Ref }, report.Move.Candidates);
    }

    [Fact]
    public async Task Advance_Execution_Revalidation_Rejects_When_Enemy_On_Front()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 1); // 敌占前线（槽 1）
        CommandTestKit.Activate(mover);
        var oldSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        var targetSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0); // 空槽（前置不满足——防御性双保险拒绝）

        // 执行兜底复验：分派后、执行前复验前置条件——不满足＝ValidationRejected（零副作用：槽位不变、行动状态不消耗）。
        var stream = await match.CommandManager.UnitMoveTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [CommandDataKeys.Unit] = mover.Ref,
                [CommandDataKeys.OldPosition] = oldSlot.Ref,
                [CommandDataKeys.NewPosition] = targetSlot.Ref,
            });

        Assert.Equal(ExecutionOutcome.ValidationRejected, stream.Outcome);
        Assert.Same(mover, oldSlot);
        Assert.Empty(match.Battlefield.FrontLine.Members); // 零副作用：目标位置未被占用
        Assert.True(mover.GetData<CommandData>().CanMove);
        Assert.Equal(1, playerA.Points); // 零副作用：费用未扣
    }

    [Fact]
    public async Task Bomber_Interception_Blocks_Non_Fighter_Targets_On_Fighter_Line()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0);
        var frontInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var supportInfantry = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(bomber);

        var report = match.CommandManager.GetCommandAvailability(bomber);

        // 轰炸机拦截：前线存在存活敌战斗机 → 该战线非战斗机目标置黑（前线步兵不可选、战斗机可选）；
        // 跨战线其他目标不受影响（敌支援线无战斗机 → 该线步兵与 HQ 均可选）。
        Assert.True(report.Attack.CanUse);
        Assert.Contains(enemyFighter.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(frontInfantry.Ref, report.Attack.Candidates);
        Assert.Contains(supportInfantry.Ref, report.Attack.Candidates);
        Assert.Contains(playerB.Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
    }

    [Fact]
    public async Task Bomber_Interception_Includes_Hq_Of_Line_With_Fighter()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.FighterId, 1);
        var supportInfantry = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        var frontInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(bomber);

        var report = match.CommandManager.GetCommandAvailability(bomber);

        // HQ 拦截：HQ 位于敌方支援线——该战线存在存活敌战斗机时不可选（须先攻击战斗机）；
        // 同线非战斗机（支援线步兵）同样置黑；战斗机可选；前线无战斗机 → 前线目标不受影响。
        Assert.Contains(enemyFighter.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(supportInfantry.Ref, report.Attack.Candidates);
        Assert.DoesNotContain(playerB.Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
        Assert.Contains(frontInfantry.Ref, report.Attack.Candidates);
    }

    [Fact]
    public async Task Bomber_Interception_Requires_Living_Fighter()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var beast = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 0);
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 2);
        var enemyFighter = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.FighterId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        await match.EndTurn(); // → B 回合
        await match.EndTurn(); // → A 回合 3（2 点；两 bool 恢复）
        CommandTestKit.Activate(beast);

        // 战前：敌支援线存在存活战斗机 → HQ 置黑。
        CommandTestKit.Activate(bomber);
        var before = match.CommandManager.GetCommandAvailability(bomber);
        Assert.DoesNotContain(playerB.Hq.Ref, before.Attack.Candidates); // W3-3：HQ 目标＝实体引用

        // 杀死战斗机（以存活为限：死亡不计）→ 拦截解除、HQ 恢复可选。
        var kill = await CommandTestKit.RunCommandAsync(match, bridge, beast, enemyFighter.Ref);
        Assert.Equal(CommandResultStatus.Success, kill.Status);
        Assert.True(enemyFighter.GetData<UnitStateData>().IsDestroyed);

        var after = match.CommandManager.GetCommandAvailability(bomber);
        Assert.Contains(playerB.Hq.Ref, after.Attack.Candidates); // W3-3：HQ 目标＝实体引用
    }

    [Fact]
    public async Task Bomber_Interception_Confirmation_Is_Constrained()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var bomber = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemyFighter = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.FighterId, 0);
        var frontInfantry = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(bomber);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var hqRef = playerB.Hq.Ref; // W3-3：HQ 目标＝实体引用

        var task = match.CommandManager.BeginCommandAsync(bomber);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 候选面：被拦截目标（同线步兵）不在允许集；战斗机与跨战线目标（HQ）在。
        Assert.Contains(enemyFighter.Ref, description.AllowedTargets);
        Assert.Contains(hqRef, description.AllowedTargets);
        Assert.DoesNotContain(frontInfantry.Ref, description.AllowedTargets);

        // 确认面全程受同一筛选约束：提交被拦截目标＝Failed → 内部重入（同一选择器）；提交战斗机＝确认。
        var rejected = responder.Complete(
            description.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, frontInfantry.Ref));
        Assert.True(rejected);
        var (reentryDescription, reentryResponder) = await bridge.WaitForNextBeginAsync();
        var accepted = reentryResponder.Complete(
            reentryDescription.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, enemyFighter.Ref));
        Assert.True(accepted);

        var result = await task;
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(enemyFighter.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Non_Bomber_Attackers_Ignore_Fighter_Interception()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var fighter = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.FighterId, 2);
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 2);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.FighterId, 1); // 敌支援线战斗机
        CommandTestKit.Activate(fighter);
        CommandTestKit.Activate(artillery);
        var hqRef = playerB.Hq.Ref; // W3-3：HQ 目标＝实体引用

        // 仅轰炸机攻击者受拦截约束：战斗机 / 炮兵的攻击筛选不受影响（HQ 可选）。
        var fighterReport = match.CommandManager.GetCommandAvailability(fighter);
        Assert.Contains(hqRef, fighterReport.Attack.Candidates);
        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(hqRef, artilleryReport.Attack.Candidates);
    }
}
