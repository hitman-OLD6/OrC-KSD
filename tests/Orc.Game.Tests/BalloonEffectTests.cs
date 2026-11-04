using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R8「漂浮气球」效果**验收：检索空军入手 → 授予「闪击」＋ 包内压制预制体；
/// 打出当回合不施加；我方回合开始补一次；敌方回合开始不触发；已压制不累计；离场后授予保留；
/// 无空军/满手爆牌＝正常空转、不授予、不报错。
/// </summary>
public class BalloonEffectTests
{
    [Fact]
    public async Task 命中_进手牌并获得闪击与压制效果_打出当回合不施加()
    {
        var (match, player, balloon) = await PlayBalloonAsync();

        var air = FindAir(player);
        Assert.NotNull(air);
        Assert.Contains(air, player.Hand);
        Assert.True(KeywordRules.HasKeyword(air!, Su3BalloonKit.BlitzKeyword));
        Assert.Contains(
            match.Engine.Model!.ComponentsOf(air!.ModelEntity!),
            attachment => string.Equals(attachment.Type.Id, Su3BalloonKit.PinPrefabId, StringComparison.Ordinal));

        // 打出当回合不施加压制（该回合仍可用闪击攻击）。
        Assert.False(SuppressRules.IsSuppressed(air));
        Assert.DoesNotContain(balloon, player.Hand); // 指令按既有流程离手
    }

    [Fact]
    public async Task 我方回合开始施加_敌方回合开始不触发_连续两回合不累计()
    {
        var (match, player, _) = await PlayBalloonAsync();
        var air = FindAir(player)!;
        await JoinAsync(match, player, air);
        Assert.False(SuppressRules.IsSuppressed(air));

        // 我方回合结束：不施加（检查点不是回合结束）。
        await match.TurnManager.EndTurn();
        Assert.False(SuppressRules.IsSuppressed(air));

        // 敌方回合结束 ⇒ 我方回合开始：施加一次。
        await match.TurnManager.EndTurn();
        Assert.True(SuppressRules.IsSuppressed(air));
        Assert.Equal(1, KeywordRules.GetKeywordValue(air, Su3BalloonKit.SuppressedKeyword));

        // 再走一圈：**不累计**（施加对已存在词条是幂等 no-op）。
        await match.TurnManager.EndTurn();
        await match.TurnManager.EndTurn();
        Assert.Equal(1, KeywordRules.GetKeywordValue(air, Su3BalloonKit.SuppressedKeyword));
    }

    [Fact]
    public async Task 离场后压制效果按UntilDestroyed保留()
    {
        var (match, player, _) = await PlayBalloonAsync();
        var air = FindAir(player)!;
        await JoinAsync(match, player, air);
        await match.TurnManager.EndTurn();
        await match.TurnManager.EndTurn(); // 我方回合开始 → 已施加
        Assert.True(SuppressRules.IsSuppressed(air));

        var model = air.ModelEntity!;
        await match.CommandManager.LeaveBattlefieldAsync(air);

        // 回手不撤销授予（UntilDestroyed）：组件实例仍在。
        Assert.Contains(
            match.Engine.Model!.ComponentsOf(model),
            attachment => string.Equals(attachment.Type.Id, Su3BalloonKit.PinPrefabId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 卡组无空军_空转不授予不报错()
    {
        var (match, player, balloon) = await PlayBalloonAsync(
            deckA: Enumerable.Repeat(CommandTestKit.InfantryId, 8));

        Assert.Null(FindAir(player));
        Assert.DoesNotContain(balloon, player.Hand); // 指令照常离手
        Assert.All(player.Hand, card => Assert.False(KeywordRules.HasKeyword(card, Su3BalloonKit.BlitzKeyword)));
    }

    [Fact]
    public async Task 命中但满手爆牌_不授予()
    {
        var pack = LoadPackOrSkip();
        var match = await Su3BalloonKit.CreateMatchAsync(
            pack,
            deckA: new[] { CommandTestKit.FighterId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 7)));
        var player = match.Players[0];

        // 手牌填到上限（8 张步兵 ＋ 本指令 ＝ 9）。
        for (var i = 0; i < Player.HandLimit - 1; i++)
        {
            await Su3BalloonKit.GiveAsync(match, player, CommandTestKit.InfantryId);
        }

        var balloon = (OrderCard)await Su3BalloonKit.GiveAsync(match, player, Su3BalloonKit.BalloonId);
        await match.ResourceManager.AddPointsAsync(player, 3);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(balloon);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Null(FindAir(player)); // 对象已销毁——没有凭空给一张已销毁的牌挂上闪击
        Assert.All(player.Hand, card => Assert.False(KeywordRules.HasKeyword(card, Su3BalloonKit.BlitzKeyword)));
        Assert.DoesNotContain(balloon, player.Hand);
    }

    // ---------- 夹具 ----------

    private static Su3BalloonKit.PackData LoadPackOrSkip()
    {
        Assert.True(Su3BalloonKit.TryLocate(
            out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects));
        return Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);
    }

    private static async Task<(Match Match, Player Player, OrderCard Balloon)> PlayBalloonAsync(
        IEnumerable<string>? deckA = null)
    {
        var pack = LoadPackOrSkip();
        var match = await Su3BalloonKit.CreateMatchAsync(
            pack,
            deckA: deckA ?? new[] { CommandTestKit.FighterId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 7)));
        var player = match.Players[0];

        var balloon = (OrderCard)await Su3BalloonKit.GiveAsync(match, player, Su3BalloonKit.BalloonId);
        await match.ResourceManager.AddPointsAsync(player, 3);

        var result = await match.PlayManager.PlayOrderAsync(balloon);
        Assert.Equal(PlayResultStatus.Success, result.Status);
        return (match, player, balloon);
    }

    /// <summary>手牌里的空军（战机／轰炸机）。</summary>
    private static UnitCard? FindAir(Player player)
        => player.Hand.OfType<UnitCard>().FirstOrDefault(
            card => card.Definition.UnitTypes.Contains(UnitType.Fighter) || card.Definition.UnitTypes.Contains(UnitType.Bomber));

    private static async Task JoinAsync(Match match, Player player, UnitCard card)
    {
        var gap = match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(1);
        var result = await match.PlayManager.JoinUnitAsync(card, gap);
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.NotNull(card.GetData<UnitStateData>().Line);
    }
}
