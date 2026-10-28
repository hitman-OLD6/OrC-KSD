using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C 验收③（攻击结算；后置项 A/B 适配后）：互伤（同时结算、以互扣前实时值为基准；豁免表全组合见 CommandImmunityTests）；
/// 死亡清理最小口径（槽位释放 / IsDestroyed / Position 置空 / 实例保留可查询 / 不发 position.changed / card.died 恰一次）；
/// 同归于尽（两枚 card.died、被攻击者在前）；HQ 归零＝立即终局（钳制到 0、不反击、状态置结束＋胜者；当次收尾照常）；
/// 攻击者死亡仍成功（照扣费、照清位）；伏击改写（先资格〔反击豁免表〕后条件：成立＝攻击者死·目标不受伤；
/// 不成立＝正常互伤与无资格；攻击者侧伏击不参与；死亡后不再改写）。
/// </summary>
public class CommandCombatTests
{
    [Fact]
    public async Task Mutual_Destruction_Emits_Two_Died_With_Target_First()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 适配（后置项 A）：原「炮兵 × 炮兵」组合现属豁免表（攻击者＝炮兵不受任何反击、不会互伤）——
        // 迁移为非豁免组合「战斗机（攻 3 / 防 2）× 炮兵（攻 2 / 防 2）」（覆盖语义保持：互伤同归）。
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0); // 攻 3 / 防 2
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 同时结算（互扣前实时值）→ 同归于尽（战斗机攻 3 ≥ 炮兵防 2；炮兵反击 2 ≥ 战斗机防 2）：
        // 两枚 card.died 均发射，顺序＝被攻击者在前、攻击者在后。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        var diedEvents = recorder.Updates.Where(u => u.Type == GameUpdates.CardDied).ToList();
        Assert.Equal(2, diedEvents.Count);
        Assert.Same(target, diedEvents[0].Payload![GameUpdates.PayloadCard]);
        Assert.Same(attacker, diedEvents[1].Payload![GameUpdates.PayloadCard]);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Effect_Damage_With_Source_Emits_Dealt_And_Killer()
    {
        // E1-50：**效应侧归属**——带来源的效果伤害同样发 `unit.damage.dealt`，且致死时 `card.died.Killer` ＝来源。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0); // 防 2
        var runtime = EffectRuntime.ResolveFor(dealer);
        Assert.NotNull(runtime);
        using var recorder = new UpdateRecorder(match.Engine);

        await runtime!.DamageAsync(target, 2, dealer);

        var dealt = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitDamageDealt);
        Assert.Same(dealer, dealt.Payload![GameUpdates.PayloadUnit]);
        Assert.Same(target, dealt.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(2, dealt.Payload![GameUpdates.PayloadAmount]);

        var died = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Same(dealer, died.Payload![GameUpdates.PayloadKiller]);
    }

    [Fact]
    public async Task Damage_Dealt_Carries_Dealer_Target_And_Amount()
    {
        // E1-47：`unit.damage.dealt` **来源侧**——双方各自的实际造成量（互伤：先攻击者、后反击方）。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BeastId, 0);        // 攻 6 / 防 7
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        var dealt = recorder.Updates.Where(u => u.Type == GameUpdates.UnitDamageDealt).ToList();
        Assert.Equal(2, dealt.Count);
        Assert.Same(attacker, dealt[0].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(target, dealt[0].Payload![GameUpdates.PayloadCard]);
        Assert.Equal(2, dealt[0].Payload![GameUpdates.PayloadAmount]);
        Assert.Same(target, dealt[1].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(attacker, dealt[1].Payload![GameUpdates.PayloadCard]);
        Assert.Equal(5, dealt[1].Payload![GameUpdates.PayloadAmount]); // 反击 6 点但防御仅 5（实际变化量＝5）
    }

    [Fact]
    public async Task CardDied_Carries_Killer_On_Combat_Kill_And_Null_Without_Attribution()
    {
        // E1-47：`card.died` 加性携带 **Killer**（对战致死＝攻击者；非归属驱动死亡＝null）。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        var died = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Same(target, died.Payload![GameUpdates.PayloadCard]);
        Assert.Same(attacker, died.Payload![GameUpdates.PayloadKiller]);

        // 非归属驱动（直接伤害致防御归零）⇒ Killer 为 null
        var plain = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.WeakId, 1);
        using var direct = new UpdateRecorder(match.Engine);
        await plain.ApplyDefenseDamageAsync(2);

        var plainDied = Assert.Single(direct.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Null(plainDied.Payload![GameUpdates.PayloadKiller]);
    }

    [Fact]
    public async Task Combat_Survived_Emits_For_Living_Participants_Target_First()
    {
        // E1-39：一次对战结算 ⇒ 参战且未阵亡者各发一次 unit.combat.survived（观察序＝被攻击者在前、攻击者在后）。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 攻击者＝炮兵（豁免表：攻击者炮兵不受反击）⇒ 双方皆存活（反击豁免只影响"是否互伤"、不影响"是否参战"）。
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BeastId, 0);         // 攻 6 / 防 7
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.False(target.GetData<UnitStateData>().IsDestroyed);
        var survived = recorder.Updates.Where(u => u.Type == GameUpdates.UnitCombatSurvived).ToList();
        Assert.Equal(2, survived.Count);
        Assert.Same(target, survived[0].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(attacker, survived[1].Payload![GameUpdates.PayloadUnit]);
    }

    [Fact]
    public async Task Combat_Survived_Skips_Dead_Participant()
    {
        // E1-39：同归于尽 ⇒ 双方皆阵亡 ⇒ 零发射（不误导"幸存"）。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.FighterId, 0); // 攻 3 / 防 2
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 1); // 攻 2 / 防 2
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
        Assert.DoesNotContain(GameUpdates.UnitCombatSurvived, recorder.Types);
    }

    [Fact]
    public async Task Combat_Survived_Fires_For_Unharmed_Target_On_Ambush_Rewrite()
    {
        // E1-39：伏击改写路径（攻击者死亡、被攻击者不受伤）⇒ 被攻击者确经交战且未阵亡 ⇒ 恰一条。
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);   // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0);   // 攻 5 / 防 6（伏击）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        var survived = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.UnitCombatSurvived);
        Assert.Same(ambusher, survived.Payload![GameUpdates.PayloadUnit]);
    }

    [Fact]
    public async Task Death_Cleanup_Frees_Slot_Keeps_Instance_And_Skips_Position_Update()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0); // 攻 1 / 防 2
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 死亡清理：槽位释放（变空槽）；IsDestroyed 置位；Position 置空、实例保留可查询；card.died 恰一次；
        // 不发 unit.position.changed（该更新专属移动语义）。
        // W2b 随改：防御扣减经门户→跑链集中触发——被攻击者（2→0）与攻击者（5→4）各一条 card.stat.changed；
        // 「先数值变化、后死亡信号」：被攻击者的 sc（外部订阅通知）先于 card.died（防御归零的统一死亡衔接——总线触发器阶段），
        // 攻击者的 sc 随后；恰一次死亡。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.DoesNotContain(target, match.Battlefield.FrontLine.Members);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
        Assert.Null(target.GetData<UnitStateData>().Line);
        Assert.True(target.Ref.IsAlive); // 实例保留（不销毁）——可查询
        Assert.Equal(
            new[]
            {
                GameUpdates.CardStatChanged, GameUpdates.CardDying, GameUpdates.CardDied, GameUpdates.CardDamaged,
                GameUpdates.UnitDamageDealt, // E1-47：来源侧（攻击者造成）
                GameUpdates.CardStatChanged, GameUpdates.CardDamaged, // E1-33：受伤害（先数值、后伤害信号）
                GameUpdates.UnitDamageDealt, // E1-47：来源侧（被攻击者反击造成）
                GameUpdates.UnitCombatSurvived, // E1-39：攻击者交战存活（被攻击者已阵亡＝不发射）
                GameUpdates.PointChanged, // 收尾扣行动费（E1-25 后续）
                GameUpdates.UnitActed,    // E1-33：攻击者行动后（存活）
            },
            recorder.Types);
        var diedPayload = recorder.Updates.Single(u => u.Type == GameUpdates.CardDied).Payload!;
        Assert.Same(target, diedPayload[GameUpdates.PayloadCard]);
        Assert.DoesNotContain(GameUpdates.UnitPositionChanged, recorder.Types);
        // 攻击者收尾照常（未死）：扣费＋清位。
        Assert.Equal(0, playerA.Points);
        Assert.False(attacker.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task Dead_Attacker_Still_Resolves_As_Success_With_Fee_And_Flag_Updates()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        // 适配（后置项 A）：原「炮兵 × 炮兵」组合现属豁免表（攻击者＝炮兵不受任何反击——攻击者不会死亡）——
        // 迁移为非豁免组合「脆皮步兵（攻 1 / 防 2）× 炮兵（攻 2 / 防 2）」：以攻击者死亡告终（覆盖语义保持）。
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.WeakId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.ArtilleryId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 攻击者死亡的攻击仍为「成功」（结算完整完成）；收尾照常：扣费（不因死亡豁免）＋清位照写（统一执行、不特判）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(0, playerA.Points);
        Assert.False(attacker.GetData<CommandData>().CanAttack);
        Assert.False(attacker.GetData<CommandData>().CanMove);
    }

    [Fact]
    public async Task Hq_Damage_Is_Clamped_To_Zero_Without_Counterattack()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mega = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MegaId, 1); // 攻 30 / 防 30（炮兵）
        CommandTestKit.Activate(mega);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var enemyHq = playerB.Hq; // W3-3：HQ 目标＝实体引用

        var result = await CommandTestKit.RunCommandAsync(match, bridge, mega, enemyHq.Ref);

        // 伤害＝实时攻击力（20-30 → 钳制到 0）；HQ 不反击（攻击者不受伤害）；HQ 生命≤0 → 立即终局
        // （状态＝结束＋胜者＝攻击方）。当次结算收尾照常完成（扣费＋清位）。
        // W3-3：伤害经 HQ 数值路径与管线（改写→介入→应用→跑链）→ 归零统一响应（判定归 HQ 侧）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(0, playerB.HqHealth);
        Assert.Equal(30, mega.GetData<UnitStateData>().Defense);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
        Assert.Same(enemyHq, match.Battlefield.PlayerBSupportLine[0]); // 位置不变：HQ 仍是敌方支援线位置 0 的元素
        Assert.Equal(0, playerA.Points); // 当次收尾照常：扣费恰一次
        Assert.False(mega.GetData<CommandData>().CanAttack); // 清位照常
        Assert.False(mega.GetData<CommandData>().CanMove);
    }

    [Fact]
    public async Task Ambush_Rewrites_When_Target_Attack_Exceeds_Attacker_Defense()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        // 伏击改写：被攻击单位实时攻击力（5）＞攻击者实时防御力（2）→ 攻击者死亡（统一死亡流程、无 HP 逐步扣减）、
        // 被攻击者完全不受伤；其他结算不发生。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, attacker.GetData<UnitStateData>().Defense); // 无 HP 扣减（直接死亡结果）
        Assert.Equal(6, ambusher.GetData<UnitStateData>().Defense); // 目标不受伤
        Assert.Equal(
            new[] { GameUpdates.CardDying, GameUpdates.CardDied, GameUpdates.UnitCombatSurvived, GameUpdates.PointChanged }, // E1-39：被攻击者存活；末条＝收尾扣行动费
            recorder.Types);
        Assert.Equal(0, playerA.Points); // 攻击者死亡不豁免收尾
    }

    [Fact]
    public async Task Ambush_Not_Triggered_When_Condition_Fails_Falls_Back_To_Default_Damage()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.BeastId, 0); // 攻 6 / 防 7
        var ambusher = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, ambusher.Ref);

        // 条件不成立（5 ＞ 7 为假）＝正常基础互伤：伏击兵 6-6=0 死；攻击者 7-5=2。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, attacker.GetData<UnitStateData>().Defense);
    }

    [Fact]
    public async Task Ambush_Is_Defense_Side_Only_Attacker_Ambush_Does_Not_Trigger()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attackerAmbush = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.AmbushId, 1); // 攻 5 / 防 6（伏击——攻击者侧）
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        CommandTestKit.Activate(attackerAmbush);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attackerAmbush, target.Ref);

        // 攻击者侧伏击不参与（伏击为防御方特性）＝正常互伤：目标 5-5=0 死；攻击者 6-2=4（受反伤）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(4, attackerAmbush.GetData<UnitStateData>().Defense);
    }

}
