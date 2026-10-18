using Orc.Core;
using Orc.Script;
using Xunit;

namespace Orc.Script.Tests;

/// <summary>
/// 资产化 S3 satellite 验收：csx「注册期入口」求值（成功／编译失败／沙箱拒绝／入口缺失／签名不符）与缓存，
/// 以及注册动作经受控面（<see cref="IPluginRegistrationContext"/>）实际生效。
/// </summary>
public class CSharpPluginScriptEvaluatorTests
{
    private const string ValidScript =
        "Action<IPluginRegistrationContext> Register = ctx => { " +
        "ctx.DeclareDependency(\"json:faction.table@abcdef01#1.0.0\"); " +
        "ctx.RegisterHandler(\"k1\", (Action)(() => { })); };";

    [Fact]
    public void 有效脚本_产出注册动作且可执行()
    {
        var evaluator = new CSharpPluginScriptEvaluator();
        var context = new FakeContext();

        var result = evaluator.Evaluate(new PluginScriptRequest(ValidScript));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Register);

        result.Register!(context);

        Assert.Equal(new[] { "json:faction.table@abcdef01#1.0.0" }, context.Dependencies);
        Assert.Equal(new[] { "k1" }, context.Handlers);
    }

    [Fact]
    public void 同一请求_命中缓存()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var first = evaluator.Evaluate(new PluginScriptRequest(ValidScript));
        var second = evaluator.Evaluate(new PluginScriptRequest(ValidScript));

        Assert.Same(first, second);
        Assert.Equal(1, evaluator.CacheCount);
    }

    [Fact]
    public void 不同缓存域_不共享缓存()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        evaluator.Evaluate(new PluginScriptRequest(ValidScript, CacheScope: "aaaaaaaa#1.0.0"));
        evaluator.Evaluate(new PluginScriptRequest(ValidScript, CacheScope: "bbbbbbbb#1.0.0"));

        Assert.Equal(2, evaluator.CacheCount);
    }

    [Fact]
    public void 语法错误_编译失败()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var result = evaluator.Evaluate(new PluginScriptRequest("this is not valid C# ;;;"));

        Assert.False(result.Success);
        Assert.Equal("compile", result.ErrorCategory);
    }

    [Fact]
    public void 命中屏蔽片段_沙箱拒绝()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var result = evaluator.Evaluate(new PluginScriptRequest(
            "var x = File.ReadAllText(\"a\"); Action<IPluginRegistrationContext> Register = ctx => { };"));

        Assert.False(result.Success);
        Assert.Equal("sandbox", result.ErrorCategory);
    }

    [Fact]
    public void 入口缺失_结构化失败()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var result = evaluator.Evaluate(new PluginScriptRequest("var x = 1;"));

        Assert.False(result.Success);
        Assert.Equal("entry-missing", result.ErrorCategory);
    }

    [Fact]
    public void 签名不符_结构化失败()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var result = evaluator.Evaluate(new PluginScriptRequest("Action Register = () => { };"));

        Assert.False(result.Success);
        Assert.Equal("signature", result.ErrorCategory);
    }

    [Fact]
    public void 运行时异常_结构化失败()
    {
        var evaluator = new CSharpPluginScriptEvaluator();

        var result = evaluator.Evaluate(new PluginScriptRequest("throw new InvalidOperationException(\"boom\");"));

        Assert.False(result.Success);
        Assert.Equal("runtime", result.ErrorCategory);
    }

    private sealed class FakeContext : IPluginRegistrationContext
    {
        public string PackageId => "test.pkg";

        public string PackageGuid8 => "abcdef01";

        public List<string> Dependencies { get; } = new();

        public List<string> Handlers { get; } = new();

        public void RegisterAssetService(string name, AssetService service)
            => throw new NotSupportedException();

        public JudicatorRegistration RegisterJudicator(string name, Judicator judicator)
            => throw new NotSupportedException();

        public void RegisterHandler(string key, Delegate handler) => Handlers.Add(key);

        public void DeclareDependency(string assetName) => Dependencies.Add(assetName);

        public string ReadAsset(string assetName) => assetName;

        public void DeclareBeforeWrapper(WrapperTarget target, string name, Action<object[]?> wrapper)
        {
        }

        public void DeclareAfterWrapper(WrapperTarget target, string name, Func<object[]?, object[]?, object[]?> wrapper)
        {
        }

        public void DeclareReplaceWrapper(WrapperTarget target, string name, Func<object[]?, object[]?> moding)
        {
        }
    }
}
