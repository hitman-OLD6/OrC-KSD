using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 引擎共享变量表测试：读写契约、类型保护、作用域隔离与清理。不依赖卡牌构造——变量表对任意对象同构。
/// </summary>
public class EngineVariablesTests
{
    private sealed class Marker
    {
        public Marker(string name) => Name = name;

        public string Name { get; }
    }

    [Fact]
    public void Set_ThenTryGet_IsVisibleToAnotherReader()
    {
        var variables = new EngineVariables();
        variables.Set("friendly.destroyed.units", new List<Marker> { new("a") });

        Assert.True(variables.TryGet<List<Marker>>("friendly.destroyed.units", out var units));
        Assert.Single(units);
        Assert.Equal("a", units[0].Name);
    }

    [Fact]
    public void Set_SameTypeTwice_LastWriteWins()
    {
        var variables = new EngineVariables();
        variables.Set("counter", 1);
        variables.Set("counter", 2);

        Assert.Equal(2, variables.Get<int>("counter"));
        Assert.Equal(1, variables.Count);
    }

    [Fact]
    public void Set_DifferentType_IsRejectedAndKeepsOldValue()
    {
        var variables = new EngineVariables();
        variables.Set("counter", 1);

        Assert.Throws<InvalidOperationException>(() => variables.Set("counter", "one"));
        Assert.Equal(1, variables.Get<int>("counter"));
    }

    [Fact]
    public void Set_BlankKeyOrNullValue_IsRejected()
    {
        var variables = new EngineVariables();

        Assert.Throws<ArgumentException>(() => variables.Set("   ", 1));
        Assert.Throws<ArgumentNullException>(() => variables.Set("k", null!));
        Assert.Equal(0, variables.Count);
    }

    [Fact]
    public void TryGet_MissingOrMismatched_ReturnsFalse()
    {
        var variables = new EngineVariables();
        variables.Set("counter", 1);

        Assert.False(variables.TryGet<int>("missing", out _));
        Assert.False(variables.TryGet<string>("counter", out _));
        Assert.False(variables.TryGet<int>("   ", out _));
    }

    [Fact]
    public void Get_MissingOrMismatched_Throws()
    {
        var variables = new EngineVariables();
        variables.Set("counter", 1);

        Assert.Throws<KeyNotFoundException>(() => variables.Get<int>("missing"));
        Assert.Throws<InvalidCastException>(() => variables.Get<string>("counter"));
    }

    [Fact]
    public void TryGet_AllowsBaseTypeRead()
    {
        var variables = new EngineVariables();
        variables.Set("marker", new Marker("a"));

        Assert.True(variables.TryGet<object>("marker", out var value));
        Assert.IsType<Marker>(value);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameInstanceOnSecondCall()
    {
        var variables = new EngineVariables();

        var first = variables.GetOrCreate<List<Marker>>("units");
        first.Add(new Marker("a"));
        var second = variables.GetOrCreate<List<Marker>>("units");

        Assert.Same(first, second);
        Assert.Single(second);
    }

    [Fact]
    public void GetOrCreate_MismatchedType_Throws()
    {
        var variables = new EngineVariables();
        variables.Set("units", "not-a-list");

        Assert.Throws<InvalidCastException>(() => variables.GetOrCreate<List<Marker>>("units"));
    }

    [Fact]
    public void Remove_IsIdempotent()
    {
        var variables = new EngineVariables();
        variables.Set("k", 1);

        Assert.True(variables.Remove("k"));
        Assert.False(variables.TryGet<int>("k", out _));
        Assert.False(variables.Remove("k"));
        Assert.False(variables.Remove("   "));
    }

    [Fact]
    public void Clear_EmptiesTable()
    {
        var variables = new EngineVariables();
        variables.Set("a", 1);
        variables.Set("b", 2);

        variables.Clear();

        Assert.Equal(0, variables.Count);
        Assert.Empty(variables.Keys);
    }

    [Fact]
    public void EngineInstances_HaveIndependentTables()
    {
        var engineA = new LogicEngine();
        var engineB = new LogicEngine();

        engineA.Variables.Set("k", 1);

        Assert.True(engineA.Variables.TryGet<int>("k", out _));
        Assert.False(engineB.Variables.TryGet<int>("k", out _));

        engineB.Variables.Clear();
        Assert.Equal(1, engineA.Variables.Count);
    }

    [Fact]
    public void ReferencesAndVariables_AreIndependent()
    {
        var engine = new LogicEngine();
        var marker = new Marker("a");
        var referenceKey = engine.References.Assign(marker);

        engine.Variables.Set("marker", marker);

        Assert.Equal(1, engine.References.Count);
        Assert.Equal(1, engine.Variables.Count);
        Assert.True(engine.Variables.TryGet<Marker>("marker", out var stored));
        Assert.Same(marker, stored);
        Assert.True(engine.References.TryResolve<Marker>(referenceKey, out var resolved));
        Assert.Same(marker, resolved);
        Assert.False(engine.Variables.Contains(referenceKey));
    }
}
