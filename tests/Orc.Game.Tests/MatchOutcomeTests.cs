using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 后置项 B（胜负判定）：HQ 生命≤0 → 立即终局——对局状态置"结束"＋记录胜者（＝使对方 HQ 归零的一方）；
/// 当次结算收尾照常完成（内部步骤不经门禁）；其后所有游戏动作入口拒绝（六类逐一：①回合推进 ②指挥入口
/// ③打出链各入口〔预打出/打出/加入/指令/反制激活/取消〕④移动/攻击流程〔含触发器级对外入口〕⑤初始化/卡牌加载链
/// ⑥Targeter 发起）；更新流冻结（无新更新）＋重复查询稳定性（只读面保持可用）。
/// </summary>
public class MatchOutcomeTests
{
    /// <summary>终局构造助手（准备巨炮并置可行动——终局前调用）。</summary>
    private static async Task<UnitCard> PrepareHqStrikerAsync(Match match)
    {
        var striker = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.MegaId, 1);
        CommandTestKit.Activate(striker);
        return striker;
    }

    /// <summary>终局构造助手（驱动巨炮攻击敌方 HQ——HQ 20-30 钳制到 0、立即终局）。
    /// W3-3：HQ 目标以 HQ 实体引用承载（hq.Ref——不再用槽位引用）。</summary>
    private static async Task<CommandResult> StrikeHqAsync(Match match, MockTargeterBridge bridge, UnitCard striker)
    {
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(
            match, bridge, striker, match.Players[1].Hq.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(match.Players[0], match.Winner);
        Assert.Equal(0, match.Players[1].HqHealth);
        return result;
    }

    [Fact]
    public async Task Hq_Zero_Ends_Match_Immediately_With_Winner_And_Settles_Strike()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var striker = await PrepareHqStrikerAsync(match);
        using var recorder = new UpdateRecorder(match.Engine); // 攻击前挂接：验证结算期间更新流
        MatchState? stateAtSignal = null; // W3-3：信号时刻对局状态（「先数值变化、后终局记录」观察序）
        using var signalProbe = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.CardStatChanged
                && ReferenceEquals(payload?[GameUpdates.PayloadCard], playerB.Hq))
            {
                stateAtSignal = match.State;
            }

            return Task.CompletedTask;
        });

        var result = await StrikeHqAsync(match, bridge, striker);

        // HQ 归零（钳制）→ 立即终局（状态＝结束、胜者＝攻击方；HQ 占位不变）；当次结算收尾照常完成
        //（扣费恰一次＋清位照常——内部步骤不经门禁）；攻击仍为成功。
        // W3-3：HQ 数值改走通用数据改变管线——结算期间恰一条数值变化更新（card.stat.changed、变化字段＝HqHealth），
        // 且「先数值变化、后终局记录」（信号时刻状态尚为进行）；判定由 HQ 侧统一响应承接（不再内联于攻击流程）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
        Assert.Equal(0, playerB.HqHealth);
        Assert.Same(playerB.Hq, match.Battlefield.PlayerBSupportLine.Members[0]); // HQ 占位不变（仍在支援线首位）
        Assert.Equal(0, playerA.Points);
        Assert.False(striker.GetData<CommandData>().CanAttack);
        Assert.False(striker.GetData<CommandData>().CanMove);
        var statUpdate = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardStatChanged);
        Assert.Equal(GameUpdates.CardStatChanged, statUpdate.Type);
        Assert.Same(playerB.Hq, statUpdate.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { CardStatFields.HqHealth }, ModifierTestKit.ChangedFieldsOf(statUpdate.Payload));
        Assert.Equal(MatchState.InProgress, stateAtSignal);
    }

    [Fact]
    public async Task End_Turn_And_Initialization_Are_Rejected_After_End()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var striker = await PrepareHqStrikerAsync(match);
        await StrikeHqAsync(match, bridge, striker);
        var turnNumber = match.TurnNumber;
        var currentPlayer = match.CurrentPlayer;

        // ①回合推进（EndTurn）＝拒绝（抛错、零副作用）；⑤对局初始化/卡牌加载链＝拒绝（不得再 Initialize）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.EndTurn());
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.Initialize());

        // 零副作用：回合数 / 当前方 / 终局状态与胜者均不变。
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Equal(turnNumber, match.TurnNumber);
        Assert.Same(currentPlayer, match.CurrentPlayer);
        Assert.Same(match.Players[0], match.Winner);
    }


    [Fact]
    public async Task Counter_Entrances_Are_Rejected_After_End()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        // 终局前：备两张反制；推两个回合（回合 3＝A 行动、2 点）。
        // R1.3：反制武装窗口只覆盖对手回合——己方回合开始即自动解除，故激活必须落在「终局前的最后一个己方回合」内；
        // 原写法在回合 1 激活后跨回合推进，会让窗口在回合 3 开局到期，与本用例「终局后两入口都被拒」的原意无关。
        var activeCounter = (CounterCard)match.CardLibrary.Instantiate(CommandTestKit.CounterCardId);
        await activeCounter.LoadAsync(playerA);
        playerA.Hand.Add(activeCounter);
        var idleCounter = (CounterCard)match.CardLibrary.Instantiate(CommandTestKit.CounterCardId);
        await idleCounter.LoadAsync(playerA);
        playerA.Hand.Add(idleCounter);

        await match.EndTurn();
        await match.EndTurn();

        // 一张先激活（覆盖「取消」入口的终局拒绝）：回合 3＝A 的己方回合（2 点、反制费 1）。
        var activated = await match.PlayManager.UseCounterAsync(activeCounter);
        Assert.Equal(PlayResultStatus.Success, activated.Status);

        var striker = await PrepareHqStrikerAsync(match);
        await StrikeHqAsync(match, bridge, striker);
        var pointsAtEnd = playerA.Points;
        var beginsAtEnd = bridge.Begins.Count;

        // ③反制激活＝拒绝（未激活的一张）；状态/点数/交互零副作用。
        var idleResult = await match.PlayManager.UseCounterAsync(idleCounter);
        Assert.Equal(PlayResultStatus.Failed, idleResult.Status);
        Assert.Equal(PlayFailureReason.GameEnded, idleResult.FailureReason);
        Assert.False(idleCounter.GetData<CounterActivationData>().IsActive);

        // ③反制取消＝亦拒绝（已激活的一张）：保持激活、点数不变（激活/取消均为动作入口）。
        var cancelResult = await match.PlayManager.UseCounterAsync(activeCounter);
        Assert.Equal(PlayResultStatus.Failed, cancelResult.Status);
        Assert.Equal(PlayFailureReason.GameEnded, cancelResult.FailureReason);
        Assert.True(activeCounter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(pointsAtEnd, playerA.Points);
        Assert.Equal(beginsAtEnd, bridge.Begins.Count);
    }

    [Fact]
    public async Task Targeter_Start_Is_Rejected_After_End()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var striker = await PrepareHqStrikerAsync(match);
        await StrikeHqAsync(match, bridge, striker);
        var beginsAtEnd = bridge.Begins.Count;
        var collectsAtEnd = bridge.CollectCalls.Count;

        // ⑥Targeter 发起（新入队）＝即时失败、零副作用（不进队列、不调桥接）。
        var targeter = match.TargeterManager.CreateTargeter();
        var result = await targeter.Targeting();

        Assert.Equal(TargeterStatus.Failed, result.Status);
        Assert.Equal(TargeterFailureReason.GameEnded, result.Reason);
        Assert.Null(result.Outcome);
        Assert.Equal(beginsAtEnd, bridge.Begins.Count);
        Assert.Equal(collectsAtEnd, bridge.CollectCalls.Count);
    }

    [Fact]
    public async Task Update_Stream_Frozen_And_Read_Surface_Stable_After_End()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        var handUnit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var striker = await PrepareHqStrikerAsync(match);
        await StrikeHqAsync(match, bridge, striker);

        using var recorder = new UpdateRecorder(match.Engine); // 终局后挂接：验证更新流不再增长
        var turnNumber = match.TurnNumber;
        var points = playerA.Points;

        // 终局后尝试各类被拒动作（回合推进抛错；指挥 / 预打出被拒）——不产生任何新更新（更新流冻结）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.EndTurn());
        var commandResult = await match.CommandManager.BeginCommandAsync(unit);
        Assert.Equal(CommandFailureReason.GameEnded, commandResult.FailureReason);
        var prePlay = await match.PlayManager.BeginUnitPrePlayAsync(handUnit);
        Assert.Equal(PlayFailureReason.GameEnded, prePlay.FailureReason);

        // "其后所有效果不再处理"的可观测口径：不再产生任何新更新（更新流不再增长）。
        Assert.Empty(recorder.Updates);
        Assert.Equal(turnNumber, match.TurnNumber);
        Assert.Equal(points, playerA.Points);

        // 只读查询面保持可用且稳定：连续两次查询状态 / 胜者 / 玩家与 HQ / 战场 / 管理器——结果一致、可读。
        var state1 = match.State;
        var winner1 = match.Winner;
        var hq1 = match.Players[1].HqHealth;
        var frontOccupant1 = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        var players1 = match.Players.Count;
        var manager1 = match.CommandManager;

        var state2 = match.State;
        var winner2 = match.Winner;
        var hq2 = match.Players[1].HqHealth;
        var frontOccupant2 = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        var players2 = match.Players.Count;
        var manager2 = match.CommandManager;

        Assert.Equal(state1, state2);
        Assert.Equal(MatchState.Ended, state1);
        Assert.Same(winner1, winner2);
        Assert.Same(playerA, winner1);
        Assert.Equal(hq1, hq2);
        Assert.Equal(0, hq1);
        Assert.Same(frontOccupant1, frontOccupant2);
        Assert.Equal(players1, players2);
        Assert.Same(manager1, manager2);

        // 查询面（可用性聚合判定）保持可用且稳定——终局不改变其纯查询语义（无副作用、与流程同源）。
        var availability1 = match.CommandManager.GetCommandAvailability(unit);
        var availability2 = match.CommandManager.GetCommandAvailability(unit);
        Assert.Equal(availability1.Move.Candidates, availability2.Move.Candidates);
        Assert.Empty(recorder.Updates); // 查询无副作用：更新流保持冻结
    }
}
