using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 判定器 moding（逻辑替换）机制测试（J1）：替换生效/纯替换对照/栈语义/注销回退/双形态同栈/快照一致/
/// 幂等宽容/句柄即权限/全局生效/跨实例隔离/异常对称/适配错误。
/// 对应验收：④⑤⑥⑦⑧。
/// </summary>
public class JudicatorWrapperTests
{
    // ---------- 统一签名：替换生效、纯替换对照、注销回退 ----------

    [Fact]
    public void Unified_Moding_Is_Pure_Replacement_And_Unregister_Falls_Back()
    {
        var registry = new JudicatorRegistry();
        var originalRuns = 0;
        var modingRuns = 0;
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => { originalRuns++; return new object[] { "默认" }; }));

        // 无 moding：默认逻辑
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(1, originalRuns);

        // 注册 moding：替换生效（纯替换——默认逻辑不执行）
        var m = registry.RegisterReplaceWrapper(handle, _ => { modingRuns++; return new object[] { "替换" }; });
        Assert.NotNull(m);
        Assert.Equal(new object[] { "替换" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(1, originalRuns); // 对照观测：原逻辑无新增执行（替换生效时不可达）
        Assert.Equal(1, modingRuns);

        // 注销：回退默认逻辑
        Assert.True(registry.UnregisterWrapper(m!));
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(2, originalRuns);
    }

    // ---------- 栈语义：最后者胜、逐层回退、重注册入栈顶 ----------

    [Fact]
    public void Stack_Last_Wins_Layered_Fallback_And_ReRegister()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));

        var m1 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "m1" });
        var m2 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "m2" });
        var m3 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "m3" });
        Assert.NotNull(m1);
        Assert.NotNull(m2);
        Assert.NotNull(m3);

        Assert.Equal(new object[] { "m3" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 最后者胜

        Assert.True(registry.UnregisterWrapper(m1!)); // 注销栈底
        Assert.Equal(new object[] { "m3" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 解析＝剩余项中最后注册者

        Assert.True(registry.UnregisterWrapper(m3!));
        Assert.Equal(new object[] { "m2" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 回退 m2

        Assert.True(registry.UnregisterWrapper(m2!));
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 全部注销＝回退默认逻辑

        var m4 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "m4" });
        Assert.NotNull(m4);
        Assert.Equal(new object[] { "m4" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 重注册按新注册序入栈顶
    }

    // ---------- 双形态：强类型面替换/回退；跨形态同一栈 ----------

    [Fact]
    public void Typed_Moding_Replaces_And_Falls_Back()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => false));

        // 强类型注入（注入者无需手工拆包/组包）
        var m = registry.RegisterReplaceWrapper(handle, (PairJudicator.Rule)((tag, count) => tag == "x"));
        Assert.NotNull(m);

        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));   // 替换生效（强类型面）
        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.PairName, new object[] { "y", 0 }));

        Assert.True(registry.UnregisterWrapper(m!));
        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));  // 注销回退默认逻辑
    }

    [Fact]
    public void Cross_Form_Stack_Ordering_Same_Stack()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => false));

        // 统一签名注入后再强类型注入：最后者胜（强类型）
        var u = registry.RegisterReplaceWrapper(handle, _ => new object[] { "统一" });
        var t = registry.RegisterReplaceWrapper(handle, (PairJudicator.Rule)((tag, count) => true));
        Assert.NotNull(u);
        Assert.NotNull(t);
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));

        // 注销强类型：回退统一签名项（跨形态同一栈的直接证据）
        Assert.True(registry.UnregisterWrapper(t!));
        Assert.Equal(new object[] { "统一" }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));

        // 反方向：强类型在前、统一签名在后 → 统一签名最后者胜；注销后回退强类型
        var t2 = registry.RegisterReplaceWrapper(handle, (PairJudicator.Rule)((tag, count) => true));
        var u2 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "统一2" });
        Assert.NotNull(t2);
        Assert.NotNull(u2);
        Assert.Equal(new object[] { "统一2" }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));

        Assert.True(registry.UnregisterWrapper(u2!));
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));
    }

    // ---------- 快照一致：执行期间重入增删（本次不打断；下一次生效） ----------

    [Fact]
    public void Snapshot_Reentrant_Register_And_Self_Unregister_During_Execution()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));

        // 执行期间重入注册（对同一条目）：本次已解析选择固定、不受影响；下一次调用生效
        JudicatorWrapperRegistration? inner = null;
        var outer = registry.RegisterReplaceWrapper(handle, _ =>
        {
            inner ??= registry.RegisterReplaceWrapper(handle, _ => new object[] { "inner" });
            return new object[] { "outer" };
        });
        Assert.NotNull(outer);
        Assert.Equal(new object[] { "outer" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 本次执行完整跑完 outer
        Assert.NotNull(inner);
        Assert.Equal(new object[] { "inner" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 下次调用：重入注册项生效

        // 执行期间自注销：本次不受影响；下一次调用回退上一未注销项
        JudicatorWrapperRegistration? self = null;
        self = registry.RegisterReplaceWrapper(handle, _ =>
        {
            registry.UnregisterWrapper(self!);
            return new object[] { "self" };
        });
        Assert.NotNull(self);
        Assert.Equal(new object[] { "self" }, registry.Invoke(JudicatorTestData.ProbeName, null));  // 注销不打断本次
        Assert.Equal(new object[] { "inner" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 下一次：回退上一项
    }

    // ---------- 幂等宽容：跨注册表注册/注销；重复注销；null 参数拒绝 ----------

    [Fact]
    public void RegisterReplaceWrapper_Cross_Registry_Fails_Fast()
    {
        var registryA = new JudicatorRegistry();
        var registryB = new JudicatorRegistry();
        var handleB = registryB.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "B默认" }));

        // 跨注册表句柄：无操作、不抛错（返回 null、不生效）
        Assert.Throws<ArgumentException>(() => registryA.RegisterReplaceWrapper(handleB, _ => new object[] { "不该执行" }));

        Assert.Equal(new object[] { "B默认" }, registryB.Invoke(JudicatorTestData.ProbeName, null)); // B 不受影响
    }

    [Fact]
    public void Unregister_Repeat_And_Cross_Registry_Are_Silent_Noop()
    {
        var registryA = new JudicatorRegistry();
        var registryB = new JudicatorRegistry();
        var handleA = registryA.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));
        var handleB = registryB.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));

        // 句柄即权限：任意持有句柄者可注销（无注册者校验）
        var mA = registryA.RegisterReplaceWrapper(handleA, _ => new object[] { "mA" });
        Assert.NotNull(mA);
        Assert.True(registryA.UnregisterWrapper(mA!));
        Assert.False(registryA.UnregisterWrapper(mA!)); // 重复注销：无操作、不抛错

        var mB = registryB.RegisterReplaceWrapper(handleB, _ => new object[] { "mB" });
        Assert.NotNull(mB);
        Assert.False(registryA.UnregisterWrapper(mB!)); // 跨注册表句柄：无操作、不抛错
        Assert.Equal(new object[] { "mB" }, registryB.Invoke(JudicatorTestData.ProbeName, null)); // B 不受影响

        // null 参数＝参数错误（明确拒绝——非句柄宽容域）
        Assert.Throws<ArgumentNullException>(() => registryA.RegisterReplaceWrapper(null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => registryA.RegisterReplaceWrapper(handleA, (Func<object[]?, object[]?>)null!));
        Assert.Throws<ArgumentNullException>(() => registryA.UnregisterWrapper(null!));
    }

    // ---------- 全局生效：按名+句柄两引用点同变、同回退；跨实例隔离 ----------

    [Fact]
    public void Global_Resolution_Name_And_Handle_Reference_Points_Change_Together()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));
        var cached = registry.Resolve(JudicatorTestData.ProbeName); // 缓存的引用点（拿到句柄不等于锁定逻辑）

        // moding 前：两引用点行为一致（默认）
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null)); // 按名解析的调用
        Assert.Equal(new object[] { "默认" }, cached.Invoke(null));                                // 持有句柄的调用

        // moding 后：两引用点行为同变（统一解析点——全局生效）
        var m = registry.RegisterReplaceWrapper(handle, _ => new object[] { "改写" });
        Assert.NotNull(m);
        Assert.Equal(new object[] { "改写" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(new object[] { "改写" }, cached.Invoke(null));

        // 注销后：两引用点同回退
        Assert.True(registry.UnregisterWrapper(m!));
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(new object[] { "默认" }, cached.Invoke(null));
    }

    [Fact]
    public void Instances_Are_Isolated()
    {
        var registryA = new JudicatorRegistry();
        var registryB = new JudicatorRegistry();
        var handleA = registryA.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "A默认" }));
        registryB.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "B默认" }));

        var m = registryA.RegisterReplaceWrapper(handleA, _ => new object[] { "A改写" });
        Assert.NotNull(m);

        Assert.Equal(new object[] { "A改写" }, registryA.Invoke(JudicatorTestData.ProbeName, null)); // 实例 A：moding 生效
        Assert.Equal(new object[] { "B默认" }, registryB.Invoke(JudicatorTestData.ProbeName, null)); // 实例 B：不受影响（跨实例隔离）
    }

    // ---------- 异常对称性：默认与替换的异常可见性一致（不得吞异常） ----------

    [Fact]
    public void Exception_Symmetry_Default_And_Moding_Exceptions_Propagate()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => throw new InvalidOperationException("默认爆炸")));

        // 默认逻辑异常：传播（直接可见）
        var ex1 = Assert.Throws<InvalidOperationException>(() => registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal("默认爆炸", ex1.Message);

        // moding 替换后：异常同样传播（不吞、不包装——可见性不变）
        var m = registry.RegisterReplaceWrapper(handle, _ => throw new InvalidOperationException("替换爆炸"));
        Assert.NotNull(m);
        var ex2 = Assert.Throws<InvalidOperationException>(() => registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal("替换爆炸", ex2.Message);
    }

    // ---------- 适配错误：不支持强类型 delegate 的判定器＝fail-fast ----------

    [Fact]
    public void Typed_Injection_On_Non_Typed_Judicator_Fails_Fast()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "统一形态" }));

        // 目标判定器未声明该强类型 delegate：适配错误＝明确失败（fail-fast）
        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterReplaceWrapper(handle, (PairJudicator.Rule)((tag, count) => true)));
        Assert.Contains(JudicatorTestData.ProbeName, ex.Message);

        // 注册被拒绝、栈未变：调用行为不受影响
        Assert.Equal(new object[] { "统一形态" }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }

    // ---------- 玩家域：同一条目按玩家分桶、互不串改 ----------

    [Fact]
    public void Player_Domains_Are_Isolated_And_Default_Domain_Is_Separate()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "默认" }));

        var w0 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "P0" }, playerIndex: 0);
        var w1 = registry.RegisterReplaceWrapper(handle, _ => new object[] { "P1" }, playerIndex: 1);
        Assert.NotNull(w0);
        Assert.NotNull(w1);

        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null));
        Assert.Equal(new object[] { "P0" }, registry.Invoke(JudicatorTestData.ProbeName, null, 0));
        Assert.Equal(new object[] { "P1" }, registry.Invoke(JudicatorTestData.ProbeName, null, 1));

        Assert.True(registry.UnregisterWrapper(w0!));
        Assert.Equal(new object[] { "默认" }, registry.Invoke(JudicatorTestData.ProbeName, null, 0));
        Assert.Equal(new object[] { "P1" }, registry.Invoke(JudicatorTestData.ProbeName, null, 1));
    }

    [Fact]
    public void Player_Domain_After_Wrapper_Applies_Only_To_That_Player()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { true }));

        var w = registry.RegisterAfterWrapper(handle, (_, _) => new object[] { false }, playerIndex: 1);
        Assert.NotNull(w);

        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.ProbeName, null, 0));
        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.ProbeName, null, 1));
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }
}
