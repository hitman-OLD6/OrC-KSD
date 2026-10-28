using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点④⑤（挂载侧，v4 边表形态）：订阅＝<c>engine#&lt;信号&gt;</c> 上的一条外来边。
/// 保留：常量形态、位次读面（边序＝执行序、快照语义）、入参校验、多信号订阅、句柄撤销。
/// 删除：<c>Engine.Mount</c>／<c>UnmountOwner</c>／触发器实例状态（"同一实例跨总线拒绝""重挂复位注册序"
/// 等）——新模型没有触发器实例、也没有按所有者批量卸载（撤销落在句柄上），见 PROGRESS §28。
/// </summary>
public class SignalMountTests
{
    // ---------- 常量形态 ----------

    [Fact]
    public void Updates_And_UpdatePriorities_Constants_Have_Fixed_Forms()
    {
        Assert.Equal("card.placed", Updates.CardPlaced);
        Assert.Equal("card.destroyed", Updates.CardDestroyed);
        Assert.Equal("card.data", Updates.CardData);
        Assert.Equal("effect.removed", Updates.EffectRemoved);

        Assert.Equal(100, UpdatePriorities.High);
        Assert.Equal(200, UpdatePriorities.Normal);
        Assert.Equal(300, UpdatePriorities.Low);
    }

    // ---------- 读面：边序＝执行序 ----------

    [Fact]
    public async Task AuditEdges_AreOrdered_LikeExecutionOrder()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "A", trace);
        SignalTestHelpers.Subscribe(engine, "u", "B", trace, priority: UpdatePriorities.High);
        SignalTestHelpers.Subscribe(engine, "u", "C", trace);

        var edges = AuditTestKit.EdgesInto(engine, "u");
        Assert.Equal(new[] { "B", "A", "C" }, edges.Select(e => e.Member)); // 位次 1..3＝优先级升序→订阅序升序
        Assert.Equal(new[] { 1, 2, 3 }, edges.Select(e => e.Position));

        await engine.Emit("u");
        Assert.Equal(new[] { "B", "A", "C" }, trace); // 边序＝执行序

        SignalTestHelpers.Subscribe(engine, "u", "D", trace, priority: UpdatePriorities.High);
        Assert.Equal(new[] { "B", "A", "C" }, edges.Select(e => e.Member)); // 快照语义：已导出的图不受后续订阅影响
        Assert.Equal(
            new[] { "B", "D", "A", "C" },
            AuditTestKit.EdgesInto(engine, "u").Select(e => e.Member)); // 新调用见更新（High 组内先挂先执行）
    }

    /// <summary>审查面读面的入参校验与"未知成员＝空集"（开放集合：不报错）。</summary>
    [Fact]
    public void AuditEdges_ValidateInput_AndReturnEmptyForUnknown()
    {
        var engine = new LogicEngine();
        Assert.Throws<ArgumentNullException>(() => AuditTestKit.EdgesInto(engine, null!));
        Assert.Empty(AuditTestKit.EdgesInto(engine, "unknown.update"));
        Assert.Empty(AuditTestKit.EdgesInto(engine, "   ")); // 空白名不是任何成员 → 空集
    }

    // ---------- 多信号订阅 ----------

    [Fact]
    public async Task Multi_Signal_Subscription_Is_Consistent_Across_Tables()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, new[] { "a", "b" }, "X", trace);
        SignalTestHelpers.Subscribe(engine, "a", "Y", trace);

        Assert.Equal(new[] { "X", "Y" }, AuditTestKit.EdgesInto(engine, "a").Select(e => e.Member));
        Assert.Equal(new[] { "X" }, AuditTestKit.EdgesInto(engine, "b").Select(e => e.Member));
        Assert.All(AuditTestKit.EdgesInto(engine, "a"), e => Assert.Equal(EdgeSource.Explicit, e.Source));

        await engine.Emit("a");
        await engine.Emit("b");
        Assert.Equal(new[] { "X", "Y", "X" }, trace);
    }

    // ---------- 句柄撤销（旧 UnmountOwner 的等价读面） ----------

    [Fact]
    public async Task Revoke_Handle_RemovesEdge_AndStopsFutureActivation()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var handle = SignalTestHelpers.Subscribe(engine, "u", "T", trace);

        await engine.Emit("u");
        Assert.Equal(new[] { "T" }, trace);

        handle.Dispose(); // 撤销＝边从边表移除、端点连同撤销（幂等）
        handle.Dispose();
        Assert.Empty(AuditTestKit.EdgesInto(engine, "u"));

        trace.Clear();
        await engine.Emit("u");
        Assert.Empty(trace);
    }

    [Fact]
    public async Task Revoke_Leaves_Other_Subscribers_Untouched()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var a = SignalTestHelpers.Subscribe(engine, "u", "A", trace);
        SignalTestHelpers.Subscribe(engine, "u", "B", trace);

        a.Dispose();

        Assert.Equal(new[] { "B" }, AuditTestKit.EdgesInto(engine, "u").Select(e => e.Member));
        await engine.Emit("u");
        Assert.Equal(new[] { "B" }, trace);
    }
}
