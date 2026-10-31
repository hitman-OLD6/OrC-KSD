using System.Text;
using System.Text.Json;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// 生成器（S7／R6／R12／D9／D16／D19）：确定性、内容寻址 id、格式冻结 F1–F12、
/// 循环上限只求值一次、表达式内联、节点名不进文本。
/// </summary>
public class GeneratorTests
{
    private static HandlerDslGenerationResult Generate(string actionsJson, IHandlerDslCatalog? catalog = null)
    {
        var read = HandlerDslReader.Read(Fixtures.Doc(actionsJson), catalog ?? Fixtures.Catalog);
        Assert.NotNull(read.Document);
        Assert.Empty(read.Errors);
        return HandlerDslGenerator.Generate(read.Document!, catalog ?? Fixtures.Catalog, new DslView(new[]
        {
            new DslViewField("Defense", PinType.Number, Writable: true),
            new DslViewField("Target", PinType.Entity),
        }));
    }

    [Fact]
    public void 同一份DSL两次生成逐字节一致()
    {
        var actions = """
            {"op":"damage","target":{"view":"Target"},"amount":{"op":"sub","args":[{"view":"Defense"},{"lit":1}]}}
            """;

        var first = Generate(actions);
        var second = Generate(actions);

        Assert.Equal(first.Csx, second.Csx);
        Assert.Equal(first.HandlerId, second.HandlerId);
    }

