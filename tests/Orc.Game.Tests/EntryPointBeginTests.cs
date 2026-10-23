using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 02-输入接口 S7（新增入口与三类选择器槽位验收）：
/// ①独立移动/攻击入口（成功／取消／发起拒绝零副作用；与 <c>GetCommandAvailability</c> 同源）；
/// ②指令统一预行为入口（<c>BeginOrderPrePlayAsync</c> 与 <c>PlayOrderAsync</c> 同一实现）；
/// ③三类选择器槽位（槽位按声明序呈现、槽位参数随请求描述交付前端）。
/// </summary>
public class EntryPointBeginTests
{
    // ---------- ① 独立移动入口 ----------

    [Fact]
    public async Task BeginMoveAsync_Confirms_And_Executes_Move_With_Slot_Parameter()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        CommandTestKit.Activate(mover);
        var front = match.Battlefield.FrontLine;
        var pointsBefore = playerA.Points;

        var task = match.CommandManager.BeginMoveAsync(mover);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 槽位＝场上单位指向（单选）；槽位参数＝被拖动单位（随请求描述交付前端）
        var slot = Assert.Single(description.Slots);
        Assert.Equal(SelectorNames.FieldUnit, slot.Name);
        Assert.True(slot.HasParameter);
        Assert.Same(mover, slot.Parameter);

        Assert.True(responder.Complete(
            description.RequestId, TargeterTestKit.Selection(SelectorNames.FieldUnit, front[0].Ref)));
        var result = await task;

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Same(front[0], mover.GetData<UnitStateData>().Line);
        Assert.DoesNotContain(mover, match.Battlefield.PlayerASupportLine.Members);
        Assert.False(mover.GetData<CommandData>().CanMove);      // 收尾清位
        Assert.Equal(pointsBefore - 1, playerA.Points);          // 收尾扣费（行动费 1）
    }

    [Fact]
    public async Task BeginMoveAsync_Cancel_Leaves_Zero_Side_Effect()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        CommandTestKit.Activate(mover);
        var pointsBefore = playerA.Points;

        var task = match.CommandManager.BeginMoveAsync(mover);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.True(responder.Cancel(description.RequestId));
        var result = await task;

        Assert.Equal(CommandResultStatus.Cancelled, result.Status);
        Assert.Same(match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1), mover.GetData<UnitStateData>().Line);
        Assert.True(mover.GetData<CommandData>().CanMove);       // 未消耗行动
        Assert.Equal(pointsBefore, playerA.Points);              // 未扣费
    }

    [Fact]
    public async Task BeginMoveAsync_Ineligible_Rejects_Without_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerB = match.Players[1];
        var stranger = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        CommandTestKit.Activate(stranger);

        var result = await match.CommandManager.BeginMoveAsync(stranger);

        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NonOwnerTurn, result.FailureReason);
        Assert.Empty(bridge.Begins);                             // 发起拒绝：不进入交互、零副作用
    }

    // ---------- ① 独立攻击入口 ----------

    [Fact]
    public async Task BeginAttackAsync_Confirms_And_Executes_Attack()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        CommandTestKit.Activate(attacker);

        var task = match.CommandManager.BeginAttackAsync(attacker);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(SelectorNames.FieldUnit, Assert.Single(description.Slots).Name);
        Assert.True(responder.Complete(
            description.RequestId, TargeterTestKit.Selection(SelectorNames.FieldUnit, target.Ref)));
        var result = await task;

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed); // 攻 2 vs 防 2
        Assert.False(attacker.GetData<CommandData>().CanAttack);  // 收尾清位
    }

    [Fact]
    public async Task BeginAttackAsync_No_Candidates_Rejects_Without_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var lonely = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        CommandTestKit.Activate(lonely);

        var result = await match.CommandManager.BeginAttackAsync(lonely);

        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NoActionAvailable, result.FailureReason); // 与聚合判定同源
        Assert.Empty(bridge.Begins);
        Assert.True(lonely.GetData<CommandData>().CanAttack);    // 失败不消耗行动
    }

    // ---------- ② 指令统一预行为入口 ----------

    [Fact]
    public async Task BeginOrderPrePlayAsync_Is_Unified_PreAction_Entry_With_Selector_Slots()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCheapId);
        IReadOnlyList<Selector>? observedSlots = null;
        command.AddPrePlayHandler("观测槽位声明", (view, ctx, ct) =>
        {
            observedSlots = view.SelectorSlots;
            return Task.CompletedTask;
        });

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.PlayManager.BeginOrderPrePlayAsync(command);

        // 与兼容入口同一实现：照发 card.played（恰一次）、照扣费（E1-25 后续：收尾发 point.changed）、离手
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { GameUpdates.CardPlayed, GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(command, player.Hand);

        // 预打出视图携带本卡声明的选择器槽位（手牌起始指向）
        Assert.Equal(SelectorNames.HandOrigin, Assert.Single(observedSlots!).Name);
    }

    [Fact]
    public async Task PlayOrderAsync_Is_Equivalent_To_PreAction_Entry()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCheapId);

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(command, player.Hand);
    }

    // ---------- ③ 单位预打出视图携带槽位；三类槽位工厂 ----------

    [Fact]
    public async Task BeginUnitPrePlayAsync_View_Carries_Declared_Selector_Slots()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        bridge.CollectScript = PlayChainTestKit.AllSlotsCandidatesScript(match);
        IReadOnlyList<Selector>? observedSlots = null;
        unit.PrePlayTrigger.Register("观测槽位声明", (view, ctx, ct) =>
        {
            observedSlots = view.SelectorSlots;
            return Task.CompletedTask;
        });

        var task = match.PlayManager.BeginUnitPrePlayAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(description.Slots[0].Name, description.AllowedTargets[0])));
        var result = await task;

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(SelectorNames.UnitHandDrag, Assert.Single(observedSlots!).Name); // 单位＝手牌拖出形态
    }

    [Fact]
    public async Task Selector_Templates_Produce_Declared_Names()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();

        Assert.Equal(SelectorNames.HandOrigin, SelectorTemplates.HandOrigin.Name);
        Assert.Equal(SelectorNames.UnitHandDrag, SelectorTemplates.UnitHandDrag.Name);
        Assert.Equal(SelectorNames.AfterPlacement, SelectorTemplates.AfterPlacement.Name);
        Assert.Equal(SelectorNames.FieldUnit, SelectorTemplates.FieldUnit.Name);
    }

    [Fact]
    public async Task Selector_Delivers_Parameter_To_Bridge()
    {
        var bridge = new MockTargeterBridge();
        var manager = new TargeterManager(bridge);
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var card = match.CardLibrary.Instantiate(PlayChainTestKit.CommandCheapId);
        var candidate = new Entity("候选引用");

        var task = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.HandOrigin,
                new ReferenceSetParameter(new[] { candidate.Ref }, 1, 1, tag: card));
            return step.IsOk ? TargeterResult.Ok() : TargeterResult.FromSelectorFailure(step.Failure);
        });

        var (description, responder) = await bridge.WaitForNextBeginAsync();

        var slot = Assert.Single(description.Slots);
        Assert.Equal(SelectorNames.HandOrigin, slot.Name);
        Assert.True(slot.HasParameter);
        Assert.Same(card, slot.Parameter);

        Assert.True(responder.Complete(
            description.RequestId, TargeterTestKit.Selection(SelectorNames.HandOrigin, candidate.Ref)));
        Assert.True((await task).IsOk);
    }
}
