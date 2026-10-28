using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点⑤（v4 形态）：<c>Stop</c>（仅本级成员：本成员剩余步骤不执行、调用方其余步骤照常）与
/// <c>Interrupt</c>（链级标志：沿嵌套链传播、各层可查、写警告留痕）。
/// <para>
/// 与旧实现的差异（§27.1／§28）：中断**不再由执行器在事件边界自动跳过剩余步骤**——
/// 帧上的 <c>Interrupted</c> 只做标记，是否止步由各流程自检（如 <see cref="OrderFlow"/>）；
/// 因此旧"剩余事件未执行"的断言不再成立，改为断言"标志传播到位 + 消费者可读"。
/// </para>
/// </summary>
public class TriggerControlTests
{
    private static OwnedEdge Edge(Action<Context> body)
        => new((KernelHandler)((view, ctx, ct) =>
        {
            body(ctx);
            return Task.CompletedTask;
        }));

    [Fact]
    public async Task Stop_Stops_Current_Member_Remainder_But_Not_Caller()
    {
        var engine = new LogicEngine();
        var order = new List<string>();

        var x = new MemberDefinition("X", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            Edge(ctx =>
            {
                order.Add("x1");
                ctx.Stop();
            }),
            Edge(_ => order.Add("x2")),
        });

        var p = new MemberDefinition("P", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            new OwnedEdge((KernelHandler)(async (_, ctx, ct) =>
            {
                order.Add("p1");
                await engine.InvokeMemberAsync("X", ctx);
                order.Add("p1-after");
            })),
            Edge(_ => order.Add("p2")),
        });

        KernelTestKit.Register(engine, x);
        KernelTestKit.Register(engine, p);

        await engine.InvokeMemberAsync("P");

        Assert.Equal(new[] { "p1", "x1", "p1-after", "p2" }, order); // X 的 x2 未执行；调用方照常
        Assert.Contains(engine.RootStream.Entries, e => e.Keywords.Contains("stop")); // 停止写留痕
    }

    [Fact]
    public async Task Stop_Does_Not_Affect_Sub_Execution()
    {
        var engine = new LogicEngine();
        var order = new List<string>();

        var y = new MemberDefinition("Y", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            Edge(_ => order.Add("y1")),
            Edge(_ => order.Add("y2")),
        });

        var x = new MemberDefinition("X", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            new OwnedEdge((KernelHandler)(async (_, ctx, _) =>
            {
                await engine.InvokeMemberAsync("Y", ctx); // 子执行完整跑完
                ctx.Stop();                              // 仅停 X 本级
                order.Add("x1-after");
            })),
            Edge(_ => order.Add("x2")),
        });

        KernelTestKit.Register(engine, y);
        KernelTestKit.Register(engine, x);

        await engine.InvokeMemberAsync("X");

        // Y 完整执行；X 剩余步骤 x2 未执行。
        // 注意（§28 行为差异）：Stop 标记落在**当前栈顶作用域**——若子执行仍在途（未 await 完成）就调用 Stop，
        // 标记会落在子作用域而非本级；旧的"已触发子执行不受影响"用例因此改写为顺序形态。
        Assert.Equal(new[] { "y1", "y2", "x1-after" }, order);
    }

    [Fact]
    public async Task Interrupt_Propagates_Along_Chain_And_Is_Readable_At_Every_Layer()
    {
        var engine = new LogicEngine();
        var order = new List<string>();
        Context? outerCtx = null;
        var innerSawInterrupted = false;

        var inner = new MemberDefinition("内", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            Edge(ctx =>
            {
                order.Add("c1");
                ctx.Interrupt();
                order.Add("c1-after");
            }),
            Edge(ctx =>
            {
                order.Add("c2");
                innerSawInterrupted = ctx.Interrupted;
            }),
        });

        var outer = new MemberDefinition("外", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            new OwnedEdge((KernelHandler)(async (_, ctx, _) =>
            {
                outerCtx = ctx;
                await engine.InvokeMemberAsync("内", ctx);
                order.Add("p1-after");
            })),
            Edge(_ => order.Add("p2")),
        });

        KernelTestKit.Register(engine, inner);
        KernelTestKit.Register(engine, outer);

        await engine.InvokeMemberAsync("外");

        // Interrupt 的契约（`ExecutionFrame.RequestInterrupt`）：标记**整条调用栈**，各层在**各自下一检查边界**
        // 停止、已中断状态下的新执行空转。因此：
        //   · `c1-after` 仍执行——它与 Interrupt 同一 handler，Handler 内不是检查边界；
        //   · `c2` 不执行——它是下一条边，边循环前是检查边界；
        //   · `p1-after` 仍执行——它在外层 handler 的错误处理之后，handler 内同样不是边界；
        //   · `p2` 不执行——外层的下一条边。
        Assert.Equal(new[] { "c1", "c1-after", "p1-after" }, order);
        Assert.False(innerSawInterrupted);           // `c2` 未被推进
        Assert.True(outerCtx!.Interrupted);          // 但祖先层已被标记且可查（链级传播）
        Assert.True(outerCtx!.Interrupted);

        // 中断行为写入事件流（引擎级可见）
        var record = engine.RootStream.Entries.Single(e => e.Keywords.Contains("interrupt"));
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("执行被中断", record.Message);
    }

    [Fact]
    public void Context_Stop_And_Interrupt_Outside_Execution_Session_Are_NoOps()
    {
        var ctx = new Context();

        ctx.Stop();
        ctx.Interrupt();

        Assert.False(ctx.Stopped);
        Assert.False(ctx.Interrupted);
        Assert.Null(ctx.Engine);
    }
}
