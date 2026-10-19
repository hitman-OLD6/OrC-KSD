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

    [Fact]
    public async Task Order_Runs_All_Active_Effects_In_Registration_Order_With_Single_CardPlayed()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCheapId);

        var ran = new List<string>();
        order.AddEffect(new P4CastEffect("甲", (view, ctx, ct) => { ran.Add("甲"); return Task.CompletedTask; }));
        order.AddEffect(new P4CastEffect("乙", (view, ctx, ct) => { ran.Add("乙"); return Task.CompletedTask; }));
        order.AddEffect(new P4CastEffect("丙", (view, ctx, ct) => { ran.Add("丙"); return Task.CompletedTask; }));

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.PlayManager.PlayOrderAsync(order);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "甲", "乙", "丙" }, ran); // 多主动效果全部执行（禁 FirstOrDefault）
        Assert.Equal(1, recorder.Types.Count(type => type == GameUpdates.CardPlayed)); // 恰一次
        Assert.Equal(
            new[] { GameUpdates.CardPlayed, GameUpdates.PointChanged }, // 宣告（Resolve/0）→ 收尾扣费（Finalize/100）
            recorder.Types);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(order, player.Hand);
    }

    // ---------- ② 反制命中：Stop＝效果不触发、照常扣费/离手 ----------

    [Fact]
    public async Task Counter_Stop_Skips_Effects_But_Still_Charges_And_Discards_Order()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 反制只能在己方回合激活：B 回合激活，之后 A 回合施放。
        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        counter.AddEffectHandler("反制", (view, ctx, ct) => { ctx.Stop(); return Task.CompletedTask; });
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(counter)).Status);

        await match.EndTurn();
        Assert.Same(playerA, match.CurrentPlayer);

        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var effectRan = false;
        order.AddEffect(new P4CastEffect("不应执行", (view, ctx, ct) => { effectRan = true; return Task.CompletedTask; }));

        var pointsBeforeB = playerB.Points;
        var pointsBeforeA = playerA.Points;
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(order);

        // 指令照常扣费、照常离手；效果不触发。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(effectRan);
        Assert.Equal(pointsBeforeA - 1, playerA.Points);
        Assert.DoesNotContain(order, playerA.Hand);

        // 宣告点在 Resolve/0——被反制（Counter 段 Stop）时 Resolve 整体不执行，故本次不发 card.played（视为未成功打出）。
        Assert.DoesNotContain(GameUpdates.CardPlayed, recorder.Types);
        // 扣费恰一次（Finalize 只跑一次）。
        Assert.Equal(1, recorder.Types.Count(type => type == GameUpdates.PointChanged));

        // 反制一次性消耗：移出手牌 + 弃置信号 + 未激活 + 不退费。
        Assert.DoesNotContain(counter, playerB.Hand);
        Assert.Contains(GameUpdates.CardDiscarded, recorder.Types);
        Assert.Equal(pointsBeforeB, playerB.Points); // 不退费
        Assert.False(counter.GetData<CounterActivationData>().IsActive);
        Assert.Empty(counter.RegisteredEffectHandlerNames);
        Assert.True(counter.IsConsumed);

        // 已消耗：不再对后续指令生效（第二张指令正常结算，且不再产生弃置）。
        recorder.Clear();
        var second = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var secondRan = false;
        second.AddEffect(new P4CastEffect("第二次", (view, ctx, ct) => { secondRan = true; return Task.CompletedTask; }));
        var secondResult = await match.PlayManager.PlayOrderAsync(second);

        Assert.Equal(PlayResultStatus.Success, secondResult.Status);
        Assert.True(secondRan);
        Assert.DoesNotContain(GameUpdates.CardDiscarded, recorder.Types);
        Assert.DoesNotContain(counter, playerB.Hand);
    }

    // ---------- ③ 反制命中：Interrupt＝牌留手、不扣费 ----------

    [Fact]
    public async Task Counter_Interrupt_Leaves_Order_In_Hand_And_Uncharged()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        counter.AddEffectHandler("强反制", (view, ctx, ct) => { ctx.Interrupt(); return Task.CompletedTask; });
        await match.PlayManager.UseCounterAsync(counter);

        await match.EndTurn();
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var effectRan = false;
        order.AddEffect(new P4CastEffect("不应执行", (view, ctx, ct) => { effectRan = true; return Task.CompletedTask; }));

        var pointsBefore = playerA.Points;
        var result = await match.PlayManager.PlayOrderAsync(order);

        Assert.Equal(PlayResultStatus.Cancelled, result.Status); // Interrupt：两阶段全停
        Assert.False(effectRan);
        Assert.Equal(pointsBefore, playerA.Points); // 不扣费
        Assert.Contains(order, playerA.Hand);       // 牌留手
        Assert.True(counter.IsConsumed);            // Interrupt 同样算命中（一次性消耗）
        Assert.DoesNotContain(counter, playerB.Hand);
    }

    // ---------- ④ 未命中：保持激活、不消耗 ----------

    [Fact]
    public async Task Counter_Miss_Keeps_Activation_And_Does_Not_Consume()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        counter.AddEffectHandler("观察", (view, ctx, ct) => Task.CompletedTask); // 不 Stop＝未命中
        await match.PlayManager.UseCounterAsync(counter);

        await match.EndTurn();
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var effectRan = false;
        order.AddEffect(new P4CastEffect("照常结算", (view, ctx, ct) => { effectRan = true; return Task.CompletedTask; }));

        var result = await match.PlayManager.PlayOrderAsync(order);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.True(effectRan);
        Assert.True(counter.GetData<CounterActivationData>().IsActive);
        Assert.False(counter.IsConsumed);
        Assert.Contains(counter, playerB.Hand);
        Assert.Equal(new[] { "观察" }, counter.RegisteredEffectHandlerNames);
    }

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

    [Fact]
    public async Task OrderFlow_Requires_Only_Source()
    {
        var engine = new LogicEngine();
        var source = new Card(engine, "来源");

        var empty = await engine.OrderFlow.ExecuteAsync(
            source, Array.Empty<ICastAction>(), target: null, amount: 0);
        var nullOrder = await engine.OrderFlow.ExecuteAsync(
            source, order: null, target: null, amount: 0);

        Assert.NotNull(empty);
        Assert.NotNull(nullOrder);
        Assert.False(empty.Interrupted);

        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.OrderFlow.ExecuteAsync(
            null!, Array.Empty<ICastAction>(), target: null, amount: 0));
    }

    // ---------- ⑧ 缺口修补：反制与宣告语义、目标传递 ----------

    [Fact]
    public async Task Countered_Order_Skips_Announcement_But_Charges_Once_And_Returns_Success()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        counter.AddEffectHandler("反制", (view, ctx, ct) => { ctx.Stop(); return Task.CompletedTask; });
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(counter)).Status);

        await match.EndTurn();
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var effectRan = false;
        order.AddEffect(new P4CastEffect("不应执行", (view, ctx, ct) => { effectRan = true; return Task.CompletedTask; }));

        var pointsBefore = playerA.Points;
        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.PlayManager.PlayOrderAsync(order);

        // 被反制＝未成功打出：不发 card.played（0 次），但仍是成功结果（照常扣费/离手）。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(0, recorder.Types.Count(type => type == GameUpdates.CardPlayed));
        // 扣费恰一次 + 离手。
        Assert.Equal(1, recorder.Types.Count(type => type == GameUpdates.PointChanged));
        Assert.Equal(pointsBefore - 1, playerA.Points);
        Assert.DoesNotContain(order, playerA.Hand);
        // 只是效果不触发（现有被动效果挂在 card.played 上，故同样不触发）。
        Assert.False(effectRan);
    }

    [Fact]
    public async Task Normal_Order_Announces_Once_Before_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, player, PlayChainTestKit.CommandCheapId);

        using var recorder = new UpdateRecorder(match.Engine);
        var playedSeenInEffect = false;
        var playedCountAtFirstEffect = -1;
        order.AddEffect(new P4CastEffect("观察", (view, ctx, ct) =>
        {
            playedSeenInEffect = recorder.Types.Contains(GameUpdates.CardPlayed);
            playedCountAtFirstEffect = recorder.Types.Count(type => type == GameUpdates.CardPlayed);
            return Task.CompletedTask;
        }));

        var result = await match.PlayManager.PlayOrderAsync(order);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.True(playedSeenInEffect);                 // 宣告先于效果
        Assert.Equal(1, playedCountAtFirstEffect);       // 效果执行时已恰宣告一次
        Assert.Equal(1, recorder.Types.Count(type => type == GameUpdates.CardPlayed)); // 全程恰一次
    }

    [Fact]
    public async Task PrePlay_Captured_Target_Reaches_OrderFlow_And_Counter_Adapter()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 反制：观察适配视图目标（不 Stop＝未命中，保持激活以覆盖两次施放）。
        await match.EndTurn();
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, playerB, PlayChainTestKit.CounterCheapId);
        var counterTargets = new List<Card?>();
        counter.AddEffectHandler("观察目标", (view, ctx, ct) =>
        {
            counterTargets.Add(view.Target);
            return Task.CompletedTask;
        });
        await match.PlayManager.UseCounterAsync(counter);

        await match.EndTurn();
        var target = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, playerB, PlayChainTestKit.UnitCheapId, toHand: false);

        // ① 捕获列表（首个非卡值 + 卡引用）→ 取第一个可解析元素 → 主动效果与反制适配视图都可读。
        var order = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var effectTarget = (Card?)null;
        var effectSawTarget = false;
        order.AddEffect(new P4CastEffect("观察目标", (view, ctx, ct) =>
        {
            effectTarget = view.Target;
            effectSawTarget = view.Target is not null;
            return Task.CompletedTask;
        }));
        order.AddPrePlayHandler("捕获目标", (view, ctx, ct) =>
        {
            view.CaptureBox!.Capture("不是卡");
            view.CaptureBox.Capture(target.Ref); // 多值捕获＝object?[]；解析取第一个可解析元素
            return Task.CompletedTask;
        });

        var first = await match.PlayManager.PlayOrderAsync(order);

        Assert.Equal(PlayResultStatus.Success, first.Status);
        Assert.True(effectSawTarget);
        Assert.Same(target, effectTarget);
        Assert.Same(target, Assert.Single(counterTargets)); // CardTriggerView.Target 已适配

        // ② 捕获非卡值 → 目标为 null；反制 handler 拿到 null 而不是崩溃，施放不阻断。
        counterTargets.Clear();
        var second = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(
            match, playerA, PlayChainTestKit.CommandCheapId);
        var secondEffectRan = false;
        second.AddEffect(new P4CastEffect("照常执行", (view, ctx, ct) => { secondEffectRan = true; return Task.CompletedTask; }));
        second.AddPrePlayHandler("捕获非卡值", (view, ctx, ct) =>
        {
            view.CaptureBox!.Capture("不是卡");
            return Task.CompletedTask;
        });

        var secondResult = await match.PlayManager.PlayOrderAsync(second);

        Assert.Equal(PlayResultStatus.Success, secondResult.Status);
        Assert.True(secondEffectRan);
        Assert.Null(Assert.Single(counterTargets));
    }

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

    private sealed class P4CastEffect : ActiveEffect<P4CastView>
    {
        public P4CastEffect(string name, Func<P4CastView, Context, CancellationToken, Task> handler)
            : base(name, castEvents: new[] { new TriggerEvent<P4CastView>("施放", handler) })
        {
        }
    }
}
