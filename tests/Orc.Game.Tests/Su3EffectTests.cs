using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R7「苏-3」效果**验收：订阅 <c>unit.attacked</c>（其他友方单位攻击后）、每回合一次（拥有者回合基准）、
/// 触发即扣额度、被压制跳过、随机取靶来自合法候选、级联由「一回合一次」终止、离场回场额度复位。
/// </summary>
public class Su3EffectTests
{
    [Fact]
    public async Task 其他友方单位攻击_发起一次效果攻击且目标来自合法候选()
    {
        var (match, su3, friend, enemy) = await ArrangeAsync();
        var runtime = EffectRuntime.ResolveFor(su3);
        Assert.NotNull(runtime);
        var candidates = runtime!.AttackCandidates(su3);
        Assert.NotEmpty(candidates);

        using var recorder = new UpdateRecorder(match.Engine);
        await TriggerAsync(friend, enemy);

        var su3Attack = Assert.Single(Su3Attacks(recorder, su3));
        Assert.Equal(true, su3Attack.Payload![GameUpdates.PayloadByEffect]);
        Assert.Contains(su3Attack.Payload![GameUpdates.PayloadTarget], candidates);
    }

    [Fact]
    public async Task 触发后_本回合不再触发_下一个拥有者回合恢复()
    {
        var (match, su3, friend, enemy) = await ArrangeAsync();
        using var recorder = new UpdateRecorder(match.Engine);

        await TriggerAsync(friend, enemy);
        Assert.Single(Su3Attacks(recorder, su3));

        // 同回合第二次友方攻击 ⇒ 不再触发。
        await TriggerAsync(friend, enemy);
        Assert.Single(Su3Attacks(recorder, su3));

        // 回合切换一圈（我方结束 → 敌方结束 → 我方开始）⇒ 额度复位。
        await match.TurnManager.EndTurn();
        await match.TurnManager.EndTurn();
        await TriggerAsync(friend, enemy);
        Assert.Equal(2, Su3Attacks(recorder, su3).Count);
    }

    [Fact]
    public async Task 敌方回合开始不重置额度()
    {
        var (match, su3, friend, enemy) = await ArrangeAsync();
        using var recorder = new UpdateRecorder(match.Engine);

        await TriggerAsync(friend, enemy);
        Assert.Single(Su3Attacks(recorder, su3));

        // 只结束我方回合（当前方变成敌方——敌方回合开始不得重置我方单位的额度）。
        await match.TurnManager.EndTurn();
        await TriggerAsync(friend, enemy);
        Assert.Single(Su3Attacks(recorder, su3));
    }

    [Fact]
    public async Task 被压制_不发起_且额度已在触发瞬间扣掉()
    {
        var (match, su3, friend, enemy) = await ArrangeAsync();
        Assert.True(await SuppressRules.ApplyAsync(su3));
        using var recorder = new UpdateRecorder(match.Engine);

        await TriggerAsync(friend, enemy);
        Assert.Empty(Su3Attacks(recorder, su3));

        // 解除压制后再触发：额度已被上一次触发消耗（「触发即扣额度」）。
        await su3.Keywords.RevokeAsync(KeywordIds.Suppressed);
        Assert.False(SuppressRules.IsSuppressed(su3));
        await TriggerAsync(friend, enemy);
        Assert.Empty(Su3Attacks(recorder, su3));
    }

    [Fact]
    public async Task 两个苏3_各自触发一次_级联由一回合一次终止()
    {
        var (match, first, friend, enemy) = await ArrangeAsync();
        var player = match.Players[0];
        var second = await CommandTestKit.PrepareOnSupportAsync(match, player, Su3BalloonKit.Su3Id, 1);
        Assert.NotSame(second, first);

        using var recorder = new UpdateRecorder(match.Engine);
        await TriggerAsync(friend, enemy);

        // 苏-3 A 的攻击会再发 unit.attacked，从而触发苏-3 B；两条链都因「额度已用」在下一次终止。
        var su3Ids = new[] { first, second };
        var attacks = recorder.Updates
            .Where(u => u.Type == GameUpdates.UnitAttacked)
            .Count(u => su3Ids.Any(s => ReferenceEquals(s, u.Payload![GameUpdates.PayloadUnit])));
        Assert.Equal(2, attacks);
    }

    // ---------- 夹具 ----------

    private static async Task<(Match Match, UnitCard Su3, UnitCard Friend, UnitCard Enemy)> ArrangeAsync()
    {
        Assert.True(Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects));
        var pack = Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);
        var match = await Su3BalloonKit.CreateMatchAsync(pack);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var su3 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, Su3BalloonKit.Su3Id, 0);
        // 轰炸机触发（轰炸机不被反击、也不触发拦截）；目标是巨炮（防 30——多轮也打不死，保证可重复触发）。
        var friend = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BomberId, 1);
        var enemy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.MegaId, 1);
        return (match, su3, friend, enemy);
    }

    private static Task TriggerAsync(UnitCard friend, UnitCard enemy)
        => EffectRuntime.ResolveFor(friend)!.AttackByEffectAsync(friend, enemy, friend);

    private static List<(string Type, IReadOnlyDictionary<string, object?>? Payload)> Su3Attacks(
        UpdateRecorder recorder, UnitCard su3)
        => recorder.Updates
            .Where(u => u.Type == GameUpdates.UnitAttacked
                        && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], su3))
            .ToList();
}
