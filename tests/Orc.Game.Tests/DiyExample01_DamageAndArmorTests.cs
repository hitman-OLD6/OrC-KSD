using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 1（R6a）证据：指令「火力支援」——单目标 3 点伤害 ＋ 本方 HQ +5 防御。
/// 数据侧：<c>samples/card-pack-diy/cards/diy.fire-support.card.json</c> ＋ 效果库 csx。
/// 断言走世界状态（目标有效防御 −3、HQ 有效生命 +5），不比日志。
/// </summary>
public class DiyExample01_DamageAndArmorTests
{
    private const string CardId = "diy.fire-support";

    [Fact]
    public async Task 火力支援_打出_单目标3伤害且本方总部加5防()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 目标：敌方步兵（防御 5）。
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.Equal(5, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        var hqBefore = playerA.Hq.Health;
        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, CardId);

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 3
        Assert.Equal(hqBefore + 5, playerA.Hq.Health);                               // HQ 有效生命 +5
    }
}
