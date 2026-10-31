using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.HandlerDsl;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 「DSL → csx」的**编译闭环**：用**真实扫描出来的目录**，为每个可图形化节点各生成一段 csx，
/// 再交给既有 csx 链真编译一次。
/// <para>
/// 存在的理由：文本向量只能证明「生成器吐出了某段字符串」，证明不了那段字符串**是合法 C#**。
/// 隐式宿主实参、<c>CancellationToken</c> 传递、枚举落型、多输出拆解这几类问题，
/// 只有真编译才会暴露——而它们恰恰都真的漏过（手写夹具与真实扫描不一致，把问题盖住了）。
/// </para>
/// <para>
/// 本用例刻意**不复用** <c>Fixtures.Catalog</c>：那类是手写目录，字段一旦与扫描器不同步，
/// 这里就会双双变绿而实际链路是断的。
/// </para>
/// </summary>
public class HandlerDslCodegenCompilesTests
{
    /// <summary>通用视图：覆盖封闭集每种引脚类型（含可写字段，供 <c>write</c> 类用例延伸使用）。</summary>
    private static readonly HandlerDslView View = new(new[]
    {
        new HandlerDslViewField("Unit", "entity", false),
        new HandlerDslViewField("Target", "entity", false),
        new HandlerDslViewField("Units", "list<entity>", false),
        new HandlerDslViewField("Numbers", "list<number>", false),
        new HandlerDslViewField("Amount", "number", false),
        new HandlerDslViewField("Text", "string", false),
        new HandlerDslViewField("Flag", "boolean", false),
        new HandlerDslViewField("Field", "number", true),
    });

