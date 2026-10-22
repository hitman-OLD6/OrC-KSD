using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收⑤②（指令；P4 指令归一后）：默认空效果集照常打出（card.played/扣费/离手）、
/// 预打出捕获箱（单引用/列表承载演练）、targeter 形态演练（预打出段内执行产出引用并可被消费）、
/// 主动效果集（登记序、card.played 先于效果、收尾在效果之后、异常隔离）、
/// 预打出取消（截断打出）、预打出拒绝（点数不足；handler 不执行）、复验失败（点数漂移；零副作用）。
/// </summary>
public class PlayChainCommandTests
{
    [Fact]
    public async Task Command_Plays_With_Default_Empty_Handler_Set()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 默认空集不改变流程：照发 card.played（恰一次）、照扣费（收尾；E1-25 后续：发 point.changed）、离手。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { GameUpdates.CardPlayed, GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(command, player.Hand);
    }

    [Fact]
    public async Task Command_Active_Effects_Run_Between_Played_And_Finalize()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var targetEntity = new Entity("被捕获引用");
        using var recorder = new UpdateRecorder(match.Engine);
        var playedSeenInEffect = false;
        int? pointsAtEffect = null;
        Card? sourceSeen = null;

        command.AddPrePlayHandler("捕获单引用", (view, ctx, ct) =>
        {
            view.CaptureBox!.Capture(targetEntity.Ref);
            Assert.Same(targetEntity.Ref, Assert.IsType<Ref<Entity>>(view.CaptureBox.Result));
            return Task.CompletedTask;
        });
        command.AddEffect(new TestCastEffect("消费捕获", (view, ctx, ct) =>
        {
            sourceSeen = view.Source;
            playedSeenInEffect = recorder.Types.Contains(GameUpdates.CardPlayed);
            pointsAtEffect = player.Points;
            return Task.CompletedTask;
        }));

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Same(command, sourceSeen); // OrderFlow 向主动效果传 Source
        // 固定顺序：card.played（Resolve/0）先于主动效果（Resolve/100）；收尾扣费（Finalize/100）在效果之后。
        Assert.True(playedSeenInEffect);
        Assert.Equal(1, pointsAtEffect);
        Assert.Equal(0, player.Points);
    }

    [Fact]
    public async Task Command_Capture_Box_Accumulates_Multiple_Values_As_List()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var first = new Entity("甲");
        var second = new Entity("乙");
        object? observed = null;

        command.AddPrePlayHandler("捕获两个", (view, ctx, ct) =>
        {
            view.CaptureBox!.Capture(first.Ref);
            view.CaptureBox.Capture(second.Ref);
            observed = view.CaptureBox.Result;
            return Task.CompletedTask;
        });

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 多值捕获＝列表承载（object?[]；提交序）。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        var list = Assert.IsType<object?[]>(observed);
        Assert.Equal(new object?[] { first.Ref, second.Ref }, list);
    }

    [Fact]
    public async Task Command_Targeter_Request_Executed_In_PrePlay_Phase()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var payload = new Entity("延迟选择的产出");
        bridge.CollectScript = _ => Task.FromResult(TargeterTestKit.Candidates(payload.Ref));
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();

        // P4：主动效果不再接收预打出捕获结果；交互（targeter）在预打出段内完成。
        Ref<Entity>? consumed = null;
        command.AddPrePlayHandler("执行请求", async (view, ctx, ct) =>
        {
            var request = new Func<ITargeterFlow, Task<TargeterResult>>(async f =>
            {
                var step = await f.Step(
                    SelectorTemplates.TargetPoint,
                    new ReferenceSetParameter(new[] { payload.Ref }, 1, 1));
                if (step.IsOk)
                {
                    consumed = step.Value;
                }

                return step.IsOk ? TargeterResult.Ok() : TargeterResult.FromSelectorFailure(step.Failure);
            });
            view.CaptureBox!.Capture(request);
            await match.TargeterManager.RunAsync(request);
        });

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Same(payload.Ref, consumed);
    }

    [Fact]
    public async Task Command_Active_Effects_Trigger_In_Registration_Order()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var order = new List<string>();

        command.AddEffect(new TestCastEffect("A", (view, ctx, ct) => { order.Add("A"); return Task.CompletedTask; }));
        command.AddEffect(new TestCastEffect("B", (view, ctx, ct) => { order.Add("B"); return Task.CompletedTask; }));
        command.AddEffect(new TestCastEffect("C", (view, ctx, ct) => { order.Add("C"); return Task.CompletedTask; }));

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 多主动效果按登记序**全部**执行（禁 FirstOrDefault）。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "A", "B", "C" }, order);
    }

    [Fact]
    public async Task Command_Active_Effect_Exception_Isolated_And_Play_Completes()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var order = new List<string>();

        command.AddEffect(new TestCastEffect("A", (view, ctx, ct) => { order.Add("A"); return Task.CompletedTask; }));
        command.AddEffect(new TestCastEffect("B", (view, ctx, ct) => throw new InvalidOperationException("handler 爆炸")));
        command.AddEffect(new TestCastEffect("C", (view, ctx, ct) => { order.Add("C"); return Task.CompletedTask; }));

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 异常＝沿用引擎异常隔离（记录并继续）：不阻断后续效果与收尾；打出照常完成。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "A", "C" }, order);
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(command, player.Hand);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Source.Contains("B") && entry.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task Command_PrePlay_Handler_Exception_Isolated_And_Play_Continues()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var ran = new List<string>();
        object? observed = null;
        command.AddPrePlayHandler("甲", (view, ctx, ct) => { ran.Add("甲"); return Task.CompletedTask; });
        command.AddPrePlayHandler("乙", (view, ctx, ct) => throw new InvalidOperationException("预打出 handler 爆炸"));
        command.AddPrePlayHandler("丙", (view, ctx, ct) =>
        {
            ran.Add("丙");
            view.CaptureBox!.Capture("丙的捕获");
            observed = view.CaptureBox.Result;
            return Task.CompletedTask;
        });

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 预打出 handler 异常＝沿用引擎异常隔离（记录并继续）：不阻断登记序后续 handler 与打出段。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "甲", "丙" }, ran);
        Assert.Equal("丙的捕获", observed);
        Assert.Equal(0, player.Points);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Source.Contains("预打出触发器") && entry.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task Command_PrePlay_Cancel_Prevents_Play()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var anyEntity = new Entity("任意目标");
        bridge.CollectScript = _ => Task.FromResult(TargeterTestKit.Candidates(anyEntity.Ref));
        bridge.InteractionScript = (description, responder) => responder.Cancel(description.RequestId); // 交互取消
        using var recorder = new UpdateRecorder(match.Engine);

        // 预打出 handler 显式发起交互；交互被取消 ⇒ 请求取消预打出（预打出段失败/取消 ⇒ 打出不发生）。
        var targeter = match.TargeterManager.CreateTargeter();
        command.AddPrePlayHandler("交互取消即中止", async (view, ctx, ct) =>
        {
            var targeting = await targeter.Targeting();
            if (targeting.Status == TargeterStatus.Cancelled)
            {
                view.CaptureBox!.CancelPrePlay();
            }
        });

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Cancelled, result.Status);
        Assert.DoesNotContain(GameUpdates.CardPlayed, recorder.Types); // 打出不发生
        Assert.Equal(1, player.Points); // 无扣费
        Assert.Contains(command, player.Hand); // 留手
    }

    [Fact]
    public async Task Command_PrePlay_Rejected_When_Not_Enough_Points()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCostlyId); // 花费 5
        var handlerRan = false;
        command.AddPrePlayHandler("不应执行", (view, ctx, ct) => { handlerRan = true; return Task.CompletedTask; });
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(command);

        // 指令预打出一并执行指挥点合法性检查（不足＝拒绝）；验证拒绝＝事件不执行、零更新、留手。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PrePlayPointShortage, result.FailureReason);
        Assert.False(handlerRan);
        Assert.Empty(recorder.Updates);
        Assert.Equal(1, player.Points);
        Assert.Contains(command, player.Hand);
    }

    [Fact]
    public async Task Command_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        var spender = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player); // 预打出段内花点的单位
        var line = match.Battlefield.PlayerASupportLine;
        using var recorder = new UpdateRecorder(match.Engine);

        // 预打出段（handler 内）把唯一 1 点花掉 → 打出段复验：点数不足 ⇒ 打出链中止（仅本次取消＋留痕）。
        command.AddPrePlayHandler("预打出段内花点", async (view, ctx, ct) =>
        {
            var spent = await match.PlayManager.PlayUnitAsync(spender, line.GetInsertionGaps()[1]);
            Assert.Equal(PlayResultStatus.Success, spent.Status);
        });

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PlayVerificationRejected, result.FailureReason);
        // 零副作用：不触发主动 handler 集/不扣费/不发更新/不离手（本测试无 handler 集；更新仅来自 spender 的部署链——
        // W3-A3 起含放置驱动信号 card.placed）。
        Assert.Equal(
            new[]
            {
                GameUpdates.CardPlayed, Updates.CardPlaced, GameUpdates.UnitDeployed,
                GameUpdates.PointChanged, // spender 部署收尾扣费（E1-25 后续）
            },
            recorder.Types);
        Assert.Equal(0, player.Points);
        Assert.Contains(command, player.Hand);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Keywords.Contains("validation:rejected"));
    }

    /// <summary>主动效果施放视图（P4：OrderFlow 只传 Source/Target/Amount 三键）。</summary>
    [ContextView]
    public class TestCastView
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

    /// <summary>测试主动效果（<see cref="ICastAction"/> 由 <see cref="ActiveEffect{TView}"/> 承载）。</summary>
    private sealed class TestCastEffect : ActiveEffect<TestCastView>
    {
        public TestCastEffect(string name, Func<TestCastView, Context, CancellationToken, Task> handler)
            : base(name, castEvents: new[] { new TriggerEvent<TestCastView>("施放", handler) })
        {
        }
    }
}
