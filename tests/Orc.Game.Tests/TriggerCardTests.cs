using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2c X1 验收（触发者卡牌——4 个内置流程触发器视图承载与调用点填充）：
/// ①主动指挥（含其嵌套调用）＝缺省空（指挥/攻击/伤害/移动链路）；
/// ②效果引发＝调用方显式携带（构造调用／BeginCommandAsync 可选参数）；
/// ③同一效果链内传递（「攻击→伤害」链内一致；指挥→嵌套移动/攻击透传）；
/// ④缺省调用（未携带）＝空（向后兼容）；判等以引用同一性为准（同一引用实例）。
/// </summary>
public class TriggerCardTests
{
    [Fact]
    public async Task Player_Command_Attack_Leaves_TriggerCard_Empty_On_All_Links()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var commandLog = ProbeCommand(match.CommandManager);
        var attackLog = ProbeAttack(match.CommandManager);
        var damageLog = ProbeDamage(match.CommandManager);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 主动指挥：非效果引发——指挥/攻击/伤害三链路触发者均空（缺省携带＝空；两集合互斥穷尽）
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Null(Assert.Single(commandLog));
        Assert.Null(Assert.Single(attackLog));
        Assert.Null(Assert.Single(damageLog));
    }

    [Fact]
    public async Task Effect_Initiated_Attack_Carries_TriggerCard_Through_Attack_And_Damage()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var effectHost = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(attacker);

        var attackLog = ProbeAttack(match.CommandManager);
        var damageLog = ProbeDamage(match.CommandManager);

        // 效果引发：调用方（效果侧）显式携带触发者——逐次指定「本次操作由哪张卡引发」（构造调用驱动底层触发器）
        var stream = await match.CommandManager.UnitAttackTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [CommandDataKeys.Attacker] = attacker.Ref,
                [CommandDataKeys.Target] = target.Ref,
                [CommandDataKeys.TriggerCard] = effectHost,
            });

        // 攻击链路照常执行（复验兼容触发者引用）；触发者随「攻击→伤害」链传递（两链路同引用实例）
        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
        Assert.Same(effectHost, Assert.Single(attackLog));
        Assert.Same(effectHost, Assert.Single(damageLog));
        Assert.Equal(3, target.GetData<UnitStateData>().Defense); // 真实结算：5 − 2
    }

    [Fact]
    public async Task Effect_Initiated_Damage_Directly_Carries_TriggerCard()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var effectHost = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);

        var damageLog = ProbeDamage(match.CommandManager);

        // 效果直接引发伤害（不经攻击链路）：触发者＝E（「直接因由」——与链路传递收敛结果一致）
        var stream = await match.CommandManager.AttackDamageTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [CommandDataKeys.Attacker] = attacker.Ref,
                [CommandDataKeys.Target] = target.Ref,
                [CommandDataKeys.Resolution] = new AttackDamageResolution(),
                [CommandDataKeys.TriggerCard] = effectHost,
            });

        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
        Assert.Same(effectHost, Assert.Single(damageLog));
    }

    [Fact]
    public async Task Move_View_Leaves_TriggerCard_Empty_On_Player_Command()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var moveLog = ProbeMove(match.CommandManager);
        var frontSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, mover, frontSlot.Ref);

        // 主动指挥移动：非效果引发——移动链路触发者空
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Null(Assert.Single(moveLog));
        Assert.Same(mover, frontSlot);
    }

    [Fact]
    public async Task Move_View_Carries_TriggerCard_On_Effect_Initiated_Move()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var effectHost = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(mover);
        var moveLog = ProbeMove(match.CommandManager);
        var oldSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        var newSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        // 效果引发移动：显式携带触发者（构造调用方式）——真实执行、填充正确
        var stream = await match.CommandManager.UnitMoveTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [CommandDataKeys.Unit] = mover.Ref,
                [CommandDataKeys.OldPosition] = oldSlot.Ref,
                [CommandDataKeys.NewPosition] = newSlot.Ref,
                [CommandDataKeys.TriggerCard] = effectHost,
            });

        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
        Assert.Same(effectHost, Assert.Single(moveLog));
        Assert.Same(mover, newSlot);
    }

    [Fact]
    public async Task Command_View_Carries_TriggerCard_And_Propagates_To_Inner_Triggers()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var effectHost = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(mover);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var commandLog = ProbeCommand(match.CommandManager);
        var moveLog = ProbeMove(match.CommandManager);
        var frontSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        // 效果引发指挥：经入口可选参数显式携带触发者
        var task = match.CommandManager.BeginCommandAsync(mover, triggerCard: effectHost.Ref);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        var accepted = responder.Complete(
            description.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, frontSlot.Ref));
        Assert.True(accepted);
        var result = await task;

        // 指挥视图＝E；同一效果链内传递：嵌套移动调用随指挥链透传（链内一致）
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Same(effectHost, Assert.Single(commandLog));
        Assert.Same(effectHost, Assert.Single(moveLog));
        Assert.Same(mover, frontSlot);
    }

    [Fact]
    public async Task Command_Entry_Without_TriggerCard_Leaves_Inner_Move_Empty()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var commandLog = ProbeCommand(match.CommandManager);
        var moveLog = ProbeMove(match.CommandManager);
        var frontSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        // 主动指挥：入口缺省（不携带）——指挥与嵌套移动均空（向后兼容）
        var result = await CommandTestKit.RunCommandAsync(match, bridge, mover, frontSlot.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Null(Assert.Single(commandLog));
        Assert.Null(Assert.Single(moveLog));
    }

    // ---------- 探测辅助（注册在默认链之后的读取探针：链路执行完成后读取视图触发者字段） ----------

    private static List<Card?> ProbeCommand(CommandManager commandManager)
    {
        var log = new List<Card?>();
        commandManager.CommandTrigger.Register("X1探测-指挥", (view, ctx, ct) =>
        {
            log.Add(view.TriggerCard);
            return Task.CompletedTask;
        });
        return log;
    }

    private static List<Card?> ProbeMove(CommandManager commandManager)
    {
        var log = new List<Card?>();
        commandManager.UnitMoveTrigger.Register("X1探测-移动", (view, ctx, ct) =>
        {
            log.Add(view.TriggerCard);
            return Task.CompletedTask;
        });
        return log;
    }

    private static List<Card?> ProbeAttack(CommandManager commandManager)
    {
        var log = new List<Card?>();
        commandManager.UnitAttackTrigger.Register("X1探测-攻击", (view, ctx, ct) =>
        {
            log.Add(view.TriggerCard);
            return Task.CompletedTask;
        }, Orc.Game.Actions.ActionTriggerBands.Initiate);
        return log;
    }

    private static List<Card?> ProbeDamage(CommandManager commandManager)
    {
        var log = new List<Card?>();
        commandManager.AttackDamageTrigger.Register("X1探测-伤害", (view, ctx, ct) =>
        {
            log.Add(view.TriggerCard);
            return Task.CompletedTask;
        }, Orc.Game.Actions.ActionHandleBands.Apply);
        return log;
    }
}
