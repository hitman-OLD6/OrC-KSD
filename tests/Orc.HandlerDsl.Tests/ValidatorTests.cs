using Orc.Core;
using Orc.Core.Model;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// 校验器（S6／R26）：<c>write-forbidden</c>、<c>node-unknown</c> 疑似改名、13 码封闭集与装载分类映射、
/// <c>PrefabLoadError</c> 新字段（既有消费者零改动）。
/// </summary>
public class ValidatorTests
{
    private static readonly DslView View = new(new[]
    {
        new DslViewField("Target", PinType.Entity),
        new DslViewField("Defense", PinType.Number, Writable: true),
        new DslViewField("Name", PinType.String, Writable: false),
        new DslViewField("Units", PinType.List(PinType.Entity)),
    });

    /// <summary>内核形态视图（编译器接口吃这个形态）。</summary>
    private static readonly HandlerDslView CoreView = new(new[]
    {
        new HandlerDslViewField("Target", "entity", false),
        new HandlerDslViewField("Defense", "number", true),
        new HandlerDslViewField("Name", "string", false),
        new HandlerDslViewField("Units", "list<entity>", false),
    });

    private static IReadOnlyList<HandlerDslError> Validate(string actionsJson, DslView? view = null) =>
        HandlerDslValidator.Validate(Fixtures.Doc(actionsJson), Fixtures.Catalog, view ?? View);

