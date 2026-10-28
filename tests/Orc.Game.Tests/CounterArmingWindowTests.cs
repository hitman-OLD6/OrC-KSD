using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 反制武装窗口验收（R1）：①己方指令不被己方反制拦截（归属过滤）；②只在对手回合生效（回合门禁）；
/// ③己方回合开始自动解除（<c>counter.disarmed</c>、不退点、不消耗、卡留手牌）；④解除后可重新激活；
/// ⑤主动取消仍退点且不发解除信号。跨方命中与一次性消耗的既有语义由 <see cref="P4OrderFlowTests"/> 守卫，本类不重复。
/// </summary>
public class CounterArmingWindowTests
{
    // ---------- R1.1 归属过滤：己方指令不被己方反制拦截 ----------


    [Fact]
    public async Task Unknown_Order_Ownership_Is_Still_Countered()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // B 在自己回合武装、覆盖 A 的下个回合（窗口语义的正常路径）。
        await match.EndTurn();
        Assert.Same(playerB, match.CurrentPlayer);
        var counter = await CreateArmedCounterAsync(match, playerB);

        await match.EndTurn();
        Assert.Same(playerA, match.CurrentPlayer);

        // 裸卡（无归属）＝归属未知：不做归属过滤，忠实于窗口语义——照常被反制。
        var source = new Card(match.Engine, "流程源");
        var target = new Card(match.Engine, "流程标");
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);

        Assert.True(counter.IsConsumed);
    }

    [Fact]
    public async Task Unreachable_Turn_Context_Falls_Back_To_Ownership_Filter()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        await match.EndTurn();
        await match.EndTurn();
        Assert.Same(playerA, match.CurrentPlayer);

        var counter = await CreateArmedCounterAsync(match, playerA);

        // 回合上下文不可达（提供器返回 null）＝门禁放行：此时只能靠归属过滤兜底。
        counter.TurnPlayerProvider = () => null;

        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var result = await match.PlayManager.PlayOrderAsync(order);

        // 归属过滤照常生效：己方反制不拦己方指令。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(counter.IsConsumed);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);

        // 上下文抛「回合未开始」同样视为不可达（不冒泡、不中断指令流）——
        // 直接驱动 OrderFlow（点数已在上一张指令用尽，玩家动作入口不再可达，但门禁行为与入口无关）。
        counter.TurnPlayerProvider = () => throw new InvalidOperationException("回合尚未开始（当前行动方未定）。");
        var secondOrder = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId, toHand: false);
        await match.Engine.OrderFlow.ExecuteAsync(secondOrder, (ICastAction?)null, null, 1);

        Assert.False(counter.IsConsumed);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
    }

    [Fact]
    public async Task Unknown_Order_In_Own_Turn_Is_Blocked_By_Turn_Gate()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // A 的回合内武装：窗口只覆盖对手回合。
        var counter = await CreateArmedCounterAsync(match, playerA);

        // 裸卡（归属未知）＋ 己方回合：回合门禁先于归属过滤拦住——不命中、不消耗。
        // 与 Unknown_Order_Ownership_Is_Still_Countered 互为反例，共同证明两道门各自独立成立。
        var source = new Card(match.Engine, "流程源");
        var target = new Card(match.Engine, "流程标");
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);

        Assert.False(counter.IsConsumed);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.Single(counter.RegisteredEffectHandlerNames);
    }

    // ---------- R1.3 自动解除：不退点、不消耗、发信号 ----------

    [Fact]
    public async Task Turn_Start_Disarms_Without_Refund_And_Emits_CounterDisarmed()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var counter = await CreateArmedCounterAsync(match, playerA);
        var pointsWhileArmed = playerA.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        // 直接发 turn.start（归属＝A）：跳过回合结算，才能把「不退点」与结算增量分开观测。
        await match.Engine.Emit(GameUpdates.TurnStart, new Dictionary<string, object?>
        {
            [GameUpdates.PayloadPlayer] = playerA,
            [GameUpdates.PayloadTurnNumber] = 2,
        });

        // 解除序列落定：未激活、handler 已注销、卡仍在手牌、已扣的点不返还。
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Empty(counter.RegisteredEffectHandlerNames);
        Assert.Contains(counter, playerA.Hand);
        Assert.False(counter.IsConsumed);
        Assert.Equal(pointsWhileArmed, playerA.Points);

        // 信号在解除之后发出：读者在信号时点即读到「未激活」。
        var disarm = Assert.Single(recorder.Updates, update => update.Type == GameUpdates.CounterDisarmed);
        Assert.Same(counter, disarm.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, disarm.Payload![GameUpdates.PayloadPlayer]);
        Assert.Same(counter, disarm.Payload![GameUpdates.PayloadSource]);
        Assert.Equal("turn-start", disarm.Payload!["Reason"]);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
    }

    [Fact]
    public async Task Disarmed_Counter_Can_Be_Reactivated_And_Charged_Again()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        await match.EndTurn();
        await match.EndTurn();
        var counter = await CreateArmedCounterAsync(match, playerA);

        // 跨过对手回合 → 回到 A 的回合：窗口到期、自动解除（不消耗）。
        await match.EndTurn();
        await match.EndTurn();
        Assert.Same(playerA, match.CurrentPlayer);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.False(counter.IsConsumed);
        Assert.Contains(counter, playerA.Hand);

        // 解除是本窗口的终点、不是卡的一次性结局：可再次武装（再扣一次点）。
        var pointsBefore = playerA.Points;
        var reactivated = await match.PlayManager.UseCounterAsync(counter);

        Assert.Equal(PlayResultStatus.Success, reactivated.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(pointsBefore - counter.Modifiers.GetEffectiveValue(CardStatFields.DeployCost), playerA.Points);
        Assert.Single(counter.RegisteredEffectHandlerNames);
    }

    // ---------- 主动取消：既有语义不回归 ----------

    [Fact]
    public async Task Manual_Cancel_Still_Refunds_And_Emits_No_Disarm()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        await match.EndTurn();
        await match.EndTurn();
        var pointsBefore = playerA.Points;
        var counter = await CreateArmedCounterAsync(match, playerA);
        Assert.Equal(pointsBefore - 1, playerA.Points);

        using var recorder = new UpdateRecorder(match.Engine);
        var cancelled = await match.PlayManager.UseCounterAsync(counter);

        // 主动取消＝按实扣额退点；不发解除信号（退点撤销不属「窗口到期」）。
        Assert.Equal(PlayResultStatus.Success, cancelled.Status);
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Equal(pointsBefore, playerA.Points);
        Assert.Empty(counter.RegisteredEffectHandlerNames);
        Assert.DoesNotContain(GameUpdates.CounterDisarmed, recorder.Types);
    }

    // ---------- 夹具 ----------

    /// <summary>实例化并武装一张轻反制（停牌效果：遇敌方指令即 Stop）——武装须在归属玩家自己的回合内。</summary>
    private static async Task<CounterCard> CreateArmedCounterAsync(Match match, Player owner)
    {
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, owner, PlayChainTestKit.CounterCheapId);
        counter.AddEffectHandler("反制", (_, ctx, _) =>
        {
            ctx.Stop();
            return Task.CompletedTask;
        });

        var result = await match.PlayManager.UseCounterAsync(counter);
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        return counter;
    }

    /// <summary>主动效果施放视图（OrderFlow 只传 Source/Target/Amount 三键）。</summary>
    [ContextView]
    public class WindowCastView
    {
        [Optional]
        [Read]
        public virtual Card? Source { get; set; }

        [Optional]
        [Read]
        public virtual int Amount { get; set; }
    }

}
