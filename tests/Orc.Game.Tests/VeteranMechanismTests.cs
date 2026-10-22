using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S1（老兵机制——组件替换式升级／信号／数据映射／读取面）验收：
/// V1-V5 机制级全链（触发源经公共面手工构造、发动经「老兵触发器」——不依赖 S3）＋
/// 清空专项（损伤/修饰/词条逐项＋信号断言＋重入环）＋失败专项（预检拒绝＋执行段失败回滚）＋
/// 数据映射专项（自含内嵌数据体 JSON → 加载路径 → 端口可读＋不再留痕）＋读取面（单位级判定读口）。
/// 数据构造＝等价数据（内嵌数据体 JSON；经 <see cref="CardDataLoader"/> 真实加载路径——不依赖 outputs 外部产物）。
/// </summary>
public class VeteranMechanismTests
{
    // ---------- 卡 id（自含数据；代表性配对仿官方 12th_guards_mechanised / panzer_ib） ----------

    private const string GuardsId = "vet_guards";                    // 2/1 行动费 1；烟幕；BecomesVeteran（12th_guards_mechanised 等价）
    private const string GuardsVetId = "vet_guards_vet";             // 2/2；闪击；VeteranOf（老兵版）
    private const string PanzerId = "vet_panzer";                    // 1/2；BecomesVeteran（panzer_ib 等价）
    private const string PanzerVetId = "vet_panzer_vet";             // 2/2；VeteranOf
    private const string PurgeId = "vet_purge";                      // 3/5 行动费 2（清空专项）
    private const string PurgeVetId = "vet_purge_vet";               // 4/7；闪击＋奋战
    private const string RewardId = "vet_reward";                    // 奖励卡（加入手牌用）
    private const string OrphanId = "vet_orphan";                    // BecomesVeteran 指向未注册 id（目标缺失）
    private const string IncompleteId = "vet_incomplete";            // BecomesVeteran → 无 VeteranOf 的老兵定义（目标不完整）
    private const string IncompleteVetId = "vet_incomplete_vet";
    private const string MismatchId = "vet_mismatch";                // 来源错配（vet.VeteranOf ≠ 当前卡）
    private const string MismatchVetId = "vet_mismatch_vet";

