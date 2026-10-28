using Orc.Cards;
using Orc.Core;
using Orc.Output;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S5 验收点⑤（三骨架接口）：IEngineBridge 桥与 Subscribe 回调——最小接线真实贯通＋轻量冒烟断言：
/// 桥在 Emit 时点被调用（含更新类型/载荷）；未装配时引擎照常；桥/回调异常隔离（不破坏更新广播主流程）且互不干扰；
/// 多订阅者注册序回调、取消注册后不再触发（幂等）。
/// </summary>
public class SkeletonTests
{
    [Fact]
    public async Task Bridge_Receives_Update_On_Emit_With_Type_And_Payload()
    {
        var engine = new LogicEngine();
        var bridge = new RecordingBridge();
        engine.Bridge = bridge;

        await engine.Emit("demo.updated", new Dictionary<string, object?> { ["k"] = 1 });

        Assert.Equal(1, bridge.CallCount); // 更新发生时桥被真实调用（接线方向成立）
        Assert.Equal("demo.updated", bridge.LastUpdateType);
        Assert.Equal(1, bridge.LastPayload!["k"]);
    }

    [Fact]
    public async Task Bridge_Absence_Is_Fine_And_Emit_Still_Writes_Update_Entry()
    {
        var engine = new LogicEngine();

        await engine.Emit("demo.noop"); // 未装配桥：引擎照常运转（跳过调用、不抛）

        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Kind == LogEntryKind.Update && e.Keywords.Contains("demo.noop")); // 更新条目照常写入
    }

    [Fact]
    public async Task Bridge_Exception_Is_Isolated_And_Callbacks_Still_Fire()
    {
        var engine = new LogicEngine();
        engine.Bridge = new ThrowingBridge();
        var got = 0;
        using var subscription = engine.Subscribe((t, p, c) =>
        {
            got++;
            return Task.CompletedTask;
        });

        await engine.Emit("demo.x"); // 桥抛异常：不破坏更新广播主流程（不向调用方抛）

        Assert.Equal(1, got); // 桥异常不阻断回调通道
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Kind == LogEntryKind.Update && e.Keywords.Contains("demo.x")); // 更新条目照常
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Keywords.Contains("exception:InvalidOperationException")); // 桥异常被隔离记录
    }

    [Fact]
    public async Task Callback_Exception_Is_Isolated_And_Other_Callbacks_Continue()
    {
        var engine = new LogicEngine();
        var seen = new List<string>();
        var first = engine.Subscribe((t, p, c) =>
        {
            seen.Add($"first:{t}");
            throw new InvalidOperationException("回调炸弹");
        });
        var second = engine.Subscribe((t, p, c) =>
        {
            seen.Add($"second:{t}");
            return Task.CompletedTask;
        });

        await engine.Emit("demo.boom"); // 回调抛异常：不破坏广播主流程

        Assert.Equal(new[] { "first:demo.boom", "second:demo.boom" }, seen); // 异常不阻断后续回调（注册序保持）
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Keywords.Contains("exception:InvalidOperationException")); // 隔离记录
    }

    [Fact]
    public async Task Subscribe_Multiple_Registration_Order_And_Unsubscribe_Stops_Future_Callbacks()
    {
        var engine = new LogicEngine();
        var seen = new List<string>();
        var sub1 = engine.Subscribe((t, p, c) =>
        {
            seen.Add("s1");
            return Task.CompletedTask;
        });
        var sub2 = engine.Subscribe((t, p, c) =>
        {
            seen.Add("s2");
            return Task.CompletedTask;
        });

        await engine.Emit("demo.a");
        Assert.Equal(new[] { "s1", "s2" }, seen); // 多订阅者＝注册序

        sub1.Dispose();
        sub1.Dispose(); // 取消注册幂等（重复取消无操作、不抛）
        await engine.Emit("demo.b");
        Assert.Equal(new[] { "s1", "s2", "s2" }, seen); // 取消后不再触发

        sub2.Dispose();
        await engine.Emit("demo.c");
        Assert.Equal(new[] { "s1", "s2", "s2" }, seen);
    }

    [Fact]
    public async Task Bridge_And_Callbacks_Fire_Only_On_Emit_Not_On_Other_Activity()
    {
        var engine = new LogicEngine();
        var bridge = new RecordingBridge();
        engine.Bridge = bridge;
        var callbacks = 0;
        using var subscription = engine.Subscribe((t, p, c) =>
        {
            callbacks++;
            return Task.CompletedTask;
        });

        // 非 Emit 活动（成员直接执行、卡牌创建、快照导出）：不引入额外触发源（桥/回调不被调用）
        await engine.InvokeMemberAsync(Updates.CardPlaced);
        _ = new Card(engine, "静默卡");
        _ = SnapshotJson.SerializeAll(engine);

        Assert.Equal(0, bridge.CallCount);
        Assert.Equal(0, callbacks);

        // Emit：恰调用一次（无重复触发）
        await engine.Emit("demo.only");
        Assert.Equal(1, bridge.CallCount);
        Assert.Equal(1, callbacks);
    }
}

/// <summary>记录型桥（测试）：记录调用次数/更新类型/载荷。</summary>
public sealed class RecordingBridge : IEngineBridge
{
    public int CallCount;

    public string? LastUpdateType;

    public IReadOnlyDictionary<string, object?>? LastPayload;

    public Task OnUpdate(string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
    {
        CallCount++;
        LastUpdateType = updateType;
        LastPayload = payload;
        return Task.CompletedTask;
    }
}

/// <summary>爆炸桥（测试）：更新通知时抛异常（隔离验证用）。</summary>
public sealed class ThrowingBridge : IEngineBridge
{
    public Task OnUpdate(string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
        => throw new InvalidOperationException("桥炸弹");
}
