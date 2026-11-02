using Orc.Core;
using Orc.Core.Model;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// **R32 守卫合成**：四档 guard 各生成正确的过滤代码，且守卫恒在作者效果体**之前**。
/// <para>
/// 契约要点：<c>self</c> 用引用相等比较宿主与载荷实体；<c>owner-same</c>／<c>owner-different</c> 在
/// <c>Owner</c> 为 null（无主卡）时**不抛异常**——前者按「不同主」跳过效果体，后者按「不同主」放行；
/// <c>any</c> 档不合成任何代码（模板文案也不显示）。
/// </para>
/// </summary>
public class GuardGenerationTests
{
    private static readonly HandlerDslView View = new(new[]
    {
        new HandlerDslViewField("Unit", "entity", false),
        new HandlerDslViewField("Player", "entity", false),
    });

    private static readonly string Doc = """{"schemaVersion":1,"actions":[{"op":"stop"}]}""";

    private static string Generate(HandlerGuardSpec? guard)
    {
        var result = new HandlerDslCompiler().CompileGuarded(Doc, Fixtures.Catalog, View, guard!);
        Assert.True(result.Success, string.Join("；", result.Errors.Select(static e => e.Message)));
        return result.Csx!;
    }

    [Fact]
    public void self档_用引用相等比较宿主与载荷实体()
    {
        var csx = Generate(new HandlerGuardSpec(HandlerGuard.Self, "Unit", string.Empty));

        Assert.Contains("var orcSelf = (Orc.Cards.Card)view.Host;", csx, StringComparison.Ordinal);
        Assert.Contains("if (!object.ReferenceEquals((object)orcSelf, (object)view.Unit))", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("__guardOwner", csx, StringComparison.Ordinal); // self 档不读归属
    }

    [Fact]
    public void owner_same档_无主卡按不同主处理且不抛异常()
    {
        var csx = Generate(new HandlerGuardSpec(HandlerGuard.OwnerSame, string.Empty, "Player"));

        Assert.Contains("var __guardOwner = orcSelf is null ? null : orcSelf.Owner;", csx, StringComparison.Ordinal);
        // Owner 为 null ⇒ 条件为真 ⇒ return（＝不同主）。
        Assert.Contains(
            "if (__guardOwner is null || !object.ReferenceEquals((object)__guardOwner, (object)view.Player))",
            csx,
            StringComparison.Ordinal);
    }

    [Fact]
    public void owner_different档_无主卡按不同主处理即放行()
    {
        var csx = Generate(new HandlerGuardSpec(HandlerGuard.OwnerDifferent, string.Empty, "Player"));

        // Owner 为 null ⇒ 条件为假 ⇒ 不 return（＝按不同主放行），也不会解引用 null。
        Assert.Contains(
            "if (__guardOwner is not null && object.ReferenceEquals((object)__guardOwner, (object)view.Player))",
            csx,
            StringComparison.Ordinal);
    }

    [Fact]
    public void any档_不合成任何守卫代码()
    {
        var csx = Generate(new HandlerGuardSpec(HandlerGuard.Any, string.Empty, string.Empty));

        Assert.DoesNotContain("__guardOwner", csx, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceEquals", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void 守卫恒在作者效果体之前_且作者DSL无法删除它()
    {
        var csx = Generate(new HandlerGuardSpec(HandlerGuard.Self, "Unit", string.Empty));

        var guard = csx.IndexOf("object.ReferenceEquals", StringComparison.Ordinal);
        var body = csx.IndexOf("ctx.Stop();", StringComparison.Ordinal);

        Assert.True(guard > 0 && body > guard, "守卫必须在作者效果体之前：\n" + csx);
        // 守卫是**生成器独占**的语句：DSL 只有 10 个内建 op，没有 return／删除语句，
        // 绑定名也不得以 __ 开头（D9）——故作者无法覆盖或删除它。
        Assert.Equal(1, csx.Split("object.ReferenceEquals").Length - 1);
    }

    [Fact]
    public void 守卫改变生成文本_因此handler_id随内容变化()
    {
        var plain = new HandlerDslCompiler().Compile(Doc, Fixtures.Catalog, View);
        var guarded = new HandlerDslCompiler().CompileGuarded(
            Doc, Fixtures.Catalog, View, new HandlerGuardSpec(HandlerGuard.Self, "Unit", string.Empty));

        Assert.True(plain.Success && guarded.Success);
        Assert.NotEqual(plain.Csx, guarded.Csx);
        Assert.NotEqual(HandlerDslGenerator.HandlerIdOf(plain.Csx!), HandlerDslGenerator.HandlerIdOf(guarded.Csx!));
    }
}
