using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>目录生成的命名与合并规则（SPEC §12.1–§12.2 的纯函数部分；扫描器在 S4 调用它们）。</summary>
public class NamingTests
{
    [Theory]
    [InlineData("DamageAsync", "damage")]
    [InlineData("Damage", "damage")]
    [InlineData("Stop", "stop")]
    [InlineData("ExileAsync", "exile")]
    [InlineData("BatchDrawAsync", "batchDraw")]
    public void 节点名默认派生_去Async后缀且首字母小写(string method, string expected)
    {
        Assert.Equal(expected, HandlerDslNaming.DeriveNodeName(method));
    }

    [Fact]
    public void 节点名派生_空方法名拒绝()
    {
        Assert.Throws<ArgumentException>(() => HandlerDslNaming.DeriveNodeName(string.Empty));
    }

    [Fact]
    public void 隐式宿主白名单封闭且只有三个()
    {
        Assert.Equal(3, HandlerDslNaming.ImplicitHostParameterNames.Count);
        Assert.True(HandlerDslNaming.IsImplicitHost("host"));
        Assert.True(HandlerDslNaming.IsImplicitHost("viewer"));
        Assert.True(HandlerDslNaming.IsImplicitHost("self"));
        Assert.False(HandlerDslNaming.IsImplicitHost("owner"));
        Assert.False(HandlerDslNaming.IsImplicitHost("card"));
    }

    [Fact]
    public void 重载合并_参数取并集_部分重载出现者为可选()
    {
        var overloads = new List<IReadOnlyList<HandlerDslNaming.ParameterSpec>>
        {
            new[] { new HandlerDslNaming.ParameterSpec("value", PinType.Number) },
            new[] { new HandlerDslNaming.ParameterSpec("value", PinType.Number), new HandlerDslNaming.ParameterSpec("count", PinType.Number) },
        };

        var pins = HandlerDslNaming.MergeOverloads(overloads, out var error);
        Assert.Null(error);
        Assert.NotNull(pins);
        Assert.Equal(new[] { "value", "count" }, pins!.Select(p => p.Name));

        Assert.True(pins[0].Required);
        Assert.False(pins[1].Required); // 只在部分重载里出现→可选
    }

    [Fact]
    public void 重载合并_有默认值的引脚可省略()
    {
        var overloads = new List<IReadOnlyList<HandlerDslNaming.ParameterSpec>>
        {
            new[] { new HandlerDslNaming.ParameterSpec("count", PinType.Number, HasDefault: true) },
        };

        var pins = HandlerDslNaming.MergeOverloads(overloads, out var error);
        Assert.Null(error);
        Assert.False(Assert.Single(pins!).Required);
    }

    [Fact]
    public void 重载合并_同名参数类型不同_目录生成失败()
    {
        var overloads = new List<IReadOnlyList<HandlerDslNaming.ParameterSpec>>
        {
            new[] { new HandlerDslNaming.ParameterSpec("value", PinType.Number) },
            new[] { new HandlerDslNaming.ParameterSpec("value", PinType.String) },
        };

        var pins = HandlerDslNaming.MergeOverloads(overloads, out var error);
        Assert.Null(pins);
        Assert.NotNull(error);
        Assert.Contains("value", error!, StringComparison.Ordinal);
        Assert.Contains("拆成两个节点", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录元件带折叠标记()
    {
        var damage = Fixtures.Damage();
        Assert.True(damage.HasAwait);
        Assert.True(damage.HasSideEffect);
        Assert.Equal("EffectRuntime", damage.Face);
        Assert.Equal("DamageAsync", damage.Method);
    }
}
