using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 服务管道（判定器注册表）：before LIFO／after FIFO／replace 取核心，参数就地改写与结果替换，强类型工厂的严格校验。
/// <para>
/// **触发器侧的包装管道段已删除**（§28）：<c>Trigger.RegisterBeforeWrapper/…/Replace</c> 随触发器类型退役；
/// 成员包装的新面（before/after/replace 相位、声明序位次、replace 接管整个边集合、免疫与停止语义）
/// 已在 <c>Model/WrapChannelTests</c> 逐条覆盖，且相位顺序已明确为 **before FIFO / after FIFO**（不再是 LIFO）。
/// </para>
/// </summary>
public class WrapperPipelineTests
{
    [Fact]
    public void Service_Pipeline_Before_Lifo_After_Fifo_Replace_Core()
    {
        var registry = new JudicatorRegistry();
        var trace = new List<string>();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ =>
        {
            trace.Add("core");
            return new object[] { "core-result" };
        }));

        registry.RegisterBeforeWrapper(handle, _ => trace.Add("b1"));
        registry.RegisterBeforeWrapper(handle, _ => trace.Add("b2"));
        registry.RegisterAfterWrapper(handle, (_, result) =>
        {
            trace.Add("a1");
            return result;
        });
        registry.RegisterAfterWrapper(handle, (_, result) =>
        {
            trace.Add("a2");
            return result;
        });

        var result = registry.Invoke(JudicatorTestData.ProbeName, null);

        Assert.Equal(new[] { "b2", "b1", "core", "a1", "a2" }, trace);
        Assert.Equal(new object[] { "core-result" }, result);
    }

    [Fact]
    public void Service_Before_Can_Rewrite_Args_In_Place_And_After_Can_Replace_Result()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(args => args!));

        registry.RegisterBeforeWrapper(handle, args => args![0] = "改写");
        registry.RegisterAfterWrapper(handle, (_, result) => new object[] { ((string)result![0]!) + "-后置" });

        var result = registry.Invoke(JudicatorTestData.ProbeName, new object[] { "原始" });

        Assert.Equal(new object[] { "改写-后置" }, result);
    }

    [Fact]
    public void Service_Typed_Factory_Before_After_And_Strict_Validation()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "ok" && count > 0));

        // 匹配签名：before＝(string,int)->void；after＝(string,int,bool)->bool
        var before = registry.RegisterBeforeWrapper(handle, Wrapper.Before<string, int>((_, _) => { }));
        var after = registry.RegisterAfterWrapper(handle, Wrapper.After<string, int, bool>((_, _, result) => !result));
        Assert.NotNull(before);
        Assert.NotNull(after);

        Assert.Equal(new object[] { false }, registry.Invoke(JudicatorTestData.PairName, new object[] { "ok", 1 }));
        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "no", 1 }));

        // 严格校验：参数类型不符＝挂载期 fail-fast
        Assert.Throws<ArgumentException>(() =>
            registry.RegisterBeforeWrapper(handle, Wrapper.Before<int>(_ => { })));
        Assert.Throws<ArgumentException>(() =>
            registry.RegisterAfterWrapper(handle, Wrapper.After<string, bool>((_, result) => result)));
    }

    [Fact]
    public void Typed_Before_Is_Observation_Only_Unified_Signature_Is_Required_For_Rewrite()
    {
        var registry = new JudicatorRegistry();
        var seen = string.Empty;
        var handle = registry.Register(JudicatorTestData.PairName, new PairJudicator((tag, count) => tag == "ok"));

        registry.RegisterBeforeWrapper(handle, Wrapper.Before<string, int>((tag, _) => seen = tag));

        Assert.Equal(new object[] { true }, registry.Invoke(JudicatorTestData.PairName, new object[] { "ok", 0 }));
        Assert.Equal("ok", seen);
    }
}
