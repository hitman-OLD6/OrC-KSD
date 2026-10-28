using Orc.Core;
using Orc.Core.Model;
using Orc.Script;
using Xunit;

namespace Orc.Script.Tests;

/// <summary>
/// S5 验收（承载视图 + csx 装载期预编译）：<c>ViewType</c> 恒为承载视图、按"形状 + 首参可接收性"校验签名、
/// 缓存不含形状维度、静态检查失败分类为 <c>binding</c>、编译失败即拒绝注册且不留残留、
/// csx 在装载期编译并在执行期经 <c>view.&lt;Name&gt;</c>／<c>owner.&lt;Name&gt;</c> 动态读写。
/// </summary>
public class CarrierScriptLoadingTests
{
    private static readonly ViewSpec View = new(new[]
    {
        new ViewFieldDefinition("amount", ViewFieldType.Number, required: false),
        new ViewFieldDefinition("writableAmount", ViewFieldType.Number, writable: true, required: false),
    });

    private const string WriteScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
        + "{\n"
        + "    view.writableAmount = 42;\n"
        + "    return Task.CompletedTask;\n"
        + "};";

    private static ScriptRequest Request(string source, ViewSpec? view = null)
        => new(source, "HandleAsync", typeof(ViewCarrier), View: view ?? View, OwnerMembers: Array.Empty<string>());

    [Fact]
    public void 承载视图请求编译成功且入口绑定为内核handler()
    {
        var evaluator = new CSharpScriptEvaluator();

        var result = evaluator.Evaluate(Request(WriteScript));

        Assert.True(result.Success, result.Error);
        Assert.IsType<KernelHandler>(result.Handler);
        Assert.NotNull(result.Binding);
        Assert.True(result.Binding!.IsValid);
    }

    [Fact]
    public void 未声明字段报binding与compile可区分()
    {
        var evaluator = new CSharpScriptEvaluator();

        var binding = evaluator.Evaluate(Request(
            "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
            + "{\n"
            + "    var x = view.nope;\n"
            + "    return Task.CompletedTask;\n"
            + "};"));
        Assert.False(binding.Success);
        Assert.Equal(ScriptErrorCategories.Binding, binding.ErrorCategory);

        var compile = evaluator.Evaluate(Request(
            "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
            + "{\n"
            + "    this is not csharp;\n"
            + "};"));
        Assert.False(compile.Success);
        Assert.Equal(ScriptErrorCategories.Compile, compile.ErrorCategory);
    }

    [Fact]
    public void 首参不能接收承载视图时按signature拒绝()
    {
        var evaluator = new CSharpScriptEvaluator();

        var result = evaluator.Evaluate(Request(
            "Func<string, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;"));

        Assert.False(result.Success);
        Assert.Equal(ScriptErrorCategories.Signature, result.ErrorCategory);
    }

    [Fact]
    public void 缓存键不含形状维度()
    {
        var evaluator = new CSharpScriptEvaluator();
        var otherShape = new ViewSpec(new[]
        {
            new ViewFieldDefinition("amount", ViewFieldType.Number, required: false),
            new ViewFieldDefinition("writableAmount", ViewFieldType.Number, writable: true, required: false),
            new ViewFieldDefinition("unused", ViewFieldType.String, required: false),
        });

        var first = evaluator.Evaluate(Request(WriteScript, View));
        var second = evaluator.Evaluate(Request(WriteScript, otherShape));

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);

        // ViewType 恒为承载视图 ⇒ 同一源码只产生一个缓存条目（不再按"资产声明的形状"分桶）。
        Assert.Equal(1, evaluator.CacheCount);
    }

    [Fact]
    public void MicrosoftCSharp进入沙箱引用白名单()
    {
        Assert.Contains("Microsoft.CSharp", CSharpScriptSecurityOptions.DefaultAllowedAssemblies);
    }

    [Fact]
    public void 装载期预编译失败即拒绝注册且不留残留()
    {
        var handlers = new HandlerRegistry();
        var evaluator = new CSharpScriptEvaluator();

        var error = Assert.Throws<PrefabLoadException>(() => handlers.RegisterScript(
            "bad",
            "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
            + "{\n"
            + "    var x = view.nope;\n"
            + "    return Task.CompletedTask;\n"
            + "};",
            evaluator,
            View,
            Array.Empty<string>()));

        Assert.Equal(ScriptErrorCategories.Binding, error.Category);
        Assert.Equal(0, handlers.Count);
        Assert.False(handlers.TryResolve("bad", out _));
    }

    [Fact]
    public async Task csx在装载期编译_执行期经承载视图写回数据面()
    {
        var handlers = new HandlerRegistry();
        var evaluator = new CSharpScriptEvaluator();
        var csx = handlers.RegisterScript("csx.write", WriteScript, evaluator, View, Array.Empty<string>());

        object? observed = null;
        var reader = handlers.Register("reader", (view, ctx, ct) =>
        {
            observed = ((ViewCarrier)view).Get("writableAmount");
            return Task.CompletedTask;
        });

        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition(
                "engine",
                EntityKind.Engine,
                members: new[]
                {
                    new MemberDefinition("csx", View, MemberVisibility.All, new[] { new OwnedEdge(csx) }),
                }))
            .Add(new PrefabDefinition("card.base", EntityKind.Card))
            .Add(new PrefabDefinition(
                "comp.reader",
                EntityKind.Component,
                members: new[]
                {
                    new MemberDefinition(
                        "reader",
                        ViewSpec.Of(("writableAmount", ViewFieldType.Number)),
                        MemberVisibility.Mountable,
                        new[] { new OwnedEdge(reader) }),
                },
                mounts: new[] { new MountDeclaration("engine#csx", "reader") }));

        var runtime = new OrchestrationRuntime(catalog, handlers);
        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.reader", "读取组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "csx");

        Assert.Equal(42, observed);
    }

    [Fact]
    public async Task csx里owner动态通道可写私有字段且不落数据面()
    {
        var handlers = new HandlerRegistry();
        var evaluator = new CSharpScriptEvaluator();
        var view = ViewSpec.Empty;
        var csx = handlers.RegisterScript(
            "csx.owner",
            "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n"
            + "{\n"
            + "    dynamic owner = view.Owner;\n"
            + "    owner.ammo = 9;\n"
            + "    return Task.CompletedTask;\n"
            + "};",
            evaluator,
            view,
            new[] { "ammo" });

        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition(
                "engine",
                EntityKind.Engine,
                members: new[]
                {
                    new MemberDefinition("go", ViewSpec.Empty, MemberVisibility.All),
                }))
            .Add(new PrefabDefinition("card.base", EntityKind.Card))
            .Add(new PrefabDefinition(
                "comp.owner",
                EntityKind.Component,
                privateFields: new[] { new PrivateFieldDefinition("ammo", ViewFieldType.Number, 1) },
                members: new[]
                {
                    new MemberDefinition(
                        "tick",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        new[] { new OwnedEdge(csx) }),
                },
                mounts: new[] { new MountDeclaration("engine#go", "tick") }));

        var runtime = new OrchestrationRuntime(catalog, handlers);
        var host = runtime.CreateEntity("card.base", "宿主");
        var component = runtime.CreateEntity("comp.owner", "组件", host);
        runtime.Bind(component);

        await runtime.RunMemberAsync(runtime.Engine, "go");

        Assert.Equal(9, component.GetPrivateField("ammo"));
    }
}
