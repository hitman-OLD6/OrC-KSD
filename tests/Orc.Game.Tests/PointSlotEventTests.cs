using Orc.Core;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// E1-25（指挥点槽事件改进）验收：三条信号（<c>slot.gained</c> / <c>slot.lost</c> / <c>slot.changed</c>）的发射语义、
/// 受控面 <c>GainSlotsAsync</c> / <c>LoseSlotsAsync</c> 的钳制与"零变化零信号"、
/// 三条资源判定器的默认行为与 moding 改写（递增 / 数字包裹）、以及 <see cref="EffectRuntime"/> 可达面。
/// </summary>
public class PointSlotEventTests
{
    [Fact]
    public async Task GainSlots_Emits_Gained_Then_Changed_And_Caps_At_Max()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.ResourceManager.GainSlotsAsync(player, 3)); // 1 → 4
        Assert.Equal(4, player.PointSlots);
        Assert.Equal(1, player.Points); // E1-25：加槽不改点数
        Assert.Equal(new[] { GameUpdates.SlotGained, GameUpdates.SlotChanged }, recorder.Types);
        Assert.Equal(3, recorder.Updates[0].Payload![GameUpdates.PayloadAmount]);
        Assert.Equal(1, recorder.Updates[1].Payload![GameUpdates.PayloadOldSlots]);
        Assert.Equal(4, recorder.Updates[1].Payload![GameUpdates.PayloadNewSlots]);

        // 触顶：Δ = min(100, 12-4) = 8 → 12（Amount＝**实际** Δ，非请求值）
        recorder.Clear();
        Assert.True(await match.ResourceManager.GainSlotsAsync(player, 100));
        Assert.Equal(12, player.PointSlots);
        Assert.Equal(8, recorder.Updates[0].Payload![GameUpdates.PayloadAmount]);

        // 已在上限：零信号
        recorder.Clear();
        Assert.False(await match.ResourceManager.GainSlotsAsync(player, 1));
        Assert.Empty(recorder.Types);
    }

    [Fact]
    public async Task LoseSlots_Emits_Lost_Then_Changed_And_Floors_At_Zero()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        await match.ResourceManager.GainSlotsAsync(player, 3); // 1 → 4
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.ResourceManager.LoseSlotsAsync(player, 2)); // 4 → 2
        Assert.Equal(2, player.PointSlots);
        Assert.Equal(1, player.Points); // E1-25：减槽不改点数
        Assert.Equal(new[] { GameUpdates.SlotLost, GameUpdates.SlotChanged }, recorder.Types);
        Assert.Equal(2, recorder.Updates[0].Payload![GameUpdates.PayloadAmount]);
        Assert.Equal(4, recorder.Updates[1].Payload![GameUpdates.PayloadOldSlots]);
        Assert.Equal(2, recorder.Updates[1].Payload![GameUpdates.PayloadNewSlots]);

        // 见底：Δ = min(100, 2) = 2 → 0
        recorder.Clear();
        Assert.True(await match.ResourceManager.LoseSlotsAsync(player, 100));
        Assert.Equal(0, player.PointSlots);

        // 已为 0：零信号
        recorder.Clear();
        Assert.False(await match.ResourceManager.LoseSlotsAsync(player, 1));
        Assert.Empty(recorder.Types);
    }

    [Fact]
    public async Task Settle_Emits_Only_SlotChanged_Not_Gained_Or_Lost()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        using var recorder = new UpdateRecorder(match.Engine);

        await match.EndTurn(); // 回合 2：后手结算 0 → 1

        Assert.Contains(GameUpdates.SlotChanged, recorder.Types);
        Assert.DoesNotContain(GameUpdates.SlotGained, recorder.Types);
        Assert.DoesNotContain(GameUpdates.SlotLost, recorder.Types);

        var change = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.SlotChanged);
        Assert.Same(match.Players[1], change.Payload![GameUpdates.PayloadPlayer]);
        Assert.Equal(0, change.Payload[GameUpdates.PayloadOldSlots]);
        Assert.Equal(1, change.Payload[GameUpdates.PayloadNewSlots]);
    }

    [Fact]
    public async Task SlotIncrement_Judicator_Moding_Changes_Turn_Start_Increment_And_Falls_Back()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var handle = match.Judicators.Resolve(JudicatorNames.PointSlotIncrement);

        var moding = match.Judicators.RegisterReplaceWrapper(
            handle, (Func<object[]?, object[]?>)(_ => new object[] { 2 }));
        Assert.NotNull(moding);

        await match.EndTurn(); // 回合 2：后手结算 0 → 2（改写后的递增）
        Assert.Equal(2, match.Players[1].PointSlots);
        Assert.Equal(2, match.Players[1].Points);

        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        await match.EndTurn(); // 回合 3：先手 1 → 2（注销回退默认增量 1）
        Assert.Equal(2, match.Players[0].PointSlots);
    }

    [Fact]
    public async Task SlotGainAmount_Judicator_Moding_Rewrites_Requested_Amount()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        var handle = match.Judicators.Resolve(JudicatorNames.PointSlotGain);

        var moding = match.Judicators.RegisterReplaceWrapper(
            handle, (Func<object[]?, object[]?>)(_ => new object[] { 5 }));
        Assert.NotNull(moding);

        Assert.True(await match.ResourceManager.GainSlotsAsync(player, 1)); // 请求 1 → 实际 5
        Assert.Equal(6, player.PointSlots);

        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.True(await match.ResourceManager.GainSlotsAsync(player, 1)); // 注销回退恒等（请求值即实际值）
        Assert.Equal(7, player.PointSlots);
    }

    [Fact]
    public async Task EffectRuntime_Gain_And_Lose_Point_Slots_Reach_ResourceManager()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        var runtime = EffectRuntime.ResolveFor(player.Hq);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GainPointSlotsAsync(player.Hq, 2)); // 1 → 3
        Assert.Equal(3, player.PointSlots);

        Assert.True(await runtime.LosePointSlotsAsync(player.Hq, 1)); // 3 → 2
        Assert.Equal(2, player.PointSlots);

        Assert.False(await runtime.GainPointSlotsAsync(player.Hq, 0)); // 非正 = 拒绝（降级 false）
    }
}
