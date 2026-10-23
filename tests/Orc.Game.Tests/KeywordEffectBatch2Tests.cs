using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 词条效果化·批 2（B 档扩展）验收（「壳＋效果行为」模式——伤害改写族：伏击／重甲／免疫）：
/// ①伏击：改写行为随效果独立卸载/复装（黑盒证据——真实战斗结算）＋词条移除→撤销/复装→恢复＋死亡注销停止。
/// ②重甲：减伤随效果生灭＋参值运行期读取（改写后随动、置空＝无减伤）＋死亡注销（登记/参值保留、仅行为撤销）。
/// ③免疫：单位侧归零随效果生灭＋死亡注销＋HQ 侧改写器随动（来源＝效果实例——改签）。
/// ④运行期授予闭环：授予→行为生效、撤销→行为停止（重甲＋伏击为代表）。
/// ⑤独立构造（无装载上下文）＝不注册（防御、不抛错、加载不失败——功能不可用；观测面＝加载完成/词条完整/效果装载）。
/// 回滚路径＝通道级复用（批 0／批 1 已验——申报引用，不重做失败注入）；机制矩阵（五路径全集）复用批 1 验证。
/// </summary>
public class KeywordEffectBatch2Tests
{
    // ---------- 定义与工具 ----------

    private const string ArmorTenUnitId = "u_fe2_armor10";   // 重甲 2（攻 2 / 防 10——多轮受击探针）
    private const string ImmuneTenUnitId = "u_fe2_immune10"; // 免疫（攻 2 / 防 10——多轮受击探针）
    private const string GranteeUnitId = "u_fe2_grantee";    // 无词条（攻 6 / 防 6——运行期授予探针；攻 6＞坦克防 5：伏击条件用）

    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(ArmorTenUnitId, new CardDefinition(
            "重甲十", 1, 1, 2, 10, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Armor, 2) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ImmuneTenUnitId, new CardDefinition(
            "免疫十", 1, 1, 2, 10, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Immune) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(GranteeUnitId, new CardDefinition(
            "授予兵", 1, 1, 6, 6, unitTypes: new[] { UnitType.Infantry },
            faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    private static Match CreateMatch(MockTargeterBridge? bridge = null)
        => CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: CreateDefinitions());

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    /// <summary>效果检索（按名称——黑盒观察面：容器在列＋装载态）。</summary>
    private static Effect? FindEffect(Card card, string name)
        => card.Effects.FirstOrDefault(effect => effect.Name == name);

    /// <summary>准备一个已激活的 B 方坦克（B 前线槽位——供真实攻击结算；攻 3 / 防 5）。</summary>
    private static async Task<UnitCard> PrepareActiveTankAsync(Match match, int index)
    {
        var tank = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[1], CommandTestKit.TankId, index);
        CommandTestKit.Activate(tank);
        return tank;
    }

    /// <summary>驱动一次攻击（攻击者 → 目标）并断言成功。</summary>
    private static async Task AttackAsync(Match match, MockTargeterBridge bridge, UnitCard attacker, UnitCard target)
    {
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
    }

    // ---------- ① 伏击：行为随效果独立卸载/复装（黑盒证据——真实战斗结算） ----------

    [Fact]
    public async Task Ambush_Rewrite_Follows_Effect_Detach_And_Reattach()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0); // 攻 5 / 防 6（伏击）
        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 效果随词条装载（内嵌效果通道）：在列、装载态。
        var effect = Assert.IsType<AmbushRewriteEffect>(FindEffect(ambusher, "伏击·伤害改写"));
        Assert.True(effect.IsMounted);