    [Fact]
    public void 真实目录下每个节点生成的csx都能被csx链编译()
    {
        var scanned = HandlerDslCatalogScanner.Scan(typeof(EffectRuntime).Assembly);
        Assert.True(scanned.Success, "目录扫描失败：" + string.Join("；", scanned.Errors));

        var catalog = scanned.Catalog!;
        Assert.NotEmpty(catalog.Nodes);

        var compiler = new HandlerDslCompiler();
        var evaluator = new CSharpScriptEvaluator();
        var failures = new List<string>();
        var compiled = 0;

        foreach (var node in catalog.Nodes)
        {
            var document = BuildDocument(node);
            var generation = compiler.CompileToGeneration(document, catalog, View, out var errors);
            if (generation is null)
            {
                failures.Add($"{node.Name}：生成失败 {Describe(errors)}");
                continue;
            }

            var result = evaluator.Evaluate(
                new ScriptRequest(generation.Csx, HandlerDslGenerator.EntryName, typeof(ViewCarrier)));
            if (!result.Success)
            {
                failures.Add(
                    $"{node.Name}：编译失败 [{result.ErrorCategory}] {result.Error}{Environment.NewLine}"
                    + $"--- 生成文本 ---{Environment.NewLine}{generation.Csx}");
                continue;
            }

            compiled++;
        }

        Assert.True(failures.Count == 0, $"共 {failures.Count} 个节点生成/编译失败：{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        Assert.Equal(catalog.Nodes.Count, compiled);
    }

    /// <summary>
    /// 动作调用的两个「必传但不出引脚」的实参：隐式宿主与 <c>CancellationToken</c>。
    /// 单独立一条是因为它们都不会让生成器报错、只会让生成物悄悄缺参或失去取消能力。
    /// </summary>
    [Fact]
    public void 动作调用必须传隐式宿主与CancellationToken()
    {
        var scanned = HandlerDslCatalogScanner.Scan(typeof(EffectRuntime).Assembly);
        Assert.True(scanned.Success);
        var catalog = scanned.Catalog!;

        var compiler = new HandlerDslCompiler();

        // damage：首参 target（真实引脚）、source 折叠、ct 必传。
        var damage = compiler.CompileToGeneration(
            """{ "schemaVersion": 1, "actions": [ { "op": "damage", "target": { "view": "Unit" }, "amount": { "lit": 2 } } ] }""",
            catalog,
            View,
            out var errors);
        Assert.True(damage is not null, Describe(errors));
        Assert.Contains("ct: ct", damage!.Csx, StringComparison.Ordinal);
        Assert.Contains("source: orcSelf", damage.Csx, StringComparison.Ordinal);

        // draw：首参是隐式宿主 viewer（不出引脚），必须补成 orcSelf，否则缺参编译不过。
        var draw = compiler.CompileToGeneration(
            """{ "schemaVersion": 1, "actions": [ { "op": "draw", "count": { "lit": 1 } } ] }""",
            catalog,
            View,
            out var drawErrors);
        Assert.True(draw is not null, Describe(drawErrors));
        Assert.Contains("viewer: orcSelf", draw!.Csx, StringComparison.Ordinal);
        Assert.Contains("ct: ct", draw.Csx, StringComparison.Ordinal);
    }

    /// <summary>
    /// 契约包里的 <c>vectors/gen/*.expected.csx</c> 也必须真能编译。
    /// <para>
    /// 文本向量原先只比字符串，于是出现过「<c>target: "deck"</c>（字符串当实体）」「<c>FetchAsync</c>（方法名根本不存在）」
    /// 这类期望文本——比对全绿、实际编译必炸。把期望文本送去编译，向量才配当基准。
    /// </para>
    /// </summary>
    [Fact]
    public void 契约包里的gen期望文本都能被csx链编译()
    {
        var directory = Path.Combine(FindContractsRoot(), "handler-dsl", "v1", "vectors", "gen");
        var files = Directory.GetFiles(directory, "*.expected.csx");
        Assert.NotEmpty(files);

        var evaluator = new CSharpScriptEvaluator();
        var failures = new List<string>();

        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var csx = File.ReadAllText(file);
            var result = evaluator.Evaluate(
                new ScriptRequest(csx, HandlerDslGenerator.EntryName, typeof(ViewCarrier)));
            if (!result.Success)
            {
                failures.Add($"{Path.GetFileName(file)}：[{result.ErrorCategory}] {result.Error}");
            }
        }

        Assert.True(failures.Count == 0, $"期望文本编译失败：{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>从测试输出目录向上找 <c>contracts/</c>（与 <c>Fixtures.RepoRoot</c> 同一思路，避免硬编码层级）。</summary>
    private static string FindContractsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "contracts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"未能从 '{AppContext.BaseDirectory}' 向上找到 contracts 目录。");
    }

    /// <summary>为节点拼一条「只填必填引脚」的最小文档。</summary>
    private static string BuildDocument(DslNodeSignature node)
    {
        var arguments = new List<string>();
        foreach (var pin in node.Inputs)
        {
            if (!pin.Required)
            {
                continue;
            }

            arguments.Add($"\"{pin.Name}\": {ValueFor(pin)}");
        }

        var body = arguments.Count == 0
            ? $"{{\"op\": \"{node.Name}\"}}"
            : $"{{\"op\": \"{node.Name}\", {string.Join(", ", arguments)}}}";

        return $"{{ \"schemaVersion\": 1, \"actions\": [ {body} ] }}";
    }

    private static string ValueFor(DslPin pin)
    {
        if (pin.EnumValues.Count > 0)
        {
            return $"{{\"lit\": \"{pin.EnumValues[0]}\"}}";
        }

        return pin.Type.Format() switch
        {
            "entity" => "{\"view\": \"Unit\"}",
            "number" => "{\"lit\": 1}",
            "string" => "{\"lit\": \"x\"}",
            "boolean" => "{\"lit\": true}",
            "list<entity>" => "{\"view\": \"Units\"}",
            "list<number>" => "{\"view\": \"Numbers\"}",
            var other => throw new InvalidOperationException($"测试未覆盖的引脚类型 '{other}'（{pin.Name}）。"),
        };
    }

    private static string Describe(IReadOnlyList<HandlerDslError> errors) =>
        errors.Count == 0 ? "（无错误）" : string.Join("；", errors.Select(static e => $"{e.Code}@{e.NodePath} {e.Message}"));
}
