using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// A2 信号通路的机制验收（内核侧）：自定义信号只能经运行期注册（R1e）进入 <c>engine</c> 命名空间，
/// 注册后即可挂载／发射，审查面上它是一条**真实的边**（不再是"未解析 hook"的图缺口）。
/// </summary>
public class SignalBridgeTests
{
    /// <summary>记录型订阅边（无视图：本组用例只验证"被执行到"，不验证视图投影）。</summary>
    private static OwnedEdge Probe(Action onHit)
        => new((KernelHandler)((_, _, _) =>
        {
            onHit();
            return Task.CompletedTask;
        }));

    [Fact]
    public void CustomSignal_BeforeRegistration_IsAGraphGap()
    {
        var engine = new LogicEngine();
        engine.AttachMemberEdge("sandbox.custom", "自定义", "自定义", Probe(() => { }));

        var edge = Assert.Single(engine.BuildAuditGraph().EdgesInto("sandbox.custom"));
        Assert.True(edge.GraphGap);
        Assert.Equal("hook#sandbox.custom", edge.Target);
    }

    [Fact]
    public async Task CustomSignal_AfterRuntimeRegistration_IsMountableAndEmittable_AndAuditedAsSignalEdge()
    {
        var engine = new LogicEngine();
        var hits = 0;

        // R1e：注册在挂载之前（挂载一个未声明的名字＝图缺口，不自动注册）。
        var signal = engine.RegisterSignal("sandbox.custom");
        Assert.Equal(PrefabLayer.Runtime, signal.Layer);
        Assert.True(signal.IsSignal);

        engine.AttachMemberEdge("sandbox.custom", "自定义", "自定义", Probe(() => hits++));
        await engine.Emit("sandbox.custom", new Dictionary<string, object?> { ["Card"] = "某卡" });

        Assert.Equal(1, hits);

        // 审查面：目标已是信号成员（engine#…），不再是图缺口。
        var edge = Assert.Single(engine.BuildAuditGraph().EdgesInto("sandbox.custom"));
        Assert.False(edge.GraphGap);
        Assert.Equal("engine#sandbox.custom", edge.Target);
        Assert.Equal(PrefabLayer.Runtime, edge.MemberLayer);
    }

    [Fact]
    public async Task Emit_OfUnknownName_RegistersOnFirstUse()
    {
        var engine = new LogicEngine();
        var hits = 0;
        engine.AttachMemberEdge("sandbox.lazy", "懒注册探针", "懒注册探针", Probe(() => hits++));

        await engine.Emit("sandbox.lazy");

        Assert.Equal(1, hits);
        Assert.DoesNotContain(engine.BuildAuditGraph().EdgesInto("sandbox.lazy"), e => e.GraphGap);
    }
}
