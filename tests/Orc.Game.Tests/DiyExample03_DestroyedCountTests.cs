using Orc.Game.Cards;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 3（R6c + D2）证据：指令「纪奠」——本方 HQ 回复量＝本局已被摧毁的单位数。
/// 数据侧：全局监听 <c>card.died</c>（双方均计）→ 计数存 <c>EngineVariables</c>；打出时按计数回复。
/// 断言走世界状态：计数＝2 → HQ 有效生命 +2。
/// </summary>
public class DiyExample03_DestroyedCountTests
{
    private const string CardId = "diy.memorial";
    private const string CounterKey = "diy.example3.destroyed";

    [Fact]
    public async Task 纪奠_打出_按本局被摧毁单位数回复总部()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 先装载指令（其死亡计数触发器随效果装载）——计数自装载起算（数据侧口径，见示例说明）。
        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, CardId);

        // 双方各阵亡一个单位（D2：含双方）。
        var ownVictim = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyVictim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var ownRuntime = EffectRuntime.ResolveFor(ownVictim);
        var enemyRuntime = EffectRuntime.ResolveFor(enemyVictim);
        Assert.NotNull(ownRuntime);
        Assert.NotNull(enemyRuntime);
        Assert.True(await ownRuntime!.KillAsync(ownVictim));
        Assert.True(await enemyRuntime!.KillAsync(enemyVictim));

        Assert.Equal(2, match.Engine.Variables.Get<int>(CounterKey));

        var hqBefore = playerA.Hq.Health;
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(hqBefore + 2, playerA.Hq.Health);
    }

    [Fact]
    public async Task 纪奠_零阵亡_不回复且不抛()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory);
        var playerA = match.Players[0];

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, CardId);
        var hqBefore = playerA.Hq.Health;

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(hqBefore, playerA.Hq.Health);
    }
}
