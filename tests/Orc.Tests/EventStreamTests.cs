using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收点⑥：事件流——唯一 ID、自动挂载（顶层执行挂总流）、attach 日志含父子 ID、
/// 条目含关键词、读面便于后续 JSON 序列化、update 写入 API 就绪。
/// <para>
/// v4 迁移：执行会话＝**成员执行**（<c>engine#&lt;成员名&gt;</c>），不再有触发器实例。
/// 嵌套执行的流拓扑另有变化：数据面形态的成员调用与 <c>Emit</c> 都沿用调用方的流（不再另开子流），
/// 因此旧的"子执行成子流、条目逐层冒泡"两条用例没有等价物（§28 记录）。
/// </para>
/// </summary>
public class EventStreamTests
{
    [Fact]
    public async Task Top_Level_Execution_Mounts_To_Root_With_Attach_Entry_And_Unique_Ids()
    {
        var engine = new LogicEngine();
        var member = KernelTestKit.Member("顶层", new OwnedEdge((_, _, _) => Task.CompletedTask));

        await KernelTestKit.RunAsync(engine, member);

        var stream = Assert.Single(engine.RootStream.Children); // 自动挂载（无需手动）
        Assert.False(string.IsNullOrEmpty(stream.Id));          // 唯一 ID
        Assert.False(string.IsNullOrEmpty(engine.RootStream.Id));
        Assert.NotEqual(stream.Id, engine.RootStream.Id);
        Assert.Same(engine.RootStream, stream.Parent);          // 顶层流父引用＝总流对象
        Assert.Null(engine.RootStream.Parent);                  // 总流为根

        var attach = engine.RootStream.Entries.Single(e => e.Kind == LogEntryKind.Attach);
        Assert.Equal(engine.RootStream.Id, attach.Data["parentId"]); // attach 必备父/子流 ID
        Assert.Equal(stream.Id, attach.Data["childId"]);
        Assert.False(string.IsNullOrEmpty(attach.Source));
        Assert.True(attach.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-1)); // UTC 时间戳
    }

    [Fact]
    public async Task Multiple_Top_Level_Executions_Create_Sibling_Streams_Under_Root()
    {
        var engine = new LogicEngine();
        var member = KernelTestKit.Member("多次", new OwnedEdge((_, _, _) => Task.CompletedTask));
        KernelTestKit.Register(engine, member);

        await engine.InvokeMemberAsync("多次");
        await engine.InvokeMemberAsync("多次");

        var streams = engine.RootStream.Children.ToArray();
        Assert.Equal(2, streams.Length);                            // 各自成流
        Assert.NotSame(streams[0], streams[1]);
        Assert.All(streams, s => Assert.Same(engine.RootStream, s.Parent)); // 共同挂于总流之下（兄弟顶层流）
        Assert.Equal(2, engine.RootStream.Entries.Count(e => e.Kind == LogEntryKind.Attach));
    }

    [Fact]
    public void Entry_Exposes_Complete_Structure_And_Read_Surface_Is_Navigable()
    {
        var engine = new LogicEngine();

        var entry = engine.RootStream.WriteLog(
            "ops",
            "人工记录",
            LogLevel.Warning,
            keywords: new[] { "manual", "level:warning" },
            data: new Dictionary<string, object?> { ["n"] = 1 });

        Assert.Equal(LogEntryKind.Log, entry.Kind);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("ops", entry.Source);
        Assert.Equal("人工记录", entry.Message);
        Assert.Equal(new[] { "manual", "level:warning" }, entry.Keywords);
        Assert.Equal(1, (int)entry.Data["n"]!);
        Assert.True(entry.Timestamp <= DateTimeOffset.UtcNow);
        Assert.Contains(entry, engine.RootStream.Entries);

        // 读面导航（面向 JSON 序列化：条目/子流可枚举、父子可导航）：
        Assert.Empty(engine.RootStream.Children);
    }

    [Fact]
    public async Task Nested_Member_Execution_Creates_Child_Stream_And_Bubbles_Entries()
    {
        // 子流仍存在，但只有"另起数据面执行"这一形态会建：子流挂调用方流，条目经冒泡可达祖先。
        var engine = new LogicEngine();
        var child = KernelTestKit.Member("子", new OwnedEdge((_, _, _) => Task.CompletedTask));
        KernelTestKit.Register(engine, child);

        var parent = KernelTestKit.Member("父", new OwnedEdge((KernelHandler)(async (_, _, _) =>
            await KernelTestKit.RunNestedAsync(engine, "子"))));
        await KernelTestKit.RunAsync(engine, parent);

        var parentStream = Assert.Single(engine.RootStream.Children);
        var childStream = Assert.Single(parentStream.Children);
        Assert.Same(parentStream, childStream.Parent);

        var entry = childStream.WriteLog("测试源", "第一条", keywords: new[] { "sample:2", "trace" });

        Assert.Contains(entry, childStream.Entries);
        Assert.Contains(entry, parentStream.Entries);
        Assert.Contains(entry, engine.RootStream.Entries);
        Assert.Same(entry, engine.RootStream.Entries.First(x => x.Message == "第一条"));
    }
}
