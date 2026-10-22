using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 词条效果化·批 1（A 档试点）验收（「壳＋效果行为」模式）：
/// ①模式样板（动员）：『回合开始+1/+1』与『受伤失去』由效果承载——行为等价全链＋效果随词条生灭
///   （装载/卸载/复装/死亡注销）＋行为可证由效果承载（独立卸载/复装效果——黑盒证据）。
/// ②推广词条：压制（状态管理迁效果——参值改写随动/移除复装/死亡注销）、情报（触发结构迁效果——
///   被使用时触发/参值随动与缺省/非本卡不触发/移除后不触发/复装/运行时授予随动/死亡注销）。
/// ③抑制（零改动）与亡计（核对已符合）由既有测试与申报承载；回滚路径＝通道级复用
///   （批 0「装载失败→词条授予整体回滚」已验——申报引用，不重做失败注入）。
/// 批 4（数据化）适配：动员二效果形态由 C# 效果迁「数据壳＋行为引用」（<see cref="DynamicPassiveEffect"/>——
/// 类型断言随形态调整、与行为断言并存；4a 口径）；行为等价断言（累积/失去/随动/幂等/死亡注销）保持；
/// 机制层专项（库装载/绑定核验/运行时失败回滚/独立构造/端到端）见 <see cref="KeywordEffectBatch4Tests"/>。
/// </summary>
[Collection("IntelligenceStaticSerial")]
public class KeywordEffectBatch1Tests
{
    // ---------- 定义与工具 ----------

