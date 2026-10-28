using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P4 验收（S12/S13/S14）：指令归一后的施放链、两阶段收尾、反制注入与一次性消耗。
/// <para>
/// 覆盖：①全部主动效果按登记序执行、<c>card.played</c> 恰一次且在效果之前；②反制命中（Stop）＝宣告仍发生、效果不触发、
/// 照常扣费/离手；③反制命中（Interrupt）＝牌留手/不扣费；④未命中不消耗；⑤首张 Stop 后第二张保持激活；
/// ⑥反制消耗＝移出手牌 + <c>card.discarded</c> + 注销 + 未激活 + 不退费，且不再对后续指令生效；
/// ⑦适配视图可读反制卡/指令/目标/数值；⑧内核只要求 source 非空（target 可空、orders 可空）；
/// ⑨预打出捕获的目标随流程传递（主动效果与反制适配视图均可读；非卡值＝null、不阻断）。
/// </para>
/// </summary>
public class P4OrderFlowTests
{
    // ---------- ① 指令施放链 ----------


    // ---------- ② 反制命中：Stop＝效果不触发、照常扣费/离手 ----------


    // ---------- ③ 反制命中：Interrupt＝牌留手、不扣费 ----------


    // ---------- ④ 未命中：保持激活、不消耗 ----------


    // ---------- ⑤ 多张反制：首个 Stop 后第二张保持激活 ----------

    [Fact]
    public async Task Second_Active_Counter_Stays_Active_When_First_Stops()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await match.EndTurn();
        playerB.Points = 2; // 两张廉价反制（各 1 点）

        var first = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        var second = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);

        var ordered = new List<string>();
        first.AddEffectHandler("第一张", (view, ctx, ct) => { ordered.Add("第一张"); ctx.Stop(); return Task.CompletedTask; });
        second.AddEffectHandler("第二张", (view, ctx, ct) => { ordered.Add("第二张"); return Task.CompletedTask; });

        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(first)).Status);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(second)).Status);

        await match.EndTurn();
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var result = await match.PlayManager.PlayOrderAsync(order);

        // 按激活序执行：第一张 Stop 终止本级后续 → 第二张未执行、保持激活、不消耗。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "第一张" }, ordered);
        Assert.True(first.IsConsumed);
        Assert.False(second.IsConsumed);
        Assert.True(second.GetData<CounterActivationData>().IsActive);
        Assert.Contains(second, playerB.Hand);
    }

    // ---------- ⑥ 适配视图：反制卡/指令/目标/数值 ----------

    [Fact]
    public async Task Counter_Adapter_View_Exposes_Counter_Order_Target_And_Amount()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);

        object? seenCard = null;
        object? seenPlayer = null;
        Card? seenOrder = null;
        Card? seenTarget = null;
        object? seenArgument = null;
        counter.AddEffectHandler("观察适配视图", (view, ctx, ct) =>
        {
            seenCard = view.Card;
            seenPlayer = view.Player;
            seenOrder = view.Source;
            seenTarget = view.Target;
            seenArgument = view.Argument;
            return Task.CompletedTask;
        });
        await match.PlayManager.UseCounterAsync(counter);

        await match.EndTurn();
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var target = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, playerB, PlayChainTestKit.UnitCheapId, toHand: false);

        // 直接经动作入口施放（PlayManager 入口无目标参数；本用例验证视图字段）。
        var outcome = await new Orc.Foundation.Actions.OrderAction(match.Engine.OrderFlow).ExecuteAsync(order, target, 4);

        Assert.Equal(ActionOutcomeKind.Applied, outcome.Targets[0].Outcome);
        Assert.Same(counter, seenCard);
        Assert.Same(playerB, seenPlayer);
        Assert.Same(order, seenOrder);
        Assert.Same(target, seenTarget); // CardTriggerView.Target 加性字段
        Assert.Equal(4, seenArgument);
    }

    // ---------- ⑦ 内核契约：只要求 source 非空 ----------


    // ---------- ⑧ 缺口修补：反制与宣告语义、目标传递 ----------




    // ---------- fixtures ----------

    /// <summary>主动效果施放视图（OrderFlow 只传 Source/Target/Amount 三键）。</summary>
    [ContextView]
    public class P4CastView
    {
        [Optional]
        [Read]
        public virtual Card? Source { get; set; }

        [Optional]
        [Read]
        public virtual Card? Target { get; set; }

        [Optional]
        [Read]
        public virtual int Amount { get; set; }
    }

}
