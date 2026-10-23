using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 核对锁定（后置项 D/F「不做」的防回归锁定）：①普通单位指挥 targeter 无后撤/横移路径——
/// 移动候选与交互允许集永不含支援线内部槽位（负面/不变式断言）；前线单位无移动候选；
/// ②反制不做激活区——激活/取消前后卡保持手牌位置不变（保序集合内位置不变性）；
/// ③反制可重复激活——连续多轮激活↔取消，每轮费用「激活扣一次、取消退一次」、点数对账一致（无重复扣退）。
/// </summary>
public class CommandLockInTests
{
    [Fact]
    public async Task Move_Candidates_Never_Contain_Support_Line_Interior_Slots()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 己方占位（无敌人）
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(mover);
        var aSupport = match.Battlefield.PlayerASupportLine;
        var bSupport = match.Battlefield.PlayerBSupportLine;
        var front = match.Battlefield.FrontLine;

        var candidates = match.CommandManager.GetCommandAvailability(mover).Move.Candidates;

        // 正面：移动候选＝前线全部插入空隙（有序列表模型：N 元素 → N+1 个落位请求；无横移——已占位置不会移除落点）。
        Assert.Equal(front.GetInsertionGaps().Select(gap => gap.Ref), candidates);
        Assert.All(candidates, reference => Assert.Same(front, Assert.IsType<FormationGap>(reference.Value).Line));

        // 负面（无后撤锁定）：候选不含任何支援线插入点——己方（含 HQ 邻位/内部）与敌方支援线全部。
        foreach (var line in new[] { aSupport, bSupport })
        {
            Assert.DoesNotContain(candidates, reference =>
                line.GetInsertionGaps().Any(gap => ReferenceEquals(gap, reference.Value)));
        }
    }

    [Fact]
    public async Task Command_Targeter_Never_Offers_Support_Line_Interior_Slots()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 己方占位
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 敌方支援线（步兵不可达＝不入攻击候选）
        CommandTestKit.Activate(mover);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var front = match.Battlefield.FrontLine;

        var task = match.CommandManager.BeginCommandAsync(mover);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 交互允许集＝移动候选（前线插入空隙）——永不含支援线操作候选（无后撤/横移路径的交互面锁定）。
        Assert.Equal(front.GetInsertionGaps().Select(gap => gap.Ref), description.AllowedTargets);
        foreach (var line in new[] { match.Battlefield.PlayerASupportLine, match.Battlefield.PlayerBSupportLine })
        {
            Assert.DoesNotContain(description.AllowedTargets, reference =>
                line.GetInsertionGaps().Any(gap => ReferenceEquals(gap, reference.Value)));
        }

        responder.Cancel(description.RequestId);
        var result = await task;
        Assert.Equal(CommandResultStatus.Cancelled, result.Status); // 取消＝零副作用（收尾）
    }

    [Fact]
    public async Task Front_Line_Unit_Has_No_Move_Candidates_And_No_Same_Line_Products()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(unit);
        var front = match.Battlefield.FrontLine;

        var report = match.CommandManager.GetCommandAvailability(unit);

        // 前线单位无移动候选（仅推进、无后撤/横移；无同线移动产物）——负面断言：不可用且候选空。
        Assert.False(report.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, report.Move.BlockReason);
        Assert.Empty(report.Move.Candidates);
        // 无同线移动产物：前线自身的插入点也不得成为候选（有序列表下"格子"无对应物，改断言空隙面）。
        Assert.DoesNotContain(report.Move.Candidates, reference =>
            front.GetInsertionGaps().Any(gap => ReferenceEquals(gap, reference.Value)));
    }

    [Fact]
    public async Task Counter_Card_Keeps_Hand_Position_Across_Activation_Cycle()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCheapId);
        await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCheapId);
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(match, player, PlayChainTestKit.CounterCheapId);
        await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCheapId);
        var indexBefore = player.Hand.ToList().IndexOf(counter);
        Assert.True(indexBefore >= 0); // 卡在手牌中（起手 4 ＋ 手动装载 4；位置＝装载序）
        var countBefore = player.Hand.Count;

        // 激活：卡保持手牌位置不变（不做激活区——无移出/移入行为）。
        var activate = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, activate.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(countBefore, player.Hand.Count);
        Assert.Same(counter, player.Hand[indexBefore]);
        Assert.Equal(indexBefore, player.Hand.ToList().IndexOf(counter));

        // 取消：仍在原位置（取消后去向＝留在手牌原位置）。
        var deactivate = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, deactivate.Status);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(countBefore, player.Hand.Count);
        Assert.Same(counter, player.Hand[indexBefore]);
        Assert.Equal(indexBefore, player.Hand.ToList().IndexOf(counter));

        // 再激活：位置不变性在重复翻转下保持。
        var reactivate = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, reactivate.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(countBefore, player.Hand.Count);
        Assert.Equal(indexBefore, player.Hand.ToList().IndexOf(counter));
    }

    [Fact]
    public async Task Counter_Reactivation_Cycle_Fee_Accounting_Is_Exact()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1 先手（点数 1）
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(match, player, PlayChainTestKit.CounterCheapId);

        // 可重复激活（无次数上限）：连续 5 轮激活↔取消；每轮「激活扣一次、取消退一次」，点数对账一致（无重复扣退）。
        for (var round = 1; round <= 5; round++)
        {
            var activate = await match.PlayManager.UseCounterAsync(counter);
            Assert.Equal(PlayResultStatus.Success, activate.Status);
            Assert.True(counter.GetData<CounterActivationData>().IsActive);
            Assert.Equal(0, player.Points); // 激活：恰扣一次

            var deactivate = await match.PlayManager.UseCounterAsync(counter);
            Assert.Equal(PlayResultStatus.Success, deactivate.Status);
            Assert.False(counter.GetData<CounterActivationData>().IsActive);
            Assert.Equal(1, player.Points); // 取消：恰退一次（回到初值＝无漂移）
        }

        Assert.Equal(1, player.Points); // 全程无重复扣退
    }
}
