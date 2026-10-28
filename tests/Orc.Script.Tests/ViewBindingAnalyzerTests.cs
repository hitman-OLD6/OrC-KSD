using Orc.Core.Model;
using Orc.Script;
using Xunit;

namespace Orc.Script.Tests;

/// <summary>
/// S5 验收（csx 视图静态语法检查）：Roslyn 语法树口径——未声明字段／写只读字段拒绝、字段名＋位置可定位；
/// "算写"恰为五类、注释与字符串不参与、直接别名被追踪、经方法传递被放行并计数；
/// <c>view</c> 名字空间不含私有字段、<c>owner</c> 通道只认已声明成员。
/// </summary>
public class ViewBindingAnalyzerTests
{
    private static readonly ViewSpec View = new(new[]
    {
        new ViewFieldDefinition("amount", ViewFieldType.Number),
        new ViewFieldDefinition("writableAmount", ViewFieldType.Number, writable: true),
    });

    private static string Script(string body)
        => "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n{\n" + body + "\n    return Task.CompletedTask;\n};";

    [Fact]
    public void 引用未声明字段被拒绝且给出字段名与位置()
    {
        var analysis = ViewBindingAnalyzer.Analyze(
            Script("        var x = view.ammount;"), View, Array.Empty<string>());

        Assert.False(analysis.IsValid);
        var error = Assert.Single(analysis.Errors);
        Assert.Equal("undeclared-view", error.Kind);
        Assert.Equal("ammount", error.Field);
        Assert.Equal(3, error.Line);
        Assert.Contains("view.ammount", error.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void 写只读字段被拒绝_写可写字段通过()
    {
        var readonlyWrite = ViewBindingAnalyzer.Analyze(
            Script("        view.amount = 1;"), View, Array.Empty<string>());
        Assert.False(readonlyWrite.IsValid);
        Assert.Equal("readonly-write", Assert.Single(readonlyWrite.Errors).Kind);

        var writableWrite = ViewBindingAnalyzer.Analyze(
            Script("        view.writableAmount = 1;"), View, Array.Empty<string>());
        Assert.True(writableWrite.IsValid, writableWrite.Describe());
    }

    [Theory]
    [InlineData("        view.amount = 1;")]
    [InlineData("        view.amount += 1;")]
    [InlineData("        view.amount -= 1;")]
    [InlineData("        view.amount++;")]
    [InlineData("        view.amount--;")]
    [InlineData("        Consume(ref view.amount);")]
    [InlineData("        Consume(out view.amount);")]
    public void 算写的五类形态都被判为写(string body)
    {
        var analysis = ViewBindingAnalyzer.Analyze(Script(body), View, Array.Empty<string>());

        Assert.False(analysis.IsValid);
        Assert.Contains(analysis.Errors, static e => e.Kind == "readonly-write");
    }

    [Fact]
    public void 其他复合赋值属未识别形态_放行并留痕()
    {
        var analysis = ViewBindingAnalyzer.Analyze(
            Script("        view.writableAmount *= 2;"), View, Array.Empty<string>());

        Assert.True(analysis.IsValid);
        Assert.Equal(1, analysis.UnverifiedCount);
        Assert.Equal("unverified-pass", analysis.UnverifiedViewPasses[0].Kind);
    }

    [Fact]
    public void 注释与字符串字面量不参与比对()
    {
        var analysis = ViewBindingAnalyzer.Analyze(
            Script("        // view.nope = 1;\n        var text = \"view.nope = 1\";"), View, Array.Empty<string>());

        Assert.True(analysis.IsValid, analysis.Describe());
        Assert.Empty(analysis.UnverifiedViewPasses);
    }

    [Fact]
    public void 直接别名被追踪_经方法传递被放行并计数()
    {
        var alias = ViewBindingAnalyzer.Analyze(
            Script("        var v = view;\n        v.amount = 1;"), View, Array.Empty<string>());
        Assert.False(alias.IsValid);
        Assert.Equal("readonly-write", Assert.Single(alias.Errors).Kind);

        var passed = ViewBindingAnalyzer.Analyze(
            Script("        Helper(view);"), View, Array.Empty<string>());
        Assert.True(passed.IsValid);
        Assert.Equal(1, passed.UnverifiedCount);
        Assert.Contains("view 被交给方法", passed.UnverifiedViewPasses[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void owner通道只认已声明成员()
    {
        var ok = ViewBindingAnalyzer.Analyze(
            Script("        owner.ammo = 3;"), View, new[] { "ammo" });
        Assert.True(ok.IsValid, ok.Describe());

        var bad = ViewBindingAnalyzer.Analyze(
            Script("        owner.nope = 3;"), View, new[] { "ammo" });
        Assert.False(bad.IsValid);
        var error = Assert.Single(bad.Errors);
        Assert.Equal("undeclared-owner", error.Kind);
        Assert.Equal("nope", error.Field);
    }

    [Fact]
    public void view名字空间不含私有字段()
    {
        // ammo 是组件私有字段（owner 通道），不在视图声明里：view.ammo 必须按 binding 失败。
        var analysis = ViewBindingAnalyzer.Analyze(
            Script("        view.ammo = 3;"), View, new[] { "ammo" });

        Assert.False(analysis.IsValid);
        Assert.Equal("undeclared-view", Assert.Single(analysis.Errors).Kind);
    }
}
