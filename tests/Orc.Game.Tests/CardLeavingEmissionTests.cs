using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// R29·`card.leaving` 的语义边界守护：**非死亡离场**专属信号——死亡永不触发，三者互斥不重叠。
/// <para>
/// 断言口径（不是「有发射」，而是「互斥」）：同一条用例内同时观察
/// <c>card.leaving</c>／<c>card.dying</c>／<c>card.died</c> 三个计数——
/// 非死亡离场路径：leaving 恰一次、dying／died 零次；
/// 死亡路径：leaving **零次**、dying／died 各一次。
/// 只断言「leaving 有发射」无法区分「语义错误地也覆盖死亡」，故必须三条同看。
/// </para>
/// <para>运行期零改动（R29 第 4 条）：本用例只观测既有发射点，不新增/移动任何发射。</para>
/// </summary>
public class CardLeavingEmissionTests
{
    [Fact]
    public async Task Non_Death_Exit_Emits_CardLeaving_Once_And_No_Death_Signals()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 非死亡离场（撤退回手／被移除／回手三路共用的受控入口）
        await match.CommandManager.LeaveBattlefieldAsync(unit);

        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLeaving));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDying));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDied));

        // 载荷＝{ Card, Reason }（与 GameSignals.Build() 的声明一致）
        var payload = recorder.PayloadOf(GameUpdates.CardLeaving)!;
        Assert.Same(unit, payload[GameUpdates.PayloadCard]);
        Assert.Equal("leave", payload[GameSignals.PayloadReason]);

        // 离场不等于死亡：单位仍存活（正是「非死亡」的字面证据）
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Death_Path_Emits_No_CardLeaving_And_Exactly_One_Dying_One_Died()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];

        var victim = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 死亡路径（伤害门户 → 死亡衔）
        await victim.ApplyDefenseDamageAsync(20);
        Assert.True(victim.GetData<UnitStateData>().IsDestroyed);

        Assert.Equal(0, recorder.CountOf(GameUpdates.CardLeaving)); // 死亡永不触发 leaving
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDying));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDied));

        // 次序：dying 先于 died（死亡已确认 → 死亡完成），两者都在 leaving 之前的位置上取代了它
        var types = recorder.Types;
        Assert.True(types.ToList().IndexOf(GameUpdates.CardDying) < types.ToList().IndexOf(GameUpdates.CardDied));
    }
}
