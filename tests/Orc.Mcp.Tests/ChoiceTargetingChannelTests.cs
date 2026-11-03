using Orc.Core;
using Orc.Game.Targeting;
using Orc.Mcp;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// R38（`choice` 接通交互通道）在**提交通道这一侧**的验收：
/// ① 呈现文案的传递面（Q7：只有 <c>choice</c> 填、`ask` 不变）；
/// ② 标识集选择器的提交回填（Q6：`picks` 按 `identifiers` 的 **1 起序号**解释）。
/// <para>
/// 这两条都是 issue #2 里「即使 <c>ChooseAsync</c> 接了通道也跑不通」的断点——
/// 引擎侧（<c>KeywordEffectActionFace.ChooseAsync</c>）另有用例覆盖，这里只钉桥接面。
/// </para>
/// </summary>
public class ChoiceTargetingChannelTests
{
    private static TargeterBridgeAdapter NewAdapter() =>
        new(TimeSpan.FromSeconds(10), static reference => reference.Name);

    /// <summary>等桥接发布下一个待决请求（发布是异步的；用事件而不是轮询）。必须在发起流程**之前**订阅。</summary>
    private static Task<PendingSelection> NextPendingAsync(TargeterBridgeAdapter adapter)
    {
        var completion = new TaskCompletionSource<PendingSelection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.PendingPublished += pending => completion.TrySetResult(pending);
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task choice的prompt_一路到达待决请求()
    {
        var adapter = NewAdapter();
        var manager = new TargeterManager(adapter);

        // 先订阅再发起：桥接可能在 RunAsync 内同步发布（队列非空 ⇒ 驱动循环一路跑到底）。
        var pendingTask = NextPendingAsync(adapter);

        var run = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Option,
                new IdentifierSetParameter(new[] { "抽 1 张", "放弃" }, 1, 1)
                {
                    Action = SelectorActions.Effect,
                    Prompt = "选择一项：抽牌 或 放弃",
                });

            return step.IsOk ? TargeterResult.Ok() : TargeterResult.Cancelled();
        });

        var pending = await pendingTask;
        Assert.Equal(SelectorNames.Option, pending.SelectorName);   // 复用 option 选择器，不新增类型
        Assert.Equal("选择一项：抽牌 或 放弃", pending.Prompt);       // R38 Q7：prompt 有传递面
        Assert.Equal(new[] { "抽 1 张", "放弃" }, pending.Identifiers);
        Assert.Empty(pending.Candidates);                           // 标识集选择器：引用候选恒空

        // 收束用取消：本条只验「文案有传递面」，按序号回填属 S23／Q6 的另一条验收。
        Assert.True(adapter.Submit(pending.SessionId, Array.Empty<int>(), cancel: true));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task 未注入prompt_待决请求的prompt为null()
    {
        var adapter = NewAdapter();
        var manager = new TargeterManager(adapter);
        var entity = new Entity("E1");

        var pendingTask = NextPendingAsync(adapter);

        var run = manager.RunAsync(async flow =>
        {
            // ask 的形状：只给 Owner／Action，不给 Prompt（Q7 明令不动 ask）。
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                new ReferenceSetParameter(new[] { entity.Ref }, 1, 1) { Action = SelectorActions.Effect });

            return step.IsOk ? TargeterResult.Ok() : TargeterResult.Cancelled();
        });

        var pending = await pendingTask;
        Assert.Null(pending.Prompt);

        Assert.True(adapter.Submit(pending.SessionId, Array.Empty<int>(), cancel: true));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// R38 Q6：标识集选择器的 `target.submit(picks=[2])` 必须选中**第 2 个标识**。
    /// 现状（修前）`candidates` 恒空 ⇒ 任何 `picks` 都越界 ⇒ 空 `PickEvent` ⇒ `InvalidSelection` ⇒ 重试到上限。
    /// </summary>
    [Fact]
    public async Task 标识集选择器_按1起序号回填被选项()
    {
        var adapter = NewAdapter();
        var manager = new TargeterManager(adapter);

        var pendingTask = NextPendingAsync(adapter);
        string? picked = null;

        var run = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Option,
                new IdentifierSetParameter(new[] { "甲", "乙", "丙" }, 1, 1));

            if (!step.IsOk)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            picked = step.Value;
            return TargeterResult.Ok();
        });

        var pending = await pendingTask;
        Assert.Empty(pending.Candidates);
        Assert.Equal(new[] { "甲", "乙", "丙" }, pending.Identifiers);

        Assert.True(adapter.Submit(pending.SessionId, new[] { 2 }, cancel: false));
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10))).IsOk);
        Assert.Equal("乙", picked);
    }

    /// <summary>引用类路径逐条不变：越界仍交回引擎判 <c>InvalidSelection</c>（不静默丢弃、不取首项）。</summary>
    [Fact]
    public async Task 标识集选择器_越界序号仍交回引擎判非法()
    {
        var adapter = NewAdapter();
        var manager = new TargeterManager(adapter);

        var pendingTask = NextPendingAsync(adapter);

        var run = manager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Option,
                new IdentifierSetParameter(new[] { "甲", "乙" }, 1, 1));

            return TargeterResult.FromSelectorFailure(step.Failure);
        });

        var pending = await pendingTask;
        Assert.True(adapter.Submit(pending.SessionId, new[] { 3 }, cancel: false));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailed);
        Assert.Equal(TargeterFailureReason.InvalidSelection, result.Reason);
    }

    [Fact]
    public void PendingJson_有文案才写prompt键()
    {
        static PendingSelection Pending(string? prompt) => new(
            "s-1", "option", "Click", 1, 1,
            Array.Empty<PendingCandidate>(),
            new[] { "抽 1 张", "放弃" },
            Seat: 0,
            Action: SelectorActions.Effect,
            Prompt: prompt);

        var withPrompt = McpService.PendingJson(Pending("选择一项"));
        Assert.Equal("选择一项", withPrompt!["prompt"]!.GetValue<string>());

        // ask 的形态：不写 prompt 键（「ask 的输出逐条不变」是同一轮的验收项）。
        var without = McpService.PendingJson(Pending(null));
        Assert.False(without!.AsObject().ContainsKey("prompt"));
    }
}
