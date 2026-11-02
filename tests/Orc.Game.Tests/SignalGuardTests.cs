using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R32／D25／D27 的文案与白名单守护**：中文名覆盖率**逐层各一条**（合并成「39 对 39」会让内核 4 条漏写永远绿），
/// 白名单「未登记即不出档」，且**判据不是载荷顺序、也不是字段类型**。
/// </summary>
public class SignalGuardTests
{
    [Fact]
    public void 信号中文名覆盖率_逐层各一条()
    {
        Assert.Equal(
            KernelSignals.Names.OrderBy(static n => n, StringComparer.Ordinal),
            KernelSignals.Labels.Keys.OrderBy(static n => n, StringComparer.Ordinal));
        Assert.Equal(
            GameSignals.Names.OrderBy(static n => n, StringComparer.Ordinal),
            GameSignals.Labels.Keys.OrderBy(static n => n, StringComparer.Ordinal));

        Assert.All(KernelSignals.Labels.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(GameSignals.Labels.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));

        // 两层合起来才是 39；单层的数字写死在测试里，任何一层漏写都会当场失败。
        Assert.Equal(4, KernelSignals.Labels.Count);
        Assert.Equal(35, GameSignals.Labels.Count);
        Assert.Equal(39, SignalGuards.Labels.Count);
    }

    [Fact]
    public void 白名单与信号表逐条自洽_错字即失败()
    {
        Assert.Empty(SignalGuards.ValidateWhitelists());

        // 字段写错（载荷里没有它）⇒ 专属守护失败，不允许静默退化成「不出该档」。
        var typo = new Dictionary<string, string>(StringComparer.Ordinal) { [GameUpdates.UnitDeployed] = "UnitTypo" };
        var errors = SignalGuards.ValidateTable("游戏层", GameSignals.Names, GameSignals.Members, typo, "Subjects");
        Assert.Single(errors);
        Assert.Contains("UnitTypo", errors[0], StringComparison.Ordinal);

        // 跨层登记（游戏层表里写内核信号）⇒ 也要报错。
        var crossLayer = new Dictionary<string, string>(StringComparer.Ordinal) { [KernelSignals.Names[0]] = "Card" };
        Assert.Single(SignalGuards.ValidateTable("游戏层", GameSignals.Names, GameSignals.Members, crossLayer, "Subjects"));
    }

    [Fact]
    public void 未登记即不出档_不按字段类型或顺序推断()
    {
        // 回合五连的载荷里有 Player（entity）——按「第一个实体字段」推断会误判成可用 self 档。
        Assert.False(SignalGuards.HasSelf(GameUpdates.TurnStart));
        Assert.DoesNotContain("self", AvailableGuards(GameUpdates.TurnStart));

        // deck.shuffled（Deck）／slot.*／point.* 同理。
        foreach (var signal in new[]
                 {
                     GameUpdates.DeckShuffled, GameUpdates.SlotGained, GameUpdates.SlotLost,
                     GameUpdates.SlotChanged, GameUpdates.PointGained, GameUpdates.PointLost, GameUpdates.PointChanged,
                 })
        {
            Assert.False(SignalGuards.HasSelf(signal), signal);
            Assert.True(SignalGuards.HasOwner(signal), signal);
        }

        // 反向：card.* 死亡离场族／unit.* 不带 Player ⇒ 不出 owner-* 档（哪怕载荷里有别的 entity 字段）。
        foreach (var signal in new[]
                 {
                     GameUpdates.CardDied, GameUpdates.CardDying, GameUpdates.CardLeaving,
                     GameUpdates.UnitDeployed, GameUpdates.CardDamaged,
                 })
        {
            Assert.True(SignalGuards.HasSelf(signal), signal);
            Assert.False(SignalGuards.HasOwner(signal), signal);
        }

        // 解析失败时必须给出「未登记」的理由（不是悄悄返回 null）。
        Assert.False(SignalGuards.TryResolve(HandlerGuard.Self, GameUpdates.TurnStart, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains("Subjects", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void 四档措辞与中文名_any档不显示()
    {
        Assert.Equal(
            new[] { "any", "self", "owner-same", "owner-different" },
            HandlerGuardNames.All);

        Assert.Null(HandlerGuardNames.DisplayName(HandlerGuard.Any));
        Assert.Equal("本单位", HandlerGuardNames.DisplayName(HandlerGuard.Self));
        Assert.Equal("友方", HandlerGuardNames.DisplayName(HandlerGuard.OwnerSame));
        Assert.Equal("敌方", HandlerGuardNames.DisplayName(HandlerGuard.OwnerDifferent));

        foreach (var name in HandlerGuardNames.All)
        {
            Assert.NotNull(HandlerGuardNames.Parse(name));
            Assert.Equal(name, HandlerGuardNames.Format(HandlerGuardNames.Parse(name)!.Value));
        }

        Assert.Null(HandlerGuardNames.Parse("owner"));
    }

    [Fact]
    public void card_leaving的语义边界写在信号定义处()
    {
        // R29 第 3 条／R35：文案真源在信号定义处，模板 description 直接取它（不双写）。
        var note = GameSignals.Notes[GameUpdates.CardLeaving];
        Assert.Contains("非死亡", note, StringComparison.Ordinal);
        Assert.Contains("死亡不触发", note, StringComparison.Ordinal);
        Assert.Contains("card.dying", note, StringComparison.Ordinal);

        Assert.Contains("非死亡", GameSignals.Labels[GameUpdates.CardLeaving], StringComparison.Ordinal);
        Assert.True(GameSignals.Labels.ContainsKey(GameUpdates.CardDied));
    }

    private static List<string> AvailableGuards(string signal)
    {
        var guards = new List<string> { "any" };
        if (SignalGuards.HasSelf(signal))
        {
            guards.Add("self");
        }

        if (SignalGuards.HasOwner(signal))
        {
            guards.Add("owner-same");
            guards.Add("owner-different");
        }

        return guards;
    }
}
