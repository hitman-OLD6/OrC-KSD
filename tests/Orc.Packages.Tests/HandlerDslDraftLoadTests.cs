using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.Game.Model;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S8 草案装载期**（R28 第 5 条）：<c>members[].handler.dsl</c> → 装载器**现场生成** csx（不写盘）→ 既有
/// <c>RegisterScript</c> 预编译链；生成失败＝装载拒绝，错误带图错误码与**节点路径**（不是 csx 行号）。
/// <para>
/// 本工程装配了编译器与目录（<see cref="HandlerDslAssembly.Create"/>），所以「含 DSL 成员的草案装得上」
/// 与「生成失败怎么报」都在这里验证；纯内核环境只能验证「未装配＝拒绝」（<c>tests/Orc.Tests</c>）。
/// </para>
/// </summary>
public class HandlerDslDraftLoadTests
{
    [Fact]
    public void 草案装载期_现场生成csx并注册_不写盘()
    {
        const string dsl = """{"schemaVersion":1,"actions":[{"op":"stop"}]}""";
        using var pack = new DraftPack(dsl);

        // 记录型编译器：证明「装载器真的现场调了编译器」，且用的是快照目录 ＋ 挂载点视图。
        var environment = HandlerDslAssembly.Create();
        var recorder = new RecordingCompiler(environment.Compiler);

        var runtime = GameEnginePrefabs.CreateRuntime();
        var result = AssetPackLoader.Load(
            runtime, pack.Directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(),
            handlerDsl: new HandlerDslEnvironment(recorder, environment.Catalog));

        Assert.True(result.Loaded, Describe(result));
        Assert.Equal(new[] { "draft.probe" }, result.AssetIds);

        // `.hdsl.json` 不被资产扫描模式（*.component.json）吃掉（D11）：包内只有 1 份资产。
        Assert.Equal(1, result.Files);

        // ① 编译器被调了一次，入参＝DSL 文件原文 ＋ 装配目录 ＋ 从成员视图投影出的字段声明。
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(dsl, call.DslJson);
        Assert.Equal(environment.Catalog.Revision, call.Catalog.Revision);
        Assert.Contains(call.View!.Fields, f => f.Name == "Card" && f.Type == "entity" && !f.Writable);

        // ② 生成文本进了既有注册面（内容来自 DSL，而不是任何 csx 文件）。
        Assert.Contains("ctx.Stop();", call.Csx!, StringComparison.Ordinal);
        Assert.Contains("Func<dynamic, Context, CancellationToken, Task> HandleAsync", call.Csx!, StringComparison.Ordinal);
        Assert.NotNull(runtime.Handlers.Resolve("draft.probe#onDeployed"));

        // ③ 不写盘（R28 第 5 条）：包内文件集合与内容都不变。
        Assert.False(File.Exists(Path.Combine(pack.Directory, "handler-dsl", "m.csx")), "生成物不得落盘");
        Assert.False(File.Exists(Path.Combine(pack.Directory, "draft.probe.csx")), "生成物不得落盘");
        Assert.Equal(pack.Initial, pack.Snapshot());
    }

    [Fact]
    public void 生成失败_装载拒绝且错误带码与节点路径()
    {
        using var pack = new DraftPack("""{"schemaVersion":1,"actions":[{"op":"explode"}]}""");

        var runtime = GameEnginePrefabs.CreateRuntime();
        var result = AssetPackLoader.Load(
            runtime, pack.Directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(),
            handlerDsl: HandlerDslAssembly.Create());

        Assert.False(result.Loaded);
        Assert.Empty(result.AssetIds);

        var error = Assert.Single(result.Errors);
        Assert.Equal("node-unknown", error.Code);
        Assert.Equal("actions[0]", error.NodePath);
        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Equal("self#onDeployed", error.Target);

        // 整包原子：失败时正式目录里没有任何该资产的残留。
        Assert.DoesNotContain("draft.probe", runtime.Catalog.Definitions.Select(static d => d.Id));
    }