    /// <summary>自含数据体 JSON 集 → 真实加载路径 → 定义集（每测例独立临时目录；加载后即删除——定义已构造）。</summary>
    private static IReadOnlyList<CardDefinitionEntry> LoadVeteranDefinitions()
    {
        var jsons = new[]
        {
            CardJson(GuardsId, "近卫机械化第 12 旅", "Soviet", 1, 1, 2, 1, "infantry",
                $"BecomesVeteran:{GuardsVetId}", "OnlySpawnable", "smokescreen"),
            CardJson(GuardsVetId, "近卫机械化第 12 旅", "Soviet", 1, 1, 2, 2, "infantry",
                $"VeteranOf:{GuardsId}", "blitz"),
            CardJson(PanzerId, "一号坦克 B 型", "Germany", 1, 1, 1, 2, "tank",
                $"BecomesVeteran:{PanzerVetId}", "OnlySpawnable"),
            CardJson(PanzerVetId, "一号坦克 B 型", "Germany", 1, 1, 2, 2, "tank",
                $"VeteranOf:{PanzerId}"),
            CardJson(PurgeId, "清空测试兵", "Germany", 2, 2, 3, 5, "infantry",
                $"BecomesVeteran:{PurgeVetId}"),
            CardJson(PurgeVetId, "清空测试兵", "Germany", 2, 2, 4, 7, "infantry",
                $"VeteranOf:{PurgeId}", "blitz", "fury"),
            CardJson(RewardId, "奖励兵", "Germany", 1, 1, 1, 1, "infantry"),
            CardJson(OrphanId, "孤儿兵", "Germany", 1, 1, 1, 1, "infantry",
                $"BecomesVeteran:{OrphanId}_vet"),
            CardJson(IncompleteId, "残缺兵", "Germany", 1, 1, 1, 1, "infantry",
                $"BecomesVeteran:{IncompleteVetId}"),
            CardJson(IncompleteVetId, "残缺老兵", "Germany", 1, 1, 2, 2, "infantry",
                "blitz"),
            CardJson(MismatchId, "错配兵", "Germany", 1, 1, 1, 1, "infantry",
                $"BecomesVeteran:{MismatchVetId}"),
            CardJson(MismatchVetId, "错配老兵", "Germany", 1, 1, 2, 2, "infantry",
                "VeteranOf:vet_elsewhere"),
        };

        var directory = Path.Combine(Path.GetTempPath(), "orc-veteran-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < jsons.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"card{i:D2}.card.json"), jsons[i]);
            }

            var loaded = CardDataLoader.LoadDirectory(directory);
            Assert.Empty(loaded.Failures);
            return loaded.Definitions;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CardJson(
        string id, string name, string faction, int kredits, int operationCost, int attack, int defense,
        string unitType, params string[] attributes)
    {
        var attributeList = attributes.Length == 0
            ? "[]"
            : "[" + string.Join(", ", attributes.Select(a => $"\"{a}\"")) + "]";
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "{{name}}",
          "components": [
            { "component": "typeCategory", "type": ["{{unitType}}"] },
            { "component": "factionCost", "faction": "{{faction}}", "kredits": {{kredits}} },
            { "component": "battleStats", "operationCost": {{operationCost}}, "attack": {{attack}}, "defense": {{defense}} },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "keywords", "attributes": {{attributeList}} }
          ]
        }
        """;
    }

    private static Match CreateMatch(MockTargeterBridge? bridge = null)
        => CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: LoadVeteranDefinitions());

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    // ---------- 契约冻结（触发器官名 / 标记标识 / 读取面） ----------

    [Fact]
    public void Contract_Trigger_Name_Marker_Id_Are_Frozen_And_Readable()
    {
        // 对外契约（对 S3）：触发器名＝「老兵触发器」（查证无冲突）；标记标识＝「老兵」。
        Assert.Equal("老兵触发器", VeteranRules.TriggerName);
        Assert.Equal("老兵", KeywordIds.Veteran);
        Assert.True(KeywordRegistry.IsDefined(KeywordIds.Veteran));
        Assert.Contains(KeywordIds.Veteran, KeywordIds.All);
        Assert.False(KeywordRegistry.IsBattleKeyword(KeywordIds.Veteran)); // 纯内容标记——不打对战词条标

        // 具名登记可寻址（效果预制体 injects 按名解析的注入目标面）。
        var engine = new LogicEngine();
        var unit = new UnitCard(
            engine,
            new CardDefinition("契约检查", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.True(unit.TryFindNamedTrigger(VeteranRules.TriggerName, out var trigger, out var viewType));
        Assert.Same(unit.VeteranTrigger, trigger);
        Assert.Equal(typeof(CardTriggerView), viewType);

        // 读取面降级：null/非单位卡＝false 降级、不抛错。
        Assert.False(VeteranRules.IsVeteran(null));
        var command = new OrderCard(
            engine,
            new CardDefinition("指令", 1, 0, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.False(VeteranRules.IsVeteran(command));
    }

    // ---------- V1：在场上的第三回合开始时升为老兵（机制级全链：触发源手工构造） ----------

    [Fact]
    public async Task V1_On_Field_Third_Turn_Start_Upgrades_Guards_Pair_With_Stats_And_Keywords()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsId, 0);
        Assert.Equal(1, unit.TurnsInPlay); // 在场回合数（既有读取面——入场即第 1 回合）
        Assert.True(unit.Keywords.Has(KeywordIds.SmokeScreen));
        Assert.Equal(1, DefenseOf(unit));
        Assert.False(VeteranRules.IsVeteran(unit));

        // 触发源（机制级手工构造）：监听回合开始信号——归属玩家回合且在场第 3 回合时经专属触发器发动。
        using var subscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.TurnStart
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadPlayer, out var value)
                && ReferenceEquals(value, playerA)
                && unit.TurnsInPlay == 3)
            {
                return unit.InvokeVeteranTriggerAsync(ct);
            }

            return Task.CompletedTask;
        });

        using var recorder = new UpdateRecorder(match.Engine);

        // 回合推进：1(A)→2(B)；3(A)＝在场第 2 回合（不触发）；5(A)＝在场第 3 回合（触发）。
        await match.EndTurn(); // 2
        await match.EndTurn(); // 3（A）→ 2
        Assert.Equal(2, unit.TurnsInPlay);
        Assert.False(VeteranRules.IsVeteran(unit));

        await match.EndTurn(); // 4
        await match.EndTurn(); // 5（A）→ 3 → 升级

        // 数值/词条替换结果：2/1 烟幕 → 2/2 闪击＋老兵标记（行动费保持 1）。
        Assert.Equal(3, unit.TurnsInPlay);
        Assert.True(VeteranRules.IsVeteran(unit));
        Assert.False(unit.Keywords.Has(KeywordIds.SmokeScreen));  // 旧词条全清
        Assert.True(unit.Keywords.Has(KeywordIds.Blitz));          // 新词条集授予
        Assert.Equal(2, AttackOf(unit));
        Assert.Equal(2, DefenseOf(unit));
        Assert.Equal(1, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

        // 信号：unit.upgraded 恰一次；升级时刻 card.stat.changed 恰一次（防御 1→2）；先数值后机制。
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitUpgraded
            && ReferenceEquals(u.Payload![GameUpdates.PayloadUnit], unit)));
        var types = recorder.Types.ToList();
        var statIndex = types.IndexOf(GameUpdates.CardStatChanged);
        var upgradedIndex = types.IndexOf(GameUpdates.UnitUpgraded);
        Assert.True(statIndex >= 0);
        Assert.True(upgradedIndex > statIndex);
    }

    // ---------- V1b：panzer_ib 等价对（1/2 → 2/2；无词条差异——仅数值替换） ----------

    [Fact]
    public async Task V1b_Panzer_Pair_Replaces_Stats_To_Two_Two()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, PanzerId, 0);
        Assert.Equal(1, AttackOf(unit));
        Assert.Equal(2, DefenseOf(unit));

        // 发动：经效果运行时门面（csx handler 的唯一游戏层受控入口——S3 对接路径；内部经专属触发器）。
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);
        var result = await runtime!.UpgradeAsync(unit);

        Assert.Equal(VeteranPromotionOutcome.Promoted, result.Outcome);
        Assert.Null(result.RejectionReason);
        Assert.True(result.IsPromoted);
        Assert.Equal(2, AttackOf(unit));   // 1 → 2
        Assert.Equal(2, DefenseOf(unit));  // 2 → 2（满值恒定）
        Assert.True(VeteranRules.IsVeteran(unit));
    }

    // ---------- V2：对敌方总部造成伤害时升为老兵；升级时效果（监听 unit.upgraded）执行 ----------

    [Fact]
    public async Task V2_Damage_To_Enemy_Hq_Upgrades_And_Upgrade_Listener_Adds_Card_To_Hand()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsId, 0);

        // 触发源①（升级条件——机制级手工构造）：对敌方总部造成伤害时发动升级。
        using var damageSubscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitDamageDealt
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadUnit, out var dealer)
                && ReferenceEquals(dealer, unit)
                && payload.TryGetValue(GameUpdates.PayloadCard, out var target)
                && ReferenceEquals(target, playerB.Hq))
            {
                return unit.InvokeVeteranTriggerAsync(ct);
            }

            return Task.CompletedTask;
        });

        // 触发源②（升级时效果——监听 unit.upgraded 的普通效果）：将 1 张奖励卡加入手牌（经公共面——卡牌服务）。
        using var upgradedSubscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitUpgraded
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadUnit, out var value)
                && ReferenceEquals(value, unit))
            {
                var service = MatchCardService.ResolveFor(unit);
                Assert.NotNull(service);
                return service.CreateAndPlaceToHandAsync(RewardId, playerA, ct);
            }

            return Task.CompletedTask;
        });

        var handBefore = playerA.Hand.Count;

        // 行动：本单位对敌方总部造成伤害（经公共面——效果运行时门面；带来源）。
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);
        await runtime!.DamageAsync(playerB.Hq, 1, unit);

        // 升级已发生 ＋「升级时效果」已执行（1 张奖励卡加入手牌）。
        Assert.True(VeteranRules.IsVeteran(unit));
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Contains(playerA.Hand, card => card.Name == "奖励兵");
    }

    // ---------- V3：友方单位升为老兵时，本单位获得 +2+2（unit.upgraded 跨卡监听） ----------

    [Fact]
    public async Task V3_Friendly_Upgrade_Buffs_Observer_By_Two_Two_Via_Upgraded_Listener()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var veteran = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsId, 0);
        var observer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 2/5

        var buffSource = new object();
        using var subscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitUpgraded
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadUnit, out var value)
                && value is UnitCard upgradedUnit
                && ReferenceEquals(upgradedUnit.Owner, observer.Owner) // 友方判定：监听者从单位自读归属（读取现势）
                && ReferenceEquals(upgradedUnit, veteran))
            {
                return observer.Modifiers.AddModifiersAsync(
                    new Modifier[]
                    {
                        new AddModifier(CardStatFields.Attack, 2, buffSource),
                        new AddModifier(CardStatFields.Defense, 2, buffSource),
                    },
                    ct);
            }

            return Task.CompletedTask;
        });

        Assert.Equal(2, AttackOf(observer));
        Assert.Equal(5, DefenseOf(observer));

        await veteran.InvokeVeteranTriggerAsync();

        // 观察者获得 +2+2（监听升级事件后执行修饰——监听者所见 veteran 已为终态）。
        Assert.True(VeteranRules.IsVeteran(veteran));
        Assert.Equal(4, AttackOf(observer));
        Assert.Equal(7, DefenseOf(observer));
    }

    // ---------- V4：使 1 个老兵单位获得奋战（读取面筛选升级后的单位） ----------

    [Fact]
    public async Task V4_ReadFace_Filters_Veteran_Unit_Then_Keyword_Grant()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var candidate = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsId, 0);
        var plain = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 读取面初态：无老兵单位（升级前 false）。
        var before = new[] { candidate, plain }.Where(VeteranRules.IsVeteran).ToList();
        Assert.Empty(before);

        await candidate.InvokeVeteranTriggerAsync();

        // 列表级筛选（「老兵单位」——基于单位级判定读口的组合；筛选到升级后的单位）。
        var veterans = new[] { candidate, plain }.Where(VeteranRules.IsVeteran).ToList();
        var chosen = Assert.Single(veterans);
        Assert.Same(candidate, chosen);

        // 「使 1 个老兵单位获得奋战和冲击」——机制级等价：对筛选所得老兵单位授予已实现词条「奋战」
        // （「冲击」已注册（批 5）且解析层已回补（批 4·序列③——『获得冲击』转真映射、两 grant 并列）；
        // 本测试仅演示运行时授予链的机制等价，解析层声明/授予断言见 KeywordLineDeclarationTests）。
        Assert.True(await chosen.Keywords.GrantAsync(KeywordIds.Fury));
        Assert.True(chosen.Keywords.Has(KeywordIds.Fury));
        Assert.False(plain.Keywords.Has(KeywordIds.Fury));

        // 死亡后读取面保持只读可用（登记保留——按既有词条查询面语义）。
        await chosen.ApplyDefenseDamageAsync(999);
        Assert.True(chosen.GetData<UnitStateData>().IsDestroyed);
        Assert.True(VeteranRules.IsVeteran(chosen));
    }

    // ---------- V5：对战并存活后升为老兵（战斗存活路径） ----------

    [Fact]
    public async Task V5_Combat_Survival_Upgrades_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, playerA, PanzerId, 1);        // 1/2
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0); // 1/2（互伤各 1——双方存活）

        // 触发源（机制级手工构造）：本单位对战并存活后发动升级。
        using var subscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitCombatSurvived
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadUnit, out var value)
                && ReferenceEquals(value, attacker))
            {
                return attacker.InvokeVeteranTriggerAsync(ct);
            }

            return Task.CompletedTask;
        });

        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(attacker.GetData<UnitStateData>().IsDestroyed);
        Assert.True(VeteranRules.IsVeteran(attacker));
        Assert.Equal(2, AttackOf(attacker));  // 1 → 2
        Assert.Equal(2, DefenseOf(attacker)); // 2 → 2（战斗中受 1 伤——升级清损伤＝新基准满值）
    }

    // ---------- 清空专项（损伤/修饰/词条逐项＋信号＋重入环） ----------

    [Fact]
    public async Task Clear_Special_Purges_Damage_Modifiers_Keywords_With_Signals_And_Reentry_Loop()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, PurgeId, 0); // 3/5 行动费 2
        var state = unit.GetData<UnitStateData>();

        // 预置：损伤（2）＋永久修饰（+1/+1）＋运行时授予词条（动员——含运行时授予项）。
        await unit.ApplyDefenseDamageAsync(2);
        var buffSource = new object();
        await unit.Modifiers.AddModifiersAsync(new Modifier[]
        {
            new AddModifier(CardStatFields.Attack, 1, buffSource),
            new AddModifier(CardStatFields.Defense, 1, buffSource),
        });
        await unit.Keywords.GrantAsync(KeywordIds.Mobilize);

        // 升级前置：范围外字段与操作状态快照（升级后须保持不变）。
        var definitionBefore = unit.Definition;
        var deployCostBefore = unit.Modifiers.GetEffectiveValue(CardStatFields.DeployCost);
        var typesBefore = state.UnitTypes.ToArray();
        var positionBefore = state.Line;
        var turnsBefore = unit.TurnsInPlay;
        CommandTestKit.Activate(unit); // 行动状态置位（升级不得重置）

        // 重入环（必测边界项）：升级→监听方再尝试发动→幂等零信号（不中断触发链）。
        var reentryOutcomes = new List<VeteranPromotionOutcome>();
        using var reentrySubscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitUpgraded
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadUnit, out var value)
                && ReferenceEquals(value, unit))
            {
                return ReenterAsync();
            }

            return Task.CompletedTask;

            async Task ReenterAsync()
            {
                var reentry = await unit.InvokeVeteranTriggerAsync(ct);
                reentryOutcomes.Add(reentry.Outcome);
            }
        });

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await unit.InvokeVeteranTriggerAsync();

        // 三态：成功升级。
        Assert.Equal(VeteranPromotionOutcome.Promoted, result.Outcome);

        // 清空逐项：损伤归零；修饰器全清（含永久型）；词条全清（含运行时授予）→ 授予新集；数值＝新基准满值。
        Assert.Equal(0, state.DefenseLoss);
        Assert.Empty(unit.Modifiers.All);
        Assert.False(unit.Keywords.Has(KeywordIds.Mobilize));      // 运行时授予被清
        Assert.True(VeteranRules.IsVeteran(unit));                  // 新词条集（含标记）
        Assert.Single(unit.Keywords.Components, c => c.Keyword == KeywordIds.Veteran); // 标记存在且唯一（无重复残留）
        Assert.True(unit.Keywords.Has(KeywordIds.Blitz));
        Assert.True(unit.Keywords.Has(KeywordIds.Fury));
        Assert.Equal(4, AttackOf(unit));                            // 4/7 满值（旧损伤与全部修饰一并清除）
        Assert.Equal(7, DefenseOf(unit));
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost));

        // 信号（批 1 适配——词条效果化：动员词条携带内嵌效果〔回合累积/受伤失去〕，全清时随词条卸载按
        // 内嵌效果通道既有语义发 effect.removed 恰 2 条——执行段清理步、先于收尾发射）：
        // effect.removed 恰 2 条＋card.stat.changed 恰一次（有变更）＋unit.upgraded 恰一次；
        // 先清理信号、后数值、再机制；重入零信号。
        Assert.Equal(4, recorder.Updates.Count);
        Assert.Equal(2, recorder.Updates.Count(u => u.Type == Updates.EffectRemoved));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.CardStatChanged));
        Assert.Equal(1, recorder.Updates.Count(u => u.Type == GameUpdates.UnitUpgraded));
        var types = recorder.Types.ToList();
        var statIdx = types.IndexOf(GameUpdates.CardStatChanged);
        var upgradedIdx = types.IndexOf(GameUpdates.UnitUpgraded);
        Assert.True(statIdx >= 0 && upgradedIdx > statIdx, "信号顺序＝先数值（card.stat.changed）、后机制（unit.upgraded）。");
        Assert.True(
            types.LastIndexOf(Updates.EffectRemoved) < statIdx,
            "信号顺序＝词条内嵌效果卸载（effect.removed）先于数值变化（全清先于收尾重跑）。");
        Assert.Equal(new[] { VeteranPromotionOutcome.AlreadyVeteran }, reentryOutcomes);

        // 范围外字段不变：定义引用/部署费/类型集。
        Assert.Same(definitionBefore, unit.Definition);
        Assert.Equal(deployCostBefore, unit.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Equal(typesBefore, state.UnitTypes);

        // 操作与状态保持：位置/指挥状态/在场回合数不因升级重置。
        Assert.Same(positionBefore, state.Line);
        Assert.Equal(turnsBefore, unit.TurnsInPlay);
        Assert.True(unit.GetData<CommandData>().CanMove);
        Assert.True(unit.GetData<CommandData>().CanAttack);

        // 直接重复发动：幂等无操作（零副作用、零信号）。
        var before = recorder.Updates.Count;
        var second = await unit.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionOutcome.AlreadyVeteran, second.Outcome);
        Assert.Equal(before, recorder.Updates.Count);
    }

    // ---------- 失败专项①：预检拒绝（类别化可读＋零副作用） ----------

    [Fact]
    public async Task Failure_Precheck_Rejections_Are_Categorized_With_Zero_Side_Effects()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);

        // ① 不在场（未入场——手牌中的单位）：拒绝。
        var onHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, GuardsId, toHand: true);
        recorder.Clear();
        var notOnField = await onHand.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionOutcome.Rejected, notOnField.Outcome);
        Assert.Equal(VeteranPromotionRejectionReason.NotOnField, notOnField.RejectionReason);
        Assert.Empty(recorder.Updates);
        Assert.False(VeteranRules.IsVeteran(onHand));

        // ② 不在场（已死亡）：拒绝。
        var dead = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsId, 0);
        await dead.ApplyDefenseDamageAsync(999);
        recorder.Clear();
        var deadResult = await dead.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.NotOnField, deadResult.RejectionReason);
        Assert.Empty(recorder.Updates);

        // ③ 无老兵版本·未声明（基础形态未声明 BecomesVeteran）：拒绝。
        var plain = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        recorder.Clear();
        var notDeclared = await plain.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.VersionNotDeclared, notDeclared.RejectionReason);
        Assert.Empty(recorder.Updates);

        // ④ 无老兵版本·目标缺失（声明了但运行时查不到对应老兵定义）：拒绝。
        var orphan = await CommandTestKit.PrepareOnFrontAsync(match, playerA, OrphanId, 2);
        recorder.Clear();
        var missing = await orphan.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.VersionMissing, missing.RejectionReason);
        Assert.Empty(recorder.Updates);

        // ⑤ 无老兵版本·目标不完整（老兵定义缺 VeteranOf）：拒绝。
        var incomplete = await CommandTestKit.PrepareOnFrontAsync(match, playerA, IncompleteId, 3);
        recorder.Clear();
        var incompleteResult = await incomplete.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.VersionIncomplete, incompleteResult.RejectionReason);
        Assert.Empty(recorder.Updates);

        // ⑥ 无老兵版本·来源错配（老兵定义来源未指向当前卡）：拒绝。
        var mismatch = await CommandTestKit.PrepareOnFrontAsync(match, playerA, MismatchId, 4);
        recorder.Clear();
        var mismatchResult = await mismatch.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.VersionSourceMismatch, mismatchResult.RejectionReason);
        Assert.Empty(recorder.Updates);

        // 拒绝后原状不变、可再发动（再次调用＝同类拒绝——稳定）。
        var again = await mismatch.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionRejectionReason.VersionSourceMismatch, again.RejectionReason);
        Assert.False(VeteranRules.IsVeteran(mismatch));
        Assert.Equal(1, AttackOf(mismatch));
    }

    // ---------- 失败专项②：执行段失败回滚（恢复升级前完整原状＋异常上抛） ----------

    [Fact]
    public async Task Failure_Execution_Rolls_Back_To_PreState_And_Allows_Retry()
    {
        // 失败构造（测试装配层）：老兵版本词条集含「授予时抛异常」的自定义词条（唯一命名注册、用后注销）。
        var failingKeywordId = "测试升级失败词条" + Guid.NewGuid().ToString("N");
        KeywordRegistry.Register(failingKeywordId, (_, _) => new ThrowingOnGrantKeywordComponent(failingKeywordId));
        try
        {
            var definitions = new[]
            {
                new CardDefinitionEntry("vet_fail_base", new CardDefinition(
                    "失败基础兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                    unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard,
                    becomesVeteran: "vet_fail_vet")),
                new CardDefinitionEntry("vet_fail_vet", new CardDefinition(
                    "失败老兵", deployCost: 1, operateCost: 1, attack: 4, defense: 7,
                    unitTypes: new[] { UnitType.Infantry },
                    keywords: new[] { new KeywordDeclaration(failingKeywordId) },
                    faction: Faction.Germany, rarity: Rarity.Standard,
                    veteranOf: "vet_fail_base")),
            };

            var match = CommandTestKit.CreateCommandMatch(extraDefinitions: definitions);
            await match.Initialize();
            var playerA = match.Players[0];

            var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, "vet_fail_base", 0);
            var state = unit.GetData<UnitStateData>();

            // 预置原状要素：损伤＋永久修饰＋运行时词条（验证回滚恢复到完整原状）。
            await unit.ApplyDefenseDamageAsync(2);
            var buffSource = new object();
            await unit.Modifiers.AddModifiersAsync(new Modifier[]
            {
                new AddModifier(CardStatFields.Attack, 1, buffSource),
            });
            await unit.Keywords.GrantAsync(KeywordIds.Mobilize);

            using var recorder = new UpdateRecorder(match.Engine);
            recorder.Clear();

            // 执行段失败：异常上抛（原异常可辨识——发动方视角）。
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => unit.InvokeVeteranTriggerAsync());
            Assert.Contains("测试", ex.Message);

            // 回滚：恢复升级前完整原状（损伤/修饰/词条/数值全还原）＋零信号。
            Assert.False(VeteranRules.IsVeteran(unit));
            Assert.False(unit.Keywords.Has(failingKeywordId));            // 新词条无残留
            Assert.True(unit.Keywords.Has(KeywordIds.Mobilize));          // 原词条恢复
            Assert.Equal(2, state.DefenseLoss);                           // 损伤恢复
            Assert.Single(unit.Modifiers.All);                            // 原修饰器恢复（+1 攻击）
            Assert.Equal(3, AttackOf(unit));                              // 2 + 1（修饰恢复）
            Assert.Equal(3, DefenseOf(unit));                             // 5 - 2（损伤恢复；无防御修饰）

            // 信号（批 1 适配——词条效果化）：执行段「词条集全清」步骤的正当卸载信号——动员内嵌效果随
            // 词条卸载按通道既有语义发 effect.removed 恰 2 条；此外全程零信号（回滚段不发射——
            // 无「结果类」信号〔card.stat.changed / unit.upgraded〕、无其它残余）。
            Assert.Equal(2, recorder.Updates.Count(u => u.Type == Updates.EffectRemoved));
            Assert.DoesNotContain(recorder.Updates, u => u.Type != Updates.EffectRemoved);
            Assert.True(unit.GetData<UnitStateData>().Line is not null); // 单位保持可正常操作

            // 可再次发动（结构未变——再次得到同类的执行段失败；单位未被半态污染）。
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.InvokeVeteranTriggerAsync());
            Assert.False(VeteranRules.IsVeteran(unit));
        }
        finally
        {
            KeywordRegistry.Unregister(failingKeywordId);
        }
    }

    // ---------- 数据映射专项（自含数据——真实链路：数据体 JSON → 加载 → 端口可读＋不再留痕） ----------

    [Fact]
    public void Data_Mapping_Loads_Ports_Readable_And_No_Longer_Traced()
    {
        var definitions = LoadVeteranDefinitions();
        CardDefinition DefinitionOf(string id) => definitions.Single(entry => entry.Id == id).Definition;

        // ① 基础卡：BecomesVeteran 端口可读（升级链信息——静态端口）；不再留痕。
        var baseDef = DefinitionOf(GuardsId);
        Assert.Equal(GuardsVetId, baseDef.BecomesVeteran);
        Assert.Null(baseDef.VeteranOf);
        Assert.DoesNotContain(baseDef.UnmappedAttributes, a => a.StartsWith("BecomesVeteran:", StringComparison.Ordinal));
        Assert.Contains("OnlySpawnable", baseDef.UnmappedAttributes); // 其它未实现项照常留痕（原语义保持）
        Assert.Contains(baseDef.Keywords, k => k.Id == KeywordIds.SmokeScreen); // 普通词条映射不受影响

        // ② 老兵卡：VeteranOf 端口可读；「老兵」标记随内容产出（单一真源——随加载/授予落地）。
        var vetDef = DefinitionOf(GuardsVetId);
        Assert.Equal(GuardsId, vetDef.VeteranOf);
        Assert.Null(vetDef.BecomesVeteran);
        Assert.Contains(vetDef.Keywords, k => k.Id == KeywordIds.Veteran);
        Assert.Contains(vetDef.Keywords, k => k.Id == KeywordIds.Blitz);

        // ③ 畸形声明（空 id / 多重）＝不进正式承载、归留痕（不 fail-fast）。
        var empty = KeywordsDefinition.Read(ParseAttributes("""["BecomesVeteran:"]"""));
        Assert.Null(empty.BecomesVeteran);
        Assert.Equal(new[] { "BecomesVeteran:" }, empty.UnmappedAttributes);

        var multi = KeywordsDefinition.Read(ParseAttributes("""["BecomesVeteran:a","BecomesVeteran:b"]"""));
        Assert.Null(multi.BecomesVeteran);
        Assert.Equal(new[] { "BecomesVeteran:a", "BecomesVeteran:b" }, multi.UnmappedAttributes);

        var blankOf = KeywordsDefinition.Read(ParseAttributes("""["VeteranOf:   "]"""));
        Assert.Null(blankOf.VeteranOf);
        Assert.Equal(new[] { "VeteranOf:   " }, blankOf.UnmappedAttributes);
        Assert.DoesNotContain(blankOf.Keywords, k => k.Id == KeywordIds.Veteran); // 畸形不产出标记
    }

    private static System.Text.Json.JsonElement ParseAttributes(string arrayJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"attributes\": " + arrayJson + "}");
        return document.RootElement.Clone();
    }

    // ---------- 内容语义：老兵卡独立在场（不经升级动作）＝读面 true（内容即真源） ----------

    [Fact]
    public async Task Content_Semantics_Standalone_Veteran_Card_Reads_As_Veteran()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 老兵卡（_vet）以任意途径在场的实例：读面 true——「升级动作」不是标记成立的必要条件。
        var vetCard = await CommandTestKit.PrepareOnFrontAsync(match, playerA, GuardsVetId, 0);
        Assert.True(VeteranRules.IsVeteran(vetCard));
        Assert.True(vetCard.Keywords.Has(KeywordIds.Blitz));

        // 幂等语义：已是老兵形态再发动＝无操作（零信号）——对 _vet 卡（无 BecomesVeteran 声明）同样成立。
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var result = await vetCard.InvokeVeteranTriggerAsync();
        Assert.Equal(VeteranPromotionOutcome.AlreadyVeteran, result.Outcome);
        Assert.Empty(recorder.Updates);
    }

    // ---------- 执行段失败构造：授予时抛异常的词条组件（测试装配层） ----------

    private sealed class ThrowingOnGrantKeywordComponent : KeywordComponent
    {
        public ThrowingOnGrantKeywordComponent(string keyword)
            : base(keyword)
        {
        }

        // 跨程序集覆写：R11 起 Orc.Game 对本测试程序集开放内部访问（InternalsVisibleTo），
        // protected internal 成员在友元程序集须以 protected internal 覆写。
        protected internal override void OnGrant()
            => throw new InvalidOperationException($"测试构造：词条 '{Keyword}' 授予失败（执行段意外）。");
    }
}
