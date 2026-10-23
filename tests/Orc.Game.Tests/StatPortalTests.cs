using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2b G3 接线——门户（单位数值受控变更面）与防御力上限语义测试：
/// ①门户操作面（伤害扣减/修复：跑链衔接、有变更集中触发、表现位同步、基准不变、幂等/明确拒绝、零值静默）；
/// ②上限语义（加防同步加上限／伤害扣减只扣当前不减上限／修复＝恢复到上限／数值表现钳制不低于 0；
///   上限经只读查询面 GetEffectiveDefenseCap 直接断言）；
/// ③撤销/到期的 ≤0 死亡边界（对局内经统一死亡衔接；死亡终态：修饰集合空、上限回落至基准）；
/// ③+ 死亡冻结与死亡清理（死亡后数值操作＝明确拒绝或幂等无效、零副作用；死亡清理＝注销全部修饰器
///   〔含期限订阅随销〕＋数值整合至最终态、零新发射、无二次 died）；
/// ④伤害衔接（经既有基础互伤流程真实触发＋直达门户操作面；变化字段集合正确；无变化零发射）。
/// 机制级用例遵循「首轮基线」约定：直接构造卡在首次变更前先显式跑链一次（装配完成点等价；零发射）。
/// </summary>
public class StatPortalTests
{
    // ---------- ① 门户操作面（伤害扣减） ----------

