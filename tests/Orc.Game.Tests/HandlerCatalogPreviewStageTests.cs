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
    public void 内建op_choice为alpha且无原因()
    {
        // R38 已接通交互通道：`choice` 不再是预览态（有桥走通道／无桥明确失败），
        // 因此 builtInOps 里它的 stage 必须是 alpha、problem 必须消失。
        var choice = Catalog().BuiltInOps.Single(o => o.Op == "choice");

        Assert.Equal("alpha", choice.Stage);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void 内建op_全部为alpha且无原因()
    {
        var ops = Catalog().BuiltInOps;

        Assert.NotEmpty(ops);
        foreach (var op in ops)
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
    /// **行为背书**（R36 的闭环）：`preview` 不是一句声明——登记过的 op 必须真的跑不起来。
    /// <para>
    /// R38 把 `choice` 接通后，本条改写为「**有桥走通道／无桥明确失败**」：它现在不再无差别抛
    /// <see cref="NotSupportedException"/>，而是要么经 targeter 提交通道取得裁决、要么以
    /// <c>choice-failed[bridge-not-assembled]</c> 明确失败（不返回哨兵、不选首项）。
    /// 若将来 `choice` 又退回「跑不起来」，这条会先红，逼人回来重新登记 <see cref="HandlerDslContract.PreviewOps"/>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 行为背书_choice有桥走通道_无桥明确失败()
    {
        // 有桥：走既有的 option 选择器（标识集），选第 2 项 ⇒ 返回 1（0 起下标）。
        var bridge = CommandTestKit.AutoPickBridge();
        bridge.InteractionScript = (description, responder) =>
            responder.Complete(
                description.RequestId,
                TargeterTestKit.IdentifierSelection(TargeterTestKit.PrimarySlot(description), "乙"));

        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var card = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        await card.LoadAsync(match.Players[0]);

        var choose = KeywordEffectActionFace.ChooseAsync(card, "选一个", ["甲", "乙"]);
        var (description, _) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(Orc.Game.Targeting.SelectorNames.Option, description.Slots[0].Name);
        Assert.Equal(1, await choose);

        // 无桥：明确失败（不是「静默取首项」，也不是 `NotSupportedException`）。
        var bare = new Card(new LogicEngine(), "预览态测试宿主");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => KeywordEffectActionFace.ChooseAsync(bare, "选一个", ["甲", "乙"]));
        Assert.StartsWith("choice-failed[bridge-not-assembled]:", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 守护：<see cref="HandlerDslContract.PreviewOps"/> 的键必须都是真实的内建 op。
    /// 写错 op 名会静默失效（前端拿不到任何 preview 标记），比漏标更难查。
    /// <para>
    /// R38 后该表**为空**，上面的循环因此恒真——所以末尾补一条**反向自检**：拼错的 op 名必须被
    /// <see cref="HandlerDslContract.IsBuiltinOp"/> 判否。否则这条守护在空表下退化成恒真、形同虚设。
    /// </para>
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

        foreach (var problem in HandlerDslContract.PreviewOps.Values)
        {
            // 登记了却不给原因＝前端只能显示一个空警告（与漏标同样没用）。
            Assert.False(string.IsNullOrWhiteSpace(problem), "PreviewOps 的条目必须写明原因。");
        }

        // 空表自检：拼写错误必须被拦下。
        Assert.False(HandlerDslContract.IsBuiltinOp("choicee"));
        Assert.True(HandlerDslContract.IsBuiltinOp("choice"));
    }
}
