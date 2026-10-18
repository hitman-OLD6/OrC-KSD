using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 管道式包装器验收：前置 LIFO／后置 FIFO／替换只替换核心段、动作缝（按 handler 隔离、未声明 fail-fast）、
/// 异常口径（前置跳过核心+后置、后置保留核心结果）、服务级三类管道与强类型工厂严格校验。
/// </summary>
public class WrapperPipelineTests
{
    private static Task Nop(CounterView view, Context ctx, CancellationToken ct) => Task.CompletedTask;

    private static Task Rec(List<string> trace, string label)
    {
        trace.Add(label);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Before_Lifo_After_Fifo_Replace_Only_Replaces_Core()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "管道");
        var core = trigger.Register("核心", (v, c, t) => Rec(trace, "core"));

        trigger.RegisterBeforeWrapper(core, (v, c, t) => Rec(trace, "b1"));
        trigger.RegisterBeforeWrapper(core, (v, c, t) => Rec(trace, "b2"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => Rec(trace, "a1"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => Rec(trace, "a2"));
        trigger.RegisterReplaceWrapper(core, (v, c, t) => Rec(trace, "replace"));

        await trigger.InvokeAsync(engine);

        Assert.Equal(new[] { "b2", "b1", "replace", "a1", "a2" }, trace);
    }

    [Fact]
    public async Task Replace_Last_Registered_Wins_And_Unregister_Falls_Back()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "替换回退");
        var core = trigger.Register("核心", (v, c, t) => Rec(trace, "core"));

