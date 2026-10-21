using Orc.Core;
using Orc.Packages;
using Xunit;
using static Orc.Packages.Tests.AssetLoadTestKit;

namespace Orc.Packages.Tests;

/// <summary>
/// S5 验收（加载器）：列表缓存、从**首个变更层**起增量重载其后全部资产、
/// 装载失败**全有或全无**（回滚）、重载门禁 <c>reloadGate</c> 结构化返回（不抛错）。
/// </summary>
public class AssetLoaderTests
{
    [Fact]
    public void 畸形lock判Invalid且不改变已缓存列表()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var loader = new AssetLoader(registry);
        loader.Load([FormalLock("p", a)]);
        var cached = loader.Current;

        var malformed = AssetLock.Create("p", AssetDomain.Formal,
        [
            Entry(a),
            new AssetLockEntry(a.AssetGuid, AssetKind.Card, "pack/p/a-shadow", AssetVersion.Parse("2.0.0")),
        ]);

        var result = loader.Load([malformed]);

        Assert.Equal(AssetLoadStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == AssetLoadIssueCode.DuplicateLockEntry);
        Assert.Same(cached, loader.Current); // 结构化拒绝不污染缓存
    }

    [Fact]
    public void 首次装载全量落地并缓存列表()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);

        var result = loader.Load([FormalLock("p", a, b)]);

        Assert.Equal(AssetLoadStatus.Loaded, result.Status);
        Assert.True(result.Ok);
        Assert.Null(result.Failure);
        Assert.Equal(0, result.FirstChangedLayer);
        Assert.Equal(new[] { "pack/p/a", "pack/p/b" }, result.Reloaded.Select(item => item.AppId));
        Assert.Empty(result.Retired);
        Assert.Equal(new[] { "pack/p/a", "pack/p/b" }, sink.Applied);
        Assert.Equal(2, loader.Current!.Formal.Count);
        Assert.Equal(2, loader.Current.Formal.LayerCount);
    }

    [Fact]
    public void 从首个变更层起增量重载其后全部资产()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var c1 = Register(registry, "p", "c", version: "1.0.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);
        var c2 = Register(registry, "p", "c", version: "1.1.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);

        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);
        loader.Load([FormalLock("p", a, b, c1)]);
        var callsAfterFirstLoad = sink.Calls.Count;

        var result = loader.Load([FormalLock("p", a, b, c2)]);

        Assert.Equal(AssetLoadStatus.Reloaded, result.Status);
        Assert.Equal(2, result.FirstChangedLayer);
        Assert.Equal(new[] { "pack/p/c" }, result.Reloaded.Select(item => item.AppId));
        Assert.Equal(new[] { "pack/p/c" }, result.Retired.Select(item => item.AppId));

        // 0/1 层不动：只有变化层起的条目进出装载面。
        Assert.Equal([$"revert:pack/p/c#1.0.0", "apply:pack/p/c#1.1.0"], sink.Calls.Skip(callsAfterFirstLoad));
        Assert.Equal(new[] { "pack/p/a", "pack/p/b", "pack/p/c" }, sink.Applied);
        Assert.Equal(
            AssetVersion.Parse("1.1.0"),
            loader.Current!.Formal.Layers[2].Items.Single().Version);
    }

    [Fact]
    public void 第零层变更时整表重载()
    {
        var registry = new AssetRegistry();
        var a1 = Register(registry, "p", "a", version: "1.0.0");
        var a2 = Register(registry, "p", "a", version: "1.1.0");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);

        var loader = new AssetLoader(registry);
        loader.Load([FormalLock("p", a1, b)]);

        var result = loader.Load([FormalLock("p", a2, b)]);

        Assert.Equal(0, result.FirstChangedLayer);
        Assert.Equal(2, result.Reloaded.Count);
        Assert.Equal(new[] { "pack/p/a", "pack/p/b" }, result.Reloaded.Select(item => item.AppId));
    }

    [Fact]
    public void 列表无变更时不重复装载()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var sink = new RecordingSink();
        var judicators = new JudicatorRegistry();
        var gate = new RecordingGate();
        judicators.Register(ReloadGateJudicator.Name, gate);
        var loader = new AssetLoader(registry, sink, judicators);

        loader.Load([FormalLock("p", a)]);
        var callsAfterFirstLoad = sink.Calls.Count;
        var result = loader.Load([FormalLock("p", a)]);

        Assert.Equal(AssetLoadStatus.Unchanged, result.Status);
        Assert.Equal(-1, result.FirstChangedLayer);
        Assert.Empty(result.Reloaded);
        Assert.Equal(callsAfterFirstLoad, sink.Calls.Count);
        Assert.Empty(gate.Requests); // 无变更不过门禁
    }

    [Fact]
    public void 装载失败全有或全无并保持旧列表()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var c1 = Register(registry, "p", "c", version: "1.0.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);
        var c2 = Register(registry, "p", "c", version: "1.1.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);

        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);
        loader.Load([FormalLock("p", a, b, c1)]);

        sink.FailOnApplyVersion = "1.1.0";
        var failed = loader.Load([FormalLock("p", a, b, c2)]);

        Assert.Equal(AssetLoadStatus.Failed, failed.Status);
        Assert.False(failed.Ok);
        Assert.NotNull(failed.Failure);
        Assert.Contains("测试注入", failed.Failure!, StringComparison.Ordinal);
        Assert.Null(failed.Plan); // 缓存未被替换

        // 回滚：缓存与装载面都回到旧列表（旧段被恢复）。
        Assert.Equal(
            AssetVersion.Parse("1.0.0"),
            loader.Current!.Formal.Layers[2].Items.Single().Version);
        Assert.Equal(new[] { "pack/p/a", "pack/p/b", "pack/p/c" }, sink.Applied);

        // 失败后可再次重载。
        sink.FailOnApplyVersion = null;
        var retried = loader.Load([FormalLock("p", a, b, c2)]);
        Assert.Equal(AssetLoadStatus.Reloaded, retried.Status);
        Assert.Equal(
            AssetVersion.Parse("1.1.0"),
            loader.Current!.Formal.Layers[2].Items.Single().Version);
    }

    [Fact]
    public void 门禁拒绝时结构化返回且状态不变()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);

        var sink = new RecordingSink();
        var judicators = new JudicatorRegistry();
        judicators.Register(ReloadGateJudicator.Name, new DenyingGate("对局正在等待目标选择"));
        var loader = new AssetLoader(registry, sink, judicators);
        loader.Load([FormalLock("p", a)]);

        var callsAfterFirstLoad = sink.Calls.Count;
        var rejected = loader.Load([FormalLock("p", a, b)]);

        Assert.Equal(AssetLoadStatus.Rejected, rejected.Status);
        Assert.False(rejected.Ok);
        Assert.Equal("对局正在等待目标选择", rejected.Failure);
        Assert.NotNull(rejected.Plan); // 列表已算出，只是未落地
        Assert.Equal(callsAfterFirstLoad, sink.Calls.Count);
        Assert.Equal(1, loader.Current!.Formal.Count);
    }

    [Fact]
    public void 门禁收到首变更层与待重载条目()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var c1 = Register(registry, "p", "c", version: "1.0.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);
        var c2 = Register(registry, "p", "c", version: "1.1.0", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);

        var judicators = new JudicatorRegistry();
        var gate = new RecordingGate();
        judicators.Register(ReloadGateJudicator.Name, gate);
        var loader = new AssetLoader(registry, sink: null, judicators);
        loader.Load([FormalLock("p", a, b, c1)]);

        loader.Load([FormalLock("p", a, b, c2)]);

        var request = Assert.Single(gate.Requests);
        Assert.Equal(AssetDomain.Formal, request.Domain);
        Assert.Equal(2, request.FirstChangedLayer);
        Assert.Equal(new[] { "pack/p/c" }, request.Reloaded.Select(item => item.AppId));
        Assert.Equal(new[] { "pack/p/c" }, request.Retired.Select(item => item.AppId));
    }

    [Fact]
    public void 未注册门禁视为无门禁()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);

        var loader = new AssetLoader(registry, sink: null, new JudicatorRegistry());
        loader.Load([FormalLock("p", a)]);

        var result = loader.Load([FormalLock("p", a, b)]);

        Assert.Equal(AssetLoadStatus.Reloaded, result.Status);
    }

    [Fact]
    public void 移除的资产从装载面摘除()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var c = Register(registry, "p", "c", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);

        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);
        loader.Load([FormalLock("p", a, b, c)]);

        var result = loader.Load([FormalLock("p", a, b)]);

        Assert.Equal(AssetLoadStatus.Reloaded, result.Status);
        Assert.Equal(2, result.FirstChangedLayer); // 第 2 层整体消失
        Assert.Equal(new[] { "pack/p/c" }, result.Retired.Select(item => item.AppId));
        Assert.Empty(result.Reloaded);
        Assert.Equal(new[] { "pack/p/a", "pack/p/b" }, sink.Applied);
        Assert.Equal(2, loader.Current!.Formal.Count);
    }

    [Fact]
    public void 两域差量各自独立_只有测试段变化时只重装测试段()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var t1 = Register(registry, "p", "t", version: "1.0.0.1", domain: AssetDomain.Test);
        var t2 = Register(registry, "p", "t", version: "1.0.0.2", domain: AssetDomain.Test);

        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);
        loader.Load([FormalLock("p", a), TestLock("p", t1)]);

        var result = loader.Load([FormalLock("p", a), TestLock("p", t2)]);

        Assert.Equal(AssetLoadStatus.Reloaded, result.Status);
        Assert.False(result.Deltas.Single(delta => delta.Domain == AssetDomain.Formal).Changed);
        var testDelta = result.Deltas.Single(delta => delta.Domain == AssetDomain.Test);
        Assert.Equal(0, testDelta.FirstChangedLayer);
        Assert.Equal(new[] { "pack/p/t" }, testDelta.Reloaded.Select(item => item.AppId));
        Assert.Equal(AssetDomain.Test, result.Deltas.First(delta => delta.Changed).Domain);

        // 正式段条目未被触碰：正式域的 guid 仍在装载面，测试段换成了新版本。
        var planned = loader.Current!.Test.Items.Single();
        Assert.Equal(t2.AssetGuid, planned.AssetGuid);
    }

    [Fact]
    public void 结构性非法时不装载且缓存不变()
    {
        var registry = new AssetRegistry();
        var v1 = Register(registry, "p", "a", version: "1.0.0");
        var v2 = Register(registry, "p", "a", version: "1.1.0");

        var sink = new RecordingSink();
        var loader = new AssetLoader(registry, sink);
        loader.Load([FormalLock("p", v1)]);
        var callsAfterFirstLoad = sink.Calls.Count;

        var invalid = loader.Load([FormalLock("p", v1), FormalLock("q", v2)]);

        Assert.Equal(AssetLoadStatus.Invalid, invalid.Status);
        Assert.Contains(invalid.Issues, issue => issue.Code == AssetLoadIssueCode.LockConflict);
        Assert.Equal(callsAfterFirstLoad, sink.Calls.Count);
        Assert.Equal(AssetVersion.Parse("1.0.0"), loader.Current!.Formal.Items.Single().Version);
    }
}
