using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R3／S2：<c>unit.attacked</c> 发射矩阵**——两条发起路径（指挥／效果）各恰一次，
/// 判据＝<c>Applied</c> 即发（不看伤害量、不看攻击者生死），未结算零发射；
/// 时机＝指挥路径在收尾（扣费/清位/unit.acted）之后；<c>unit.acted</c> 的既有语义逐项不变。
/// </summary>
public class UnitAttackedSignalTests
{
    [Fact]
    public async Task 指挥攻击_结算成功_恰一次且ByEffect为false_时机在unit_acted之后()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 互伤但双方都存活：巨兽 攻 6 / 防 7 × 巨兽 —— 保证收尾（含 unit.acted）真的发生。
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.BeastId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        var attacked = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitAttacked);
        Assert.Same(attacker, attacked.Payload![GameUpdates.PayloadUnit]);
        Assert.Same(target, attacked.Payload![GameUpdates.PayloadTarget]);
        Assert.Equal(false, attacked.Payload![GameUpdates.PayloadByEffect]);

        // 相对顺序：收尾（unit.acted）之后才发 unit.attacked。
        var types = recorder.Types.ToList();
        Assert.True(
            types.IndexOf(GameUpdates.UnitActed) >= 0
            && types.IndexOf(GameUpdates.UnitActed) < types.IndexOf(GameUpdates.UnitAttacked),
            "unit.acted 必须先于 unit.attacked（收尾完成后才算这次攻击彻底结束）。");

        // unit.acted 的既有载荷不变（不新增字段）。
        var acted = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitActed);
        Assert.Same(attacker, acted.Payload![GameUpdates.PayloadUnit]);
        Assert.Equal(
            new[] { GameUpdates.PayloadUnit, GameUpdates.PayloadSource }.OrderBy(k => k, StringComparer.Ordinal),
            acted.Payload!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 效果攻击_结算成功_恰一次且ByEffect为true_不发unit_acted与card_played()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.BeastId, 1);
        var runtime = EffectRuntime.ResolveFor(attacker);
        Assert.NotNull(runtime);
        var points = playerA.Points;
        var canMoveBefore = attacker.GetData<CommandData>().CanMove;
        var canAttackBefore = attacker.GetData<CommandData>().CanAttack;
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await runtime!.AttackByEffectAsync(attacker, target, attacker));

        var attacked = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitAttacked);
        Assert.Same(attacker, attacked.Payload![GameUpdates.PayloadUnit]);
        Assert.Same(target, attacked.Payload![GameUpdates.PayloadTarget]);
        Assert.Equal(true, attacked.Payload![GameUpdates.PayloadByEffect]);

        // R4：零行动经济面——不扣点、不写行动位、不发 unit.acted / card.played。
        Assert.Equal(points, playerA.Points);
        Assert.Equal(canAttackBefore, attacker.GetData<CommandData>().CanAttack);
        Assert.Equal(canMoveBefore, attacker.GetData<CommandData>().CanMove);
        Assert.DoesNotContain(GameUpdates.UnitActed, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardPlayed, recorder.Types);
    }

    [Fact]
    public async Task 伏击改写_零伤害_仍然发unit_attacked()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.WeakId, 0);   // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6
        var runtime = EffectRuntime.ResolveFor(attacker);
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await runtime!.AttackByEffectAsync(attacker, ambusher, attacker));

        // 伏击成立：攻击者当场阵亡、被攻击者不受伤——攻击发生了，只是结果被改写。
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.False(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusher.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitAttacked);
    }

    [Fact]
    public async Task 攻击者当场阵亡_仍然发unit_attacked且目标阵亡不抛()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0); // 攻 3 / 防 2
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var runtime = EffectRuntime.ResolveFor(attacker);
        using var recorder = new UpdateRecorder(match.Engine);

        // 互伤同归：目标在结算中阵亡（Ref 已失效）——发射点用卡对象引用承载目标，不得解引用 Ref。
        Assert.True(await runtime!.AttackByEffectAsync(attacker, target, attacker));

        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
        var attacked = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitAttacked);
        Assert.Same(target, attacked.Payload![GameUpdates.PayloadTarget]);
    }

    [Fact]
    public async Task 目标不合法_零发射()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0);
        var smoked = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.SmokeId, 1); // 烟幕：不可被攻击
        var runtime = EffectRuntime.ResolveFor(attacker);
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.False(await runtime!.AttackByEffectAsync(attacker, smoked, attacker));
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitAttacked));
        Assert.False(smoked.GetData<UnitStateData>().IsDestroyed);

        // 候选面同源：烟幕单位不在候选里。
        Assert.DoesNotContain(runtime.AttackCandidates(attacker), c => ReferenceEquals(c, smoked));
    }

    [Fact]
    public async Task 指挥取消_零发射()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.CancelCommandAsync(match, bridge, attacker);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitAttacked));
    }

    [Fact]
    public async Task 敌方HQ为目标_正常结算_不发反击且发unit_attacked()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0);
        var runtime = EffectRuntime.ResolveFor(attacker);
        var health = playerB.Hq.Health;
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await runtime!.AttackByEffectAsync(attacker, playerB.Hq, attacker));

        Assert.True(playerB.Hq.Health < health);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed); // HQ 不反击
        var attacked = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitAttacked);
        Assert.Same(playerB.Hq, attacked.Payload![GameUpdates.PayloadTarget]);
    }

    [Fact]
    public async Task 候选枚举与指挥入口同源()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0);
        CommandTestKit.Activate(attacker);
        var enemyUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var smoked = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.SmokeId, 2); // 烟幕剔除
        var runtime = EffectRuntime.ResolveFor(attacker);

        var fromRuntime = runtime!.AttackCandidates(attacker);
        var fromCommand = match.CommandManager.GetCommandAvailability(attacker).Attack.Candidates;

        Assert.Contains(enemyUnit, fromRuntime);
        Assert.Contains(playerB.Hq, fromRuntime);
        Assert.DoesNotContain(smoked, fromRuntime);

        // 同源：两侧候选集合逐项相等（不比较顺序——运行时面只保留卡引用）。
        Assert.Equal(fromCommand.Count, fromRuntime.Count);
        foreach (var reference in fromCommand)
        {
            Assert.Contains(reference.Value, fromRuntime);
        }
    }
}