        trigger.RegisterBeforeWrapper(core, (v, c, t) => Rec(trace, "before"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => Rec(trace, "after"));
        var r1 = trigger.RegisterReplaceWrapper(core, (v, c, t) => Rec(trace, "r1"));
        var r2 = trigger.RegisterReplaceWrapper(core, (v, c, t) => Rec(trace, "r2"));

        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "before", "r2", "after" }, trace);

        Assert.True(trigger.UnregisterWrapper(r2));
        trace.Clear();
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "before", "r1", "after" }, trace);

        Assert.True(trigger.UnregisterWrapper(r1));
        trace.Clear();
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "before", "core", "after" }, trace);
    }

    [Fact]
    public async Task Seam_Is_Per_Handler_And_Lifo()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "动作缝");

        var h1 = trigger.Register(
            "h1",
            async (v, c, t) =>
            {
                await c.SeamAsync("s", t);
                trace.Add("h1");
            },
            seams: new[] { "s" });
        var h2 = trigger.Register(
            "h2",
            async (v, c, t) =>
            {
                await c.SeamAsync("s", t);
                trace.Add("h2");
            },
            seams: new[] { "s" });

        trigger.RegisterBeforeWrapper(h1, "s", (v, c, t) => Rec(trace, "h1-seam-1"));
        trigger.RegisterBeforeWrapper(h1, "s", (v, c, t) => Rec(trace, "h1-seam-2"));
        trigger.RegisterBeforeWrapper(h2, "s", (v, c, t) => Rec(trace, "h2-seam"));

        await trigger.InvokeAsync(engine);

        // 同名缝按 handler 隔离；缝内 LIFO（后注册先执行）
        Assert.Equal(new[] { "h1-seam-2", "h1-seam-1", "h1", "h2-seam", "h2" }, trace);
    }

    [Fact]
    public async Task Seam_Undeclared_Is_Isolated_As_Handler_Exception()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "未声明缝");
        trigger.Register("h", async (v, c, t) =>
        {
            await c.SeamAsync("missing", t);
            trace.Add("不该执行");
        });
        trigger.Register("后续", (v, c, t) => Rec(trace, "后续"));

        var stream = await trigger.InvokeAsync(engine);

        Assert.Equal(new[] { "后续" }, trace);
        Assert.Contains(stream.Entries, e => e.Keywords.Contains("exception:KeyNotFoundException"));
    }

    [Fact]
    public async Task Seam_Outside_Handler_Fails_Fast()
    {
        var ctx = new Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SeamAsync("x"));
    }

    [Fact]
    public async Task Before_Exception_Skips_Core_And_After_But_Continues_Next_Event()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "前置异常");
        var core = trigger.Register("核心", (v, c, t) => Rec(trace, "core"));
        trigger.RegisterBeforeWrapper(core, (v, c, t) => throw new InvalidOperationException("前置爆炸"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => Rec(trace, "after"));
        trigger.Register("后续", (v, c, t) => Rec(trace, "后续"));

        var stream = await trigger.InvokeAsync(engine);

        Assert.Equal(new[] { "后续" }, trace);
        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
        Assert.Contains(stream.Entries, e => e.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task After_Exception_Keeps_Core_Result_And_Continues_Remaining_After()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "后置异常");
        var core = trigger.Register("核心", (v, c, t) => Rec(trace, "core"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => throw new InvalidOperationException("后置爆炸"));
        trigger.RegisterAfterWrapper(core, (v, c, t) => Rec(trace, "after-2"));

        var stream = await trigger.InvokeAsync(engine);

        Assert.Equal(new[] { "core", "after-2" }, trace);
        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
    }

    [Fact]
    public void Service_Pipeline_Before_Lifo_After_Fifo_Replace_Core()
    {
        var registry = new JudicatorRegistry();
        var trace = new List<string>();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ =>
        {
            trace.Add("core");
            return new object[] { "core-result" };
        }));

        registry.RegisterBeforeWrapper(handle, _ => trace.Add("b1"));
        registry.RegisterBeforeWrapper(handle, _ => trace.Add("b2"));
        registry.RegisterAfterWrapper(handle, (_, result) =>
        {
            trace.Add("a1");
            return result;
        });
        registry.RegisterAfterWrapper(handle, (_, result) =>
        {
            trace.Add("a2");
            return result;
        });

        var result = registry.Invoke(JudicatorTestData.ProbeName, null);

        Assert.Equal(new[] { "b2", "b1", "core", "a1", "a2" }, trace);
        Assert.Equal(new object[] { "core-result" }, result);
    }

    [Fact]
    public void Service_Before_Can_Rewrite_Args_In_Place_And_After_Can_Replace_Result()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(args => args!));

        registry.RegisterBeforeWrapper(handle, args => args![0] = "改写");
        registry.RegisterAfterWrapper(handle, (_, result) => new object[] { ((string)result![0]!) + "-后置" });

        var result = registry.Invoke(JudicatorTestData.ProbeName, new object[] { "原始" });

        Assert.Equal(new object[] { "改写-后置" }, result);
    }

    [Fact]
    public void Service_Typed_Factory_Before_After_And_Strict_Validation()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "ok" && count > 0));

        // 匹配签名：before＝(string,int)->void；after＝(string,int,bool)->bool
        var before = registry.RegisterBeforeWrapper(handle, Wrapper.Before<string, int>((_, _) => { }));
        var after = registry.RegisterAfterWrapper(handle, Wrapper.After<string, int, bool>((_, _, result) => !result));
        Assert.NotNull(before);
        Assert.NotNull(after);

        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.PairName, new object[] { "ok", 1 }));
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "no", 1 }));

        // 严格校验：参数类型不符＝挂载期 fail-fast
        Assert.Throws<ArgumentException>(() =>
            registry.RegisterBeforeWrapper(handle, Wrapper.Before<int>(_ => { })));
        Assert.Throws<ArgumentException>(() =>
            registry.RegisterAfterWrapper(handle, Wrapper.After<string, bool>((_, result) => result)));
    }

    [Fact]
    public void Typed_Before_Is_Observation_Only_Unified_Signature_Is_Required_For_Rewrite()
    {
        var registry = new JudicatorRegistry();
        var seen = string.Empty;
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "ok"));

        registry.RegisterBeforeWrapper(handle, Wrapper.Before<string, int>((tag, _) => seen = tag));

        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "ok", 0 }));
        Assert.Equal("ok", seen);
    }
}
