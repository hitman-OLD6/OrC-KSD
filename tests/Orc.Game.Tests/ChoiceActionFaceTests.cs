using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// R38 D36：`choice` 的失败落点——**单一异常类型 ＋ 稳定前缀**，且「对局取消」与「玩家取消」不得混用。
/// <para>
/// 端到端的「整张牌打出被中断」在 <see cref="HandlerDslEndToEndTests"/>；
/// 这里钉的是**不带对局的入口语义**（它们不需要对局，故也更快、更直接）。
/// </para>
/// </summary>
public class ChoiceActionFaceTests
{
    private static Card Host() => new(new LogicEngine(), "choice 入口测试宿主");

    /// <summary>
    /// D36④：`ct` 已取消 ⇒ <see cref="OperationCanceledException"/>（**不是** `InvalidOperationException`）。
    /// 判定的位置也可观测：先判 `ct` 再找通道——否则无归属的宿主会先报 `bridge-not-assembled`。
    /// </summary>
    [Fact]
    public async Task ct已取消_抛OCE而不是choice失败()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => KeywordEffectActionFace.ChooseAsync(Host(), "选一项", new[] { "甲", "乙" }, cts.Token));
    }

    /// <summary>
    /// D36①：无交互通道只有一种类型（`InvalidOperationException`），消息以
    /// `choice-failed[bridge-not-assembled]:` 开头并带 prompt 与选项数。
    /// </summary>
    [Fact]
    public async Task 无桥_单一类型加稳定前缀()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => KeywordEffectActionFace.ChooseAsync(Host(), "选一项", new[] { "甲", "乙" }));

        Assert.StartsWith("choice-failed[bridge-not-assembled]:", error.Message, StringComparison.Ordinal);
        Assert.Contains("选一项", error.Message, StringComparison.Ordinal);
        Assert.Contains("选项数=2", error.Message, StringComparison.Ordinal);
    }
}