    [Fact]
    public void 写未声明字段_映射到view分类()
    {
        using var pack = new DraftPack(
            """{"schemaVersion":1,"actions":[{"op":"write","field":"Missing","value":{"lit":1}}]}""");

        var runtime = GameEnginePrefabs.CreateRuntime();
        var result = AssetPackLoader.Load(
            runtime, pack.Directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(),
            handlerDsl: HandlerDslAssembly.Create());

        Assert.False(result.Loaded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("write-forbidden", error.Code);
        Assert.Equal("actions[0].field", error.NodePath);

        // D14：write-forbidden 判的是「这个字段不能写」⇒ view 类，不是 structure。
        Assert.Equal(PrefabErrorCategories.View, error.Category);
    }

    [Fact]
    public void 引用未声明的视图字段_装载期拒绝并指名()
    {
        using var pack = new DraftPack(
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"view":"NotDeclared"},"amount":{"lit":1}}]}""");

        var runtime = GameEnginePrefabs.CreateRuntime();
        var result = AssetPackLoader.Load(
            runtime, pack.Directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(),
            handlerDsl: HandlerDslAssembly.Create());

        Assert.False(result.Loaded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("pin-unknown", error.Code);
        Assert.Equal("actions[0].target", error.NodePath);

        // R13 第 3 条：错误指名该字段。
        Assert.Contains("NotDeclared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 生成的csx进既有预编译链_失败仍归compile三分类()
    {
        // 脚本正文是作者 csx（R15）：正文编译不过＝gen 出来的文本过不了 RegisterScript 预编译——
        // 仍落既有 binding／compile／signature 三类，**不新增分类**（R13 第 4 条）。
        using var pack = new DraftPack(
            """{"schemaVersion":1,"actions":[{"op":"script","inputs":[],"outputs":[],"body":"this is not c#"}]}""");

        var runtime = GameEnginePrefabs.CreateRuntime();
        var result = AssetPackLoader.Load(
            runtime, pack.Directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(),
            handlerDsl: HandlerDslAssembly.Create());

        Assert.False(result.Loaded);
        var error = Assert.Single(result.Errors);
        Assert.Contains(
            error.Category,
            new[]
            {
                PrefabErrorCategories.Binding, PrefabErrorCategories.Compile, PrefabErrorCategories.Signature,
            });

        // csx 链维持现状：那四类以 Category 身份出现，Code 不填（只有 DSL 链填 Code）。
        Assert.Null(error.Code);
        Assert.Null(error.NodePath);
    }

    private static string Describe(AssetPackLoadResult result)
        => string.Join("；", result.Errors.Select(static e => $"[{e.Category}] {e.Target}：{e.Message}"));

    /// <summary>一次编译调用（DSL 原文 ＋ 目录 ＋ 视图 ＋ 产出的 csx）。</summary>
    private sealed record CompileCall(
        string DslJson, IHandlerCatalog Catalog, HandlerDslView? View, string? Csx);

    /// <summary>记录型编译器：委托给真实实现，顺便记下每次调用的入参与产出。</summary>
    private sealed class RecordingCompiler(IHandlerDslCompiler inner) : IHandlerDslCompiler
    {
        public List<CompileCall> Calls { get; } = new();

        public HandlerDslCompileResult Compile(string dslJson, IHandlerCatalog catalog, HandlerDslView? view = null)
        {
            var result = inner.Compile(dslJson, catalog, view);
            Calls.Add(new CompileCall(dslJson, catalog, view, result.Csx));
            return result;
        }
    }

    /// <summary>一个临时"包"：组件资产 ＋ 一份 DSL 文档（D11：<c>handler-dsl/&lt;name&gt;.hdsl.json</c>）。</summary>
    private sealed class DraftPack : IDisposable
    {
        public DraftPack(string dslJson)
        {
            Directory = Path.Combine(Path.GetTempPath(), "orc-hdsl-draft-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "handler-dsl"));
            File.WriteAllText(Path.Combine(Directory, "handler-dsl", "m.hdsl.json"), dslJson);
            File.WriteAllText(Path.Combine(Directory, "draft.probe.component.json"), Asset);
            Initial = Snapshot();
        }

        public string Directory { get; }

        /// <summary>构造完成时的文件快照（「生成物不写盘」的比对基准）。</summary>
        public string Initial { get; }

        public string Snapshot() => string.Join("\n", CurrentFiles());

        public IEnumerable<string> CurrentFiles()
            => System.IO.Directory
                .EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                .OrderBy(static f => f, StringComparer.Ordinal)
                .Select(f => Path.GetFileName(f) + ":" + File.ReadAllText(f).Length);

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private const string Asset = """
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
                    "view": {
                      "fields": [
                        { "name": "Card", "type": "entity", "writable": false, "required": false }
                      ]
                    },
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
}
