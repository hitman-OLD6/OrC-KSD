using Orc.Game.Effects;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 效果选靶的通道拆分（S9／S12／R2／A-6）：唯一入口 <see cref="EffectRuntime.SelectAsync"/>。
/// <para>
/// 契约：确定性（<c>all</c>／<c>self</c>）经判定器、不产生 targeter；<c>random</c> 归引擎随机、
/// 生产对局禁止（未开启＝明确错误）；其余（<c>one</c>／<c>any</c>）＝外部裁定，**必须经提交通道**，
/// 无前端接入＝明确失败（不静默取首个，R2）。死代码 <c>BeginTargetSelectionAsync</c> 已删除（唯一入口）。
/// </para>
/// </summary>
public class EffectSelectorChannelTests
{
    [Fact]
    public async Task 确定性选择_all_不需要前端接入()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        var picked = await runtime!.SelectAsync(viewer, new EffectSelector("all", "enemy"));

        Assert.Equal(new[] { enemy }, picked);
    }

    [Fact]
    public async Task 外部裁定_one_无前端接入_明确失败()
    {
        var match = CommandTestKit.CreateCommandMatch(); // 无桥接＝无前端
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime!.SelectAsync(viewer, new EffectSelector("one", "enemy")));

        // 不静默降级：错误信息必须指向"需要前端／提交通道"（R2）。
        Assert.Contains("外部裁定", error.Message);
        Assert.Contains("引擎不代选", error.Message);
    }

    [Fact]
    public async Task 引擎随机_生产对局未开启_明确失败()
    {
        var match = CommandTestKit.CreateCommandMatch(); // AllowRandomTargeting 缺省 false
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime!.SelectAsync(viewer, new EffectSelector("random", "enemy", Count: 1)));

        Assert.Contains("AllowRandomTargeting", error.Message);
    }

    [Fact]
    public async Task 引擎随机_测试场景显式开启后可用()
    {
        var match = CommandTestKit.CreateCommandMatch(allowRandomTargeting: true);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        var picked = await runtime!.SelectAsync(viewer, new EffectSelector("random", "enemy", Count: 1));

        Assert.Equal(new[] { enemy }, picked);
    }

    [Fact]
    public async Task 引擎随机_审查面列出本对局用到的随机选择器()
    {
        var match = CommandTestKit.CreateCommandMatch(allowRandomTargeting: true);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        await runtime!.SelectAsync(viewer, new EffectSelector("random", "enemy", Count: 1));

        Assert.Contains(match.Engine.RandomSelections, r => r.Selector.StartsWith("sel=random", StringComparison.Ordinal));

        var json = match.Engine.ExportAuditChainJson();
        Assert.Contains("\"randomSelectors\"", json, StringComparison.Ordinal);
        Assert.Contains("sel=random", json, StringComparison.Ordinal);

        var text = match.Engine.ExportAuditChainText();
        Assert.Contains("randomSelectors(1):", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 外部裁定_经提交通道落定并标注_effect_动作类别()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var viewer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var runtime = EffectRuntime.ResolveFor(viewer);
        Assert.NotNull(runtime);

        var task = runtime!.SelectAsync(viewer, new EffectSelector("one", "enemy"));
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 前端契约：候选集就是"可被指向"的名单（引擎不代选，只提供候选）。
        Assert.Contains(enemy.Ref, description.AllowedTargets);
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), enemy.Ref)));

        var result = await task;
        Assert.Equal(new[] { enemy }, result);
    }
}