    [Fact]
    public void handlerId是内容寻址_前缀与长度固定()
    {
        var generated = Generate("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":2}}""");

        Assert.StartsWith("hdsl/v1/", generated.HandlerId, StringComparison.Ordinal);
        Assert.Equal("hdsl/v1/".Length + 32, generated.HandlerId.Length);
        Assert.Equal(HandlerDslGenerator.HandlerIdOf(generated.Csx), generated.HandlerId);
        Assert.Matches("^hdsl/v1/[0-9a-f]{32}$", generated.HandlerId);
    }

    [Fact]
    public void 同结构仅节点名不同_生成文本逐字节相同()
    {
        var actions = """{"op":%NODE%,"target":{"view":"Target"},"amount":{"lit":2}}""";
        var alpha = Generate(actions.Replace("%NODE%", "\"damage\"", StringComparison.Ordinal), CatalogWithName("damage"));
        var omega = Generate(actions.Replace("%NODE%", "\"strike\"", StringComparison.Ordinal), CatalogWithName("strike"));

        Assert.Equal(alpha.Csx, omega.Csx);
        Assert.Equal(alpha.HandlerId, omega.HandlerId);

        // 生成的码里**不得**出现节点名（D12／F8）。
        Assert.DoesNotContain("damage", alpha.Csx, StringComparison.Ordinal);
        Assert.DoesNotContain("strike", omega.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 格式冻结_LF与四空格缩进与单个尾换行()
    {
        var csx = Generate("""
            {"op":"branch","test":{"lit":true},"then":[{"op":"damage","target":{"view":"Target"},"amount":{"lit":1}}]}
            """).Csx;

        Assert.DoesNotContain("\r", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", csx, StringComparison.Ordinal);
        Assert.False(csx.StartsWith('\n'));
        Assert.EndsWith("\n", csx, StringComparison.Ordinal);
        Assert.False(csx.EndsWith("\n\n", StringComparison.Ordinal), "文件末尾必须恰好一个换行（F3）");

        foreach (var line in csx.Split('\n'))
        {
            var indent = line.Length - line.TrimStart(' ').Length;
            Assert.Equal(0, indent % 4);
        }
    }

    [Fact]
    public void 空handler_生成可编译的空体且无多余变量()
    {
        var generated = Generate(string.Empty);

        Assert.Equal(
            "Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n};\n",
            generated.Csx);
        Assert.DoesNotContain("var ", generated.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 不写bind_不产生未使用变量()
    {
        var generated = Generate("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":2}}""");

        // 两个门面别名（隐式宿主 ＋ 运行时门面），不产生绑定变量（D12 规定生成码固定用 orcSelf）。
        var declarations = generated.Csx.Split('\n').Where(l => l.Contains("var ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, declarations.Length);
        Assert.Contains("var orcSelf", declarations[0], StringComparison.Ordinal);
        Assert.Contains("var __runtime", declarations[1], StringComparison.Ordinal);
        Assert.Contains("__runtime.DamageAsync(", generated.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 写bind_单输出用var_多输出逐输出取名()
    {
        var single = Generate("""{"op":"draw","count":{"lit":2},"bind":"drawn"}""");
        Assert.Contains("var drawn = await ", single.Csx, StringComparison.Ordinal);

        var multiple = Generate("""{"op":"fetch","from":{"lit":"deck"},"bind":{"status":"st","card":"c"}}""");
        Assert.Contains("var __res__actions_0 = await ", multiple.Csx, StringComparison.Ordinal);
        Assert.Contains("var st = __res__actions_0.Status;", multiple.Csx, StringComparison.Ordinal);
        Assert.Contains("var c = __res__actions_0.Card;", multiple.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 循环上限为字面量_直接内联且不引入临时变量()
    {
        var csx = Generate("""{"op":"loop","times":{"lit":3},"actions":[]}""").Csx;

        Assert.Contains("for (var __i__actions_0 = 0; __i__actions_0 < 3; __i__actions_0++)", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("__bound", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("while (", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 循环上限为运行期值_只落一个临时变量且由for承担()
    {
        var csx = Generate("""
            {"op":"loop","times":{"op":"count","args":[{"view":"Units"}]},"actions":[
                {"op":"damage","target":{"view":"Target"},"amount":{"lit":1}}
            ]}
            """).Csx;

        Assert.Contains("var __bound__actions_0 = (view.Units).Count;", csx, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(csx, "__bound__actions_0 ="));
        Assert.Contains("__i__actions_0 < __bound__actions_0", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("while (", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void while的test每轮重估_limit只求值一次()
    {
        var csx = Generate("""
            {"op":"while","test":{"op":"gt","args":[{"view":"Defense"},{"lit":0}]},
             "limit":{"lit":5},"actions":[{"op":"write","field":"Defense","value":{"lit":0}}]}
            """).Csx;

        Assert.Contains(
            "for (var __i__actions_0 = 0; __i__actions_0 < 5 && ((view.Defense > 0)); __i__actions_0++)",
            csx,
            StringComparison.Ordinal);
        Assert.Contains("view.Defense = 0;", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 上限为负数_仍是for且不抛异常()
    {
        var csx = Generate("""{"op":"loop","times":{"lit":-2},"actions":[]}""").Csx;
        Assert.Contains("__i__actions_0 < -2;", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 表达式内联_除循环上限外不引入临时变量()
    {
        var csx = Generate("""
            {"op":"damage","target":{"view":"Target"},
             "amount":{"op":"add","args":[{"op":"mul","args":[{"view":"Defense"},{"lit":2}]},{"lit":1}]}}
            """).Csx;

        Assert.Contains("amount: ((view.Defense * 2) + 1)", csx, StringComparison.Ordinal);

        // 表达式本身不引入临时变量：只允许两个门面别名（隐式宿主 ＋ 运行时门面）。
        var locals = csx.Split('\n').Where(l => l.Contains("var ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, locals.Length);
        Assert.Contains(locals, static l => l.Contains("orcSelf", StringComparison.Ordinal));
        Assert.Contains(locals, static l => l.Contains("__runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void 表达式内多个取值_保持书写顺序()
    {
        var csx = Generate("""
            {"op":"announce","text":{"op":"add","args":[
                {"op":"count","args":[{"view":"Units"}]},
                {"read":{"of":"self","path":"attack"}}]}}
            """).Csx;

        var first = csx.IndexOf("(view.Units).Count", StringComparison.Ordinal);
        var second = csx.IndexOf("ReadValue(orcSelf, \"self\", \"attack\")", StringComparison.Ordinal);
        Assert.True(first > 0 && second > first, "表达式内取值必须保持书写顺序（F10），不为省 await 重排");
    }

    [Fact]
    public void 增删脚本节点_其余片段逐字节一致()
    {
        const string damage = """{"op":"damage","target":{"view":"Target"},"amount":{"lit":2}}""";
        var without = Generate(damage);
        var with = Generate(damage + "," + """{"op":"script","inputs":[],"outputs":[],"body":"// noop"}""");

        var withoutLine = without.Csx.Split('\n').First(l => l.Contains("DamageAsync", StringComparison.Ordinal));
        var withLine = with.Csx.Split('\n').First(l => l.Contains("DamageAsync", StringComparison.Ordinal));

        Assert.Equal(withoutLine, withLine);
        Assert.Contains("// noop", with.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 上下文原语_生成ctx调用()
    {
        var csx = Generate("""{"op":"stop"},{"op":"interrupt"}""").Csx;

        Assert.Contains("ctx.Stop();", csx, StringComparison.Ordinal);
        Assert.Contains("ctx.Interrupt();", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("orcSelf", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void source折叠_缺省接宿主()
    {
        var csx = Generate("""{"op":"kill","target":{"view":"Target"}}""").Csx;
        Assert.Contains("KillAsync(target: view.Target, source: orcSelf)", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 循环体内的绑定在每次迭代重新声明()
    {
        var csx = Generate("""
            {"op":"loop","times":{"lit":2},"actions":[
                {"op":"draw","count":{"lit":1},"bind":"drawn"},
                {"op":"iterate","over":{"view":"Units"},"as":"u","actions":[
                    {"op":"damage","target":{"var":"u"},"amount":{"lit":1}}
                ]}
            ]}
            """).Csx;

        // 声明落在 for 块内部（每次迭代重新求值），不在块外。
        var forIndex = csx.IndexOf("for (var ", StringComparison.Ordinal);
        var bindingIndex = csx.IndexOf("var drawn", StringComparison.Ordinal);
        var elementIndex = csx.IndexOf("foreach (var u in", StringComparison.Ordinal);
        Assert.True(forIndex > 0 && bindingIndex > forIndex, "循环体绑定应声明在循环体内");
        Assert.True(elementIndex > bindingIndex, "iterate 的元素绑定应声明在循环体内");
    }

    [Fact]
    public void 生成器不产出时间戳与随机值_同一输入稳定()
    {
        var csx = Generate("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":2}}""").Csx;

        Assert.DoesNotMatch("[0-9]{4}-[0-9]{2}-[0-9]{2}", csx);
        Assert.DoesNotContain("//", csx, StringComparison.Ordinal);
    }

    // ── 生成类向量（kind: "gen"） ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(GenVectorFiles))]
    public void gen向量_与期望文本逐字节一致(string file)
    {
        var vector = LoadGenVector(file);
        var generated = HandlerDslGenerator.Generate(vector.Document, vector.Catalog, vector.View);

        // 逐字节全等（F12）：**不做任何规范化**——行尾／缩进／尾换行都算，两份只在行尾不同的文本就是两个 id。
        var expected = File.ReadAllText(vector.ExpectedPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal(expected, generated.Csx);
        Assert.Equal(HandlerDslGenerator.HandlerIdOf(expected), generated.HandlerId);
    }

    /// <summary>
    /// 生成类期望文本的**重写入口**（与仓库既有基线同一约定：只有人显式设了开关才写文件）。
    /// <para>
    /// 为什么需要它：期望文本是**逐字节**比的（F12），手抄一份新生成的文本等于自欺；而「测试跑起来就顺手覆盖期望」
    /// 会让生成器的任何改动变成假绿。故重写只走 <c>ORC_WRITE_VECTORS=1</c>，常规测试运行只读不写。
    /// </para>
    /// <para>
    /// 改了生成格式就要重写期望文本 ⇒ 按 SPEC §13 那也是**破坏性变更**（必须同时升 <c>schemaVersion</c>）。
    /// </para>
    /// </summary>
    [Fact]
    public void gen向量期望文本_仅在显式开关下重写()
    {
        if (Environment.GetEnvironmentVariable("ORC_WRITE_VECTORS") != "1")
        {
            return; // 常规运行：只读不写。
        }

        foreach (var file in Directory.GetFiles(
                     Path.Combine(Fixtures.ContractRoot, "vectors", "gen"), "*.gen.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var vector = LoadGenVector(Path.GetFileName(file));
            var generated = HandlerDslGenerator.Generate(vector.Document, vector.Catalog, vector.View);
            File.WriteAllText(vector.ExpectedPath, generated.Csx, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public static TheoryData<string> GenVectorFiles
    {        get
        {
            var data = new TheoryData<string>();
            var dir = Path.Combine(Fixtures.ContractRoot, "vectors", "gen");
            if (!Directory.Exists(dir))
            {
                return data;
            }

            foreach (var file in Directory.GetFiles(dir, "*.gen.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    private static GenVector LoadGenVector(string file)
    {
        var path = Path.Combine(Fixtures.ContractRoot, "vectors", "gen", file);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        Assert.Equal("gen", root.GetProperty("kind").GetString());

        var documentJson = root.GetProperty("document").GetRawText();
        var catalog = Fixtures.CatalogFromSnapshot(root.GetProperty("catalog"));
        var view = Fixtures.ViewFromSnapshot(root.GetProperty("view"));
        var read = HandlerDslReader.Read(documentJson, catalog);
        Assert.NotNull(read.Document);
        Assert.Empty(read.Errors);

        var expectedName = root.GetProperty("expect").GetProperty("csx").GetString()!;
        return new GenVector(
            read.Document!,
            catalog,
            view,
            Path.Combine(Path.GetDirectoryName(path)!, expectedName));
    }

    private sealed record GenVector(DslDocument Document, DslCatalog Catalog, DslView View, string ExpectedPath);

    private static DslCatalog CatalogWithName(string nodeName) => new(
        7,
        new[]
        {
            new DslNodeSignature(
                nodeName,
                new[]
                {
                    new DslPin("target", PinType.Entity.WithSubtypes("unit", "hq")),
                    new DslPin("amount", PinType.Number),
                },
                Array.Empty<DslPin>(),
                hasAwait: true,
                hasSideEffect: true,
                face: "EffectRuntime",
                method: "DamageAsync"),
        },
        Fixtures.Catalog.Readables);

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
