using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 第 2 批·A2 新词条验收（9 项：压制/抑制/动员/钳击/预报/重甲/情报/免疫/对战词条之行为面）：
/// ①9 词条机制与行为（各含至少一个行为场景测试；「预报」按骨架级——装配/读取、行为豁免）；②抑制专项
/// （清空处置三件套＋防御复位＋交叉断言）；③免疫专项（归零＋不限制索敌＋HQ 同族）；④参值域钳制与定义校验面。
/// 对战词条（打标/读取/随机授予组合）见 BattleKeywordTests。
/// </summary>
[Collection("IntelligenceStaticSerial")]
public class KeywordBatch2Tests
{
    // ---------- 定义与工具 ----------

    private const string PincerUnitId = "u_a2_pincer";
    private const string MobilizeUnitId = "u_a2_mobilize";
    private const string ForecastUnitId = "u_a2_forecast";
    private const string IntelUnitId = "u_a2_intel";
    private const string ArmorTwoUnitId = "u_a2_armor2";
    private const string ArmorFiveUnitId = "u_a2_armor5";

    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(PincerUnitId, new CardDefinition(
            "钳击兵", 1, 1, 2, 3, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Pincer) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(MobilizeUnitId, new CardDefinition(
            "动员兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Mobilize) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ForecastUnitId, new CardDefinition(
            "预报兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Forecast) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(IntelUnitId, new CardDefinition(
            "情报兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Intelligence, 2) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ArmorTwoUnitId, new CardDefinition(
            "重甲兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Armor, 2) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ArmorFiveUnitId, new CardDefinition(
            "超重甲兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Armor, 5) }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    private static Match CreateMatch(MockTargeterBridge? bridge = null)
        => CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: CreateDefinitions());

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    // ---------- 注册面与参值钳制 ----------

    [Fact]
    public void A2_Keyword_Ids_Are_Registered()
    {
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Suppressed));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Inhibited));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Mobilize));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Pincer));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Forecast));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Immune));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Armor));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Intelligence));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.CannotBeSuppressed));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.CannotBeInhibited));
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Guard)); // 批 4：守护注册（数据体 `guard` 映射同单落地）
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Shock)); // 批 5：冲击注册（数据体 `shock` 映射同单落地）
        // A4 受控变更（旧→新：14 → 15）：注册清单新增一枚「亡计」（A4；注册清单＝对外契约基线）。
        // S1 受控变更（旧→新：15 → 16）：注册清单新增一枚「老兵」（S1；标记型——老兵读取面承载）。
        // S2 受控变更（旧→新：16 → 17）：注册清单新增一枚「隐蔽」（S2；标记型——隐蔽读取面/豁免/揭示承载）。
        // 批 4 受控变更（旧→新：17 → 18）：注册清单新增一枚「守护」（批 4；标记型——守护维护链读点承载；打对战词条标）。
        // 批 5 受控变更（旧→新：18 → 19）：注册清单新增一枚「冲击」（批 5；标记型——C5 判定器条款＋攻击执行段消耗承载；打对战词条标）。
        Assert.Equal(19, KeywordIds.All.Count); // 4 既有 + 10 新增（A2）+ 1 新增（A4 亡计）+ 1 新增（S1 老兵）+ 1 新增（S2 隐蔽）+ 1 新增（批 4 守护）+ 1 新增（批 5 冲击）
    }

    [Fact]
    public async Task Armor_Value_Clamped_On_Declare_Grant_SetValue_And_Increment()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 定义声明「重甲5」→ 值封顶 3（词条层校验/封顶——写入受上限约束）。
        var declared = await CommandTestKit.InstantiateLoadedAsync(match, player, ArmorFiveUnitId);
        Assert.True(declared.Keywords.Has(KeywordIds.Armor));
        Assert.Equal(3, declared.Keywords.GetValue(KeywordIds.Armor));

        // 运行时授予负值 → 钳制为 0（下限 0）。
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        Assert.True(await unit.Keywords.GrantAsync(KeywordIds.Armor, -1));
        Assert.Equal(0, unit.Keywords.GetValue(KeywordIds.Armor));
        Assert.True(await unit.Keywords.RevokeAsync(KeywordIds.Armor));

        // 「+1 重甲」：无重甲＝以 1 授予（当前视为 0）；重复叠加即递增；值封顶 3。
        await ArmorRules.AddArmorAsync(unit, 1);
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Armor));
        await ArmorRules.AddArmorAsync(unit, 1);
        Assert.Equal(2, unit.Keywords.GetValue(KeywordIds.Armor));
        await ArmorRules.AddArmorAsync(unit, 1);
        Assert.Equal(3, unit.Keywords.GetValue(KeywordIds.Armor));
        await ArmorRules.AddArmorAsync(unit, 1);
        Assert.Equal(3, unit.Keywords.GetValue(KeywordIds.Armor)); // 封顶

        // 参值改写口同样受钳制（增改均受上限约束）。
        unit.Keywords.SetValue(KeywordIds.Armor, 9);
        Assert.Equal(3, unit.Keywords.GetValue(KeywordIds.Armor));
        unit.Keywords.SetValue(KeywordIds.Armor, -5);
        Assert.Equal(0, unit.Keywords.GetValue(KeywordIds.Armor));
    }

    // ---------- 压制（被压制） ----------

    [Fact]
    public async Task Suppress_ScenarioA_Applied_In_Owner_Turn_Blocks_Then_Lifts_At_Next_Owner_Turn_End()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);

        // 我方回合（T1）中施加：施加即时受限（两个动作均被阻断）。
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed)); // 剩余拥有者回合数（默认 1）
        Assert.True(SuppressRules.IsSuppressed(unit));
        var availability = match.CommandManager.GetCommandAvailability(unit);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Move.BlockReason);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Attack.BlockReason);

        // T1 结束（拥有者回合结束）＝「下一个回合」不含当前回合：不递减。
        await match.EndTurn(); // → T2（B）
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed));

        // T3（A 的下一回合）：受限（不能移动或攻击——行动合法性消费面读标记）。
        await match.EndTurn(); // → T3（A）
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(CommandBlockReason.Suppressed, match.CommandManager.GetCommandAvailability(unit).Move.BlockReason);

        // T3 结束（「施加后首先到来的拥有者回合」结束）＝解除（走标记移除路径）。
        await match.EndTurn(); // → T4（B）
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
    }

    [Fact]
    public async Task Suppress_ScenarioB_Applied_In_Enemy_Turn_Lifts_At_Next_Owner_Turn_End()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        await match.EndTurn(); // → T2（B）

        // 敌方回合中施加：我方下一个回合（T3）受限；T3 结束时解除。
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));

        await match.EndTurn(); // → T3（A）：受限
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(CommandBlockReason.Suppressed, match.CommandManager.GetCommandAvailability(unit).Attack.BlockReason);

        await match.EndTurn(); // → T4（B）：T3 结束＝解除
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
    }

    [Fact]
    public async Task Suppress_Extend_Adds_One_More_Owner_Turn()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 「额外压制一回」＝剩余拥有者回合数 +1（延长路径承载）。
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.True(await SuppressRules.ExtendAsync(unit));
        Assert.Equal(2, unit.Keywords.GetValue(KeywordIds.Suppressed));

        await match.EndTurn(); // → T2：T1 结束（skip 消费、不递减）
        await match.EndTurn(); // → T3（A）：受限
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));

        await match.EndTurn(); // → T4：T3 结束 → 2→1（仍被压制）
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.Equal(1, unit.Keywords.GetValue(KeywordIds.Suppressed));

        await match.EndTurn(); // → T5（A）：受限
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));

        await match.EndTurn(); // → T6：T5 结束 → 1→0 → 解除
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));

        // 对无压制标记者延长＝无操作（false、零副作用）。
        Assert.False(await SuppressRules.ExtendAsync(unit));
    }

    [Fact]
    public async Task Suppress_Rejected_By_CannotBeSuppressed_And_Applied_After_Clear()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 施加前资格判定（对位防护标记）：无法被压制 → 拒绝、无副作用（拒绝而非部分执行）。
        await unit.Keywords.GrantAsync(KeywordIds.CannotBeSuppressed);
        Assert.False(await SuppressRules.ApplyAsync(unit));
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.False(SuppressRules.IsSuppressed(unit));

        // 防护经移除路径清除后＝正常施加（正反两态）。
        Assert.True(await unit.Keywords.RevokeAsync(KeywordIds.CannotBeSuppressed));
        Assert.True(await SuppressRules.ApplyAsync(unit));
        Assert.True(unit.Keywords.Has(KeywordIds.Suppressed));
    }

    // ---------- 抑制（清空处置三件套＋防御复位） ----------

    [Fact]
    public async Task Inhibit_Clears_Keywords_Modifiers_Effects_And_Resets_Defense()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5

        // ① 词条准备：奋战（额外词条）＋被压制（其它施加物）——先施加压制（无防护时成功）；再补防护标记（并存备查）。
        await unit.Keywords.GrantAsync(KeywordIds.Fury);
        Assert.True(await SuppressRules.ApplyAsync(unit)); // 「被压制」标记
        await unit.Keywords.GrantAsync(KeywordIds.CannotBeSuppressed); // 防护标记（落入清除范围）

        // ② 修饰器准备：+2 防御、+1 攻击（不同来源）。
        var defenseSource = new object();
        var attackSource = new object();
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 2, defenseSource));
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 1, attackSource));
        Assert.Equal(7, DefenseOf(unit)); // 5+2（上限 7）
        Assert.Equal(3, AttackOf(unit));  // 2+1

        // ③ 损伤：受伤 4（上限 7 → 当前 3）——复位条款的触发条件（清空后上限＞当前）。
        await unit.ApplyDefenseDamageAsync(4);
        Assert.Equal(3, DefenseOf(unit));

        // ④ 效果准备：受控效果（装载留痕＋挂 +3 攻击）。
        TrackingEffect.MountedCount = 0;
        TrackingEffect.UnmountedCount = 0;
        var effect = new TrackingEffect();
        unit.AddEffect(effect);
        Assert.Equal(1, TrackingEffect.MountedCount);
        Assert.Equal(6, AttackOf(unit)); // 2+1+3

        // ⑤ 施加抑制（清空处置全链）。
        Assert.True(await InhibitRules.ApplyAsync(unit));

        // 断言 1：词条仅剩「被抑制」（字面全量——含其它施加物与防护标记）。
        Assert.True(unit.Keywords.Has(KeywordIds.Inhibited));
        Assert.True(InhibitRules.IsInhibited(unit));
        Assert.False(unit.Keywords.Has(KeywordIds.Fury));
        Assert.False(unit.Keywords.Has(KeywordIds.CannotBeSuppressed));
        Assert.False(unit.Keywords.Has(KeywordIds.Suppressed));
        Assert.False(SuppressRules.IsSuppressed(unit));

        // 断言 2：修饰器全清（两来源均不在）。
        Assert.DoesNotContain(
            unit.Modifiers.All,
            modifier => ReferenceEquals(modifier.Source, defenseSource) || ReferenceEquals(modifier.Source, attackSource));

        // 断言 3：效果清除（卸载链走完、容器空）。
        Assert.Empty(unit.Effects);
        Assert.Equal(1, TrackingEffect.UnmountedCount);

        // 断言 4：抑制只重置上限（清修饰器后上限 5）；损伤量不碰（4）→ 当前值 5−4=1（不回血）。
        Assert.Equal(5, unit.GetEffectiveDefenseCap());
        Assert.Equal(1, DefenseOf(unit));
        Assert.Equal(4, unit.GetData<UnitStateData>().DefenseLoss);

        // 断言 5：攻击力＝清空修饰器后的自然回落。
        Assert.Equal(2, AttackOf(unit));
    }

    [Fact]
    public async Task Inhibit_Rejected_When_CannotBeInhibited_No_Side_Effects()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 施加前资格判定：无法被抑制 → 拒绝（拒绝而非部分执行——其余词条/修饰器不受影响）。
        await unit.Keywords.GrantAsync(KeywordIds.CannotBeInhibited);
        await unit.Keywords.GrantAsync(KeywordIds.Fury);
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 1, new object()));

        Assert.False(await InhibitRules.ApplyAsync(unit));
        Assert.False(unit.Keywords.Has(KeywordIds.Inhibited));
        Assert.True(unit.Keywords.Has(KeywordIds.Fury));          // 未清空（零副作用）
        Assert.True(unit.Keywords.Has(KeywordIds.CannotBeInhibited));
        Assert.Contains(unit.Modifiers.All, modifier => modifier.Field == CardStatFields.Attack);
    }

    // ---------- 动员 ----------

    [Fact]
    public async Task Mobilize_Accrues_PlusOne_PlusOne_At_Owner_Turn_Start_And_Keeps()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1); // 攻 2 / 防 5

        Assert.Equal(2, AttackOf(unit));

        await match.EndTurn(); // → T2（B）：敌方回合开始不加
        Assert.Equal(2, AttackOf(unit));

        await match.EndTurn(); // → T3（A）：友方回合开始 → +1/+1
        Assert.Equal(3, AttackOf(unit));
        Assert.Equal(6, DefenseOf(unit));

        await match.EndTurn(); // → T4
        await match.EndTurn(); // → T5（A）：继续累积（+1/+1）
        Assert.Equal(4, AttackOf(unit));
        Assert.Equal(7, DefenseOf(unit));
    }

    [Fact]
    public async Task Mobilize_Lost_On_Actual_Damage_Keeping_Accrued_Bonus()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1);

        await match.EndTurn();
        await match.EndTurn(); // → T3：攻 3 / 防 6（既得 +1/+1）
        Assert.Equal(3, AttackOf(unit));

        // 受到实际伤害（净伤害 2＞0）→ 失去动员（词条移除链）；既得保留、不撤销。
        await unit.ApplyDefenseDamageAsync(2);
        Assert.False(unit.Keywords.Has(KeywordIds.Mobilize));
        Assert.Equal(3, AttackOf(unit));  // 既得 +1 保留
        Assert.Equal(4, DefenseOf(unit)); // 6-2

        // 失去后不再累积（下一友方回合不 +1/+1）。
        await match.EndTurn(); // → T4
        await match.EndTurn(); // → T5（A）
        Assert.Equal(3, AttackOf(unit));
    }

    [Fact]
    public async Task Mobilize_Kept_When_Incoming_Damage_Zeroed_By_Immune()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var defender = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 1); // 动员兵 攻 2 / 防 5
        await defender.Keywords.GrantAsync(KeywordIds.Immune); // 运行时授予（免疫）
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.TankId, 0); // 坦克 攻 3 / 防 5

        await match.EndTurn(); // → T2（B）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, defender.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 净伤害＝0 不算「受到伤害」：动员保留、防御不变；反击照常于攻击者侧结算（坦克受 2）。
        Assert.True(defender.Keywords.Has(KeywordIds.Mobilize));
        Assert.Equal(5, DefenseOf(defender));
        Assert.Equal(3, DefenseOf(attacker));
    }

    // ---------- 钳击 ----------

    [Fact]
    public async Task Pincer_Deploy_Form_Pair_Both_Sides_Gain_Bonus()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();

        // 部署（T1）→ 部署链收尾发起同伴选择（可选交互）→ 形成一对一配对。
        var result = await match.PlayManager.PlayUnitAsync(pincer, supportLine.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Single(bridge.Begins); // 选择恰一次（单选交互）

        var pair = match.CommandManager.Pincers.Find(pincer);
        Assert.NotNull(pair);
        Assert.Same(pincer, pair!.First);
        Assert.Same(friend, pair.Second);
        Assert.True(match.CommandManager.Pincers.IsPaired(friend));

        // 双方获得钳击效果（本批示例＝修饰类 +2 攻击力）。
        Assert.Equal(4, AttackOf(friend)); // 2+2
        Assert.Equal(4, AttackOf(pincer)); // 2+2
        Assert.False(pair.IsBroken);
    }

    [Fact]
    public async Task Pincer_Deploy_Cancel_Leaves_No_Pair()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = (description, responder) => responder.Cancel(description.RequestId);

        // 放弃选择＝不形成钳击；部署照常完成（该单位仍具「钳击」词条）。
        var result = await match.PlayManager.PlayUnitAsync(pincer, supportLine.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Single(bridge.Begins);
        Assert.False(match.CommandManager.Pincers.IsPaired(pincer));
        Assert.False(match.CommandManager.Pincers.IsPaired(friend));
        Assert.Equal(2, AttackOf(friend)); // 无加成
        Assert.Same(pincer, supportLine.Members[1]); // 部署正常完成（HQ 之后一个位置）
        Assert.True(pincer.Keywords.Has(KeywordIds.Pincer)); // 词条登记不动
    }

    [Fact]
    public async Task Pincer_Member_Death_Breaks_Pair_Other_Side_Loses_Bonus()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();
        await match.PlayManager.PlayUnitAsync(pincer, supportLine.GetInsertionGaps().At(1));
        Assert.Equal(4, AttackOf(friend)); // 配对成立（+2）

        // 一方离场（死亡路径）：另一方即时失去钳击效果（按来源整组撤销）。
        await pincer.ApplyDefenseDamageAsync(999);
        Assert.True(pincer.GetData<UnitStateData>().IsDestroyed);
        Assert.False(match.CommandManager.Pincers.IsPaired(pincer));
        Assert.False(match.CommandManager.Pincers.IsPaired(friend));
        Assert.Equal(2, AttackOf(friend)); // 加成撤销（回到基础 2）
    }

    [Fact]
    public async Task Pincer_Deploy_Without_Candidates_Skips_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();

        // 场上无其它友方单位：无合法候选＝不发起选择（无可选项）、不形成、部署正常完成。
        var result = await match.PlayManager.PlayUnitAsync(pincer, supportLine.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Empty(bridge.Begins);
        Assert.False(match.CommandManager.Pincers.IsPaired(pincer));
        Assert.Contains(pincer, supportLine.Members);
    }

    [Fact]
    public async Task Pincer_Paired_Members_Are_Excluded_From_New_Candidates()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 先铺一个友方（为 first/second 部署保留后续插入点）
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();

        var first = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        await match.PlayManager.PlayUnitAsync(first, supportLine.GetInsertionGaps().At(1));
        Assert.Single(bridge.Begins); // 第一次形成（候选＝friend）

        // 第二次部署（T3——点数恢复）：其余单位均已配对（一对一占用——先到先得）→ 候选为空、不发起选择。
        await match.EndTurn(); // → T2
        await match.EndTurn(); // → T3（A，2 点）
        var second = await CommandTestKit.InstantiateLoadedAsync(match, playerA, PincerUnitId, toHand: true);
        var result = await match.PlayManager.PlayUnitAsync(second, supportLine.GetInsertionGaps().At(3));
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Single(bridge.Begins); // 未发起新选择
        Assert.False(match.CommandManager.Pincers.IsPaired(second));
        Assert.True(match.CommandManager.Pincers.IsPaired(friend)); // 原配对保持（不抢占/不重配）
    }

    [Fact]
    public async Task Pincer_Count_By_Query_Combination()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 「钳击单位数」＝既有查询面组合（友方单位遍历＋词条读口；不新增专门计数机制）。
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, PincerUnitId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, PincerUnitId, 2);

        var count = CommandTestKit.AllUnitsOf(match)
            .Count(unit => ReferenceEquals(unit.Owner, playerA) && unit.Keywords.Has(KeywordIds.Pincer));
        Assert.Equal(2, count);

        // 加入路径不形成配对（仅部署路径）——计数与是否形成配对无关。
        Assert.False(match.CommandManager.Pincers.IsPaired(CommandTestKit.AllUnitsOf(match).First()));
    }

    // ---------- 预报（骨架级） ----------

    [Fact]
    public async Task Forecast_Skeleton_Declare_Attach_And_Read()
    {
        var match = CreateMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 骨架级：标识声明/装配/读取（行为面不实现、申报待澄清——Q&A-1）。
        var card = await CommandTestKit.InstantiateLoadedAsync(match, player, ForecastUnitId);
        Assert.True(card.Keywords.Has(KeywordIds.Forecast));
        Assert.Null(card.Keywords.GetValue(KeywordIds.Forecast)); // 「仅标识」形态
        Assert.Contains(card.Keywords.Components, component => component.Keyword == KeywordIds.Forecast);
        Assert.True(KeywordRules.HasKeyword(card, KeywordIds.Forecast));
    }

    // ---------- 重甲 ----------

    [Fact]
    public async Task Armor_Reduces_Battle_Damage_Both_Directions_And_Not_Command_Damage()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTwoUnitId, 1); // 重甲 2；攻 2 / 防 5
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.TankId, 0); // 坦克 攻 3 / 防 5
        await ArmorRules.AddArmorAsync(attacker, 1); // 坦克获重甲 1（攻击者侧受反击减伤）
        Assert.Equal(2, ArmorRules.GetValue(armored));
        Assert.Equal(1, ArmorRules.GetValue(attacker));

        await match.EndTurn(); // → T2（B）
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, armored.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 目标方向：对重甲来源的伤害 -2（3-2=1）→ 5-1=4。
        Assert.Equal(4, DefenseOf(armored));
        // 攻击者方向（反击）：反击伤害 2 - 攻击者重甲 1 = 1 → 5-1=4。
        Assert.Equal(4, DefenseOf(attacker));

        // 指令伤害不减免：非对战路径（门户直调）全额扣减（重甲不拦）。
        await armored.ApplyDefenseDamageAsync(2);
        Assert.Equal(2, DefenseOf(armored));
    }

    // ---------- 情报（触发结构＋待办环节） ----------

    [Fact]
    public async Task Intelligence_Trigger_Structure_Invokes_Pending_Reveal_With_Value()
    {
        var captured = new List<(Card Card, int Amount)>();
        var original = IntelligenceRules.PendingRevealHandler;
        try
        {
            // 桩：验证触发结构与调用点（「明牌 X 张」执行面待接线——G18）。
            IntelligenceRules.PendingRevealHandler = (card, amount, _, _) =>
            {
                captured.Add((card, amount));
                return Task.CompletedTask;
            };

            var match = CreateMatch();
            await match.Initialize();
            var playerA = match.Players[0];
            var intel = await CommandTestKit.InstantiateLoadedAsync(match, playerA, IntelUnitId, toHand: true);

            // 骨架：声明/装配/读取（情报 2——参值通道）。
            Assert.True(intel.Keywords.Has(KeywordIds.Intelligence));
            Assert.Equal(2, intel.Keywords.GetValue(KeywordIds.Intelligence));

            // 触发结构：「具有情报的卡被使用时」触发（card.played 时点）。
            var result = await match.PlayManager.PlayUnitAsync(intel, match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(1));
            Assert.Equal(PlayResultStatus.Success, result.Status);

            var hit = Assert.Single(captured);
            Assert.Same(intel, hit.Card);
            Assert.Equal(2, hit.Amount);
        }
        finally
        {
            IntelligenceRules.PendingRevealHandler = original; // 恢复默认（隔离）
        }
    }

    // ---------- 免疫 ----------

    [Fact]
    public async Task Immune_As_Target_Zeroes_Damage_And_Keeps_Searchability()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        await immune.Keywords.GrantAsync(KeywordIds.Immune);
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.TankId, 0); // 攻 3 / 防 5

        await match.EndTurn(); // → T2（B）
        CommandTestKit.Activate(attacker);

        // 不限制索敌：免疫单位仍在攻击候选面（可被选为目标）。
        var availability = match.CommandManager.GetCommandAvailability(attacker);
        Assert.Contains(immune.Ref, availability.Attack.Candidates);

        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, immune.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // 归零：目标防御不变（伤害结算 handler 之前设为 0；攻击仍发生）；反击照常于攻击者侧结算（坦克受 2）。
        Assert.Equal(5, DefenseOf(immune));
        Assert.Equal(3, DefenseOf(attacker));
        Assert.False(immune.GetData<UnitStateData>().IsDestroyed);

        // 保护边界＝仅拦截「伤害」：非伤害效果照常作用（抑制对免疫单位照常生效——抑制≠伤害）。
        Assert.True(await InhibitRules.ApplyAsync(immune));
        Assert.True(immune.Keywords.Has(KeywordIds.Inhibited));
    }

    [Fact]
    public async Task Immune_As_Attacker_Zeroes_Counter_Only()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 攻 2 / 防 5
        await immune.Keywords.GrantAsync(KeywordIds.Immune);
        var victim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        CommandTestKit.Activate(immune);

        // 免疫单位主动攻击（T1=A）：其攻击照常造成伤害（免疫≠不会造成伤害）；其受到的「反击」归零。
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, immune, victim.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        Assert.Equal(3, DefenseOf(victim)); // 5-2：攻击照常
        Assert.Equal(5, DefenseOf(immune)); // 反击归零：免疫方不受伤
    }

    [Fact]
    public async Task Immune_Hq_Zeroes_Attack_Damage_And_Keeps_Targetable()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var mega = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.MegaId, 1); // 巨炮 攻 30
        await playerB.Hq.Keywords.GrantAsync(KeywordIds.Immune); // 「友方总部具有免疫」（HQ 同族承载）
        CommandTestKit.Activate(mega);

        // 不限制索敌：HQ 仍可被选为目标（候选含 HQ 实体引用）。
        var availability = match.CommandManager.GetCommandAvailability(mega);
        Assert.Contains(playerB.Hq.Ref, availability.Attack.Candidates);

        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, mega, playerB.Hq.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);

        // HQ 伤害路径归零（改写段）：30 伤害 → 0，血量不变。
        Assert.Equal(20, playerB.HqHealth);

        // 移除免疫（走移除路径）：归零改写器随之撤下（同族承载闭环）；其后伤害照常。
        Assert.True(await playerB.Hq.Keywords.RevokeAsync(KeywordIds.Immune));
        await playerB.Hq.ApplyDamageAsync(3);
        Assert.Equal(17, playerB.HqHealth);
    }

    // ---------- 效果（受控测试品） ----------

    /// <summary>受控效果：装载留痕＋挂 +3 攻击（来源＝本效果）；卸载留痕（验证清除走统一卸载链）。</summary>
    private sealed class TrackingEffect : PassiveEffect
    {
        public static int MountedCount;
        public static int UnmountedCount;

        public TrackingEffect()
            : base("受控效果")
        {
        }

        protected override void OnMount()
        {
            MountedCount++;
            if (Host is CardBase card)
            {
                card.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, this)).GetAwaiter().GetResult();
            }
        }

        protected override void OnUnmount() => UnmountedCount++;
    }
}
