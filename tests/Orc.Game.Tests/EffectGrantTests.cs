using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R37 运行期授予效果**：给一张已经在场的卡凭空加一份效果，并能在正确时机撤销。
/// <para>
/// 这是宿主侧 99 条预览里**仅有的 2 条真引擎缺口**之一（`grantEffect`／`bindEvent`）的引擎侧答案。
/// 断言全部落在**结构事实**上（实例存在／已装配／已销毁），因为「授予」本质是装配时序，
/// 不是某条具体效果的行为——具体效果的行为由各自的 handler 测试覆盖。
/// </para>
/// </summary>
public class EffectGrantTests
{
    private const string EffectId = "diy.fire-support.effect";

    [Fact]
    public async Task 授予后立即生效_实例存在且已装配()
    {
        var (match, unit) = await NewMatchWithUnitAsync();

        var handle = EffectGrantService.Grant(unit, EffectId);

        Assert.False(handle.IsRevoked);
        Assert.True(handle.Attachment.IsAssembled);
        Assert.Contains(handle.Attachment, match.Engine.Model!.ComponentsOf(unit.ModelEntity!));
    }

    [Fact]
    public async Task 撤销后实例销毁()
    {
        var (match, unit) = await NewMatchWithUnitAsync();
        var handle = EffectGrantService.Grant(unit, EffectId);

        handle.Revoke();

        Assert.True(handle.IsRevoked);
        Assert.True(handle.Attachment.IsDisposed);
        // 幂等：再撤一次不抛。
        handle.Revoke();
    }

    [Fact]
    public async Task 同一卡两次授予_两份独立实例()
    {
        var (match, unit) = await NewMatchWithUnitAsync();

        var first = EffectGrantService.Grant(unit, EffectId);
        var second = EffectGrantService.Grant(unit, EffectId);

        // 声明期「同一 prefab 至多一份」是 R3i 的约束；**授予**显式破例（R37）。
        Assert.NotSame(first.Attachment, second.Attachment);
        var attachments = match.Engine.Model!.ComponentsOf(unit.ModelEntity!);
        Assert.Contains(first.Attachment, attachments);
        Assert.Contains(second.Attachment, attachments);

        first.Revoke();
        Assert.True(first.Attachment.IsDisposed);
        Assert.False(second.Attachment.IsDisposed); // 撤一个不影响另一个
    }

    [Fact]
    public async Task UntilLeaving_非死亡离场即撤销()
    {
        var (match, unit) = await NewMatchWithUnitAsync();
        var handle = EffectGrantService.Grant(unit, EffectId, GrantLifetime.UntilLeaving);
        Assert.False(handle.IsRevoked);

        await match.CommandManager.LeaveBattlefieldAsync(unit);

        Assert.True(handle.IsRevoked);
    }

    [Fact]
    public async Task UntilLeaving_死亡同样撤销()
    {
        var (match, unit) = await NewMatchWithUnitAsync();
        var handle = EffectGrantService.Grant(unit, EffectId, GrantLifetime.UntilLeaving);

        await EffectRuntime.ResolveFor(unit)!.DamageAsync(unit, 99);

        // 死亡走 card.dying／card.died，与 card.leaving 互斥——只接 card.leaving 会把效果带进弃牌堆。
        Assert.True(handle.IsRevoked);
    }

    [Fact]
    public async Task 未注册的效果id_明确拒绝()
    {
        var (_, unit) = await NewMatchWithUnitAsync();

        Assert.ThrowsAny<Exception>(() => EffectGrantService.Grant(unit, "no.such.effect"));
    }

    private static async Task<(Orc.Game.Match Match, UnitCard Unit)> NewMatchWithUnitAsync()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cards, out var effects));
        var pack = DiyExampleKit.LoadPack(cards);
        var match = await DiyExampleKit.CreateMatchAsync(pack, effects);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        return (match, unit);
    }
}
