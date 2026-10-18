using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 判定器机制测试（J1）：注册表与契约——注册/引用/未注册 fail-fast/重复拒绝/句柄可寻址/契约不符/无状态。
/// 对应验收：①②③（前半）④（无状态）⑧。
/// </summary>
public class JudicatorRegistryTests
{
    // ---------- 注册与引用：按名调用成功；注册所得＝解析所得（同一可寻址锚） ----------

    [Fact]
    public void Register_Returns_Handle_Resolve_Returns_Same_Anchor_And_Invoke_Succeeds()
    {
        var registry = new JudicatorRegistry();
        var registered = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "ok" }));

        // 句柄形态：Name 可读；注册所得＝解析所得（同一可寻址锚——同一实例）
        Assert.Equal(JudicatorTestData.ProbeName, registered.Name);
        var resolved = registry.Resolve(JudicatorTestData.ProbeName);
        Assert.Same(registered, resolved);

        // 按名调用成功（经统一主方法）
        Assert.Equal(new object[] { "ok" }, registry.Invoke(JudicatorTestData.ProbeName, null));

        // 持有句柄的调用（等效通道）
        Assert.Equal(new object[] { "ok" }, registered.Invoke(null));

        // 解析所得句柄用于 moding 锚定 → 按名调用生效（解析口与 moding 口不错位）
        var m = registry.RegisterReplaceWrapper(resolved, _ => new object[] { "改写" });
        Assert.NotNull(m);
        Assert.Equal(new object[] { "改写" }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }

    [Fact]
    public void Resolve_Is_Ordinal_And_Name_Is_Validated()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => null));

        // ordinal 逐字敏感（大小写不同＝未注册——fail-fast）
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("TEST.PROBE"));

        // null/空白名＝参数错误（明确拒绝）
        Assert.Throws<ArgumentNullException>(() => registry.Resolve(null!));
        Assert.Throws<ArgumentException>(() => registry.Resolve(" "));
        Assert.Throws<ArgumentException>(() => registry.Register("", new PassThroughJudicator(_ => null)));
    }

    // ---------- 显式失败族：重复注册拒绝；未注册引用 fail-fast ----------

    [Fact]
    public void Register_Duplicate_Name_Is_Rejected()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => null));

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => null)));
        Assert.Contains(JudicatorTestData.ProbeName, ex.Message);
    }

    [Fact]
    public void Register_Null_Judicator_Is_Rejected()
    {
        var registry = new JudicatorRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(JudicatorTestData.ProbeName, null!));
    }

    [Fact]
    public void Unregistered_Reference_Fails_Fast_No_Tolerant_Bypass()
    {
        var registry = new JudicatorRegistry();

        // 按名解析/调用＝明确失败（无宽容旁路、不设 Try 变体）
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve(JudicatorTestData.ProbeName));
        Assert.Throws<KeyNotFoundException>(() => registry.Invoke(JudicatorTestData.ProbeName, null));
    }

    // ---------- 子类形态与契约：强类型适配；拆包失败 fail-fast；无状态 ----------

    [Fact]
    public void Typed_Subclass_Adapts_Payload_And_Result()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "x" && count > 2));

        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 3 }));
        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.PairName, new object[] { "y", 3 }));
    }

    [Fact]
    public void Payload_Contract_Violation_Fails_Fast()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => true));

        // 缺参：明确失败（fail-fast 族——参数契约不符）
        Assert.Throws<ArgumentException>(() => registry.Invoke(JudicatorTestData.PairName, new object[] { "x" }));

        // 类型不符：明确失败（不静默容忍）
        Assert.Throws<ArgumentException>(() => registry.Invoke(JudicatorTestData.PairName, new object[] { 123, 1 }));
    }

    [Fact]
    public void Stateless_Multiple_And_Alternating_Calls_Are_Consistent()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(args => new object[] { (int)args![0] + 1 }));
        registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "x"));

        // 同一实例、同一输入多次调用输出一致（无状态的直接行为证据）
        var a1 = registry.Invoke(JudicatorTestData.ProbeName, new object[] { 1 });
        var a2 = registry.Invoke(JudicatorTestData.ProbeName, new object[] { 1 });
        var a3 = registry.Invoke(JudicatorTestData.ProbeName, new object[] { 1 });
        Assert.Equal(a1, a2);
        Assert.Equal(a2, a3);

        // 交替调用（中间夹另一判定器调用）仍一致
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "x", 0 }));
        var a4 = registry.Invoke(JudicatorTestData.ProbeName, new object[] { 1 });
        Assert.Equal(a1, a4);
    }
}