    [Fact]
    public void write到未声明字段_write_forbidden且路径指向field()
    {
        var errors = Validate("""{"op":"write","field":"Ghost","value":{"lit":1}}""");

        var error = Fixtures.Single(errors, HandlerDslErrorCodes.WriteForbidden, "actions[0].field");
        Assert.Contains("Ghost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void write到writable_false字段_write_forbidden()
    {
        var errors = Validate("""{"op":"write","field":"Name","value":{"lit":"x"}}""");

        var error = Fixtures.Single(errors, HandlerDslErrorCodes.WriteForbidden, "actions[0].field");
        Assert.Contains("writable:false", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 视图声明里一个可写字段都没有时用write_被拒()
    {
        var errors = Validate("""{"op":"write","field":"Defense","value":{"lit":1}}""", DslView.Empty);
        Fixtures.Single(errors, HandlerDslErrorCodes.WriteForbidden, "actions[0].field");
    }

    [Fact]
    public void write到可写字段_放行()
    {
        Assert.Empty(Validate("""{"op":"write","field":"Defense","value":{"lit":1}}"""));
    }

    [Fact]
    public void 未知节点仍是node_unknown_但附带疑似改名提示()
    {
        var errors = Validate("""{"op":"damageAsync","target":{"view":"Target"},"amount":{"lit":1}}""");

        var error = Assert.Single(errors);
        Assert.Equal(HandlerDslErrorCodes.NodeUnknown, error.Code);
        Assert.Contains("疑似改名", error.Message, StringComparison.Ordinal);
        Assert.Contains("damage", error.Message, StringComparison.Ordinal);
        Assert.Equal("actions[0]", error.NodePath);
    }

    [Fact]
    public void 未知节点无改名候选时不给提示()
    {
        var errors = Validate("""{"op":"totallyUnknown","target":{"view":"Target"}}""");

        var error = Assert.Single(errors);
        Assert.Equal(HandlerDslErrorCodes.NodeUnknown, error.Code);
        Assert.DoesNotContain("疑似改名", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 编译器只产出封闭集内的码()
    {
        string[] corpus =
        {
            """{"op":"branch","test":{"lit":true},"then":[],"else":[]}""",
            """{"op":"loop","actions":[]}""",
            """{"op":"while","test":{"lit":true},"actions":[]}""",
            """{"op":"loop","times":{"lit":true},"actions":[]}""",
            """{"op":"iterate","over":{"lit":1},"as":"x","actions":[]}""",
            """{"op":"let","name":"x","value":{"var":"y"}}""",
            """{"op":"write","field":"Ghost","value":{"lit":1}}""",
            """{"op":"damage","target":{"op":"div","args":[{"lit":1},{"lit":2}]},"amount":{"lit":1}}""",
            """{"op":"damage","target":{"op":"add","args":[{"lit":1}]},"amount":{"lit":1}}""",
            """{"op":"damage","target":{"op":"add","args":[{"lit":1},{"lit":2}]},"amount":{"lit":1}}""",
            """{"op":"iterate","over":{"view":"Units"},"as":"u","actions":[{"op":"ask","kind":"unit","prompt":"p","fallback":"empty"}]}""",
            """{"op":"script","inputs":[],"outputs":[],"body":"","extra":1}""",
            """{"op":"fetch","from":{"lit":"nope"},"bind":{"status":"s"}}""",
        };

        var compiler = new HandlerDslCompiler();
        foreach (var actions in corpus)
        {
            var result = compiler.Compile(Fixtures.Doc(actions), Fixtures.Catalog, CoreView);
            Assert.False(result.Success, $"应被拒：{actions}");
            Assert.All(result.Errors, e => Assert.True(
                HandlerDslErrorCodes.IsKnown(e.Code),
                $"出现封闭集外的码 '{e.Code}'（契约违规）：{e.Message}"));
            Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.NodePath)));
        }
    }

    [Fact]
    public void 同一成员的多种错误都被收下_不遇错即停()
    {
        var errors = Validate("""
            {"op":"write","field":"Ghost","value":{"lit":1}},
            {"op":"damage","target":{"view":"Target"}}
            """);

        Assert.True(errors.Count >= 2, "收集齐而非遇错即停");
        Assert.Contains(errors, e => e.Code == HandlerDslErrorCodes.WriteForbidden);
        Assert.Contains(errors, e => e.Code == HandlerDslErrorCodes.PinMissing);
        Assert.All(errors, e => Assert.True(HandlerDslErrorCodes.IsKnown(e.Code)));
    }

    /// <summary>
    /// `__` 是生成器保留前缀（`__runtime`／`__bound_*`／`__res_*`／`__i*`／`__choice*`）：
    /// 作者用同名绑定会在生成的 csx 里造成重复声明——那是编译错误、报出来对不上图，
    /// 所以在图校验期就以 `bind-scope` 挡住（SPEC §8.7）。
    /// </summary>
    [Theory]
    [InlineData("""{"schemaVersion":1,"actions":[{"op":"let","name":"__runtime","value":{"lit":1}}]}""")]
    [InlineData("""{"schemaVersion":1,"actions":[{"op":"draw","count":{"lit":1},"bind":"__bound_x"}]}""")]
    [InlineData("""{"schemaVersion":1,"actions":[{"op":"iterate","over":{"view":"Units"},"as":"__it","actions":[]}]}""")]
    public void 绑定名不得使用生成器保留前缀(string document)
    {
        var errors = HandlerDslValidator.Validate(document, Fixtures.Catalog, View);

        Assert.Contains(errors, static e => e.Code == "bind-scope");
    }

    [Fact]
    public void 校验语料覆盖全部十三个码_每码至少一条拒绝用例()
    {
        var documents = new[]
        {
            """{"schemaVersion":2,"actions":[]}""",                                                   // schema-version
            """{"schemaVersion":1,"actions":{}}""",                                                    // graph-shape（不是数组）
            """{"schemaVersion":1,"actions":[{"op":"nope"}]}""",                                       // node-unknown
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"view":"Target"},"amount":{"lit":1},"ghost":1}]}""", // pin-unknown
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"view":"Target"}}]}""",            // pin-missing
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"lit":1},"amount":{"lit":1}}]}""", // pin-type
            """{"schemaVersion":1,"actions":[{"op":"draw","bind":{"drawn":"x"}}]}""",                    // bind-scope
            """{"schemaVersion":1,"actions":[{"op":"write","field":"Ghost","value":{"lit":1}}]}""",      // write-forbidden
            """{"schemaVersion":1,"actions":[{"op":"loop","actions":[]}]}""",                            // loop-unbounded
            """{"schemaVersion":1,"actions":[{"op":"iterate","over":{"view":"Units"},"as":"u","actions":[{"op":"ask","kind":"unit","prompt":"p","fallback":"empty"}]}]}""", // ask-in-loop
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"op":"div","args":[{"lit":1},{"lit":2}]},"amount":{"lit":1}}]}""", // expr-op-unknown
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"op":"add","args":[{"lit":1}]},"amount":{"lit":1}}]}""",          // expr-arity
            """{"schemaVersion":1,"actions":[{"op":"damage","target":{"op":"add","args":[{"lit":1},{"lit":true}]},"amount":{"lit":1}}]}""", // expr-type
        };

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var errors = HandlerDslValidator.Validate(document, Fixtures.Catalog, View);
            Assert.NotEmpty(errors);
            foreach (var error in errors)
            {
                covered.Add(error.Code);
            }
        }

        var missing = HandlerDslErrorCodes.All.Where(code => !covered.Contains(code)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"这些码没有拒绝用例：{string.Join("／", missing)}（R26 第 2 条）");
    }

    [Fact]
    public void 十三个码各自映射到structure或view()
    {
        Assert.Equal(13, HandlerDslErrorCodes.All.Count);
        Assert.Equal(13, HandlerDslErrorCategories.LoadCategories.Count);

        foreach (var code in HandlerDslErrorCodes.All)
        {
            var category = HandlerDslErrorCategories.LoadCategoryOf(code);
            Assert.True(
                category == PrefabErrorCategories.Structure || category == PrefabErrorCategories.View,
                $"'{code}' 映射到了 {category}");
        }

        Assert.Equal(PrefabErrorCategories.View, HandlerDslErrorCategories.LoadCategoryOf(HandlerDslErrorCodes.WriteForbidden));
        Assert.All(
            HandlerDslErrorCodes.All.Where(c => c != HandlerDslErrorCodes.WriteForbidden),
            code => Assert.Equal(PrefabErrorCategories.Structure, HandlerDslErrorCategories.LoadCategoryOf(code)));
    }

    [Fact]
    public void 装载分类新增capability且不复用compile()
    {
        Assert.Equal("capability", PrefabErrorCategories.Capability);
        Assert.NotEqual(PrefabErrorCategories.Compile, PrefabErrorCategories.Capability);
    }

    [Fact]
    public void PrefabLoadError的新字段可空且默认null()
    {
        var error = new PrefabLoadError(PrefabErrorCategories.Structure, "self#onDeploy", "坏了");
        Assert.Null(error.Code);
        Assert.Null(error.NodePath);

        var withCode = error with { Code = HandlerDslErrorCodes.PinMissing, NodePath = "actions[0].amount" };
        Assert.Equal("pin-missing", withCode.Code);
        Assert.Equal("actions[0].amount", withCode.NodePath);
    }

    [Fact]
    public void BuildMessage格式不变_不打印码与节点路径()
    {
        var exception = new PrefabLoadException(new[]
        {
            new PrefabLoadError(
                PrefabErrorCategories.View,
                "self#onDamageApply",
                "字段 Defense 不可写",
                new[] { "Defense" },
                HandlerDslErrorCodes.WriteForbidden,
                "actions[0].field"),
        });

        Assert.Contains("[view] self#onDamageApply：字段 Defense 不可写", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("write-forbidden", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("actions[0].field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 同一成员两条错误可由PrefabLoadReport同时收下()
    {
        var report = new PrefabLoadReport();
        report.Add(new PrefabLoadError(
            PrefabErrorCategories.Structure, "self#onDamageApply", "缺引脚", new[] { "amount" },
            HandlerDslErrorCodes.PinMissing, "actions[0].amount"));
        report.Add(new PrefabLoadError(
            PrefabErrorCategories.View, "self#onDamageApply", "字段不可写", new[] { "Name" },
            HandlerDslErrorCodes.WriteForbidden, "actions[1].field"));

        Assert.Equal(2, report.Errors.Count);
        Assert.Equal(2, report.Errors.Select(e => e.Code).Distinct().Count());
        Assert.Single(report.Errors.Select(e => e.Target).Distinct());
    }

    [Fact]
    public void 内核接口形态编译_无视图声明时view读取被拒()
    {
        IHandlerDslCompiler compiler = new HandlerDslCompiler();
        var result = compiler.Compile(
            Fixtures.Doc("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":1}}"""),
            Fixtures.Catalog);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == HandlerDslErrorCodes.PinUnknown);
    }

    [Fact]
    public void 合法文档编译成功并返回csx()
    {
        var compiler = new HandlerDslCompiler();
        var result = compiler.Compile(
            Fixtures.Doc("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":2}}"""),
            Fixtures.Catalog,
            CoreView);

        Assert.True(result.Success, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.Contains("DamageAsync", result.Csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 未提供视图声明时write一律write_forbidden_保守拒绝()
    {
        IHandlerDslCompiler compiler = new HandlerDslCompiler();
        var result = compiler.Compile(
            Fixtures.Doc("""{"op":"write","field":"Defense","value":{"lit":1}}"""),
            Fixtures.Catalog);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(HandlerDslErrorCodes.WriteForbidden, error.Code);
        Assert.Contains("view＝null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 视图字段类型超出封闭集_结构化pin_unknown而不是抛异常()
    {
        IHandlerDslCompiler compiler = new HandlerDslCompiler();
        var result = compiler.Compile(
            Fixtures.Doc("""{"op":"damage","target":{"view":"Target"},"amount":{"lit":1}}"""),
            Fixtures.Catalog,
            new HandlerDslView(new[] { new HandlerDslViewField("Target", "struct", false) }));

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(HandlerDslErrorCodes.PinUnknown, error.Code);
        Assert.Equal(HandlerDslContract.RootPath, error.NodePath);
        Assert.Contains("struct", error.Message, StringComparison.Ordinal);
    }
}
