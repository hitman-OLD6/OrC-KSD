using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 9 证据：单位「交叉掩护」——**相邻友方步兵的行动花费 -1、受到的伤害 -1**（数据侧 csx 承载）。
/// <para>两条链：①光环（`card.load` 时声明 <c>AuraDeclaration</c>，受益谓词＝三个通用判定器组合）；
/// ②伤害改写（预制体 <c>injects</c> 注入「伤害处理触发器」的 <c>Rewrite</c> 段，命中则 <c>Amount - 1</c>）。</para>
/// <para>断言面：受益/非相邻/非步兵/叠加 四类，覆盖「相邻」「兵种」「归属」三层判定与减伤落点。</para>
/// </summary>
public class DiyExample09_AdjacentCoverTests
{
    private const string CardId = "diy.adjacent-cover";

    [Fact]
    public async Task 交叉掩护_相邻友方步兵_行动费减一且受伤减一()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var match = await CreateMatchAsync(cardsDirectory, effectsDirectory);
        var playerA = match.Players[0];
        var line = match.Battlefield.GetSupportLine(playerA);

        var host = await CommandTestKit.PrepareUnitAsync(match, playerA, CardId, line.GetInsertionGaps()[0]);
        var adjacent = await CommandTestKit.PrepareUnitAsync(match, playerA, CommandTestKit.InfantryId, line.GetInsertionGaps()[1]);
        var remote = await CommandTestKit.PrepareUnitAsync(match, playerA, CommandTestKit.InfantryId, line.GetInsertionGaps()[3]);

        // ①光环：相邻步兵行动费 1 → 0；非相邻/自身不受益。
        Assert.Equal(0, OperateCostOf(adjacent));
        Assert.Equal(1, OperateCostOf(remote));
        Assert.Equal(1, OperateCostOf(host));

        // ②减伤：相邻者受 3 点 → 实扣 2；非相邻者仍受 3 点。
        Assert.True(await match.EffectRuntime.DamageAsync(adjacent, 3));
        Assert.Equal(3, DefenseOf(adjacent)); // 5 - 2

        Assert.True(await match.EffectRuntime.DamageAsync(remote, 3));
        Assert.Equal(2, DefenseOf(remote)); // 5 - 3
    }

    [Fact]
    public async Task 交叉掩护_相邻但非步兵_不生效()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var match = await CreateMatchAsync(cardsDirectory, effectsDirectory);
        var playerA = match.Players[0];
        var line = match.Battlefield.GetSupportLine(playerA);

        await CommandTestKit.PrepareUnitAsync(match, playerA, CardId, line.GetInsertionGaps()[0]);
        var tank = await CommandTestKit.PrepareUnitAsync(match, playerA, CommandTestKit.TankId, line.GetInsertionGaps()[1]);

        Assert.Equal(1, OperateCostOf(tank)); // 坦克行动费不变

        Assert.True(await match.EffectRuntime.DamageAsync(tank, 3));
        Assert.Equal(2, DefenseOf(tank)); // 5 - 3（未减伤）
    }

    [Fact]
    public async Task 交叉掩护_两个来源夹住_叠加为行动费减二与受伤减二()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var match = await CreateMatchAsync(cardsDirectory, effectsDirectory);
        var playerA = match.Players[0];

        // 前线无 HQ 占位：槽 0/1/2 连续，受益者被两个来源左右夹住。
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CardId, 0);
        var beneficiary = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CardId, 2);

        // 光环逐条合成：1 - 1 - 1 = -1（下限 0 由行动费读取口钳制）。
        Assert.Equal(-1, OperateCostOf(beneficiary));

        // 减伤逐条改写：3 - 1 - 1 = 1。
        Assert.True(await match.EffectRuntime.DamageAsync(beneficiary, 3));
        Assert.Equal(4, DefenseOf(beneficiary)); // 5 - 1
    }

    private static async Task<Orc.Game.Match> CreateMatchAsync(string cardsDirectory, string effectsDirectory)
    {
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        return await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
    }

    private static int OperateCostOf(UnitCard unit)
        => unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost);

    private static int DefenseOf(UnitCard unit)
        => unit.GetData<UnitStateData>().Defense;
}
