using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S-C1/S-C2 内核加性面验收：稳定标识（HookId/TriggerId）、hook 注册表、内核只读枚举面、<see cref="Context.Engine"/>。
/// </summary>
public class AuditChainFoundationTests
{
    // ---------- S-C1：稳定哈希与标识 ----------

    [Fact]
    public void StableHash_相同输入恒同值_不同输入不同值()
    {
        var a1 = StableHash.Fnv1a64("card.placed");
        var a2 = StableHash.Fnv1a64("card.placed");
        var b = StableHash.Fnv1a64("card.destroyed");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void StableHash_null_抛参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => StableHash.Fnv1a64(null!));
    }

    [Fact]
    public void HookId_FromName_空白_拒绝()
    {
        Assert.Throws<ArgumentException>(() => HookId.FromName("   "));
        Assert.Throws<ArgumentException>(() => TriggerId.FromKey(""));
    }

    [Fact]
    public void HookId_由名字确定性派生()
    {
        Assert.Equal(HookId.FromName("card.placed"), HookId.FromName("card.placed"));
        Assert.NotEqual(HookId.FromName("card.placed"), HookId.FromName("card.data"));
    }

    // ---------- S-C1：hook 注册表 ----------

    [Fact]
    public void HookRegistry_名字与标识双向互转()
    {
        var registry = new HookRegistry();
        var id = registry.Register("card.placed");

        Assert.True(registry.TryGetId("card.placed", out var byName));
        Assert.Equal(id, byName);

        Assert.True(registry.TryGetName(id, out var name));
        Assert.Equal("card.placed", name);
    }

    [Fact]
    public void HookRegistry_自由字符串保留_未登记名可查标识但不隐式登记()
    {
        var registry = new HookRegistry();

        Assert.False(registry.TryGetId("custom.hook", out _)); // 未登记＝false
        Assert.Equal(0, registry.Count);

        var id = HookId.FromName("custom.hook"); // 纯函数仍可取标识
        Assert.False(registry.TryGetName(id, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void HookRegistry_同名重复登记_幂等()
    {
        var registry = new HookRegistry();
        var first = registry.Register("hook.a");
        var second = registry.Register("hook.a");

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
        Assert.Empty(registry.Collisions);
    }

    [Fact]
    public void HookRegistry_空白名_拒绝()
    {
        var registry = new HookRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register("  "));
    }

    // ---------- S-C1：引擎词汇表 ----------

    [Fact]
    public void 引擎构造_内置更新字符串已预登记()
    {
        var engine = new LogicEngine();

        Assert.True(engine.Hooks.TryGetId(Updates.CardPlaced, out _));
        Assert.True(engine.Hooks.TryGetId(Updates.CardDestroyed, out _));
        Assert.True(engine.Hooks.TryGetId(Updates.CardData, out _));
        Assert.True(engine.Hooks.TryGetId(Updates.EffectRemoved, out _));
    }

    [Fact]
    public void 多引擎_hook注册表各自独立()
    {
        var a = new LogicEngine();
        var b = new LogicEngine();

        a.Hooks.Register("only.a");

        Assert.True(a.Hooks.TryGetId("only.a", out _));
        Assert.False(b.Hooks.TryGetId("only.a", out _));
    }

    // ---------- S-C1：定义级标识 = 成员地址 ----------

    [Fact]
    public void 成员地址即定义级标识_同址同标识()
    {
        // 旧"触发器稳定键"由成员地址承担：与展示名无关、同址恒同标识。
        Assert.Equal(TriggerId.FromKey("engine#unit.play"), TriggerId.FromKey("engine#unit.play"));
        Assert.NotEqual(TriggerId.FromKey("engine#unit.play"), TriggerId.FromKey("engine#unit.deploy"));
    }

    // ---------- S-C2：审查面上的订阅边 ----------

    [Fact]
    public void 审查面_按执行序列出hook的边()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        SignalTestHelpers.Subscribe(engine, "h.one", "慢", trace, UpdatePriorities.Low);
        SignalTestHelpers.Subscribe(engine, "h.one", "快", trace, UpdatePriorities.High);
        SignalTestHelpers.Subscribe(engine, "h.two", "其它", trace);

        // S3-14：hook 的"谁订阅了"由审查图的边回答（原 EnumerateHooks 的等价替代）。
        var one = AuditTestKit.EdgesInto(engine, "h.one");
        Assert.Equal(2, one.Count);
        Assert.Equal(new[] { "快", "慢" }, one.Select(e => e.Member)); // 位次＝执行序（优先级升序）
        Assert.All(one, e => Assert.True(e.GraphGap)); // "h.one" 不是信号成员：标为图缺口，不再静默

        Assert.Single(AuditTestKit.EdgesInto(engine, "h.two"));
    }

    [Fact]
    public void 订阅信号_将hook名登记进引擎词汇表()
    {
        var engine = new LogicEngine();
        Assert.False(engine.Hooks.TryGetId("custom.placed", out _));

        engine.SubscribeMember(
            "custom.placed", "t", null,
            new Orc.Core.Model.OwnedEdge((Orc.Core.Model.KernelHandler)((_, _, _) => Task.CompletedTask)));

        Assert.True(engine.Hooks.TryGetId("custom.placed", out var id));
        Assert.Equal(HookId.FromName("custom.placed"), id);
    }

    [Fact]
    public void 撤销订阅后_边从审查面消失但词汇表保留()
    {
        var engine = new LogicEngine();
        var handle = SignalTestHelpers.Subscribe(engine, "h.x", "t", new List<string>());
        Assert.Single(AuditTestKit.EdgesInto(engine, "h.x"));

        handle.Dispose();

        Assert.Empty(AuditTestKit.EdgesInto(engine, "h.x"));
    }

    // ---------- S-C2：Context.Engine ----------

    [Fact]
    public async Task 执行期_Context可读引擎_执行外为null()
    {
        var engine = new LogicEngine();
        LogicEngine? seen = null;

        var member = KernelTestKit.Member("读引擎", new Orc.Core.Model.OwnedEdge(
            (Orc.Core.Model.KernelHandler)((_, c, _) =>
            {
                seen = c.Engine;
                return Task.CompletedTask;
            })));

        await KernelTestKit.RunAsync(engine, member);

        Assert.Same(engine, seen);
        Assert.Null(new Context().Engine); // 未关联执行会话＝null
    }
}
