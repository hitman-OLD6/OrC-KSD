using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点①②（广播侧，v4 边表形态）：发射→订阅者执行（载荷经 ctx）；同信号多订阅者的执行序
/// （订阅优先级升序→订阅序升序）；取消语义；快照语义（本轮撤销不影响本轮）。
/// <para>
/// **不再等价的两条**（§28）：①"业务异常被隔离、广播继续、失败记流"——成员执行面现在原样上抛（§27.1），
/// 已改为断言穿透；②"已中断链上新发射空转"——执行帧的 <c>IsChainInterrupted</c> 在新执行面已无读取点
/// （中断由各流程自检 <c>ctx.Interrupted</c>），该用例删除，覆盖消失已记入遗留项。
/// </para>
/// </summary>
public class SignalEmitTests
{
    // ---------- 验收①：发射→订阅者执行（载荷） ----------

    [Fact]
    public async Task Emit_Reaches_Subscriber_With_Payload_In_Ctx()
    {
        var engine = new LogicEngine();
        var card = new Entity("卡牌A");
        Context? captured = null;
        Ref<Entity>? seenCard = null;
        var seenAmount = -1;

        SignalTestHelpers.Subscribe(engine, Updates.CardPlaced, "部署响应", null, (ctx, ct) =>
        {
            captured = ctx;
            seenCard = (Ref<Entity>?)ctx.Get("Card");
            seenAmount = (int)ctx.Get("Amount")!;
            ctx.Set("Amount", 42); // 数据面就地改写（同一份 Context）
            return Task.CompletedTask;
        });

        await engine.Emit(Updates.CardPlaced, new Dictionary<string, object?>
        {
            ["Card"] = card.Ref,
            ["Amount"] = 5,
            ["Marker"] = "m",
        });

        Assert.Same(card.Ref, seenCard); // 载荷引用原样入（同一 Ref 实例）
        Assert.Equal(5, seenAmount);
        Assert.NotNull(captured);
        Assert.Equal(42, (int)captured!.Get("Amount")!);
        Assert.True(captured.TryGet("Marker", out var marker));
        Assert.Equal("m", marker);
        // 框架不注入保留键、不占键名：ctx 仅含载荷键
        Assert.False(captured.TryGet("updateType", out _));
        Assert.False(captured.TryGet("UpdateType", out _));
    }

    [Fact]
    public async Task Emit_Flows_Custom_Update_Strings_Matching_By_Ordinal()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var custom = "deck.shuffled";
        SignalTestHelpers.Subscribe(engine, custom, "T", trace);
        SignalTestHelpers.Subscribe(engine, custom + " ", "T2", trace); // 尾空格名同样合法

        await engine.Emit(custom);
        Assert.Equal(new[] { "T" }, trace); // 自定义字符串与常量平等（无注册/白名单）；精确匹配

        trace.Clear();
        await engine.Emit(custom + " "); // 原样比较：尾空格是不同更新
        Assert.Equal(new[] { "T2" }, trace);

