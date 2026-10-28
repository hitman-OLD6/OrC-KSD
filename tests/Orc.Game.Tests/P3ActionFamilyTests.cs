using System.Reflection;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P3 动作族验收（S9 加入战场 / S11 攻击）：逐条钉死本阶段的**允许行为变更**与**不允许行为变更**，
/// 避免「半新半旧」的第三条路径。
/// </summary>
public class P3ActionFamilyTests
{
    // ══════════════════════════ S9：加入战场 ══════════════════════════

    [Fact]
    public async Task Deploy_Signal_Order_Is_Played_Placed_Deployed_Then_Fee()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var slot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayUnitAsync(unit, slot);

        // 不允许的行为变更（S9）：card.played → card.placed → unit.deployed → 扣部署费。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(
            new[]
            {
                GameUpdates.CardPlayed,
                Updates.CardPlaced,
                GameUpdates.UnitDeployed,
                GameUpdates.PointChanged,
            },
            recorder.Types);
        Assert.Contains(unit, slot.Line.Members);
        Assert.Same(slot.Line, unit.GetData<UnitStateData>().Line);
        Assert.DoesNotContain(unit, player.Hand);
        Assert.Equal(0, player.Points);
    }

    [Fact]
    public async Task Join_Signal_Order_Is_Placed_Then_Joined_Without_Fee()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        // 夹具（v4）：加入经卡实体成员 `host#join`，卡须经正式装配路径加载（未加载卡＝fail-fast）。
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, player, PlayChainTestKit.UnitCheapId, toHand: false);
        var slot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        var points = player.Points;
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.JoinUnitAsync(unit, slot);

        // 不允许的行为变更（S9）：card.placed → unit.joined；无宣告、无扣费。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { Updates.CardPlaced, GameUpdates.UnitJoined }, recorder.Types);
        Assert.Contains(unit, slot.Line.Members);
        Assert.Equal(points, player.Points);
    }

    [Fact]
    public async Task Deploy_Rejects_At_Initiate_When_Points_Shortage_With_Zero_Side_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCostlyId); // 部署费 5
        var slot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        using var recorder = new UpdateRecorder(match.Engine);

        // 直接驱动动作的部署触发端（绕过 PlayTrigger 验证）：代价校验必须在发起前拒绝。
        var result = await match.CommandManager.JoinBattlefieldAction.DeployAsync(unit, slot, player);

        Assert.NotEqual(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.Equal(ActionFailureKind.Precondition, result.Targets[0].Reason);
        Assert.Empty(recorder.Updates); // 零宣告、零放置、零扣费
        Assert.DoesNotContain(unit, match.Battlefield.PlayerASupportLine.Members);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
        Assert.Contains(unit, player.Hand);
    }



    [Fact]
    public async Task Illegal_Join_Target_Is_Target_Invalid_With_Zero_Side_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);

        // 非法目标＝**过期快照**（先取空隙、阵线随后变化）：空隙身份随快照失效，属结构性拒绝。
        var front = match.Battlefield.FrontLine;
        var staleGap = front.GetInsertionGaps().At(0);
        var occupant = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, match.Players[0], PlayChainTestKit.UnitCheapId, toHand: false);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(occupant, front.GetInsertionGaps().At(0))).Status);
        Assert.False(staleGap.IsCurrent);

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.CommandManager.JoinBattlefieldAction.JoinAtAsync(unit, staleGap);

        Assert.Equal(ActionOutcomeKind.TargetInvalid, result.Targets[0].Outcome);
        Assert.Empty(recorder.Updates);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    // ══════════════════════════ S11：攻击 ══════════════════════════






    [Fact]
    public async Task Suppressed_Unit_Can_Still_Be_Forced_To_Attack_By_Effect()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 防 5
        CommandTestKit.Activate(attacker);
        Assert.True(await SuppressRules.ApplyAsync(attacker));

        // 压制只落在指挥面（R16）：指挥攻击被拒。
        var availability = match.CommandManager.GetCommandAvailability(attacker);
        Assert.False(availability.Attack.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Attack.BlockReason);

        // 效果攻击不受压制阻断；且零行动经济面。
        var points = playerA.Points;
        using var recorder = new UpdateRecorder(match.Engine);
        Assert.True(await match.CommandManager.AttackByEffectAsync(attacker, target, CancellationToken.None));

        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 2
        Assert.Equal(points, playerA.Points);
        Assert.DoesNotContain(GameUpdates.UnitActed, recorder.Types);
        Assert.True(attacker.GetData<CommandData>().CanAttack);
        Assert.True(attacker.GetData<CommandData>().CanMove);
    }

    [Fact]
    public async Task Normal_Mutual_Damage_Keeps_Survived_Order_Target_Then_Attacker()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 互伤经伤害动作：双向各一条 unit.damage.dealt；幸存信号＝被攻击者在前、攻击者在后。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(2, recorder.Types.Count(t => t == GameUpdates.UnitDamageDealt));
        var survived = recorder.Updates.Where(u => u.Type == GameUpdates.UnitCombatSurvived).ToList();
        Assert.Equal(2, survived.Count);
        Assert.Same(target, survived[0].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(attacker, survived[1].Payload![GameUpdates.PayloadUnit]);
    }
}