        // 行为（改写成立）：脆皮攻击 → 攻击者死亡、伏击兵不受伤。
        var w1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 攻 1 / 防 2
        CommandTestKit.Activate(w1);
        await AttackAsync(match, bridge, w1, ambusher);
        Assert.True(w1.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(6, DefenseOf(ambusher)); // 不受伤（改写）

        // 独立卸载效果（词条仍在）：行为随效果消失——正常互伤（伏击兵受 1、攻击者被反击致死）。
        ambusher.RemoveEffect(effect);
        Assert.False(effect.IsMounted);
        Assert.True(ambusher.Keywords.Has(KeywordIds.Ambush)); // 词条不动（效果独立卸载）
        var w2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        CommandTestKit.Activate(w2);
        await AttackAsync(match, bridge, w2, ambusher);
        Assert.Equal(5, DefenseOf(ambusher)); // 受 1（正常互伤——未改写）
        Assert.True(w2.GetData<UnitStateData>().IsDestroyed); // 被反击致死（正常结算）

        // 复装（同一实例再装载）：注册重建——行为恢复。
        ambusher.AddEffect(effect);
        Assert.True(effect.IsMounted);
        var w3 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        CommandTestKit.Activate(w3);
        await AttackAsync(match, bridge, w3, ambusher);
        Assert.True(w3.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(5, DefenseOf(ambusher)); // 改写恢复（防御不变——若未改写会 5-1=4）
    }

    // ---------- ① 伏击：词条移除→行为撤销/复装→行为恢复 ----------

    [Fact]
    public async Task Ambush_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var ambusher = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.AmbushId, 0);
        await match.ResourceManager.AddPointsAsync(playerA, 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 词条移除（完整卸载）→ 效果卸载、行为消失（不再改写——正常互伤）。
        Assert.True(await ambusher.Keywords.RevokeAsync(KeywordIds.Ambush));
        Assert.Null(FindEffect(ambusher, "伏击·伤害改写"));
        var w1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        CommandTestKit.Activate(w1);
        await AttackAsync(match, bridge, w1, ambusher);
        Assert.Equal(5, DefenseOf(ambusher)); // 未改写（受 1 伤）

        // 再授予（复装）：新效果实例、行为恢复（改写再现——伏击兵不受伤）。
        Assert.True(await ambusher.Keywords.GrantAsync(KeywordIds.Ambush));
        var second = Assert.IsType<AmbushRewriteEffect>(FindEffect(ambusher, "伏击·伤害改写"));
        Assert.True(second.IsMounted);
        var w2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        CommandTestKit.Activate(w2);
        await AttackAsync(match, bridge, w2, ambusher);
        Assert.True(w2.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(5, DefenseOf(ambusher)); // 改写恢复（防御不变）
    }

    // ---------- ① 伏击：死亡注销（行为撤销＋登记保留） ----------

    [Fact]
    public async Task Ambush_Death_Revokes_Behavior_Keeping_Registration()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var ambusher = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.AmbushId, 1);
        Assert.NotNull(FindEffect(ambusher, "伏击·伤害改写"));

        // 致死伤害：死亡注销撤销行为（效果卸载）、登记保留（照常可读）。
        await ambusher.ApplyDefenseDamageAsync(999);
        Assert.True(ambusher.GetData<UnitStateData>().IsDestroyed);
        Assert.True(ambusher.Keywords.Has(KeywordIds.Ambush)); // 登记保留
        Assert.Empty(ambusher.Effects);                        // 行为撤销（效果卸载随动）

        // 死后不再改写（手动驱动「造成攻击伤害」——模拟对局内结算路径；同既有测试手法）。
        var probe = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var resolution = new AttackDamageResolution();
        await match.CommandManager.AttackDamageTrigger.InvokeAsync(
            match.Engine,
            new Dictionary<string, object?>
            {
                [CommandDataKeys.Attacker] = probe.Ref,
                [CommandDataKeys.Target] = ambusher.Ref,
                [CommandDataKeys.Resolution] = resolution,
            });

