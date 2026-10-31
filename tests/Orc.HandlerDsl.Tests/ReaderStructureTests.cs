using Orc.Core;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>S2 验收：DSL 树模型与结构校验（R18／R13／R21／D17）。</summary>
public class ReaderStructureTests
{
    [Fact]
    public void 合法文档_解析为树_顺序即执行顺序()
    {
        var json = Fixtures.Doc(
            """
            {"op":"sequence","actions":[
              {"op":"let","name":"n","value":{"op":"sub","args":[{"view":"Defense"},{"lit":1}]}},
              {"op":"branch","test":{"op":"gt","args":[{"var":"n"},{"lit":0}]},
               "then":[{"op":"damage","target":{"view":"Target"},"amount":{"var":"n"}}]},
              {"op":"draw","count":{"var":"n"},"bind":"drawn"}
            ]}
            """);

        var read = Fixtures.Read(json);
        Assert.Empty(read.Errors);
        Assert.NotNull(read.Document);

        var document = read.Document!;
        Assert.Equal(HandlerDslContract.SchemaVersion, document.SchemaVersion);
        Assert.Single(document.Actions);

        var root = Assert.IsType<SequenceNode>(document.Actions[0]);
        Assert.Equal(3, root.Actions.Count);
        Assert.Equal("branch", root.Actions[1].Op);
        Assert.Equal("draw", root.Actions[2].Op);
        Assert.Equal("actions[0].actions[1].then[0]", root.Actions[1].Children[0].Path);
        Assert.Empty(HandlerDslTypeChecker.Check(document, Fixtures.Catalog, Fixtures.View));
    }

