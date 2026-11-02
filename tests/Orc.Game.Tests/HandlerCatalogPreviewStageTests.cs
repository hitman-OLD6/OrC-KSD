using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R36 能力分区**：目录要诚实地说出「这个 op 现在跑不起来」。
/// <para>
/// 落点不在节点目录，而在 <see cref="HandlerCatalogDto.BuiltInOps"/>——内建 op（<c>sequence</c>／<c>branch</c>／
/// <c>loop</c>／…）由前端硬编码、目录里没有引脚条目，而 <c>choice</c> 的运行时实现
/// <see cref="KeywordEffectActionFace.ChooseAsync"/> 本身在排除清单里（只由生成码调用），节点级标注读不到。
/// </para>
/// <para>
/// 分区**不裁剪**：<c>choice</c> 仍然出现在清单里，只是带 <c>preview</c> 与原因。藏起来会让作者
/// 以为这版没有这个能力——那才是真的不诚实。
/// </para>
/// </summary>
public class HandlerCatalogPreviewStageTests
{
    private static HandlerCatalogDto Catalog() => new HandlerCatalogFacade().GetCatalog();

    [Fact]
    public void 内建op_choice标为预览态并给出原因()
    {
        var choice = Catalog().BuiltInOps.Single(o => o.Op == "choice");

        Assert.Equal("preview", choice.Stage);
        Assert.False(string.IsNullOrWhiteSpace(choice.Problem));
        Assert.Contains("S10", choice.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 内建op_其余全是alpha且无原因()
    {
        var others = Catalog().BuiltInOps.Where(o => o.Op != "choice").ToArray();

        Assert.NotEmpty(others);
        foreach (var op in others)
        {
            // 把能跑的 op 误标成 preview 会让作者以为一半功能不可用——与漏标同样是谎。
            Assert.Equal("alpha", op.Stage);
            Assert.Null(op.Problem);
        }
    }

    [Fact]
    public void 内建op_清单全量不因预览而缺席()
    {
        var catalog = Catalog();

        Assert.Equal(HandlerDslContract.BuiltinOps.Count, catalog.BuiltInOps.Count);
        Assert.Equal(
            HandlerDslContract.BuiltinOps.OrderBy(static o => o, StringComparer.Ordinal),
            catalog.BuiltInOps.Select(static o => o.Op).OrderBy(static o => o, StringComparer.Ordinal));
    }

    [Fact]
    public void 目录节点当前全为alpha()
    {
        var catalog = Catalog();

        Assert.NotEmpty(catalog.Nodes);
        foreach (var node in catalog.Nodes)
        {
            Assert.Equal("alpha", node.Stage);
            Assert.Null(node.Problem);
        }
    }

    /// <summary>
    /// **行为背书**：`preview` 不是一句声明——`choice` 的运行时入口现在确实会抛。
    /// 引擎接通前端桥接（S10）后这条会先变红，逼人回来删掉 <see cref="HandlerDslContract.PreviewOps"/> 里的条目。
    /// </summary>
    [Fact]
    public async Task 预览态的行为背书_choice运行期确实抛()
    {
        var host = new Card(new LogicEngine(), "预览态测试宿主");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            KeywordEffectActionFace.ChooseAsync(host, "选一个", ["甲", "乙"]));
    }

    /// <summary>
    /// 守护：<see cref="HandlerDslContract.PreviewOps"/> 的键必须都是真实的内建 op。
    /// 写错 op 名会静默失效（前端拿不到任何 preview 标记），比漏标更难查。
    /// </summary>
    [Fact]
    public void 预览表里的op名必须真实存在()
    {
        foreach (var op in HandlerDslContract.PreviewOps.Keys)
        {
            Assert.True(
                HandlerDslContract.IsBuiltinOp(op),
                $"PreviewOps 里的 '{op}' 不是内建 op（拼写错误会静默失效，前端看不到任何 preview 标记）。");
        }
    }
}