    private const string MobilizeUnitId = "u_fe1_mobilize";
    private const string IntelUnitId = "u_fe1_intel";

    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(MobilizeUnitId, new CardDefinition(
            "动员兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Mobilize) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(IntelUnitId, new CardDefinition(
            "情报兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Intelligence, 2) }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    private static Match CreateMatch() => CommandTestKit.CreateCommandMatch(extraDefinitions: CreateDefinitions());

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    /// <summary>效果检索（按名称——黑盒观察面：容器在列＋装载态）。</summary>
    private static Effect? FindEffect(UnitCard unit, string name)
        => unit.Effects.FirstOrDefault(effect => effect.Name == name);

    // ---------- ① 模式样板（动员）：装载＋行为等价 ----------

    [Fact]
    public async Task Mobilize_Effects_Load_On_Grant_And_Accrue_At_Owner_Turn_Start()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        // 效果随词条装载（内嵌效果通道——批 4 数据化：两个数据效果在列、均处装载态）。
        var accrual = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, "动员·回合累积"));
        Assert.True(accrual.IsMounted);
        var loss = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, "动员·受伤失去"));
        Assert.True(loss.IsMounted);

        // 行为等价：友方回合开始 +1/+1（累积）；敌方回合开始不加。
        Assert.Equal(2, AttackOf(unit));
        await match.EndTurn(); // → T2（B）：敌方回合开始不加
        Assert.Equal(2, AttackOf(unit));
        await match.EndTurn(); // → T3（A）：友方回合开始 → +1/+1
        Assert.Equal(3, AttackOf(unit));
        Assert.Equal(6, DefenseOf(unit));
        await match.EndTurn(); // → T4
        await match.EndTurn(); // → T5（A）：继续累积
        Assert.Equal(4, AttackOf(unit));
        Assert.Equal(7, DefenseOf(unit));
    }

    // ---------- ① 模式样板（动员）：受伤失去（信号承载）＋效果卸载 ----------

    [Fact]
    public async Task Mobilize_Loss_On_Damage_Through_Signal_Keeps_Accrued_Bonus()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        await match.EndTurn();
        await match.EndTurn(); // → T3：攻 3 / 防 6（既得 +1/+1）
        Assert.Equal(3, AttackOf(unit));

        // 受到实际伤害（净伤害 2＞0）→ 经 card.damaged 信号失去动员（词条移除链）；既得保留、不撤销。
        await unit.ApplyDefenseDamageAsync(2);
        Assert.False(unit.Keywords.Has(KeywordIds.Mobilize));
        Assert.Equal(3, AttackOf(unit));  // 既得 +1 保留
        Assert.Equal(4, DefenseOf(unit)); // 6-2

        // 效果随词条移除卸载（行为随词条生灭）——两个动员效果均不在列。
        Assert.Null(FindEffect(unit, "动员·回合累积"));
        Assert.Null(FindEffect(unit, "动员·受伤失去"));

        // 失去后不再累积（下一友方回合不 +1/+1）。
        await match.EndTurn(); // → T4
        await match.EndTurn(); // → T5（A）
        Assert.Equal(3, AttackOf(unit));

        // 失去幂等（#⑩）：失去后再受伤——无重复失去、无副作用。
        await unit.ApplyDefenseDamageAsync(1);
        Assert.False(unit.Keywords.Has(KeywordIds.Mobilize));
        Assert.Equal(3, AttackOf(unit));
        Assert.Equal(3, DefenseOf(unit)); // 4-1
    }

    // ---------- ① 模式样板（动员）：未在场跳过（边界对齐——无位置不获得） ----------

    [Fact]
    public async Task Mobilize_Not_On_Field_Skips_Accrual()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, MobilizeUnitId); // 未上场（无位置）

        await match.EndTurn();
        await match.EndTurn(); // → T3（A）：卡未在场——回合开始不获得（若错误获得将积压、上场后可见）

        // 上场（加入路径）：上场时无积压加成（攻 2 基础——未在场期间未获得）；其后友方回合开始起获得。
        await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.GetSupportLine(playerA).GetInsertionGaps()[1]);
        Assert.Equal(2, AttackOf(unit)); // 未在场期间未获得（无积压）
        await match.EndTurn(); // → T4（B）：敌方回合不加
        await match.EndTurn(); // → T5（A）：+1
        Assert.Equal(3, AttackOf(unit));
    }

    // ---------- ① 模式样板（动员）：行为可证由效果承载（独立卸载/复装效果） ----------

    [Fact]
    public async Task Mobilize_Behavior_Follows_Effect_Detach_And_Reattach_Independently()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        var accrual = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, "动员·回合累积"));

        // 独立卸载效果（词条仍在）：行为随效果消失——「行为由效果承载」的黑盒证据（订阅随效果卸载撤销）。
        unit.RemoveEffect(accrual);
        Assert.False(accrual.IsMounted);
        Assert.True(unit.Keywords.Has(KeywordIds.Mobilize)); // 词条不动（效果独立卸载）

        await match.EndTurn(); // → T2（B）
        await match.EndTurn(); // → T3（A）：无订阅——不加成
        Assert.Equal(2, AttackOf(unit));

        // 复装（同一实例再装载）：订阅重建——行为恢复。
        unit.AddEffect(accrual);
        Assert.True(accrual.IsMounted);
        await match.EndTurn(); // → T4（B）：敌方回合不触发
        await match.EndTurn(); // → T5（A）：+1
        Assert.Equal(3, AttackOf(unit));

        // 复装后再卸载（二轮生灭仍自动撤销——幂等/无残留）。
        unit.RemoveEffect(accrual);
        Assert.False(accrual.IsMounted);
        await match.EndTurn(); // → T6（B）
        await match.EndTurn(); // → T7（A）
        Assert.Equal(3, AttackOf(unit)); // 不再加成（第二轮撤订）
    }

    // ---------- ① 模式样板（动员）：词条移除→复装（既得保留＋继续累积） ----------

    [Fact]
    public async Task Mobilize_Revoke_Then_ReGrant_Restores_Accrual_Keeping_Bonus()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        await match.EndTurn();
        await match.EndTurn(); // → T3：攻 3 / 防 6（既得 +1/+1）
        var first = FindEffect(unit, "动员·回合累积");
        Assert.NotNull(first);

        // 词条移除（完整卸载）→ 效果卸载、行为消失；既得保留（不被清理）。
        Assert.True(await unit.Keywords.RevokeAsync(KeywordIds.Mobilize));
        Assert.Null(FindEffect(unit, "动员·回合累积"));
        Assert.Equal(3, AttackOf(unit));

        // 再授予（复装）：效果复装（组件重建 → 新效果实例）＋此后继续累积（既得不被清除）。
        Assert.True(await unit.Keywords.GrantAsync(KeywordIds.Mobilize));
        var second = FindEffect(unit, "动员·回合累积");
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second!.IsMounted);

        await match.EndTurn(); // → T4（B）
        await match.EndTurn(); // → T5（A）：继续累积（3+1 / 6+1）
        Assert.Equal(4, AttackOf(unit));
        Assert.Equal(7, DefenseOf(unit));
    }

    // ---------- ① 模式样板（动员）：死亡注销（行为撤销＋登记保留——#⑬核对形态） ----------

    [Fact]
    public async Task Mobilize_Death_Registration_Kept_And_Behavior_Revoked()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        // 致死伤害：统一死亡衔接先于 card.damaged 发射（跑链内）——词条死亡注销撤销行为（效果卸载）、
        // 登记保留（照常可读）。此为迁移形态核对（#⑬；现状差异见批 1 实现记录与汇报「差异申报」）。
        await unit.ApplyDefenseDamageAsync(999);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(unit.Keywords.Has(KeywordIds.Mobilize)); // 登记保留（死亡注销口径）
        Assert.Empty(unit.Effects);                          // 行为撤销（效果卸载随动）
    }

    // ---------- ② 推广（压制）：状态管理迁效果——参值改写随动 ----------

    [Fact]
    public async Task Suppression_Decrement_Follows_Rewritten_Value()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 施加（T1、A 回合）→ skip 判定；重施加核对（#⑬）：幂等——无操作、参值保持（不重置/不叠加）。
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.False(await SuppressRules.ApplyAsync(unit));
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed));

        // 参值改写为 3（运行期读取——改写后行为随动；非构造期捕获）。
        unit.Keywords.SetValue(KeywordIds.Suppressed, 3);

        await match.EndTurn(); // → T2：T1 结束（skip 消费、不递减）
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(3, unit.Keywords.GetValue(KeywordIds.Suppressed));

        await match.EndTurn(); // → T3（A）：受限
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));

        await match.EndTurn(); // → T4：T3 结束 → 3→2（随改写值递减）
        Assert.Equal(2, unit.Keywords.GetValue(KeywordIds.Suppressed));

        await match.EndTurn(); // → T5（A）
        await match.EndTurn(); // → T6：T5 结束 → 2→1
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed));

        await match.EndTurn(); // → T7（A）
        await match.EndTurn(); // → T8：T7 结束 → 1→0 → 解除
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
    }

    // ---------- ② 推广（压制）：效果随施加/移除/复装＋生命周期恢复 ----------

    [Fact]
    public async Task Suppression_Effect_Follows_Grant_Revoke_And_Reapply()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 施加（运行时授予）→ 效果装载（在列、装载态）。
        Assert.True(await SuppressRules.ApplyAsync(unit));
        var effect = Assert.IsType<SuppressionLifecycleEffect>(FindEffect(unit, "被压制·生命周期"));
        Assert.True(effect.IsMounted);

        // 移除（完整卸载）→ 效果卸载；再施加（复装）→ 效果复装（新建实例——skip 重新判定）。
        Assert.True(await unit.Keywords.RevokeAsync(KeywordIds.Suppressed));
        Assert.Null(FindEffect(unit, "被压制·生命周期"));
        Assert.True(await SuppressRules.ApplyAsync(unit));
        var second = Assert.IsType<SuppressionLifecycleEffect>(FindEffect(unit, "被压制·生命周期"));
        Assert.NotSame(effect, second);
        Assert.True(second.IsMounted);

        // 复装后的生命周期照常自解除（二轮生灭）：T1 结束（skip 消费）→ 值保持 → T3 结束 → 解除。
        await match.EndTurn(); // → T2
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed));
        await match.EndTurn(); // → T3（A）：受限
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        await match.EndTurn(); // → T4：T3 结束 → 1→0 → 解除
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
    }

    // ---------- ② 推广（压制）：死亡注销（行为撤销＋登记/参值保留） ----------

    [Fact]
    public async Task Suppression_Death_Revokes_Behavior_Keeping_Registration()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        Assert.True(await SuppressRules.ApplyAsync(unit));
        await unit.ApplyDefenseDamageAsync(999); // 致死

        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));          // 登记保留
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed)); // 参值保留
        Assert.Empty(unit.Effects);                                     // 行为撤销（效果卸载）
    }

    // ---------- ② 推广（情报）：触发结构迁效果——被使用时触发＋参值随动/缺省 ----------

    [Fact]
    public async Task Intelligence_Trigger_Follows_Value_And_Effect_Attachment()
    {
        var captured = new List<(Card Card, int Amount)>();
        var original = IntelligenceRules.PendingRevealHandler;
        try
        {
            // 桩：验证触发结构与调用点（「明牌 X 张」执行面待接线——G18；执行面契约保持）。
            IntelligenceRules.PendingRevealHandler = (card, amount, _, _) =>
            {
                captured.Add((card, amount));
                return Task.CompletedTask;
            };

            var match = CreateMatch();
            await match.Initialize();
            var playerA = match.Players[0];
            var intel = await CommandTestKit.InstantiateLoadedAsync(match, playerA, IntelUnitId, toHand: true);

            // 效果装载（触发结构就位——在列、装载态）。
            var effect = Assert.IsType<IntelligenceTriggerEffect>(FindEffect(intel, "情报·被使用时触发"));
            Assert.True(effect.IsMounted);

            // 本卡被使用（card.played 时点）→ 触发（参值 2）。
            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
            var hit = Assert.Single(captured);
            Assert.Same(intel, hit.Card);
            Assert.Equal(2, hit.Amount);

            // 参值改写后触发随动（运行期读取——改写情报 X）。
            intel.Keywords.SetValue(KeywordIds.Intelligence, 3);
            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
            Assert.Equal(2, captured.Count);
            Assert.Equal(3, captured[^1].Amount);

            // 参值置空（缺省 0 留痕口径保持——#⑩边界）。
            intel.Keywords.SetValue(KeywordIds.Intelligence, null);
            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
            Assert.Equal(3, captured.Count);
            Assert.Equal(0, captured[^1].Amount);

            // 非本卡被使用：不触发（实例级）。
            var other = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);
            await GameUpdates.EmitCardPlayed(match.Engine, other, playerA);
            Assert.Equal(3, captured.Count);

            // 词条移除后不触发＋效果卸载。
            Assert.True(await intel.Keywords.RevokeAsync(KeywordIds.Intelligence));
            Assert.Null(FindEffect(intel, "情报·被使用时触发"));
            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
            Assert.Equal(3, captured.Count);

            // 复装（运行时再授予）→ 效果装载 → 触发随动恢复（授予参值 1）。
            Assert.True(await intel.Keywords.GrantAsync(KeywordIds.Intelligence, 1));
            Assert.NotNull(FindEffect(intel, "情报·被使用时触发"));
            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA);
            Assert.Equal(4, captured.Count);
            Assert.Equal(1, captured[^1].Amount);
        }
        finally
        {
            IntelligenceRules.PendingRevealHandler = original; // 恢复默认（隔离）
        }
    }

    // ---------- ② 推广（情报）：死亡注销（行为撤销＋登记/参值保留） ----------

    [Fact]
    public async Task Intelligence_Death_Revokes_Behavior_Keeping_Registration()
    {
        var captured = new List<(Card Card, int Amount)>();
        var original = IntelligenceRules.PendingRevealHandler;
        try
        {
            IntelligenceRules.PendingRevealHandler = (card, amount, _, _) =>
            {
                captured.Add((card, amount));
                return Task.CompletedTask;
            };

            var match = CreateMatch();
            await match.Initialize();
            var playerA = match.Players[0];
            var intel = await CommandTestKit.PrepareOnSupportAsync(match, playerA, IntelUnitId, 1);

            await intel.ApplyDefenseDamageAsync(999); // 致死

            Assert.True(intel.GetData<UnitStateData>().IsDestroyed);
            Assert.True(intel.Keywords.Has(KeywordIds.Intelligence));          // 登记保留
            Assert.Equal(2, intel.Keywords.GetValue(KeywordIds.Intelligence)); // 参值保留
            Assert.Empty(intel.Effects);                                       // 行为撤销（效果卸载）

            await GameUpdates.EmitCardPlayed(match.Engine, intel, playerA); // 直接发信号（行为已撤销）
            Assert.Empty(captured);                                         // 不再触发
        }
        finally
        {
            IntelligenceRules.PendingRevealHandler = original; // 恢复默认（隔离）
        }
    }
}
