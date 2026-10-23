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
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);
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
        Assert.False(slot.IsEmpty());
        Assert.False(unit.TryGetData<UnitStateData>(out _));
        Assert.Contains(unit, player.Hand);
    }

    [Fact]
    public async Task Join_And_Deploy_Share_One_Handle_End_With_No_Entry_Flag()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var action = match.CommandManager.JoinBattlefieldAction;

        // 两触发端 + 一共享处理端；处理端无 Mode/入口类型标志（视图属性即证据）。
        Assert.Equal(typeof(ActionTriggerBands), action.Deploy.BandType);
        Assert.Equal(typeof(ActionTriggerBands), action.JoinSupportLine.BandType);
        Assert.Equal(typeof(ActionHandleBands), action.Handle.BandType);
        Assert.Single(action.Handle.Events, e => e.BandValue == (int)ActionHandleBands.Apply);
        Assert.DoesNotContain(
            typeof(JoinBattlefieldHandleView).GetProperties(),
            p => p.Name.Contains("Mode", StringComparison.OrdinalIgnoreCase));

        // 部署与加入各经同一处理端落地：card.placed 恰一次、状态一致。
        var deployed = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var joinSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);
        var joinUnit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);

        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.PlayUnitAsync(deployed, joinSlot)).Status);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(joinUnit, match.Battlefield.FrontLine.GetInsertionGaps().At(0))).Status);
        Assert.Same(joinSlot, deployed.GetData<UnitStateData>().Line);
        Assert.NotNull(joinUnit.GetData<UnitStateData>().Line);
    }

    [Fact]
    public async Task Deploy_Keywords_Run_In_Commit_Head_Before_Unitization()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var slot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);

        // 部署词条位于部署触发端 Commit 段前部（priority 50 < 默认转发 100）。
        var commit = match.CommandManager.JoinBattlefieldAction.Deploy.Events
            .Where(e => e.BandValue == (int)ActionTriggerBands.Commit)
            .Select(e => (e.Name, e.Priority))
            .ToArray();
        Assert.Equal(new[] { "部署词条", "转发到处理端" }, commit.Select(e => e.Name));
        Assert.True(commit[0].Priority < commit[1].Priority);

        var unitizedAtKeyword = new List<bool>();
        unit.AddEffect(new DeployLogicProbeEffect(
            unit, "前置探针", view => { unitizedAtKeyword.Add(unit.TryGetData<UnitStateData>(out _)); return Task.CompletedTask; }));

        var result = await match.PlayManager.PlayUnitAsync(unit, slot);

        // 部署词条先于单位化（未进入共享处理端）：探针看到「尚未单位化」。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.False(Assert.Single(unitizedAtKeyword));
    }

    [Fact]
    public async Task Illegal_Join_Target_Is_Target_Invalid_With_Zero_Side_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);
        var occupied = match.Battlefield.FrontLine.GetInsertionGaps().At(1);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.CommandManager.JoinBattlefieldAction.JoinAtAsync(unit, occupied);

        Assert.Equal(ActionOutcomeKind.TargetInvalid, result.Targets[0].Outcome);
        Assert.Empty(recorder.Updates);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    // ══════════════════════════ S11：攻击 ══════════════════════════

    [Fact]
    public async Task Attack_Action_Has_Two_Banded_Ends_And_Injects_Only_The_Damage_Action()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var action = match.CommandManager.AttackAction;

        Assert.Same(match.CommandManager.UnitAttackTrigger, action.Trigger);
        Assert.Same(match.CommandManager.AttackDamageTrigger, action.Handle);
        Assert.Equal(typeof(ActionTriggerBands), action.Trigger.BandType);
        Assert.Equal(typeof(ActionHandleBands), action.Handle.BandType);

        // 触发端：Commit 转发恰一次。
        Assert.Single(action.Trigger.Events, e => e.Name == "转发到处理端"
            && e.BandValue == (int)ActionTriggerBands.Commit);

        // 处理端：默认结算在 Apply 段。
        Assert.Single(action.Handle.Events, e => e.Name == "默认攻击结算"
            && e.BandValue == (int)ActionHandleBands.Apply);

        // S10：攻击 → 伤害（构造注入）；不注入死亡动作（普通死亡被动衔接）。
        Assert.Same(match.CommandManager.DamagePipeline.Action, action.Damage);
        Assert.DoesNotContain(
            typeof(Orc.Foundation.Actions.AttackAction).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(IDeathAction));
    }

    [Fact]
    public async Task Attack_Trigger_End_Runs_Before_Handle_End()
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

        var order = new List<string>();
        match.CommandManager.UnitAttackTrigger.Register(
            "顺序探测-触发端", (_, _, _) => { order.Add("trigger"); return Task.CompletedTask; },
            ActionTriggerBands.Initiate);
        match.CommandManager.AttackDamageTrigger.Register(
            "顺序探测-处理端", (_, _, _) => { order.Add("handle"); return Task.CompletedTask; },
            ActionHandleBands.Apply);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(new[] { "trigger", "handle" }, order);
    }

    [Fact]
    public async Task Ambush_Handles_Death_Through_Death_Action_And_Skips_Damage()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);  // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0);  // 攻 5 / 防 6
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 死亡动作**触发端**探针：伏击必须走完整两端（触发端 → 处理端），不直调处理端。
        var deathTriggerHits = new List<UnitCard>();
        match.CommandManager.DeathAction.Trigger.Register(
            "伏击死亡-触发端探针",
            (view, _, _) => { deathTriggerHits.Add(view.Unit); return Task.CompletedTask; },
            ActionTriggerBands.Initiate);

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        // 伏击＝改写方自己走完整死亡两端：攻击者死、被攻击者零伤害（互伤未发生）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Same(attacker, Assert.Single(deathTriggerHits));
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, ambusher.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.DoesNotContain(GameUpdates.UnitDamageDealt, recorder.Types);

        // card.died 的击杀者＝伏击兵（死亡动作的 killer 归属）。
        var died = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Same(ambusher, died.Payload![GameUpdates.PayloadKiller]);

        // 幸存信号：仅被攻击者（攻击者已阵亡）。
        var survived = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitCombatSurvived);
        Assert.Same(ambusher, survived.Payload![GameUpdates.PayloadUnit]);
    }

    [Fact]
    public async Task Ambush_With_Death_Prevented_Emits_Attacker_Survived()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 免死：死亡处理端改写为不发生（伏击的「以死代伤」被阻止）。
        match.CommandManager.DeathAction.Handle.Register(
            "免死", (view, _, _) => { view.ShouldDie = false; return Task.CompletedTask; },
            ActionHandleBands.Rewrite, priority: 0);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);

        // 免死＝攻击者存活 → 处理端另发一条攻击者幸存；观察序＝被攻击者在前、攻击者在后。
        var survived = recorder.Updates.Where(u => u.Type == GameUpdates.UnitCombatSurvived).ToList();
        Assert.Equal(2, survived.Count);
        Assert.Same(ambusher, survived[0].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(attacker, survived[1].Payload![GameUpdates.PayloadUnit]);
    }

    [Fact]
    public async Task Handled_Without_Rewrite_Logs_Error_And_Falls_Back_To_Normal_Damage()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 改写方置 Handled 却未处置（IsRewritten=false）——必须留痕并回退正常伤害，禁止静默吞伤害。
        match.CommandManager.AttackDamageTrigger.Register(
            "只置处置标志", (view, _, _) =>
            {
                if (view.Resolution is AttackDamageResolution resolution)
                {
                    resolution.MarkHandled();
                }

                return Task.CompletedTask;
            },
            ActionHandleBands.Rewrite);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 2：正常伤害照常落地
        Assert.Contains(
            match.Engine.RootStream.Entries,
            e => e.Keywords.Contains("attack:handled-without-rewrite") && e.Level == LogLevel.Error);
    }

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
