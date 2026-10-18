using Orc.Core;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Orc.Game.Managers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// E1-25 后续（指挥点事件改造）验收：三条点数信号（<c>point.gained</c> / <c>point.lost</c> / <c>point.changed</c>）语义、
/// 两路受控面（卡效果语义路 <c>GainPointsAsync</c>/<c>LosePointsAsync</c>；通用无语义路 <c>ChangePointsAsync</c>）、
/// 数字包裹判定器 moding、以及 <see cref="EffectRuntime"/> 可达面。
/// </summary>
public class PointEventTests
{
    [Fact]
    public async Task GainPoints_Emits_Gained_Then_Changed()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：1 点
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.ResourceManager.GainPointsAsync(player, 3)); // 1 → 4
        Assert.Equal(4, player.Points);
        Assert.Equal(new[] { GameUpdates.PointGained, GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(3, recorder.Updates[0].Payload![GameUpdates.PayloadAmount]);
        Assert.Equal(1, recorder.Updates[1].Payload![GameUpdates.PayloadOldPoints]);
        Assert.Equal(4, recorder.Updates[1].Payload![GameUpdates.PayloadNewPoints]);
    }

    [Fact]
    public async Task LosePoints_Emits_Lost_Then_Changed_And_Floors_At_Zero()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        await match.ResourceManager.GainPointsAsync(player, 3); // 1 → 4
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.ResourceManager.LosePointsAsync(player, 2)); // 4 → 2
        Assert.Equal(2, player.Points);
        Assert.Equal(new[] { GameUpdates.PointLost, GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(2, recorder.Updates[0].Payload![GameUpdates.PayloadAmount]);

        // 见底：Δ = min(100, 2) = 2 → 0
        recorder.Clear();
        Assert.True(await match.ResourceManager.LosePointsAsync(player, 100));
        Assert.Equal(0, player.Points);

        // 已为 0：零信号
        recorder.Clear();
        Assert.False(await match.ResourceManager.LosePointsAsync(player, 1));
        Assert.Empty(recorder.Types);
    }

    [Fact]
    public async Task ChangePoints_Set_And_Add_Emit_Only_Changed()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0]; // 1 点
        using var recorder = new UpdateRecorder(match.Engine);

        // 增减（负值＝减）——通用无语义路：只发 point.changed，不发 gained/lost。
        Assert.True(await match.ResourceManager.ChangePointsAsync(player, -1, PointChangeKind.Add));
        Assert.Equal(0, player.Points);
        Assert.Equal(new[] { GameUpdates.PointChanged }, recorder.Types);

        // 设为。
        recorder.Clear();
        Assert.True(await match.ResourceManager.ChangePointsAsync(player, 7, PointChangeKind.Set));
        Assert.Equal(7, player.Points);
        Assert.Equal(new[] { GameUpdates.PointChanged }, recorder.Types);
        Assert.Equal(7, recorder.Updates[0].Payload![GameUpdates.PayloadNewPoints]);

        // 设为同值＝零信号。
        recorder.Clear();
        Assert.False(await match.ResourceManager.ChangePointsAsync(player, 7, PointChangeKind.Set));
        Assert.Empty(recorder.Types);
    }

    [Fact]
    public async Task AddPointsAsync_Is_Thin_Wrapper_Over_Generic_Path()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.ResourceManager.AddPointsAsync(player, 5)); // 1 → 6
        Assert.Equal(6, player.Points);
        Assert.Equal(new[] { GameUpdates.PointChanged }, recorder.Types); // 通用路：不发 gained
    }

    [Fact]
    public async Task PointGainAmount_Judicator_Moding_Rewrites_Requested_Amount()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        var handle = match.Judicators.Resolve(JudicatorNames.PointGain);

        var moding = match.Judicators.RegisterReplaceWrapper(
            handle, (Func<object[]?, object[]?>)(_ => new object[] { 5 }));
        Assert.NotNull(moding);

        Assert.True(await match.ResourceManager.GainPointsAsync(player, 1)); // 请求 1 → 实际 5
        Assert.Equal(6, player.Points);

        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.True(await match.ResourceManager.GainPointsAsync(player, 1)); // 注销回退恒等
        Assert.Equal(7, player.Points);
    }

    [Fact]
    public async Task EffectRuntime_Gain_And_Lose_Points_Reach_ResourceManager()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var player = match.Players[0];
        var runtime = EffectRuntime.ResolveFor(player.Hq);
        Assert.NotNull(runtime);

        Assert.True(await runtime!.GainPointsAsync(player.Hq, 2)); // 1 → 3
        Assert.Equal(3, player.Points);

        Assert.True(await runtime.LosePointsAsync(player.Hq, 1)); // 3 → 2
        Assert.Equal(2, player.Points);

        Assert.False(await runtime.GainPointsAsync(player.Hq, 0)); // 非正 = 拒绝（降级 false）
    }

    [Fact]
    public async Task Turn_Start_Emits_SlotChanged_And_PointChanged_But_No_Gained_Or_Lost()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        using var recorder = new UpdateRecorder(match.Engine);

        await match.EndTurn(); // 回合 2：后手结算 0 → 1（槽与点数）

        Assert.Contains(GameUpdates.SlotChanged, recorder.Types);
        Assert.Contains(GameUpdates.PointChanged, recorder.Types);
        Assert.DoesNotContain(GameUpdates.PointGained, recorder.Types);
        Assert.DoesNotContain(GameUpdates.PointLost, recorder.Types);
        Assert.DoesNotContain(GameUpdates.SlotGained, recorder.Types);
        Assert.DoesNotContain(GameUpdates.SlotLost, recorder.Types);

        var pointChange = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.PointChanged);
        Assert.Same(match.Players[1], pointChange.Payload![GameUpdates.PayloadPlayer]);
        Assert.Equal(0, pointChange.Payload[GameUpdates.PayloadOldPoints]);
        Assert.Equal(1, pointChange.Payload[GameUpdates.PayloadNewPoints]);
    }
}
