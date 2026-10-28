using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点③（v4 形态）：数据面共享与生命周期——同一次成员执行内各步骤共用一份 <see cref="Context"/>；
/// 每次执行各建一份新上下文（外部字典入料拷贝一次）；空成员执行产出并挂载空流。
/// <para>
/// 删除项（§28）：触发器构造/注册的参数校验（<c>Trigger.Register(null)</c>、事件集合 null／重名）随类型退役；
/// "绑定失败被隔离并结局化"改为穿透（§27.1），且信号端点视图为空视图，不再有视图绑定失败的入口。
/// </para>
/// </summary>
public class TriggerSharedContextTests
{
    [Fact]
    public async Task Same_Context_Carrier_Shared_Within_One_Execution()
    {
        var engine = new LogicEngine();
        Context? first = null;
        Context? second = null;
        object? marker = null;

        var member = new MemberDefinition("共享", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            new OwnedEdge((KernelHandler)((_, ctx, _) =>
            {
                first = ctx;
                marker = new object();
                ctx.Set("handoff", marker);
                return Task.CompletedTask;
            })),
            new OwnedEdge((KernelHandler)((_, ctx, _) =>
            {
                second = ctx;
                return Task.CompletedTask;
            })),
        });

        await KernelTestKit.RunAsync(engine, member, new Dictionary<string, object?> { ["n"] = 1 });

        Assert.Same(first, second);                                  // 同一次执行共用同一份载体
        Assert.Same(marker, second!.Get("handoff"));                  // 前一步写入对后一步可见
        Assert.Equal(1, (int)second.Get("n")!);                       // 入料键仍在
    }

    [Fact]
    public async Task Each_Execution_Uses_Fresh_Context()
    {
        var engine = new LogicEngine();
        var seen = new List<Context>();

        var member = new MemberDefinition("每次新", ViewSpec.Empty, MemberVisibility.Callable, new[]
        {
            new OwnedEdge((KernelHandler)((_, ctx, _) =>
            {
                seen.Add(ctx);
                var n = ctx.TryGet("count", out var value) ? (int)value! : 0;
                ctx.Set("count", n + 1);
                return Task.CompletedTask;
            })),
        });

        KernelTestKit.Register(engine, member);
        await engine.InvokeMemberAsync("每次新", new Dictionary<string, object?>());
        await engine.InvokeMemberAsync("每次新", new Dictionary<string, object?>());

        Assert.Equal(2, seen.Count);
        Assert.NotSame(seen[0], seen[1]);                 // 每次执行新上下文（互不串味）
        Assert.Equal(1, (int)seen[0].Get("count")!);
        Assert.Equal(1, (int)seen[1].Get("count")!);
    }

    [Fact]
    public void Context_Copies_External_Dictionary_On_Construction()
    {
        var external = new Dictionary<string, object?> { ["Count"] = 1 };
        var ctx = new Context(external);

        external["Count"] = 99;

        Assert.Equal(1, (int)ctx.Get("Count")!); // 入料已拷贝一次：外部后续变化不影响本次执行
    }

    [Fact]
    public async Task Empty_Member_Execution_Produces_And_Mounts_Empty_Stream()
    {
        var engine = new LogicEngine();
        var member = new MemberDefinition("空", ViewSpec.Empty, MemberVisibility.Callable);

        await KernelTestKit.RunAsync(engine, member);

        var stream = Assert.Single(engine.RootStream.Children);
        Assert.Empty(stream.Entries);
        Assert.Same(engine.RootStream, stream.Parent);
        Assert.Contains(stream, engine.RootStream.Children);
    }
}
