using Orc.Core;
using Orc.Core.Model;
using Orc.Tests.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 判定器包装执行接线（S9 遗留项）：组件资产声明的 <c>judge-wraps</c>（<see cref="HandlerAsset.Judge"/>）
/// 在判定器调用点上生效——**与成员包装器共用同一注册面**（R1i），相位语义相同
/// （<c>before</c> FIFO／<c>after</c> FIFO／<c>replace</c> 最后声明者）。
/// </summary>
public class JudgeWrapExecutionTests
{
    private static JudicatorRegistry RegistryWithProbe(WrapRegistry? wraps, Func<object[]?, object[]?> logic)
    {
        var registry = new JudicatorRegistry { ModelWraps = wraps };
        registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(logic));
        return registry;
    }

    [Fact]
    public void 判定器包装_after在新注册面生效并改写结果()
    {
        var wraps = new WrapRegistry();
        var registry = RegistryWithProbe(wraps, _ => new object[] { "core" });

        wraps.RegisterJudge(
            WrapTarget.ForJudicator(JudicatorTestData.ProbeName),
            WrapperPhase.After,
            (_, result) => new object[] { $"{(result is { Length: > 0 } ? result[0] : null)}+wrap" });

        Assert.Equal(new object[] { "core+wrap" }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }

    [Fact]
    public void 判定器包装_before按声明序正序_after按声明序()
    {
        var wraps = new WrapRegistry();
        var trace = new List<string>();
        var registry = RegistryWithProbe(wraps, _ =>
        {
            trace.Add("core");
            return new object[] { "core" };
        });

        var target = WrapTarget.ForJudicator(JudicatorTestData.ProbeName);
        wraps.RegisterJudge(target, WrapperPhase.Before, (_, _) =>
        {
            trace.Add("before1");
            return null;
        });
        wraps.RegisterJudge(target, WrapperPhase.Before, (_, _) =>
        {
            trace.Add("before2");
            return null;
        });
        wraps.RegisterJudge(target, WrapperPhase.After, (_, result) =>
        {
            trace.Add("after1");
            return result;
        });
        wraps.RegisterJudge(target, WrapperPhase.After, (_, result) =>
        {
            trace.Add("after2");
            return result;
        });

        registry.Invoke(JudicatorTestData.ProbeName, null);

        // R1i 修订（D3）：before 与 after 同为声明序正序；旧 LIFO 期望 "before2, before1" 已按 FIFO 改写。
        Assert.Equal(new[] { "before1", "before2", "core", "after1", "after2" }, trace);
    }

    [Fact]
    public void 判定器包装_replace最后声明者接管核心逻辑()
    {
        var wraps = new WrapRegistry();
        var registry = RegistryWithProbe(wraps, _ => new object[] { "core" });
        var target = WrapTarget.ForJudicator(JudicatorTestData.ProbeName);

        wraps.RegisterJudge(target, WrapperPhase.Replace, (_, _) => new object[] { "first" });
        wraps.RegisterJudge(target, WrapperPhase.Replace, (_, _) => new object[] { "last" });

        Assert.Equal(new object[] { "last" }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }

    [Fact]
    public void 判定器包装_声明实体的承载对象追加到载荷末位()
    {
        var wraps = new WrapRegistry();
        var registry = RegistryWithProbe(wraps, args => args ?? Array.Empty<object>());

        var runtime = new OrchestrationRuntime(
            new PrefabCatalog().Add(ModelKit.EnginePrefab()), new HandlerRegistry());
        var host = runtime.Engine;
        var card = new object();
        host.Bound = card;

        object? seenHost = null;
        wraps.RegisterJudge(
            WrapTarget.ForJudicator(JudicatorTestData.ProbeName),
            WrapperPhase.After,
            (args, result) =>
            {
                seenHost = args?[^1];
                return result;
            },
            declaring: host);

        registry.Invoke(JudicatorTestData.ProbeName, new object[] { "a" });

        Assert.Same(card, seenHost);
    }

    [Fact]
    public void 判定器包装_内联包装器无声明实体时不额外补位()
    {
        var wraps = new WrapRegistry();
        var registry = RegistryWithProbe(wraps, args => args ?? Array.Empty<object>());

        var seenLength = -1;
        wraps.RegisterJudge(
            WrapTarget.ForJudicator(JudicatorTestData.ProbeName),
            WrapperPhase.After,
            (args, result) =>
            {
                seenLength = args?.Length ?? -1;
                return result;
            });

        registry.Invoke(JudicatorTestData.ProbeName, new object[] { "a", "b" });

        Assert.Equal(2, seenLength);
    }

    [Fact]
    public void 未接入新注册面的判定器注册表行为不变()
    {
        var registry = new JudicatorRegistry();
        registry.Register(JudicatorTestData.ProbeName, new PassThroughJudicator(_ => new object[] { "core" }));

        Assert.Equal(new object[] { "core" }, registry.Invoke(JudicatorTestData.ProbeName, null));
    }
}
