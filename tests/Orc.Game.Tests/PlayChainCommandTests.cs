using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Targeting;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收⑤②（指令；P4 指令归一后）：默认空效果集照常打出（card.played/扣费/离手）、
/// 预打出捕获箱（单引用/列表承载演练）、targeter 形态演练（预打出段内执行产出引用并可被消费）、
/// 主动效果集（登记序、card.played 先于效果、收尾在效果之后）、预打出 handler 异常＝成员边 fail-fast
/// （v4 口径；旧"隔离＋继续"随旧触发器退场，见对应用例注释）、
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
    public async Task Command_PrePlay_Handler_Exception_Propagates_FailFast_And_Play_Does_Not_Happen()
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
        using var recorder = new UpdateRecorder(match.Engine);

        // 改写（**断言对象随旧机制消失**）：原用例（`..._Isolated_And_Play_Continues`）断言"预打出 handler 抛错被引擎
        // 隔离（总流留下 exception:<类型>、源＝预打出触发器），登记序后续 handler 与打出段照常继续"——那是旧触发器的
        // "订阅者隔离＋继续"口径。v4 里预打出 handler 是 `host#pre-play` 的**成员边**，边异常按既定 fail-fast 口径
        // 原样上抛（旧总线隔离口径随 b5 退役、新路径不复制它）。
        // v4 等价观测面：异常上抛到调用方 ＋ 其后边不执行 ＋ 打出段不发生（零副作用——不扣费、不离手、不发更新）。
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayManager.PlayOrderAsync(command));
        Assert.Equal("预打出 handler 爆炸", thrown.Message);
        Assert.Equal(new[] { "甲" }, ran);
        Assert.Null(observed);
        Assert.Empty(recorder.Updates);
        Assert.Equal(1, player.Points);
        Assert.Contains(command, player.Hand);
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
            var spent = await match.PlayManager.PlayUnitAsync(spender, line.GetInsertionGaps().At(1));
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

        // 留痕（**v4 等价观测面**）：原断言＝旧触发链把 `validation:rejected` 写进引擎总流；该关键词的唯一写入方
        // （旧触发器/验证包装）随 v4 退场。v4 的对应面＝守卫边把拒绝写进**成员执行的数据面**
        // （`MemberGuardKeys.Rejected`／`Reason`）并 Stop 本级，入口据此映射出上面的 PlayVerificationRejected。
        // 此处按入口同一路径再执行一次 `host#play`（纯验证成员：守卫拒绝即停、零副作用），直接读该标记。
        var probe = new Context(new Dictionary<string, object?>
        {
            [GameUpdates.PayloadCard] = command,
            [GameUpdates.PayloadPlayer] = player,
        });
        await command.InvokeMemberAsync(CardProcesses.Play, probe);
        Assert.True(probe.TryGet(MemberGuardKeys.Rejected, out var guardRejected) && guardRejected is true);
    }


}
