using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C 验收⑤（更新发射）：unit.position.changed（移动时，恰一次；载荷 {Unit, OldPosition, NewPosition}——见 CommandSystemTests）；
/// card.died（死亡时，恰一次；死亡状态就绪后发射——槽位释放 / IsDestroyed 置位 / Position 置空先于发射）；
/// 死亡不发 unit.position.changed（该更新专属移动语义）；与引擎内置 card.destroyed 不同义（不混用）；
/// 攻击（无死亡）与移动（无死亡）的更新纯净性（不产生额外更新）。
/// </summary>
public class CommandUpdateTests
{
    [Fact]
    public async Task Attack_Without_Deaths_Emits_StatChanged_For_Both_Sides()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 攻击互伤但无死亡（W2b 随改——原「结算数值静默」语义废止）：数值变更经门户→跑链→集中触发——
        // 互伤双方各恰一条 card.stat.changed（目标在前、攻击者在后；载荷＝目标卡＋变化字段 [Defense]）；无其它更新。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(
            new[]
            {
                GameUpdates.CardStatChanged, GameUpdates.CardDamaged, GameUpdates.UnitDamageDealt, // E1-47：来源侧
                GameUpdates.CardStatChanged, GameUpdates.CardDamaged, GameUpdates.UnitDamageDealt, // E1-47：反击方向
                GameUpdates.UnitCombatSurvived, // E1-39：被攻击者交战存活
                GameUpdates.UnitCombatSurvived, // E1-39：攻击者交战存活（观察序＝被攻击者在前、攻击者在后）
                GameUpdates.PointChanged, // 收尾扣行动费（E1-25 后续：指挥走点数通用路径）
                GameUpdates.UnitActed,    // E1-33：行动后（攻击收尾）
            },
            recorder.Types);
        var targetUpdate = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], target));
        var attackerUpdate = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], attacker));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(targetUpdate.Payload));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(attackerUpdate.Payload));
        // 互伤数值（以互扣前有效值为基准）：5-2=3 与 5-2=3
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(3, attacker.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task Card_Died_Arrives_With_Death_State_Ready()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 发射时点探针：card.died 到达时死亡状态已就绪（消费者查询不到半死状态）。
        var probes = new List<(bool SlotFreed, bool Destroyed, bool LineCleared)>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDied)
            {
                var card = (UnitCard)payload![GameUpdates.PayloadCard]!;
                var state = card.GetData<UnitStateData>();
                probes.Add((!match.Battlefield.FrontLine.Contains(card), state.IsDestroyed, state.Line is null));
            }

            return Task.CompletedTask;
        });

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        var state = Assert.Single(probes);
        Assert.True(state.SlotFreed);      // 槽位释放先于发射
        Assert.True(state.Destroyed);      // IsDestroyed 置位先于发射
        Assert.True(state.LineCleared); // Position 置空先于发射
    }

    [Fact]
    public async Task Death_Does_Not_Emit_Position_Changed_Nor_Engine_CardDestroyed()
    {
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

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 死亡不发 unit.position.changed（专属移动语义）；与引擎内置 card.destroyed 不同义（游戏层死亡＝card.died）。
        // W2b 随改：防御扣减经门户→跑链集中触发——被攻击者（防御 2→0）与攻击者（5→4）各一条 card.stat.changed；
        // 「先数值变化、后死亡信号」：被攻击者的 sc（外部订阅通知）先于 card.died（防御归零的统一死亡衔接——总线触发器阶段），
        // 攻击者的 sc 随后；恰一次死亡。
        Assert.Equal(CommandResultStatus.Success, result.Status);
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
        Assert.DoesNotContain(GameUpdates.UnitPositionChanged, recorder.Types);
        Assert.DoesNotContain(Updates.CardDestroyed, recorder.Types);
        Assert.NotEqual(Updates.CardDestroyed, GameUpdates.CardDied);
    }

    [Fact]
    public async Task Move_Emits_Only_Position_Changed()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(3).Ref);

        // 移动纯净性：unit.position.changed ＋ 收尾扣行动费的 point.changed（E1-25 后续）；不发 card.died / card.played / turn 类。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(
            new[] { GameUpdates.UnitPositionChanged, GameUpdates.PointChanged, GameUpdates.UnitActed }, // E1-33：行动后
            recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);
    }
}
