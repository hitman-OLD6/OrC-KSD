using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>S3 验收：引脚类型封闭集与表达式类型推导（R16／D7）。</summary>
public class TypeSystemTests
{
    [Theory]
    [InlineData("entity")]
    [InlineData("number")]
    [InlineData("string")]
    [InlineData("boolean")]
    [InlineData("list<entity>")]
    [InlineData("list<number>")]
    [InlineData("list<string>")]
    [InlineData("list<boolean>")]
    public void 引脚类型封闭集_八个规范名可解析(string name)
    {
        var type = PinType.Parse(name);
        Assert.Equal(name, type.Format());
        Assert.Contains(name, PinType.CanonicalNames);
    }

    [Theory]
    [InlineData("list<list<entity>>")]
    [InlineData("map<string,number>")]
    [InlineData("entity?")]
    [InlineData("Player")]
    [InlineData("Position")]
    [InlineData("Deck")]
    [InlineData("System.String")]
    public void 五类之外的类型名_拒绝(string name)
    {
        Assert.False(PinType.TryParse(name, out _));
        var exception = Assert.Throws<FormatException>(() => PinType.Parse(name));
        Assert.Contains("封闭集", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 集合不可嵌套()
    {
        Assert.Throws<ArgumentException>(() => PinType.List(PinType.List(PinType.Entity)));
    }

    [Fact]
    public void 视图字段类型与引脚类型同构()
    {
        Assert.Equal(PinType.List(PinType.Entity), PinType.FromViewFieldType(Orc.Core.Model.ViewFieldType.EntityArray));
        Assert.Equal(PinType.Number, PinType.FromViewFieldType(Orc.Core.Model.ViewFieldType.Number));
    }

    [Fact]
    public void entity引脚接number_图校验期拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"lit\":1},\"amount\":{\"lit\":2}}"));
        var error = Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].target");
        Assert.Contains("entity", error.Message, StringComparison.Ordinal);
        Assert.Contains("number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 无隐式转换_number表达式接string引脚_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc("{\"op\":\"announce\",\"text\":{\"lit\":1}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].text");
    }

    [Fact]
    public void 实体引脚接集合_拒绝并提示改用iterate()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"view\":\"Units\"},\"amount\":{\"lit\":1}}"));
        var error = Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].target");
        Assert.Contains("iterate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 静态子类型可确定且不匹配_拒绝()
    {
        // ask kind=card → list<entity{card}> → iterate 元素是 entity{card} → damage.target 只收 unit／hq。
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"ask","kind":"card","prompt":"选一张","bind":"picked"},
              {"op":"iterate","over":{"var":"picked"},"as":"u","actions":[
                {"op":"damage","target":{"var":"u"},"amount":{"lit":1}}
              ]}
            ]}
            """));
        var error = Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].actions[1].actions[0].target");
        Assert.Contains("子类型", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 静态子类型可确定且匹配_放行()
    {
        // ask kind=unit → list<entity{unit}> → damage.target 接受 unit／hq → 通过。
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"ask","kind":"unit","prompt":"选一个单位","bind":"picked"},
              {"op":"iterate","over":{"var":"picked"},"as":"u","actions":[
                {"op":"damage","target":{"var":"u"},"amount":{"lit":1}}
              ]}
            ]}
            """));
        Assert.Empty(errors);
    }

    [Fact]
    public void 子类型不可静态确定_放行()
    {
        // view Target 的类型来自视图声明，只知 entity、不知子类型 → 放行，运行期沿用动作面语义。
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"view\":\"Target\"},\"amount\":{\"lit\":2}}"));
        Assert.Empty(errors);
    }

    [Fact]
    public void 算子名不在封闭集_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"div\",\"args\":[{\"lit\":1},{\"lit\":2}]}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.ExprOpUnknown, "actions[0].count");
    }

    [Fact]
    public void 算子入参个数不符_拒绝()
    {
        var tooMany = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"add\",\"args\":[{\"lit\":1},{\"lit\":2},{\"lit\":3}]}}"));
        Fixtures.Single(tooMany, HandlerDslErrorCodes.ExprArity, "actions[0].count");

        var tooFew = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"not\",\"args\":[{\"lit\":true},{\"lit\":false}]}}"));
        Fixtures.Single(tooFew, HandlerDslErrorCodes.ExprArity, "actions[0].count");
    }

    [Fact]
    public void 算子入参类型不符_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"add\",\"args\":[{\"lit\":1},{\"lit\":\"x\"}]}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.ExprType, "actions[0].count.args[1]");
    }

    [Fact]
    public void 聚合输入非集合_拒绝()
    {
        var scalar = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"count\",\"args\":[{\"lit\":1}]}}"));
        Fixtures.Single(scalar, HandlerDslErrorCodes.ExprType, "actions[0].count.args[0]");

        var entityList = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"sum\",\"args\":[{\"view\":\"Units\"}]}}"));
        Fixtures.Single(entityList, HandlerDslErrorCodes.ExprType, "actions[0].count.args[0]");
    }

    [Fact]
    public void 比较算子两侧类型必须相同()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"branch\",\"test\":{\"op\":\"eq\",\"args\":[{\"lit\":1},{\"lit\":\"1\"}]},\"then\":[{\"op\":\"sequence\",\"actions\":[]}]}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.ExprType, "actions[0].test");
    }

    [Fact]
    public void 判空算子返回布尔_可作为branch条件()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"branch","test":{"op":"empty","args":[{"view":"Units"}]},
             "then":[{"op":"sequence","actions":[]}]}
            """));
        Assert.Empty(errors);
    }

    [Fact]
    public void 判空算子结果接number引脚_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"empty\",\"args\":[{\"view\":\"Units\"}]}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].count");
    }

    [Fact]
    public void 计数结果可接number引脚()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"op\":\"count\",\"args\":[{\"view\":\"Units\"}]}}"));
        Assert.Empty(errors);
    }

    [Fact]
    public void 类型推导与上下文无关()
    {
        var json = Fixtures.Doc("{\"op\":\"draw\",\"count\":{\"op\":\"sub\",\"args\":[{\"view\":\"Defense\"},{\"lit\":1}]}}");
        var read = Fixtures.Read(json);
        var amount = ((ActionNode)read.Document!.Actions[0]).Arguments[0].Value;

        var first = HandlerDslTypeChecker.Infer(amount, Fixtures.Catalog, Fixtures.View);
        var second = HandlerDslTypeChecker.Infer(amount, Fixtures.Catalog, Fixtures.View);

        Assert.Equal(PinType.Number, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void 同一个表达式搬到另一个引脚_推导结果不变()
    {
        // 同一个 sub(Defense, 1)：接 number 引脚合法；接 string 引脚只会在**引脚层**报 pin-type，
        // 表达式自身的推导结果仍是 number（不做双向推断）。
        var expression = """
            {"op":"sub","args":[{"view":"Defense"},{"lit":1}]}
            """;

        var ok = Fixtures.Check(Fixtures.Doc($"{{\"op\":\"draw\",\"count\":{expression}}}"));
        Assert.Empty(ok);

        var bad = Fixtures.Check(Fixtures.Doc($"{{\"op\":\"announce\",\"text\":{expression}}}"));
        Assert.Single(bad, e => e.Code == HandlerDslErrorCodes.PinType);
    }

    [Fact]
    public void 未声明的视图字段_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"view\":\"NotDeclared\"}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count");
    }

    [Fact]
    public void read路径不在可读项内_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"read\":{\"of\":\"self\",\"path\":\"notAThing\"}}}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count.read.path");
    }

    [Fact]
    public void read对象超出封闭集_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"read\":{\"of\":\"enemy\",\"path\":\"attack\"}}}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count.read.of");
    }

    [Fact]
    public void read可读项_推导出number()
    {
        Assert.Empty(Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"read\":{\"of\":\"self\",\"path\":\"attack\"}}}")));

        var stringPin = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"announce\",\"text\":{\"read\":{\"of\":\"self\",\"path\":\"attack\"}}}"));
        Fixtures.Single(stringPin, HandlerDslErrorCodes.PinType, "actions[0].text");
    }

    [Fact]
    public void iterate元素类型来自集合元素()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"iterate","over":{"view":"Units"},"as":"u","actions":[
              {"op":"damage","target":{"var":"u"},"amount":{"view":"Defense"}}
            ]}
            """));
        Assert.Empty(errors);

        // 把元素当数字用：amount 期望 number、实际 entity → pin-type（证明 as 的类型是元素类型）。
        var bad = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"iterate","over":{"view":"Units"},"as":"u","actions":[
              {"op":"damage","target":{"view":"Target"},"amount":{"var":"u"}}
            ]}
            """));
        Fixtures.Single(bad, HandlerDslErrorCodes.PinType, "actions[0].actions[0].amount");
    }

    [Fact]
    public void iterate的over必须是集合()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"iterate\",\"over\":{\"view\":\"Target\"},\"as\":\"u\",\"actions\":[]}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.PinType, "actions[0].over");
    }

    [Fact]
    public void 未定义的绑定_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"view\":\"Target\"},\"amount\":{\"var\":\"nope\"}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.BindScope, "actions[0].amount");
    }

    [Fact]
    public void 绑定名与视图字段同名_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"let\",\"name\":\"Defense\",\"value\":{\"lit\":1}}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.BindScope, "actions[0].name");
    }

    [Fact]
    public void 同一作用域重复绑定_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"let","name":"n","value":{"lit":1}},
              {"op":"let","name":"n","value":{"lit":2}}
            ]}
            """));
        Fixtures.Single(errors, HandlerDslErrorCodes.BindScope, "actions[0].actions[1].name");
    }

    [Fact]
    public void 分支内声明分支外引用_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"branch","test":{"lit":true},
               "then":[{"op":"let","name":"n","value":{"lit":1}}]},
              {"op":"draw","count":{"var":"n"}}
            ]}
            """));
        Fixtures.Single(errors, HandlerDslErrorCodes.BindScope, "actions[0].actions[1].count");
    }

    [Fact]
    public void 循环内声明循环外引用_拒绝()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"loop","times":{"lit":2},"actions":[{"op":"let","name":"n","value":{"lit":1}}]},
              {"op":"draw","count":{"var":"n"}}
            ]}
            """));
        Fixtures.Single(errors, HandlerDslErrorCodes.BindScope, "actions[0].actions[1].count");
    }

    [Fact]
    public void 两个循环各自声明同名绑定_不算重复赋值()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"loop","times":{"lit":1},"actions":[{"op":"let","name":"n","value":{"lit":1}},
                {"op":"draw","count":{"var":"n"}}]},
              {"op":"loop","times":{"lit":1},"actions":[{"op":"let","name":"n","value":{"lit":2}},
                {"op":"draw","count":{"var":"n"}}]}
            ]}
            """));
        Assert.Empty(errors);
    }

    [Fact]
    public void 循环上限类型不是number_落loop_unbounded()
    {
        var errors = Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"loop\",\"times\":{\"lit\":\"三\"},\"actions\":[]}"));
        Fixtures.Single(errors, HandlerDslErrorCodes.LoopUnbounded, "actions[0].times");
    }

    [Fact]
    public void 负数上限不报错_零次执行由生成器保证()
    {
        Assert.Empty(Fixtures.Check(Fixtures.Doc(
            "{\"op\":\"loop\",\"times\":{\"lit\":-3},\"actions\":[]}")));
    }

    [Fact]
    public void 多输出绑定_各自类型来自目录()
    {
        var ok = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"fetch","bind":{"status":"st","card":"c"}},
              {"op":"announce","text":{"var":"st"}},
              {"op":"damage","target":{"var":"c"},"amount":{"lit":1}}
            ]}
            """));
        Assert.Empty(ok);

        var wrong = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"fetch","bind":{"status":"st","card":"c"}},
              {"op":"draw","count":{"var":"c"}}
            ]}
            """));
        Fixtures.Single(wrong, HandlerDslErrorCodes.PinType, "actions[0].actions[1].count");
    }

    [Fact]
    public void 算子集封闭且无除法()
    {
        Assert.Equal(17, HandlerDslOperators.V1.Count);
        Assert.Contains("empty", HandlerDslOperators.V1);
        Assert.DoesNotContain("div", HandlerDslOperators.V1);
        Assert.False(HandlerDslOperators.IsKnown("div"));
        Assert.True(HandlerDslOperators.TryArity("not", out var arity));
        Assert.Equal(1, arity);
    }

    [Fact]
    public void 交互节点不得出现在iterate循环体内()
    {
        var ask = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"iterate","over":{"view":"Units"},"as":"u","actions":[
              {"op":"ask","kind":"unit","prompt":"选一个"}
            ]}
            """));
        Fixtures.Single(ask, HandlerDslErrorCodes.AskInLoop, "actions[0].actions[0]");

        var choice = Fixtures.Check(Fixtures.Doc(
            """
            {"op":"iterate","over":{"view":"Units"},"as":"u","actions":[
              {"op":"choice","prompt":"选一个","options":[{"label":"甲","actions":[]}]}
            ]}
            """));
        Fixtures.Single(choice, HandlerDslErrorCodes.AskInLoop, "actions[0].actions[0]");
    }

    [Fact]
    public void 交互节点在iterate外_合法()
    {
        Assert.Empty(Fixtures.Check(Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"ask","kind":"unit","prompt":"选一个单位","bind":"picked"},
              {"op":"iterate","over":{"var":"picked"},"as":"u","actions":[
                {"op":"damage","target":{"var":"u"},"amount":{"lit":1}}
              ]}
            ]}
            """)));
    }

    [Fact]
    public void 错误码集封闭且与规范一致()
    {
        Assert.Equal(13, HandlerDslErrorCodes.All.Count);
        Assert.All(HandlerDslErrorCodes.All, code => Assert.True(HandlerDslErrorCodes.IsKnown(code)));
        Assert.False(HandlerDslErrorCodes.IsKnown("structure"));
    }
}
