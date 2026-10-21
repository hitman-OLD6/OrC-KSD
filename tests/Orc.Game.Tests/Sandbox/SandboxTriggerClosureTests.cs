using System.Reflection;
using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests.Sandbox;

/// <summary>
/// R1 封闭性验收（引擎级；经 <c>InternalsVisibleTo</c> 直取保留触发器实例）：
/// ①封闭后注册面一律抛（异常文本含触发器名、handler 集不变）；
/// ②包装器注册同样被拒；
/// ③公开面无通往保留触发器的引用路径（反射扫公开成员 ＋ 按名解析面）；
/// ④handler 缺失＝构造期 fail-fast；
/// ⑤（回归）封闭态默认不启用——既有触发器注册/执行语义不变。
/// </summary>
public sealed class SandboxTriggerClosureTests
{
    [Fact]
    public async Task Register_AfterSeal_Throws_WithTriggerName()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var (name, trigger) = match.Sandbox.ReservedTriggers[0];

        Assert.True(trigger.IsSealed, "保留触发器在构造返回时应已封闭（不存在可注册窗口）。");

        var before = trigger.Events.Count;
        var ex = Assert.Throws<InvalidOperationException>(
            () => trigger.Register("插队", static (_, _, _) => Task.CompletedTask));

        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, trigger.Events.Count);
    }

    [Fact]
    public async Task WrapperRegistration_AfterSeal_Throws()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var (name, trigger) = match.Sandbox.ReservedTriggers[0];
        var registration = match.Sandbox.InstantiateRegistration;

        Assert.Throws<InvalidOperationException>(
            () => trigger.RegisterBeforeWrapper(registration, static (_, _, _) => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(
            () => trigger.RegisterAfterWrapper(registration, static (_, _, _) => Task.CompletedTask));

        var ex = Assert.Throws<InvalidOperationException>(
            () => trigger.RegisterReplaceWrapper(registration, static (_, _, _) => Task.CompletedTask));
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);

        Assert.Single(trigger.Events);
    }

    [Fact]
    public async Task PublicSurface_DoesNotExposeReservedTriggers()
    {
        var match = await SandboxTestKit.CreateMatchAsync();

        // ① 反射扫公开成员：Match 与 SandboxPrimitives 的 public 属性/方法/字段都不是触发器类型或触发器元数据。
        AssertNoTriggerTypedMember(typeof(Match));
        AssertNoTriggerTypedMember(match.Sandbox.GetType());

        // ② 按名解析面：保留触发器不登记（无匹配名、无可达实例）。
        var namedTriggers = match.Players[0].MatchTriggers;
        Assert.NotNull(namedTriggers);
        foreach (var (name, trigger) in match.Sandbox.ReservedTriggers)
        {
            Assert.False(namedTriggers!.TryFind(name, out var resolved, out _),
                $"保留触发器 '{name}' 不应进入按名解析面。");
            Assert.Null(resolved);
        }

        // ③ 预制体 injects：保留触发器不提供任何预制体 id / handler key 登记面。
        foreach (var (name, _) in match.Sandbox.ReservedTriggers)
        {
            Assert.DoesNotContain(name, match.Engine.Prefabs.PrefabIds);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void Construction_MissingHandler_FailsFast()
    {
        // handler 不齐（含 null）＝装配性错误：构造期即抛，实例不产出（不存在"可触发但缺 handler"的中间态）。
        Assert.Throws<ArgumentNullException>(
            () => Orc.Game.Sandbox.SandboxPrimitives.CreateSealedTrigger("sandbox.probe", "probe", null, out _));
    }

    [Fact]
    public async Task Trigger_DefaultNotSealed_ExistingRegistrationStillWorks()
    {
        // 回归：内核封闭态默认不启用——既有触发器的注册与执行语义逐条不变。
        var trigger = new Trigger<SandboxProbeView>("探针");
        Assert.False(trigger.IsSealed);

        var hits = 0;
        trigger.Register("一次", (_, _, _) =>
        {
            hits++;
            return Task.CompletedTask;
        });

        var match = await SandboxTestKit.CreateMatchAsync();
        await trigger.InvokeAsync(match.Engine, new Dictionary<string, object?> { ["Value"] = 1 });

        Assert.Equal(1, hits);
        Assert.False(trigger.IsSealed, "只有显式 Seal() 才封闭——注册/执行均不改变封闭态。");
    }

    [Fact]
    public async Task Export_MarksUnauthorized_AsUnavailable()
    {
        // R2 二选一取「列出但标注」：未授权实例的诊断导出仍列出六个保留触发器，但 authorized=false（不可用）。
        var unauthorized = await SandboxTestKit.CreateMatchAsync(authorized: false);
        using var document = System.Text.Json.JsonDocument.Parse(unauthorized.ExportReservedTriggersJson());
        var items = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(6, items.Length);
        foreach (var item in items)
        {
            Assert.True(item.GetProperty("sealed").GetBoolean());
            Assert.False(item.GetProperty("authorized").GetBoolean(), "未授权实例应标注 authorized=false（unavailable）。");
            Assert.Equal(1, item.GetProperty("handlers").GetArrayLength());
        }
    }

    /// <summary>公开成员扫描：断言不存在返回/接收触发器类型（或触发器元数据接口）的成员。</summary>
    private static void AssertNoTriggerTypedMember(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            Assert.False(IsTriggerType(property.PropertyType),
                $"{type.Name}.{property.Name} 暴露了触发器类型（保留触发器须不可达）。");
        }

        foreach (var field in type.GetFields(flags))
        {
            Assert.False(IsTriggerType(field.FieldType),
                $"{type.Name}.{field.Name} 暴露了触发器类型（保留触发器须不可达）。");
        }

        foreach (var method in type.GetMethods(flags))
        {
            Assert.False(IsTriggerType(method.ReturnType),
                $"{type.Name}.{method.Name}() 返回了触发器类型（保留触发器须不可达）。");
            foreach (var parameter in method.GetParameters())
            {
                Assert.False(IsTriggerType(parameter.ParameterType),
                    $"{type.Name}.{method.Name}({parameter.Name}) 接收触发器类型（保留触发器须不可达）。");
            }
        }
    }

    private static bool IsTriggerType(Type type)
    {
        if (type == typeof(ITriggerMetadata))
        {
            return true;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Trigger<>))
            {
                return true;
            }
        }

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Trigger<>);
    }
}

/// <summary>探针视图（仅用于封闭态回归用例）。</summary>
[ContextView]
public class SandboxProbeView
{
    /// <summary>探针载荷。</summary>
    [Optional]
    [Read]
    public virtual int? Value { get; set; }
}
