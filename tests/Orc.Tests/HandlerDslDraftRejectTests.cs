using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// **S8 在纯内核环境的一条验收**（D4 已裁决的测试落点：该工程只承担这一条）：
/// 未装配 <see cref="IHandlerDslCompiler"/> 时装含 <c>members[].handler.dsl</c> 成员的草案＝**装载拒绝**，
/// 分类 <see cref="PrefabErrorCategories.Capability"/>。
/// <para>
/// 这条拒绝是**能力依赖**而非专项禁令：内核没有 DSL 实现程序集，就没有编译这一步，等同于
/// 「没有 Roslyn 就编译不了 csx」；因此这里装不了 DSL 成员是**正确结果、不是缺口**（R28 第 5 条的正向验证
/// 落在 <c>tests/Orc.Packages.Tests</c>——那里装配了编译器与目录）。
/// </para>
/// </summary>
public class HandlerDslDraftRejectTests
{
    /// <summary>假求值器：证明失败原因是「没有 DSL 编译器」，不是「没有 csx 求值器」。</summary>
    private sealed class NoopEvaluator : IScriptEvaluator
    {
        public ScriptEvaluationResult Evaluate(ScriptRequest request)
            => ScriptEvaluationResult.Ok(
                (Func<ViewCarrier, Context, CancellationToken, Task>)((v, c, ct) => Task.CompletedTask));
    }

    [Fact]
    public void 未装配DSL编译器_装含DSL成员的草案_拒绝并给出能力缺失诊断()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orc-hdsl-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "handler-dsl"));
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "handler-dsl", "m.hdsl.json"),
                """{"schemaVersion":1,"actions":[]}""");
            File.WriteAllText(Path.Combine(directory, "draft.probe.component.json"), DraftAsset);

            var runtime = new OrchestrationRuntime(KernelSignals.CreateCatalog(), new HandlerRegistry());

            // 未传 handlerDsl（＝本环境未装配编译器）；求值器已装配。
            var result = AssetPackLoader.Load(runtime, directory, new NoopEvaluator());

            Assert.False(result.Loaded);
            Assert.Empty(result.AssetIds);

            var error = Assert.Single(result.Errors);
            Assert.Equal(PrefabErrorCategories.Capability, error.Category);
            Assert.Equal("self#onDeployed", error.Target);
            Assert.Contains("本环境未装配 handler DSL 编译器，无法装载含 DSL 成员的草案", error.Message, StringComparison.Ordinal);

            // 图错误码只在真的编译过之后才可能出现：能力缺失不是 DSL 校验失败。
            Assert.Null(error.Code);
            Assert.Null(error.NodePath);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>一份最小草案：单个成员，handler 指向同包的 DSL 文档（D11 的承载形态）。</summary>
    private const string DraftAsset = """
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "draft.probe",
            "requires-host": "any",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onDeployed",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "draft.probe#onDeployed",
                  "entry": "HandleAsync",
                  "dsl": "handler-dsl/m.hdsl.json",
                  "dslHash": "sha256:0000"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;
}
