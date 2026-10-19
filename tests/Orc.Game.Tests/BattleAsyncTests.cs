using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// R10 战斗验收：<c>EffectRuntime.BattleAsync</c>——快照攻击力后同时结算、双向归属、
/// 不触发攻击类信号/行动代价、前置不满足＝false 且零副作用。
/// </summary>
public class BattleAsyncTests
{
    private const string DuelistId = "u_r10_duel3";   // 攻 3 / 防 3
    private const string BulwarkId = "u_r10_bulwark"; // 攻 3 / 防 6

    private static readonly IReadOnlyList<CardDefinitionEntry> ExtraDefinitions = new[]
    {
        new CardDefinitionEntry(DuelistId, new CardDefinition("决斗兵", 1, 1, 3, 3,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(BulwarkId, new CardDefinition("壁垒兵", 1, 1, 3, 6,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    [Fact]
    public async Task BattleAsync_Uses_Snapshot_So_Dead_First_Still_Strikes()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var first = await CommandTestKit.PrepareOnSupportAsync(match, playerA, DuelistId, 0); // 攻 3 / 防 3
        var second = await CommandTestKit.PrepareOnSupportAsync(match, playerA, DuelistId, 1); // 攻 3 / 防 3

        var runtime = EffectRuntime.ResolveFor(first)!;
        Assert.True(await runtime.BattleAsync(first, second));

        // 先结算 first→second（second 致死），second 仍按快照攻击力反击 → 同归于尽。
        Assert.True(first.GetData<UnitStateData>().IsDestroyed);
        Assert.True(second.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task BattleAsync_Attributes_Each_Direction_To_Its_Attacker()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var first = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 0);  // 攻 3 / 防 6
        var second = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 1); // 攻 3 / 防 6

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var runtime = EffectRuntime.ResolveFor(first)!;
        Assert.True(await runtime.BattleAsync(first, second));

        var dealt = recorder.Updates
            .Where(update => update.Type == GameUpdates.UnitDamageDealt)
            .Select(update => (
                Dealer: update.Payload![GameUpdates.PayloadUnit],
                Target: update.Payload[GameUpdates.PayloadCard],
                Amount: update.Payload[GameUpdates.PayloadAmount]))
            .ToArray();

        Assert.Equal(2, dealt.Length);
        Assert.Contains(dealt, item => ReferenceEquals(item.Dealer, first)
            && ReferenceEquals(item.Target, second)
            && Equals(item.Amount, 3));
        Assert.Contains(dealt, item => ReferenceEquals(item.Dealer, second)
            && ReferenceEquals(item.Target, first)
            && Equals(item.Amount, 3));
        Assert.Equal(3, DefenseOf(first));
        Assert.Equal(3, DefenseOf(second));
    }

    [Fact]
    public async Task BattleAsync_Emits_No_Attack_Or_Action_Signals()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var first = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 0);
        var second = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 1);
        var canMoveA = first.GetData<CommandData>().CanMove;
        var canAttackA = first.GetData<CommandData>().CanAttack;
        var canMoveB = second.GetData<CommandData>().CanMove;
        var canAttackB = second.GetData<CommandData>().CanAttack;

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var runtime = EffectRuntime.ResolveFor(first)!;
        Assert.True(await runtime.BattleAsync(first, second));

        Assert.DoesNotContain(GameUpdates.UnitActed, recorder.Types);
        Assert.DoesNotContain(GameUpdates.UnitCombatSurvived, recorder.Types);
        Assert.DoesNotContain(recorder.Types, type => type.StartsWith("unit.attack", StringComparison.Ordinal));

        // 不扣行动代价：行动位/可移动/可攻击均不变。
        Assert.Equal(canMoveA, first.GetData<CommandData>().CanMove);
        Assert.Equal(canAttackA, first.GetData<CommandData>().CanAttack);
        Assert.Equal(canMoveB, second.GetData<CommandData>().CanMove);
        Assert.Equal(canAttackB, second.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task BattleAsync_Returns_False_When_Participant_Not_On_Board()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var onBoard = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 0);
        var inHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, DuelistId); // 已加载但未上场

        var runtime = EffectRuntime.ResolveFor(onBoard)!;
        Assert.False(await runtime.BattleAsync(onBoard, inHand));
        Assert.False(await runtime.BattleAsync(onBoard, playerA.Hq)); // 非单位：不战斗
        Assert.Equal(6, DefenseOf(onBoard)); // 零副作用
    }

    [Fact]
    public async Task BattleAsync_Source_Is_Audit_Only_And_Does_Not_Change_Attribution()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var first = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 0);
        var second = await CommandTestKit.PrepareOnSupportAsync(match, playerA, BulwarkId, 1);
        var caster = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var runtime = EffectRuntime.ResolveFor(first)!;
        Assert.True(await runtime.BattleAsync(first, second, source: caster));

        // 归属仍是「各自为对方」——发起卡只进日志。
        var dealers = recorder.Updates
            .Where(update => update.Type == GameUpdates.UnitDamageDealt)
            .Select(update => update.Payload![GameUpdates.PayloadUnit])
            .ToArray();
        Assert.Contains(dealers, item => ReferenceEquals(item, first));
        Assert.Contains(dealers, item => ReferenceEquals(item, second));
        Assert.DoesNotContain(dealers, item => ReferenceEquals(item, caster));
    }

    private static int DefenseOf(Card card) => ((CardBase)card).Modifiers.GetEffectiveValue(CardStatFields.Defense);
}
