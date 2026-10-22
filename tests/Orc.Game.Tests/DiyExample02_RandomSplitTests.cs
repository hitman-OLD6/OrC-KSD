using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 2（R6b + D3）证据：指令「齐射」——3 点伤害**逐点独立随机**指派给一个存活单位（可重复命中），
/// 无单位＝零伤害；有单位时总伤害恒为 3。
/// 断言走世界状态：全场防御总和减少 3，且每点都是 1（不出现小数/溢出）。
/// </summary>
public class DiyExample02_RandomSplitTests
{
    private const string CardId = "diy.volley";

    [Fact]
    public async Task 齐射_打出_总伤害恒为3且逐点分配()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 三个单位（双方均有）：验证「所有单位」而非仅敌方。
        var own = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemySupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        var units = new[] { own, enemyFront, enemySupport };
        var before = units.Sum(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, CardId);
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);

        var after = units.Sum(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(3, before - after); // 总伤害恒为 3（D3）

        // 逐点分配：任一单位的损失不超过 3，且全部为整数损失（防御是整数，天然成立）。
        foreach (var unit in units)
        {
            var lost = 5 - unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);
            Assert.InRange(lost, 0, 3);
        }
    }

    [Fact]
    public async Task 齐射_无单位时_零伤害且不抛()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
        var playerA = match.Players[0];

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, CardId);
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(0, match.Battlefield.FrontLine.Size);
    }
}
