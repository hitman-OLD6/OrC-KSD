using System.Reflection;
using Orc.Cards;
using Orc.Core;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P2 动作族验收（S6 卡牌移动 / S7 抽牌与加入手牌 / S8 死亡两端）：
/// 逐条钉死本阶段的**允许行为变更**与**不允许行为变更**，避免「半新半旧」的第三条路径。
/// </summary>
public class P2ActionFamilyTests
{
    // ══════════════════════════ S6：卡牌移动族 ══════════════════════════

    [Fact]
    public async Task Effect_Move_Is_Free_And_Consumes_No_Operate_State()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(unit);

        var support = match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);
        var points = player.Points;
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.CommandManager.MoveUnitAsync(unit, support));

        // 动作状态：落地（唯一落地口）。
        Assert.Same(support, unit.GetData<UnitStateData>().Line);
        Assert.Same(unit, support);
        // 行动状态：零消耗（S6 有意变更：效果移动免费）。
        Assert.Equal(points, player.Points);
        Assert.True(unit.GetData<CommandData>().CanMove);
        Assert.True(unit.GetData<CommandData>().CanAttack);
        // 信号：位置变更恰一次；零 unit.acted、零 point.changed。
        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.UnitPositionChanged));
        Assert.DoesNotContain(GameUpdates.UnitActed, recorder.Types);
        Assert.DoesNotContain(GameUpdates.PointChanged, recorder.Types);
    }

    [Fact]
    public async Task Suppressed_Unit_Is_Rejected_As_Command_But_Still_Movable_By_Effect()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        Assert.True(await SuppressRules.ApplyAsync(unit));

        // 压制只落在**指挥资格/候选面**（R16）：指挥移动不可用、原因可辨识。
        var availability = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Move.BlockReason);

        // 效果移动不受压制阻断（动作本身零压制判定）。
        var destination = match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);
        Assert.True(await match.CommandManager.MoveUnitAsync(unit, destination));
        Assert.Same(destination.Line, unit.GetData<UnitStateData>().Line);
    }

    [Fact]
    public async Task Command_Move_Still_Pays_And_Clears_Operate_Flags()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var points = player.Points;

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);

        Assert.True(result.IsSuccess);
        Assert.Equal(points - unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost), player.Points);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public void Move_Action_Registers_Single_Commit_And_Single_Landing_Handler()
    {
        var action = new MoveAction(new LogicEngine(), new FixedMoveServices());

        Assert.Single(action.Trigger.Events, e => e.Name == "转发到处理端"
            && e.BandValue == (int)ActionTriggerBands.Commit);
        Assert.DoesNotContain(action.Trigger.Events, e => e.BandValue == (int)ActionTriggerBands.Announce);
        Assert.DoesNotContain(action.Trigger.Events, e => e.BandValue == (int)ActionTriggerBands.Counter);
        var apply = Assert.Single(action.Handle.Events);
        Assert.Equal("落地移动", apply.Name);
        Assert.Equal((int)ActionHandleBands.Apply, apply.BandValue);
    }

    // ══════════════════════════ S7：抽牌与加入手牌 ══════════════════════════

    [Fact]
    public async Task Draw_Under_Limit_Emits_Drawn_Before_HandAdd_And_Drawn_Precedes_Hand_Insertion()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        var inHandAtDrawn = new List<bool>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDrawn && payload is not null
                && payload[GameUpdates.PayloadCard] is CardBase drawnCard)
            {
                inHandAtDrawn.Add(player.Hand.Contains(drawnCard));
            }

            return Task.CompletedTask;
        });

        var card = await match.PlayerManager.DrawCard(player);

        // 信号顺序与计数（不允许的行为变更）。
        Assert.Equal(new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd }, recorder.Types);
        Assert.Contains(card, player.Hand);

        // 有意变更（P2 登记）：drawn 的观察时点提前到「入列表之前」——载荷已携带 Card 引用。
        Assert.False(Assert.Single(inHandAtDrawn));
    }

    [Fact]
    public async Task Draw_At_Hand_Limit_Burns_Via_Destroy_Without_Discarded()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        while (player.Hand.Count < Player.HandLimit)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        }

        using var recorder = new UpdateRecorder(match.Engine);
        var card = await match.PlayerManager.DrawCard(player);

        Assert.Equal(
            new[] { GameUpdates.CardDrawn, Updates.CardDestroyed, GameUpdates.CardBurned },
            recorder.Types.Where(t => t is GameUpdates.CardDrawn or Updates.CardDestroyed
                or GameUpdates.CardBurned or GameUpdates.CardDiscarded or GameUpdates.CardHandAdd));
        Assert.DoesNotContain(GameUpdates.CardDiscarded, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardHandAdd, recorder.Types);
        Assert.False(card.Life.IsAlive);
        Assert.Equal(Player.HandLimit, player.Hand.Count);
    }

    [Fact]
    public async Task Non_Draw_Add_To_Hand_Emits_Only_HandAdd()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        var placed = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, player);

        Assert.Equal(CardPlaceStatus.Placed, placed.Status);
        // 非抽牌加入：只有 card.hand.add（drawn 零次——「未经手牌」路径口径保持）。
        Assert.Equal(
            new[] { GameUpdates.CardHandAdd },
            recorder.Types.Where(t => t == GameUpdates.CardDrawn || t == GameUpdates.CardHandAdd));
        Assert.DoesNotContain(GameUpdates.CardDrawn, recorder.Types);
    }

    [Fact]
    public async Task Add_To_Hand_Action_Is_Reusable_And_Has_Both_Ends()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, player);

        var action = match.CommandManager.AddToHandAction;
        Assert.Single(action.Trigger.Events, e => e.Name == "转发到处理端"
            && e.BandValue == (int)ActionTriggerBands.Commit);
        Assert.Single(action.Handle.Events, e => e.BandValue == (int)ActionHandleBands.Apply);

        // 非抽牌路径复用：直接驱动动作（不经卡牌服务）。
        var result = await action.AddAsync(card, player);
        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Contains(card, player.Hand);
    }

    [Fact]
    public void Burn_No_Longer_Takes_An_EmitDrawn_Switch()
    {
        var method = typeof(HandLimitBurn).GetMethod("BurnAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(), p => p.ParameterType == typeof(bool));
        Assert.Equal(4, method.GetParameters().Length); // engine / player / card / ct
    }

    // ══════════════════════════ S8：死亡两端 ══════════════════════════

    [Fact]
    public async Task Death_Rewrite_Can_Change_Killer_And_Shows_In_CardDied()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        var killer = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.BeastId, 2);

        match.CommandManager.DeathAction.Handle.Register(
            "改写归属",
            (view, _, _) =>
            {
                view.Killer = killer;
                return Task.CompletedTask;
            },
            ActionHandleBands.Rewrite);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        var died = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Same(killer, died.Payload![GameUpdates.PayloadKiller]);
        Assert.Same(killer, died.Payload![GameUpdates.PayloadSource]);
    }

    [Fact]
    public async Task Death_ShouldDie_False_Is_Blocked_With_Log_And_Zero_State_Change()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        var position = unit.GetData<UnitStateData>().Line;
        var defense = unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

        match.CommandManager.DeathAction.Handle.Register(
            "免死",
            (view, _, _) =>
            {
                view.ShouldDie = false;
                return Task.CompletedTask;
            },
            ActionHandleBands.Rewrite);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        Assert.Equal(ActionOutcomeKind.Blocked, result.Targets[0].Outcome);
        Assert.Equal(ActionFailureKind.DeathPrevented, result.Targets[0].Reason);
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.Same(position, unit.GetData<UnitStateData>().Line);
        Assert.Equal(defense, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.DoesNotContain(GameUpdates.CardDying, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);

        // 「免死」只有留痕：事件流 log（不发射总线信号）。
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Keywords.Contains("death:prevented"));
    }

    [Fact]
    public async Task Death_Disposition_ToHand_Is_Executed_By_The_Action_Wrapper()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        var handBefore = player.Hand.Count;

        match.CommandManager.DeathAction.Handle.Register(
            "改为回手",
            (view, _, _) =>
            {
                view.Disposition = ActionDisposition.ToHand;
                return Task.CompletedTask;
            },
            ActionHandleBands.Rewrite);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        // 死亡链照常完成（含 card.died），处置由动作层包装执行（处理端不调加入手牌）。
        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(ActionDisposition.ToHand, result.Targets[0].Disposition);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.CardDied));

        // 回手＝新建同定义实例（不是把已毁实例塞回手牌）。
        Assert.Equal(handBefore + 1, player.Hand.Count);
        var returned = player.Hand[^1];
        Assert.NotSame(unit, returned);
        Assert.Equal(unit.Name, returned.Name);
        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.CardHandAdd));
    }

    [Fact]
    public async Task Death_Action_Has_One_Rewrite_Slot_And_No_Second_Cancel_Port()
    {
        // 旧的公开可写取消口已删除（唯一改写位＝处理端 Rewrite 段）。
        Assert.Null(typeof(CommandManager).GetProperty("DeathInterceptor"));

        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var handle = match.CommandManager.DeathAction.Handle;
        var rewrite = Assert.Single(handle.Events, e => e.BandValue == (int)ActionHandleBands.Rewrite);
        Assert.Equal("词条程序取消消灭", rewrite.Name);
        Assert.Single(handle.Events, e => e.BandValue == (int)ActionHandleBands.Veto);
        Assert.Single(handle.Events, e => e.BandValue == (int)ActionHandleBands.Apply);
    }

    // ---------- 测试桩 ----------

    private sealed class FixedMoveServices : IMoveActionServices
    {
        public Task<bool> LandMoveAsync(UnitCard unit, Orc.Game.Board.FormationGap destination, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