        Assert.False(resolution.IsRewritten); // handler 已随效果卸载撤销（双保险：装载已注销 + 存活判定）
    }

    // ---------- ② 重甲：行为随效果独立卸载/复装（真实战斗结算） ----------

    [Fact]
    public async Task Armor_Reduction_Follows_Effect_Detach_And_Reattach()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTenUnitId, 1); // 重甲 2；攻 2 / 防 10
        await match.EndTurn(); // → T2（B）
        await match.ResourceManager.AddPointsAsync(match.Players[1], 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var effect = Assert.IsType<ArmorReductionEffect>(FindEffect(armored, "重甲·减伤"));
        Assert.True(effect.IsMounted);

        // 行为（减伤 2 生效）：坦克攻 3 → 受 3-2＝1（10→9）。
        var tank1 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank1, armored);
        Assert.Equal(9, DefenseOf(armored));

        // 独立卸载效果（词条仍在）：行为随效果消失——伤害全落（3 伤：9→6）。
        armored.RemoveEffect(effect);
        Assert.False(effect.IsMounted);
        Assert.True(armored.Keywords.Has(KeywordIds.Armor));
        var tank2 = await PrepareActiveTankAsync(match, 1);
        await AttackAsync(match, bridge, tank2, armored);
        Assert.Equal(6, DefenseOf(armored)); // 3 全落（未减伤）

        // 复装：减伤恢复（3-2＝1：6→5）。
        armored.AddEffect(effect);
        Assert.True(effect.IsMounted);
        var tank3 = await PrepareActiveTankAsync(match, 2);
        await AttackAsync(match, bridge, tank3, armored);
        Assert.Equal(5, DefenseOf(armored)); // 受 1（减伤恢复）
    }

    // ---------- ② 重甲：参值运行期读取（改写后随动、置空＝无减伤） ----------

    [Fact]
    public async Task Armor_Reduction_Reads_Value_At_Settlement_Time()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTenUnitId, 1); // 重甲 2；防 10
        await match.EndTurn(); // → T2（B）
        await match.ResourceManager.AddPointsAsync(match.Players[1], 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 参值改写为 3（运行期读取——每次伤害结算时读取当前参值；非装载时快照）：坦克攻 3 → 受 3-3＝0（10 不变）。
        armored.Keywords.SetValue(KeywordIds.Armor, 3);
        var tank1 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank1, armored);
        Assert.Equal(10, DefenseOf(armored)); // 全免（若快照为 2 则受 1——可区分）

        // 参值置空（null＝无减伤效果）：坦克攻 3 → 3 全落（10→7）。
        armored.Keywords.SetValue(KeywordIds.Armor, null);
        var tank2 = await PrepareActiveTankAsync(match, 1);
        await AttackAsync(match, bridge, tank2, armored);
        Assert.Equal(7, DefenseOf(armored));
    }

    // ---------- ② 重甲：死亡注销（行为撤销＋登记/参值保留） ----------

    [Fact]
    public async Task Armor_Death_Revokes_Behavior_Keeping_Registration_And_Value()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTenUnitId, 1);
        Assert.NotNull(FindEffect(armored, "重甲·减伤"));

        await armored.ApplyDefenseDamageAsync(999); // 致死
        Assert.True(armored.GetData<UnitStateData>().IsDestroyed);
        Assert.True(armored.Keywords.Has(KeywordIds.Armor));      // 登记保留
        Assert.Equal(2, armored.Keywords.GetValue(KeywordIds.Armor)); // 参值保留
        Assert.Empty(armored.Effects);                            // 行为撤销（效果卸载随动）

        // 死后不再减伤（R11：重甲 handler 挂 combat.damage——效果卸载后注册随之撤销）。
        Assert.DoesNotContain(
            match.CommandManager.DamagePipeline.CombatDamage.Events,
            entry => entry.Name == "重甲减伤");
    }

    // ---------- ③ 免疫（单位侧）：行为随效果独立卸载/复装（真实战斗结算） ----------

    [Fact]
    public async Task Immune_Zeroing_Follows_Effect_Detach_And_Reattach()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ImmuneTenUnitId, 1); // 免疫；攻 2 / 防 10
        await match.EndTurn(); // → T2（B）
        await match.ResourceManager.AddPointsAsync(match.Players[1], 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var effect = Assert.IsType<ImmuneZeroingEffect>(FindEffect(immune, "免疫·归零"));
        Assert.True(effect.IsMounted);

        // 行为（归零生效）：坦克攻 3 → 免疫方不受伤（10 不变）。
        var tank1 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank1, immune);
        Assert.Equal(10, DefenseOf(immune));

        // 独立卸载效果（词条仍在）：行为随效果消失——伤害全落（3 伤：10→7）。
        immune.RemoveEffect(effect);
        Assert.False(effect.IsMounted);
        Assert.True(immune.Keywords.Has(KeywordIds.Immune));
        var tank2 = await PrepareActiveTankAsync(match, 1);
        await AttackAsync(match, bridge, tank2, immune);
        Assert.Equal(7, DefenseOf(immune)); // 3 全落（未归零）

        // 复装：归零恢复（7 不变）。
        immune.AddEffect(effect);
        Assert.True(effect.IsMounted);
        var tank3 = await PrepareActiveTankAsync(match, 2);
        await AttackAsync(match, bridge, tank3, immune);
        Assert.Equal(7, DefenseOf(immune)); // 归零恢复（若未恢复会 7-3=4）
    }

    // ---------- ③ 免疫（单位侧）：死亡注销（行为撤销＋登记保留） ----------

    [Fact]
    public async Task Immune_Death_Revokes_Behavior_Keeping_Registration()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ImmuneTenUnitId, 1);
        Assert.NotNull(FindEffect(immune, "免疫·归零"));

        await immune.ApplyDefenseDamageAsync(999); // 致死
        Assert.True(immune.GetData<UnitStateData>().IsDestroyed);
        Assert.True(immune.Keywords.Has(KeywordIds.Immune)); // 登记保留
        Assert.Empty(immune.Effects);                        // 行为撤销（效果卸载随动）

        // 死后不再归零（R11：单位免疫挂 damage.apply——效果卸载后注册随之撤销）。
        Assert.DoesNotContain(
            match.CommandManager.DamagePipeline.Apply.Events,
            entry => entry.Name == "免疫归零");
    }

    // ---------- ③ 免疫（HQ 侧）：改写器随授予/移除（来源＝效果实例——改签） ----------

    [Fact]
    public async Task Immune_Hq_Rewriter_Follows_Grant_And_Revoke()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // 授予（HQ 宿主即挂归零改写器——不依赖装载上下文）：效果在列、改写器挂载（来源＝效果实例）。
        Assert.True(await playerB.Hq.Keywords.GrantAsync(KeywordIds.Immune));
        var effect = Assert.IsType<ImmuneZeroingEffect>(FindEffect(playerB.Hq, "免疫·归零"));
        Assert.True(effect.IsMounted);
        var rewriter = Assert.Single(playerB.Hq.DamageRewriters);
        Assert.Same(effect, rewriter.Source); // 来源标记＝效果实例（原组件实例改签）

        // 归零生效：伤害性扣减归零（HP 不变）。
        var hpBefore = playerB.HqHealth;
        await playerB.Hq.ApplyDamageAsync(3);
        Assert.Equal(hpBefore, playerB.HqHealth);

        // 移除（走移除路径）：改写器随之撤下（同族承载闭环）；其后伤害照常。
        Assert.True(await playerB.Hq.Keywords.RevokeAsync(KeywordIds.Immune));
        Assert.Empty(playerB.Hq.Effects);
        Assert.Empty(playerB.Hq.DamageRewriters);
        await playerB.Hq.ApplyDamageAsync(3);
        Assert.Equal(hpBefore - 3, playerB.HqHealth);
    }

    // ---------- ④ 运行期授予闭环：授予→行为生效、撤销→行为停止（重甲＋伏击为代表） ----------

    [Fact]
    public async Task Runtime_Grant_Revoke_Closure_Follows_Through_Armor_And_Ambush()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var grantee = await CommandTestKit.PrepareOnSupportAsync(match, playerA, GranteeUnitId, 1); // 无词条；攻 6 / 防 6
        await match.EndTurn(); // → T2（B）
        await match.ResourceManager.AddPointsAsync(match.Players[1], 10);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 重甲：运行期授予 2 → 行为生效（坦克攻 3 → 受 1：6→5）。
        Assert.True(await grantee.Keywords.GrantAsync(KeywordIds.Armor, 2));
        var armorEffect = Assert.IsType<ArmorReductionEffect>(FindEffect(grantee, "重甲·减伤"));
        Assert.True(armorEffect.IsMounted);
        var tank1 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank1, grantee);
        Assert.Equal(5, DefenseOf(grantee)); // 受 1（减伤生效）

        // 重甲：运行期撤销 → 行为停止（坦克攻 3 → 3 全落：5→2）。
        Assert.True(await grantee.Keywords.RevokeAsync(KeywordIds.Armor));
        Assert.Null(FindEffect(grantee, "重甲·减伤"));
        var tank2 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank2, grantee);
        Assert.Equal(2, DefenseOf(grantee)); // 3 全落（减伤停止）

        // 伏击：运行期授予 → 行为生效（坦克攻 3：条件 6＞5 命中 → 改写——坦克死亡、grantee 不受伤）。
        Assert.True(await grantee.Keywords.GrantAsync(KeywordIds.Ambush));
        var ambushEffect = Assert.IsType<AmbushRewriteEffect>(FindEffect(grantee, "伏击·伤害改写"));
        Assert.True(ambushEffect.IsMounted);
        var tank3 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank3, grantee);
        Assert.True(tank3.GetData<UnitStateData>().IsDestroyed);
        Assert.Equal(2, DefenseOf(grantee)); // 不受伤（改写）

        // 伏击：运行期撤销 → 行为停止（坦克攻 3 → 正常互伤：grantee 受 3 → 死亡）。
        Assert.True(await grantee.Keywords.RevokeAsync(KeywordIds.Ambush));
        Assert.Null(FindEffect(grantee, "伏击·伤害改写"));
        var tank4 = await PrepareActiveTankAsync(match, 0);
        await AttackAsync(match, bridge, tank4, grantee);
        Assert.True(grantee.GetData<UnitStateData>().IsDestroyed); // 正常互伤致死（若改写仍在则不受伤——可区分）
        Assert.True(tank4.GetData<UnitStateData>().IsDestroyed);   // 被反击致死（正常结算）
    }

    // ---------- ⑤ 独立构造（无装载上下文）＝不注册（防御、不抛错、加载不失败） ----------

    [Fact]
    public async Task Detached_Load_Without_Context_Skips_Registration_And_Loads()
    {
        // 独立构造卡（不经对局装配链——无词条装载上下文注入）：加载不失败、词条完整、效果装载完成；
        // 「不注册」由结构承载（上下文不可达＝跳过注册——功能不可用、加载不失败；无注册可观测面）。
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("u_fe2_offline", new CardDefinition(
            "离线混词条兵", 1, 1, 5, 6, unitTypes: new[] { UnitType.Infantry },
            keywords: new[]
            {
                new KeywordDeclaration(KeywordIds.Ambush),
                new KeywordDeclaration(KeywordIds.Armor, 2),
                new KeywordDeclaration(KeywordIds.Immune),
            },
            faction: Faction.Germany, rarity: Rarity.Standard));

        var match = CreateMatch();
        await match.Initialize();
        var offline = library.Instantiate("u_fe2_offline");

        await offline.LoadAsync(match.Players[0]); // 独立构造＝无上下文：不抛错（加载不失败）

        Assert.True(offline.Keywords.Has(KeywordIds.Ambush));
        Assert.True(offline.Keywords.Has(KeywordIds.Immune));
        Assert.Equal(2, offline.Keywords.GetValue(KeywordIds.Armor));
        var ambush = Assert.IsType<AmbushRewriteEffect>(FindEffect(offline, "伏击·伤害改写"));
        Assert.True(ambush.IsMounted); // 装载完成（无半态）
        Assert.NotNull(FindEffect(offline, "重甲·减伤"));
        Assert.NotNull(FindEffect(offline, "免疫·归零"));
    }
}