        trace.Clear();
        await engine.Emit("Deck.Shuffled"); // 大小写敏感
        Assert.Empty(trace);
    }

    [Fact]
    public async Task Emit_Validates_Update_Type()
    {
        var engine = new LogicEngine();
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.Emit(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.Emit(""));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.Emit("  "));
    }

    // ---------- 验收②：多订阅者顺序 ----------

    [Fact]
    public async Task Emit_Orders_By_Subscription_Priority_Ascending_Then_Subscription_Ascending()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "低", trace, priority: UpdatePriorities.Low);
        SignalTestHelpers.Subscribe(engine, "u", "高1", trace, priority: UpdatePriorities.High);
        SignalTestHelpers.Subscribe(engine, "u", "中", trace); // 默认 Normal
        SignalTestHelpers.Subscribe(engine, "u", "高2", trace, priority: UpdatePriorities.High);

        await engine.Emit("u");

        Assert.Equal(new[] { "高1", "高2", "中", "低" }, trace); // 数值小者先；同优先级先订阅先执行
    }

    [Fact]
    public async Task Emit_Executes_Lower_Priority_Values_First()
    {
        // 方向锁定：升序（数值小者先）——执行序与订阅序相反，证明由优先级主导（而非订阅序）
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "数值大", trace, priority: 900);
        SignalTestHelpers.Subscribe(engine, "u", "数值小", trace, priority: 100);

        await engine.Emit("u");

        Assert.Equal(new[] { "数值小", "数值大" }, trace);
    }

    [Fact]
    public async Task Emit_Same_Priority_Order_Is_Subscription_Stable_Across_Rounds()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "A", trace);
        SignalTestHelpers.Subscribe(engine, "u", "B", trace);
        SignalTestHelpers.Subscribe(engine, "u", "C", trace);

        await engine.Emit("u");
        await engine.Emit("u");
        Assert.Equal(new[] { "A", "B", "C", "A", "B", "C" }, trace); // 顺序确定、可重现
    }

    [Fact]
    public async Task Emit_Accepts_Arbitrary_Priority_Values()
    {
        // 任意 int（含负值、极小极大）合法、不设界；参考常量不作校验依据
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "自定义", trace, priority: 55);
        SignalTestHelpers.Subscribe(engine, "u", "极小", trace, priority: int.MinValue);
        SignalTestHelpers.Subscribe(engine, "u", "极大", trace, priority: int.MaxValue);

        await engine.Emit("u");
        Assert.Equal(new[] { "极小", "自定义", "极大" }, trace);
    }

    // ---------- 失败：穿透（§27.1：不再隔离、不再结局化） ----------

    [Fact]
    public async Task Emit_Subscriber_Exception_Propagates_And_Stops_Remaining()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();

        SignalTestHelpers.Subscribe(engine, "u", "A", null, (ctx, ct) =>
        {
            trace.Add("A");
            throw new InvalidOperationException("业务爆炸");
        });
        SignalTestHelpers.Subscribe(engine, "u", "B", trace);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Emit("u"));

        Assert.Equal("业务爆炸", thrown.Message);
        Assert.Equal(new[] { "A" }, trace); // 后续订阅者不再执行（fail-fast，无结局化隔离）
    }

    // ---------- 取消语义 ----------

    [Fact]
    public async Task Emit_With_Cancelled_Token_Throws_Before_Entry_And_Broadcast()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "T", trace);
        var baseline = engine.RootStream.Entries.Count;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Emit("u", null, cts.Token));

        Assert.Equal(baseline, engine.RootStream.Entries.Count); // 不写条目
        Assert.Empty(trace); // 不广播
    }

    [Fact]
    public async Task Emit_Subscriber_Cancellation_Stops_Remaining_And_Propagates()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "A", null, (ctx, ct) => throw new OperationCanceledException("广播中取消"));
        SignalTestHelpers.Subscribe(engine, "u", "B", trace);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Emit("u"));

        Assert.DoesNotContain("B", trace); // 停止后续订阅者、原样向上穿透
    }

    // ---------- 快照语义 ----------

    [Fact]
    public async Task Emit_Snapshot_Semantics_Revoke_During_Broadcast_Does_Not_Affect_Current_Round()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        IDisposable? b = null;

        // 先挂 A（本轮先执行者），它的执行体中途撤销 B；B 后挂、位次在后——这是"中途撤销"能成立的前提。
        SignalTestHelpers.Subscribe(engine, "u", "A", null, (ctx, ct) =>
        {
            trace.Add("A");
            b!.Dispose(); // 本轮中途撤销 B
            return Task.CompletedTask;
        });
        b = SignalTestHelpers.Subscribe(engine, "u", "B", trace);
        Assert.Equal(new[] { "A", "B" }, AuditTestKit.EdgesInto(engine, "u").Select(e => e.Member));

        await engine.Emit("u");
        Assert.Equal(new[] { "A", "B" }, trace); // B 在本轮快照中：虽已撤销、本轮仍执行

        trace.Clear();
        await engine.Emit("u");
        Assert.Equal(new[] { "A" }, trace); // 下一轮：B 不复存在
    }
}
