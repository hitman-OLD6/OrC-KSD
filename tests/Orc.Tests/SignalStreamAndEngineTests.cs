using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点③⑥（入流与引擎壳）：更新条目位置与冒泡；引擎壳（Emit 转发等价、多实例独立、总流可达性）。
/// <para>
/// v4 语义（§28 记录）：<c>Emit</c> 的订阅者执行**不再另开子流**——发射帧沿用发射时所在的那条流
/// （边界发射＝总流；帧内发射＝发射者流），因此"订阅者流挂谁、attach 父子 ID"这组断言没有对应物。
/// 本文件改为断言条目落在哪条流、是否经冒泡可达总流。
/// </para>
/// </summary>
public class SignalStreamAndEngineTests
{
    // ---------- 验收③：更新写入事件流 ----------

    [Fact]
    public async Task Boundary_Emit_Writes_Update_Entry_To_Root()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "T", trace);
        var card = new Entity("卡");
        var payload = new Dictionary<string, object?> { ["Card"] = card.Ref, ["Amount"] = 3, ["Marker"] = "x" };

        await engine.Emit("u", payload);

        var root = engine.RootStream;
        var updateEntry = root.Entries.Single(e => e.Kind == LogEntryKind.Update);
        Assert.Equal("bus", updateEntry.Source);                     // source 固定 "bus"
        Assert.Equal("u", updateEntry.Message);                      // message＝更新类型字面值
        Assert.Equal(new[] { "u" }, updateEntry.Keywords);           // keywords 恰单元素（无附加词）
        Assert.Equal(LogLevel.Info, updateEntry.Level);
        Assert.Equal(card.Ref, updateEntry.Data["Card"]);            // 载荷键值原样
        Assert.Equal(3, (int)updateEntry.Data["Amount"]!);
        Assert.Equal("x", updateEntry.Data["Marker"]);

        Assert.Equal(new[] { "T" }, trace);
    }

    [Fact]
    public async Task In_Frame_Emit_Writes_Update_Entry_To_Emitters_Stream()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "inner", "订阅", trace);

        var member = KernelTestKit.Member("发射者", new OwnedEdge((KernelHandler)(async (_, _, _) =>
            await engine.Emit("inner", new Dictionary<string, object?> { ["Count"] = 1 }))));

        await KernelTestKit.RunAsync(engine, member);

        var emitterStream = engine.RootStream.Children.Single();

        // update 条目落发射者流（本地写入；非总流本地）
        var updateEntry = emitterStream.Entries.Single(e => e.Kind == LogEntryKind.Update);
        Assert.Equal("bus", updateEntry.Source);
        Assert.Equal("inner", updateEntry.Message);

        // 冒泡：update 条目可达总流；执行不另开子流（v4：沿用发射者流）
        Assert.Contains(engine.RootStream.Entries, e => ReferenceEquals(e, updateEntry));
        Assert.Empty(emitterStream.Children);
        Assert.Equal(new[] { "订阅" }, trace);
    }

    [Fact]
    public async Task Nested_Emit_Keeps_Single_Stream_And_Runs_Subscribers_In_Order()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();

        SignalTestHelpers.Subscribe(engine, "outer", "P1", null, async (ctx, ct) =>
        {
            trace.Add("P1");
            await engine.Emit("inner");
        });
        SignalTestHelpers.Subscribe(engine, "inner", "P2", trace);

        var emitter = KernelTestKit.Member("发射者", new OwnedEdge((KernelHandler)(async (_, _, _) =>
        {
            trace.Add("E");
            await engine.Emit("outer");
        })));

        await KernelTestKit.RunAsync(engine, emitter);

        Assert.Equal(new[] { "E", "P1", "P2" }, trace); // 嵌套 Emit 沿执行帧链串联执行

        var emitterStream = engine.RootStream.Children.Single();
        Assert.Empty(emitterStream.Children); // 不再成树：外层与内层更新条目都落在同一条流
        Assert.Contains(emitterStream.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "outer");
        Assert.Contains(emitterStream.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "inner");

        // 全部条目经冒泡可达总流
        var root = engine.RootStream;
        Assert.Contains(root.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "outer");
        Assert.Contains(root.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "inner");
    }

    [Fact]
    public async Task Update_Entry_Data_Is_Empty_When_Payload_Is_Null_And_No_Subscribers()
    {
        var engine = new LogicEngine();
        await engine.Emit("nothing.happened");        // 无订阅者：仍写条目（因果记录完整）
        await engine.Emit("nothing.happened", null);

        var updates = engine.RootStream.Entries.Where(e => e.Kind == LogEntryKind.Update).ToArray();
        Assert.Equal(2, updates.Length);
        Assert.All(updates, e => Assert.Empty(e.Data));                        // payload=null → 空字典
        Assert.All(updates, e => Assert.Equal(new[] { "nothing.happened" }, e.Keywords));
        Assert.Empty(engine.RootStream.Children);                              // 无执行流
    }

    // ---------- 验收⑥：引擎壳（总流联动、多实例独立） ----------

    [Fact]
    public void Engine_Stream_Is_Stable_And_Multi_Instance()
    {
        var engine = new LogicEngine();
        Assert.Same(engine.RootStream, engine.RootStream); // 同一引擎两次访问同实例（公开只读、不可替换）

        var other = new LogicEngine();
        Assert.NotSame(engine.RootStream, other.RootStream); // 多实例、无单例
    }

    [Fact]
    public async Task Engine_Emit_Reaches_Subscribers_And_Writes_Root_Stream()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "a", "T1", trace);
        SignalTestHelpers.Subscribe(engine, "b", "T2", trace);

        await engine.Emit("a");
        await engine.Emit("b");

        Assert.Equal(new[] { "T1", "T2" }, trace);

        var updates = engine.RootStream.Entries
            .Where(e => e.Kind == LogEntryKind.Update)
            .Select(e => e.Message)
            .ToArray();
        Assert.Equal(new[] { "a", "b" }, updates); // 两路径均写总流
    }

    [Fact]
    public async Task Multi_Engine_Instances_Are_Independent()
    {
        var a = new LogicEngine();
        var b = new LogicEngine();
        var traceA = new List<string>();
        var traceB = new List<string>();
        SignalTestHelpers.Subscribe(a, "u", "A1", traceA);
        SignalTestHelpers.Subscribe(b, "u", "B1", traceB);

        await a.Emit("u");
        Assert.Equal(new[] { "A1" }, traceA);
        Assert.Empty(traceB); // A 的广播不影响 B（边表/广播/总流均不共享）

        await b.Emit("u");
        Assert.Equal(new[] { "B1" }, traceB);

        Assert.Equal(new[] { "A1" }, AuditTestKit.EdgesInto(a, "u").Select(e => e.Member));
        Assert.Equal(new[] { "B1" }, AuditTestKit.EdgesInto(b, "u").Select(e => e.Member));
        Assert.Equal(1, a.RootStream.Entries.Count(e => e.Kind == LogEntryKind.Update));
        Assert.Equal(1, b.RootStream.Entries.Count(e => e.Kind == LogEntryKind.Update));
    }

    [Fact]
    public async Task Root_Stream_Reaches_All_Entries_By_Bubbling()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "outer", "T", null, async (ctx, ct) => await engine.Emit("inner"));
        SignalTestHelpers.Subscribe(engine, "inner", "Inner", trace);

        await engine.Emit("outer");

        var root = engine.RootStream;
        // 条目完备：update(outer)（本地）与 update(inner)（发射者流上的嵌套发射）均可达
        Assert.Contains(root.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "outer");
        Assert.Contains(root.Entries, e => e.Kind == LogEntryKind.Update && e.Message == "inner");
        Assert.Equal(2, root.Entries.Count(e => e.Kind == LogEntryKind.Update));

        Assert.Equal(new[] { "Inner" }, trace);
    }
}