    [Fact]
    public async Task Portal_Damage_Deducts_Current_Only_And_Emits_Concentrated_Trigger()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 2, defense: 5, operateCost: 1);
        using var recorder = new UpdateRecorder(engine);
        await unit.Modifiers.RequestRerunAsync(); // 建立初始快照（装配完成点等价；零发射）

        await unit.ApplyDefenseDamageAsync(2);

        // 伤害＝即时变更：只扣当前（5→3）、上限不变；表现位同步；恰一条集中触发（载荷＝目标卡＋变化字段 [Defense]）。
        Assert.Equal(3, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(3, unit.GetData<UnitStateData>().Defense); // 表现位（落定同步）
        Assert.Equal(5, unit.GetData<BattleStatsData>().Defense); // 只读基准不变（运行期变更不写基准）
        var update = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardStatChanged);
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Same(unit, update.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 到濒死线（3→0）；再到超杀（0 − 2 → 逻辑负值）：数值表现钳制不低于 0、无变化零发射。
        recorder.Clear();
        await unit.ApplyDefenseDamageAsync(3);
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardStatChanged);
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDamaged); // E1-33：受伤害信号

        recorder.Clear();
        await unit.ApplyDefenseDamageAsync(2);
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(0, unit.GetData<UnitStateData>().Defense);
        Assert.Empty(recorder.Updates);
    }

    [Fact]
    public async Task Portal_Repair_Restores_To_Cap_And_Rejects_Dead_Or_Ununitized()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, defense: 5);
        using var recorder = new UpdateRecorder(engine);
        await unit.Modifiers.RequestRerunAsync();

        await unit.ApplyDefenseDamageAsync(2); // 5 → 3
        recorder.Clear();

        // 修复＝恢复到上限：3 → 5（恰一条集中触发）
        await unit.RepairDefenseAsync();
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(5, unit.GetData<UnitStateData>().Defense);
        Assert.Single(recorder.Updates);

        // 满血（无损伤）修复＝幂等无变化（零发射）
        recorder.Clear();
        await unit.RepairDefenseAsync();
        Assert.Empty(recorder.Updates);

        // 已死亡/已毁单位：明确拒绝（fail-fast——仅对存活单位有效）
        unit.GetData<UnitStateData>().IsDestroyed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.RepairDefenseAsync());

        // 未单位化：明确拒绝（装配性错误）
        var bare = ModifierTestKit.CreateBareUnit(engine);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare.RepairDefenseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare.ApplyDefenseDamageAsync(1));

        // 参数域：负伤害＝明确拒绝
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unit.ApplyDefenseDamageAsync(-1));
    }

    // ---------- ② 上限语义 ----------

    [Fact]
    public async Task Cap_AddDefense_Synchronizes_Cap_And_Raises_Current()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, defense: 5);
        using var recorder = new UpdateRecorder(engine);
        await unit.Modifiers.RequestRerunAsync();

        // 未受伤 5/5 加 +2 → 7/7（当前值与上限同步 +2；上限经只读查询面直接断言）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, new object()));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(7, unit.GetEffectiveDefenseCap());
        Assert.Equal(7, unit.GetData<UnitStateData>().Defense);

        // 受伤中：受 3 伤（7 → 4；loss＝3 不变；上限保持 7）→ 再加 +1 → 上限 8、当前 5（损伤量不因加防回缩）
        await unit.ApplyDefenseDamageAsync(3);
        Assert.Equal(4, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(7, unit.GetEffectiveDefenseCap()); // 伤害不减上限（直接断言）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 1, new object()));
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // (5 − 3) + 2 + 1 = 5
        Assert.Equal(8, unit.GetEffectiveDefenseCap()); // 受损 4 时加防后：上限 8、当前 5

        // 修复＝恢复到上限：loss 清零 → 8/8（上限与当前直接断言）
        await unit.RepairDefenseAsync();
        Assert.Equal(8, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 + 3
        Assert.Equal(8, unit.GetEffectiveDefenseCap());
        Assert.Equal(8, unit.GetData<UnitStateData>().Defense);

        // 上限参与满血结算：从满血受 8 伤 → 0（恰为上限）
        await unit.ApplyDefenseDamageAsync(8);
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        // 上限读取面为只读（现算查询：无副作用、无发射）
        recorder.Clear();
        Assert.Equal(8, unit.GetEffectiveDefenseCap());
        Assert.Empty(recorder.Updates);
    }

    [Fact]
    public async Task Cap_Damage_Does_Not_Reduce_Cap_And_Revoke_Falls_With_Loss()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, defense: 5);
        using var recorder = new UpdateRecorder(engine);
        await unit.Modifiers.RequestRerunAsync();
        var source = new object();
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, source)); // 上限 7

        // 伤害扣减只扣当前、不减上限：受 3 伤 → 4（上限经读取面直接断言仍为 7）
        await unit.ApplyDefenseDamageAsync(3);
        Assert.Equal(4, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(7, unit.GetEffectiveDefenseCap());

        // 撤销（镜像行为）：上限回落（7→5）、当前保持 loss 不变（loss＝3）→ 当前＝新上限−loss＝2
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(5, unit.GetEffectiveDefenseCap()); // 上限回落（直接断言）
        Assert.Equal(2, unit.GetData<UnitStateData>().Defense); // 表现位同步
        Assert.Single(recorder.Updates);
    }

    // ---------- ③ 撤销/到期的 ≤0 死亡边界（对局内统一死亡衔接） ----------

    [Fact]
    public async Task Cap_Revoke_Below_Zero_Triggers_Unified_Death_Flow()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 防 5
        using var recorder = new UpdateRecorder(match.Engine);
        var source = new object();

        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, source)); // 上限 7
        await unit.ApplyDefenseDamageAsync(6); // 7 → 1（存活）
        Assert.Equal(1, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);

        // 撤销 +2：上限 7→5、loss＝6 保持 → 当前 −1（表现钳制 0）→ 照常进入死亡判定
        // （统一死亡流程：清位＋置毁＋修饰器注销〔死亡清理——数值整合至最终态、零新发射〕＋card.died；无攻击者归因）。
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(source);

        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1).IsEmpty()); // 清位
        var died = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDied);
        Assert.Same(unit, died.Payload![GameUpdates.PayloadCard]);
        // 「先数值变化、后死亡信号」：防御变化集中触发（1 → 钳制 0）先于 card.died 到达订阅者；
        // 死亡终态（修饰清除后）：修饰集合空、上限回落至基准 5、当前＝钳制值 0。
        Assert.Contains(GameUpdates.CardStatChanged, recorder.Types);
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(5, unit.GetEffectiveDefenseCap());
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task Cap_Expiry_Below_Zero_Triggers_Unified_Death_Flow()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 防 5

        // 期限修饰（turn.end 到期）＋受损：到期自注销＝与撤销同一路径（镜像行为）
        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Defense, 2, new object(), new ModifierExpiry(GameUpdates.TurnEnd)));
        await unit.ApplyDefenseDamageAsync(6); // 7 → 1（存活）
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);

        await match.Engine.Emit(GameUpdates.TurnEnd); // 相位到达：到期自注销 → 上限回落 → ≤0 → 死亡

        Assert.Empty(unit.Modifiers.All);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1).IsEmpty());
    }

    // ---------- ③+ 死亡冻结与死亡清理（轮 3／轮 4 口径） ----------

    [Fact]
    public async Task Freeze_PostDeath_Numeric_Operations_Are_Rejected_Or_Ineffective()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 防 5
        var source = new object();
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, source)); // 上限 7

        // 致死（受 7 伤＝上限）→ 统一死亡流程（sc → 死亡清理 → died）
        await unit.ApplyDefenseDamageAsync(7);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        using var recorder = new UpdateRecorder(match.Engine);

        // 死亡后数值面冻结：伤害/修复/挂载＝明确拒绝（fail-fast、零副作用）
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ApplyDefenseDamageAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.RepairDefenseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 1, new object())));

        // 撤销＝幂等无效（死亡清理后修饰集合已空）：无操作、零发射、无副作用
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Empty(unit.Modifiers.All);
        Assert.Empty(recorder.Updates);

        // 数值本体保留且只读（查询面）：当前 0、上限＝基准 5（清理整合后）、查询不抛错
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(5, unit.GetEffectiveDefenseCap());
    }

    [Fact]
    public async Task Death_Cleanup_Unmounts_All_Modifiers_Silently_And_Expiry_Subscription_Is_Released()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1); // 防 5

        // 两条修饰：带期限（turn.end 到期）+2、无期限 +1——上限 8
        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Defense, 2, new object(), new ModifierExpiry(GameUpdates.TurnEnd)));
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 1, new object()));
        using var recorder = new UpdateRecorder(match.Engine);

        // 致死（受 8 伤＝上限）→ sc（8→0）→ 死亡清理（注销全部修饰器：含期限订阅随销；数值整合至最终态、零新发射）→ died
        await unit.ApplyDefenseDamageAsync(8);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);

        // 终态断言（轮 4 口径）：修饰集合空；有效上限回落至基准 5；当前＝钳制后值 0
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(5, unit.GetEffectiveDefenseCap());
        Assert.Equal(0, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(0, unit.GetData<UnitStateData>().Defense);

        // 发射契约：致死 sc 照发且恰一条（清理不发射）；card.died 恰一次（无二次）
        var types = recorder.Types;
        Assert.Equal(1, types.Count(t => t == GameUpdates.CardDied));
        Assert.Equal(1, types.Count(t => t == GameUpdates.CardStatChanged));

        // 期限订阅随销：相位到达不再有任何反应（仅相位信号本身；无自注销/数值发射）
        recorder.Clear();
        await match.Engine.Emit(GameUpdates.TurnEnd);
        Assert.Equal(new[] { GameUpdates.TurnEnd }, recorder.Types);
    }

    // ---------- ④ 伤害衔接（经既有基础互伤流程真实触发） ----------

    [Fact]
    public async Task Damage_Chain_Through_Basic_Combat_Emits_StatChanged_And_Keeps_Modifiers()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2/防 5
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2); // 攻 2/防 5
        var source = new object();
        await target.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, source)); // 受击前挂修饰：上限 7
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 经既有「基础互伤」流程真实触发：互伤双方各自变化正确（target：7−2=5；attacker：5−2=3）、
        // 变化字段集合正确（[Defense]×2、目标在前）、无死亡、受击后修饰保持（伤害只动损伤量、不触碰修饰器）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(2, recorder.Updates.Count(u => u.Type == GameUpdates.CardStatChanged));
        Assert.Contains(GameUpdates.PointChanged, recorder.Types); // 收尾扣行动费（E1-25 后续：指挥走点数通用路径）
        var targetUpdate = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], target));
        var attackerUpdate = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], attacker));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(targetUpdate.Payload));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(attackerUpdate.Payload));
        Assert.Equal(5, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(3, attacker.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Single(target.Modifiers.All); // 修饰保持（受击不掉修饰）
        Assert.Same(source, target.Modifiers.All[0].Source);
        Assert.False(target.GetData<UnitStateData>().IsDestroyed);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Damage_Chain_Zero_Change_Emits_Nothing()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 0); // 攻 4/防 2（轰炸机——不反击）
        // 攻击力为 0（常量设值）：伤害 0 → 目标无变化；轰炸机不反击 → 攻击者亦无变化 → 全程零发射。
        await attacker.Modifiers.AddModifierAsync(new SetModifier(CardStatFields.Attack, 0, new object()));
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        // 伤害链零发射（无 card.stat.changed / card.died / 位置类）；仅收尾扣行动费一条（E1-25 后续）。
        // E1-39：交战存活信号与伤害无关（参战即发）——双方皆存活 ⇒ 两条（被攻击者在前、攻击者在后）。
        Assert.Equal(
            new[]
            {
                GameUpdates.UnitCombatSurvived, GameUpdates.UnitCombatSurvived,
                GameUpdates.PointChanged, GameUpdates.UnitActed, // E1-33：攻击收尾＝行动后（伤害 0＝无 card.damaged）
            },
            recorder.Types);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.False(target.GetData<UnitStateData>().IsDestroyed);
    }

    // ---------- ④ 补：直达门户操作面的机制级用例（伤害衔接的机制级对照） ----------

    [Fact]
    public async Task Damage_Chain_Portal_Level_Mutual_Apply_Reports_Both_Fields()
    {
        var engine = new LogicEngine();
        var unitA = ModifierTestKit.CreateReadyUnit(engine, defense: 5);
        var unitB = ModifierTestKit.CreateReadyUnit(engine, defense: 5);
        using var recorder = new UpdateRecorder(engine);
        await unitA.Modifiers.RequestRerunAsync();
        await unitB.Modifiers.RequestRerunAsync();

        // 直达门户（不经攻击流程）：双方各自伤害扣减 → 各自恰一条集中触发（不同卡、单字段）
        recorder.Clear();
        await unitA.ApplyDefenseDamageAsync(1);
        await unitB.ApplyDefenseDamageAsync(2);

        Assert.Equal(2, recorder.Updates.Count(u => u.Type == GameUpdates.CardStatChanged)); // E1-33：另有同数 card.damaged
        var updateA = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], unitA));
        var updateB = recorder.Updates.Single(
            u => u.Type == GameUpdates.CardStatChanged && ReferenceEquals(u.Payload![GameUpdates.PayloadCard], unitB));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(updateA.Payload));
        Assert.Equal(new[] { CardStatFields.Defense }, ModifierTestKit.ChangedFieldsOf(updateB.Payload));
        Assert.Equal(4, unitA.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(3, unitB.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }
}