    [Fact]
    public void 根不是对象_判为非树()
    {
        var read = Fixtures.Read("[1,2,3]");
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.GraphShape, "$");
        Assert.Null(read.Document);
    }

    [Fact]
    public void actions_不是数组_判为非树()
    {
        var read = Fixtures.Read("{\"schemaVersion\":1,\"actions\":{}}");
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.GraphShape, "$.actions");
    }

    [Fact]
    public void 子节点不是对象_判为非树且指出位置()
    {
        var read = Fixtures.Read(Fixtures.Doc("\"nope\""));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.GraphShape, "actions[0]");
    }

    [Fact]
    public void 深度上限为64_恰好64层通过()
    {
        var read = Fixtures.Read(NestedSequences(64));
        Assert.Empty(read.Errors);
    }

    [Fact]
    public void 深度超过64层_拒绝()
    {
        var read = Fixtures.Read(NestedSequences(65));
        var error = Assert.Single(read.Errors);
        Assert.Equal(HandlerDslErrorCodes.GraphShape, error.Code);
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
        Assert.StartsWith("actions[0].actions[0]", error.NodePath, StringComparison.Ordinal);
    }

    [Fact]
    public void 无else时省略字段_合法()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"branch\",\"test\":{\"lit\":true},\"then\":[{\"op\":\"sequence\",\"actions\":[]}]}"));
        Assert.Empty(read.Errors);

        var branch = Assert.IsType<BranchNode>(read.Document!.Actions[0]);
        Assert.Null(branch.Else);
    }

    [Fact]
    public void 空else数组_拒绝_同一语义只许一种文本()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"branch\",\"test\":{\"lit\":true},\"then\":[{\"op\":\"sequence\",\"actions\":[]}],\"else\":[]}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.GraphShape, "actions[0].else");
    }

    [Fact]
    public void then缺失或为空_拒绝()
    {
        var missing = Fixtures.Read(Fixtures.Doc("{\"op\":\"branch\",\"test\":{\"lit\":true}}"));
        Fixtures.Single(missing.Errors, HandlerDslErrorCodes.GraphShape, "actions[0].then");

        var empty = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"branch\",\"test\":{\"lit\":true},\"then\":[]}"));
        Fixtures.Single(empty.Errors, HandlerDslErrorCodes.GraphShape, "actions[0].then");
    }

    [Fact]
    public void 节点字段封闭_表外字段被拒()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"sequence\",\"actions\":[],\"next\":\"actions[2]\"}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].next");
    }

    [Fact]
    public void 未知节点名_含节点路径()
    {
        var read = Fixtures.Read(Fixtures.Doc("{\"op\":\"no-such-node\"}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.NodeUnknown, "actions[0]");
    }

    [Fact]
    public void 动作节点_引脚名与必填性来自目录()
    {
        var read = Fixtures.Read(Fixtures.Doc("{\"op\":\"damage\",\"target\":{\"view\":\"Target\"}}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinMissing, "actions[0].amount");

        var unknownPin = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"view\":\"Target\"},\"amount\":{\"lit\":1},\"nope\":{\"lit\":1}}"));
        Fixtures.Single(unknownPin.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].nope");
    }

    [Fact]
    public void bind单输出写字符串_合法()
    {
        var read = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"bind\":\"drawn\"}"));
        Assert.Empty(read.Errors);
        var node = Assert.IsType<ActionNode>(read.Document!.Actions[0]);
        Assert.True(node.Bind!.IsSingle);
        Assert.Equal("drawn", node.Bind.Single);
    }

    [Fact]
    public void bind单输出写对象_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"bind\":{\"drawn\":\"n\"}}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.BindScope, "actions[0].bind");
    }

    [Fact]
    public void bind多输出_键集必须恰好等于输出名集()
    {
        var ok = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"fetch\",\"bind\":{\"status\":\"st\",\"card\":\"c\"}}"));
        Assert.Empty(ok.Errors);

        var missing = Fixtures.Read(Fixtures.Doc("{\"op\":\"fetch\",\"bind\":{\"status\":\"st\"}}"));
        Assert.Contains(missing.Errors, e => e.Code == HandlerDslErrorCodes.BindScope);

        var unknown = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"fetch\",\"bind\":{\"status\":\"st\",\"card\":\"c\",\"extra\":\"e\"}}"));
        Fixtures.Single(unknown.Errors, HandlerDslErrorCodes.BindScope, "actions[0].bind.extra");

        var wrongForm = Fixtures.Read(Fixtures.Doc("{\"op\":\"fetch\",\"bind\":\"x\"}"));
        Fixtures.Single(wrongForm.Errors, HandlerDslErrorCodes.BindScope, "actions[0].bind");
    }

    [Fact]
    public void bind无返回值节点_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"damage\",\"target\":{\"view\":\"Target\"},\"amount\":{\"lit\":1},\"bind\":\"x\"}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.BindScope, "actions[0].bind");
    }

    [Fact]
    public void schemaVersion缺失_拒绝并给出当前版本()
    {
        var read = Fixtures.Read("{\"actions\":[]}");
        var error = Fixtures.Single(read.Errors, HandlerDslErrorCodes.SchemaVersion, "$.schemaVersion");
        Assert.Contains(HandlerDslContract.SchemaVersion.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void schemaVersion不匹配_错误含双方版本()
    {
        var read = Fixtures.Read("{\"schemaVersion\":2,\"actions\":[]}");
        var error = Fixtures.Single(read.Errors, HandlerDslErrorCodes.SchemaVersion, "$.schemaVersion");
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
        Assert.Null(read.Document);
    }

    [Fact]
    public void catalogRevision缺失_合法_不一致也不拒绝()
    {
        Assert.Empty(Fixtures.Read(Fixtures.Doc("")).Errors);

        var mismatch = Fixtures.Read("{\"schemaVersion\":1,\"catalogRevision\":999,\"actions\":[]}");
        Assert.Empty(mismatch.Errors);
        Assert.Equal(999, mismatch.Document!.CatalogRevision);
    }

    [Fact]
    public void catalogRevision不一致_只在已失败的错误上留痕()
    {
        var read = Fixtures.Read(
            "{\"schemaVersion\":1,\"catalogRevision\":999,\"actions\":[{\"op\":\"no-such-node\"}]}");
        var error = Assert.Single(read.Errors);
        Assert.Equal(HandlerDslErrorCodes.NodeUnknown, error.Code);
        Assert.Contains("catalogRevision=999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 文档不带资产身份与挂载声明()
    {
        foreach (var field in new[] { "assetId", "member", "mounts", "wraps", "extends", "engine#self" })
        {
            var read = Fixtures.Read(
                $"{{\"schemaVersion\":1,\"actions\":[],\"{field}\":\"x\"}}");
            var error = Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, $"$.{field}");
            Assert.Contains(field, error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("ref")]
    [InlineData("next")]
    [InlineData("target")]
    [InlineData("exec")]
    public void 节点无标识_无跳转边语法(string field)
    {
        var read = Fixtures.Read(Fixtures.Doc($"{{\"op\":\"sequence\",\"actions\":[],\"{field}\":\"x\"}}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, $"actions[0].{field}");
    }

    [Fact]
    public void 树模型无exec与next等成员()
    {
        var forbidden = new[] { "Id", "Ref", "Next", "Exec", "Target" };
        var members = typeof(DslNode).GetProperties().Select(p => p.Name)
            .Concat(typeof(ActionNode).GetProperties().Select(p => p.Name))
            .Concat(typeof(DslDocument).GetProperties().Select(p => p.Name))
            .ToArray();

        Assert.DoesNotContain(members, m => forbidden.Contains(m, StringComparer.Ordinal));
        Assert.Contains("Path", members);
    }

    [Fact]
    public void 循环缺上限_拒绝()
    {
        var loop = Fixtures.Read(Fixtures.Doc("{\"op\":\"loop\",\"actions\":[]}"));
        Fixtures.Single(loop.Errors, HandlerDslErrorCodes.LoopUnbounded, "actions[0].times");

        var whileNode = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"while\",\"test\":{\"lit\":true},\"actions\":[]}"));
        Fixtures.Single(whileNode.Errors, HandlerDslErrorCodes.LoopUnbounded, "actions[0].limit");
    }

    [Fact]
    public void 循环上限可为任意number表达式()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"loop\",\"times\":{\"op\":\"count\",\"args\":[{\"view\":\"Units\"}]},\"actions\":[]}"));
        Assert.Empty(read.Errors);
        Assert.Empty(HandlerDslTypeChecker.Check(read.Document!, Fixtures.Catalog, Fixtures.View));
    }

    [Fact]
    public void 迭代绑定名非法_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"iterate\",\"over\":{\"view\":\"Units\"},\"as\":\"1x\",\"actions\":[]}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.BindScope, "actions[0].as");
    }

    [Fact]
    public void 脚本引脚类型越界_拒绝()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"script\",\"inputs\":[{\"name\":\"x\",\"type\":\"map<string,number>\"}],\"outputs\":[],\"body\":\"\"}"));
        Fixtures.Single(read.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].inputs[0].type");
    }

    [Fact]
    public void 脚本引脚合法_解析为自带schema()
    {
        var read = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"script\",\"inputs\":[{\"name\":\"x\",\"type\":\"list<entity>\"}],\"outputs\":[{\"name\":\"n\",\"type\":\"number\"}],\"body\":\"return n;\"}"));
        Assert.Empty(read.Errors);
        var script = Assert.IsType<ScriptNode>(read.Document!.Actions[0]);
        Assert.Equal(PinType.List(PinType.Entity), script.Inputs[0].Type);
        Assert.Equal("n", script.Outputs[0].Name);
    }

    [Fact]
    public void 目录节点名与内建op冲突_构造期失败()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DslCatalog(
            1,
            new[] { new DslNodeSignature("loop", Array.Empty<DslPin>(), Array.Empty<DslPin>()) }));

        Assert.Contains("loop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 子数组只允许actions_then_else_options点actions()
    {
        // 「children」不是合法子数组名：表外字段＝pin-unknown（子数组命名规则唯一）。
        var read = Fixtures.Read(Fixtures.Doc("{\"op\":\"sequence\",\"children\":[]}"));
        Assert.Contains(read.Errors, e =>
            e.Code == HandlerDslErrorCodes.PinUnknown && e.NodePath == "actions[0].children");

        // choice 的选项体固定叫 options[].actions。
        var choice = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"choice\",\"prompt\":\"选一个\",\"options\":[{\"label\":\"甲\",\"actions\":[]}]}"));
        Assert.Empty(choice.Errors);
        Assert.Equal("actions[0].options[0].actions", Assert.Single(Assert.IsType<ChoiceNode>(choice.Document!.Actions[0]).Options).Path + ".actions");
    }

    [Fact]
    public void 引脚取值只取五种变体_第六种形态被拒()
    {
        var sixth = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"count\":{\"ref\":\"x\"}}"));
        Fixtures.Single(sixth.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count.ref");

        var twoVariants = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"lit\":1,\"view\":\"Defense\"}}"));
        Fixtures.Single(twoVariants.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count");

        var notObject = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"count\":3}"));
        Fixtures.Single(notObject.Errors, HandlerDslErrorCodes.PinUnknown, "actions[0].count");
    }

    [Fact]
    public void read缺少of或path_拒绝()
    {
        var missingOf = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"read\":{\"path\":\"attack\"}}}"));
        Fixtures.Single(missingOf.Errors, HandlerDslErrorCodes.PinMissing, "actions[0].count.read.of");

        var missingPath = Fixtures.Read(Fixtures.Doc(
            "{\"op\":\"draw\",\"count\":{\"read\":{\"of\":\"self\"}}}"));
        Fixtures.Single(missingPath.Errors, HandlerDslErrorCodes.PinMissing, "actions[0].count.read.path");
    }

    [Fact]
    public void 算子args缺失或为空_拒绝()
    {
        var missing = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"count\":{\"op\":\"add\"}}"));
        Fixtures.Single(missing.Errors, HandlerDslErrorCodes.ExprArity, "actions[0].count.args");

        var empty = Fixtures.Read(Fixtures.Doc("{\"op\":\"draw\",\"count\":{\"op\":\"add\",\"args\":[]}}"));
        Fixtures.Single(empty.Errors, HandlerDslErrorCodes.ExprArity, "actions[0].count.args");
    }

    private static string NestedSequences(int depth)
    {
        var inner = "{\"op\":\"sequence\",\"actions\":[]}";
        for (var i = 1; i < depth; i++)
        {
            inner = $"{{\"op\":\"sequence\",\"actions\":[{inner}]}}";
        }

        return Fixtures.Doc(inner);
    }
}
