using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Orc.Game.Plugins;
using Orc.Game.Triggers;
using Orc.Packages;

namespace Orc.Plugins.BuiltIn;

/// <summary>
/// 内置插件·判定器注册入口（资产化 S12b）：把原 <c>Match.Initialize</c> 的内置判定器注册段
/// （20 条）**原样迁入**——语义、注册序、条目构造与注入顺序与迁移前逐字保持。
/// 说明：内置判定器类为 <c>internal</c>（游戏层实现细节），本工程经 <c>InternalsVisibleTo</c> 获内部访问；
/// 三方插件只能用 public 面 ＋ <see cref="IMatchServices"/>。
/// </summary>
public static class BuiltInPlugin
{
    /// <summary>注册全部内置判定器（对局装配期、卡加载之前调用；恰一次）。</summary>
    /// <exception cref="ArgumentNullException">context 为 null。</exception>
    /// <exception cref="InvalidOperationException">缺少对局服务面（<see cref="IMatchPluginContext.Match"/> 为 null）。</exception>
    public static void Register(IMatchPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Match
            ?? throw new InvalidOperationException("内置插件需要对局服务面（IMatchServices）。");

        // ---------- R4：回合归属准入（打牌/移动攻击/反制三处同源） ----------
        var turnOwnerEntry = context.RegisterJudicator(
            JudicatorNames.ActionTurnOwner,
            new ActionTurnOwnerJudicator(() => match.CurrentPlayer));
        bool TurnOwner(Player player) =>
            CombatJudicatorInvoker.InvokeBool(turnOwnerEntry, player.Index, new object[] { player });

        // ---------- J2：验证判定器（费用/反制） ----------
        context.RegisterJudicator(JudicatorNames.CostCheck, new CostCheckJudicator());
        context.RegisterJudicator(JudicatorNames.CounterUse, new CounterUseJudicator(TurnOwner));

        // ---------- K1：交战合法性判定族（组合器 ＋ 三条子规则） ----------
        var combatRange = new CombatRangeJudicator(match.Battlefield, owner => match.EnemyOf(owner));
        var combatRangeEntry = context.RegisterJudicator(JudicatorNames.CombatRange, combatRange);
        DeclareKeywordRule(context, JudicatorNames.CombatRange, combatRange);

        var combatGuardEligibility = new CombatGuardEligibilityJudicator();
        var combatGuardEligibilityEntry = context.RegisterJudicator(
            JudicatorNames.CombatGuardEligibility, combatGuardEligibility);
        DeclareKeywordRule(context, JudicatorNames.CombatGuardEligibility, combatGuardEligibility);

        var combatInterception = new CombatInterceptionJudicator(match.Battlefield, owner => match.EnemyOf(owner));
        var combatInterceptionEntry = context.RegisterJudicator(JudicatorNames.CombatInterception, combatInterception);
        DeclareKeywordRule(context, JudicatorNames.CombatInterception, combatInterception);
        context.RegisterJudicator(
            JudicatorNames.CombatTargetLegal,
            new CombatTargetLegalJudicator(
                enemyOf: owner => match.EnemyOf(owner),
                isUnitGuarded: unit => match.IsUnitGuarded(unit),
                isHqGuarded: hq => match.IsHqGuarded(hq),
                rangeRule: (attacker, targetSlot) => CombatJudicatorInvoker.InvokeBool(combatRangeEntry, attacker, targetSlot),
                guardEligibilityRule: attacker => CombatJudicatorInvoker.InvokeBool(combatGuardEligibilityEntry, attacker),
                interceptionRule: (attacker, targetSlot, targetIsFighter) =>
                    CombatJudicatorInvoker.InvokeBool(combatInterceptionEntry, attacker, targetSlot, targetIsFighter)));

        // ---------- K2：反击豁免与伏击条件 ----------
        var combatCounterEligibility = new CombatCounterEligibilityJudicator();
        context.RegisterJudicator(JudicatorNames.CombatCounterEligibility, combatCounterEligibility);
        DeclareKeywordRule(context, JudicatorNames.CombatCounterEligibility, combatCounterEligibility);

        var combatAmbushCondition = new CombatAmbushConditionJudicator();
        context.RegisterJudicator(JudicatorNames.CombatAmbushCondition, combatAmbushCondition);
        DeclareKeywordRule(context, JudicatorNames.CombatAmbushCondition, combatAmbushCondition);

        // ---------- K3：复验消重（共享 leg 条目 ＋ 推进前置 ＋ 复验两条） ----------
        var moveLegEligibilityEntry = context.RegisterJudicator(
            JudicatorNames.MoveLegEligibility,
            new LegEligibilityJudicator(() => match.CurrentPlayer, match.Battlefield, isMove: true, TurnOwner));
        var attackLegEligibilityEntry = context.RegisterJudicator(
            JudicatorNames.AttackLegEligibility,
            new LegEligibilityJudicator(() => match.CurrentPlayer, battlefield: null, isMove: false, TurnOwner));
        var moveFrontlineEnemyEntry = context.RegisterJudicator(
            JudicatorNames.MoveFrontlineEnemy,
            new MoveFrontlineEnemyJudicator(match.Battlefield, owner => match.EnemyOf(owner)));
        context.RegisterJudicator(JudicatorNames.MoveRecheck, new MoveRevalidationJudicator(
            match.Lifecycle,
            (unit, position) => LegJudicatorInvoker.InvokeLeg(moveLegEligibilityEntry, unit, position),
            match.Battlefield,
            owner => CombatJudicatorInvoker.InvokeBool(moveFrontlineEnemyEntry, owner)));
        context.RegisterJudicator(JudicatorNames.AttackRecheck, new AttackRevalidationJudicator(
            match.Lifecycle,
            unit => LegJudicatorInvoker.InvokeLeg(attackLegEligibilityEntry, unit, null),
            (attacker, targetRef) => match.IsAttackTargetLegal(attacker, targetRef)));

        // ---------- S2/E1：目标候选名单 ＋ 效果无头选靶 ----------
        // 名单判定器＝候选单真源；无头选靶经条目句柄转发（不复制过滤逻辑）。
        var targetCandidateListEntry = context.RegisterJudicator(
            JudicatorNames.TargetCandidateEligibility,
            new TargetEligibilityJudicator(match.Battlefield));
        context.RegisterJudicator(
            JudicatorNames.EffectTargetResolve,
            new EffectTargetResolveJudicator(
                (viewer, selector) =>
                {
                    var ownerIndex = viewer switch
                    {
                        Hq hq => hq.Owner?.Index,
                        CardBase card => card.Owner?.Index,
                        _ => (int?)null,
                    };
                    var raw = targetCandidateListEntry.Invoke(new object[] { viewer, selector }, ownerIndex);
                    return raw is { Length: 1 } && raw[0] is IReadOnlyList<Ref<Entity>> list
                        ? list
                        : throw new InvalidOperationException(
                            $"判定器 '{JudicatorNames.TargetCandidateEligibility}' 输出契约不符：期望候选名单（fail-fast）。");
                },
                card => MatchRandomService.ResolveFor(card)));

        // ---------- E1-25：资源判定器（槽递增/包裹 ＋ 点数包裹） ----------
        context.RegisterJudicator(JudicatorNames.PointSlotIncrement, new PointSlotIncrementJudicator());
        context.RegisterJudicator(JudicatorNames.PointSlotGain, new PointSlotGainAmountJudicator());
        context.RegisterJudicator(JudicatorNames.PointSlotLose, new PointSlotLoseAmountJudicator());
        context.RegisterJudicator(JudicatorNames.PointGain, new PointGainAmountJudicator());
        context.RegisterJudicator(JudicatorNames.PointLose, new PointLoseAmountJudicator());
    }

    /// <summary>
    /// L2：把「内置基线 → 词条程序规则覆盖」注册为该判定器的一条 moding。
    /// 调用点（CommandManager／组合器／测试）不再做特判；改写经注册表统一解析点生效、注销即回退。
    /// moding 为纯替换，故基线由**内置实例**直接求值（不经注册表，避免解析栈自引用）。
    /// </summary>
    private static void DeclareKeywordRule(IMatchPluginContext context, string name, Judicator baseline)
    {
        context.DeclareReplaceWrapper(WrapperTarget.Judicator, name, args =>
        {
            var value = baseline.Invoke(args) is { Length: 1 } raw && raw[0] is bool flag
                ? flag
                : throw new InvalidOperationException(
                    $"判定器 '{name}' 基线输出契约不符：期望 object[]{{bool}}（1 元素）——fail-fast。");

            // 冲击（bypass range）：效果发起的攻击无视范围矩阵（原 InvokeBool 内联特判迁入）。
            if (name == JudicatorNames.CombatRange
                && args.FirstOrDefault() is UnitCard unit
                && KeywordProgramRuntime.BypassesRange(unit))
            {
                value = true;
            }

            if (args.FirstOrDefault() is Orc.Cards.Card actor && KeywordProgram.RuleNames.Contains(name))
            {
                value = KeywordProgramRuntime.Resolve(actor, name, value ? 1 : 0) != 0;
            }

            return new object[] { value };
        });
    }

    /// <summary>
    /// 内置插件·**postCommand 相**入口（P11 甲）：需 CommandManager 就绪的内置装配步骤——
    /// 四类流程触发器登记（底层分层）与词条效果库装配（语义与迁移前逐字一致）。
    /// </summary>
    /// <exception cref="ArgumentNullException">context 为 null。</exception>
    /// <exception cref="InvalidOperationException">缺少对局绑定面（<see cref="IMatchPluginContext.Binding"/> 为 null）。</exception>
    public static void RegisterPostCommand(IMatchPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var binding = context.Binding
            ?? throw new InvalidOperationException("内置插件（postCommand 相）需要对局绑定面（IMatchBindingServices）。");

        binding.RegisterFlowTriggers(TriggerLayer.LowLevel);
        binding.AssembleKeywordEffectLibrary();
    }

    private static int _processRegistered;

    /// <summary>
    /// 内置插件·**进程级**注册（批次 2）：把生产内建十九枚词条与词条效果绑定声明装配到
    /// <see cref="KeywordRegistry"/>（静态注册面）——内容自游戏层迁出；**幂等**（模块初始化与宿主进程相可各自调用）。
    /// </summary>
    public static void RegisterProcessDefaults()
    {
        if (Interlocked.Exchange(ref _processRegistered, 1) == 1)
        {
            return;
        }

        // 既有四枚（2C-A1 迁移）——均属对战词条（打标）。
        KeywordRegistry.Register(KeywordIds.Blitz, (_, _) => new BlitzKeywordComponent(), isBattleKeyword: true);
        KeywordRegistry.Register(KeywordIds.Fury, (_, _) => new FuryKeywordComponent(), isBattleKeyword: true);
        KeywordRegistry.Register(KeywordIds.SmokeScreen, (_, _) => new PlainKeywordComponent(KeywordIds.SmokeScreen), isBattleKeyword: true);
        KeywordRegistry.Register(KeywordIds.Ambush, (_, _) => new AmbushKeywordComponent(), isBattleKeyword: true);

        // A2 新增十枚——重甲为对战词条（打标）；其余不打标。
        KeywordRegistry.Register(KeywordIds.Suppressed, (_, value) => new SuppressedKeywordComponent(value));
        KeywordRegistry.Register(KeywordIds.Inhibited, (_, _) => new PlainKeywordComponent(KeywordIds.Inhibited));
        KeywordRegistry.Register(KeywordIds.Mobilize, (_, _) => new MobilizeKeywordComponent());
        KeywordRegistry.Register(KeywordIds.Pincer, (_, _) => new PincerKeywordComponent());
        KeywordRegistry.Register(KeywordIds.Forecast, (_, _) => new PlainKeywordComponent(KeywordIds.Forecast));
        KeywordRegistry.Register(KeywordIds.Immune, (_, _) => new ImmuneKeywordComponent());
        KeywordRegistry.Register(KeywordIds.Armor, (_, value) => new ArmorKeywordComponent(value), isBattleKeyword: true);
        KeywordRegistry.Register(KeywordIds.Intelligence, (_, value) => new IntelligenceKeywordComponent(value));
        KeywordRegistry.Register(KeywordIds.CannotBeSuppressed, (_, _) => new PlainKeywordComponent(KeywordIds.CannotBeSuppressed));
        KeywordRegistry.Register(KeywordIds.CannotBeInhibited, (_, _) => new PlainKeywordComponent(KeywordIds.CannotBeInhibited));

        // A4／S1／S2／批 4／批 5 各一枚。
        KeywordRegistry.Register(KeywordIds.Deathrattle, (_, _) => new DeathrattleKeywordComponent());
        KeywordRegistry.Register(KeywordIds.Veteran, (_, _) => new PlainKeywordComponent(KeywordIds.Veteran));
        KeywordRegistry.Register(KeywordIds.Covert, (_, _) => new PlainKeywordComponent(KeywordIds.Covert));
        KeywordRegistry.Register(KeywordIds.Guard, (_, _) => new PlainKeywordComponent(KeywordIds.Guard), isBattleKeyword: true);
        KeywordRegistry.Register(KeywordIds.Shock, (_, _) => new PlainKeywordComponent(KeywordIds.Shock), isBattleKeyword: true);

        // 批 4 数据化：词条效果绑定声明（试点：闪击/动员）。
        KeywordRegistry.DeclareEffectBindings(KeywordIds.Blitz, new[]
        {
            new KeywordEffectBinding(KeywordEffectAssembly.BlitzDeploySetPrefabId, KeywordEffectAssembly.BlitzDeploySetEffectName),
        });
        KeywordRegistry.DeclareEffectBindings(KeywordIds.Mobilize, new[]
        {
            new KeywordEffectBinding(KeywordEffectAssembly.MobilizeAccrualPrefabId, KeywordEffectAssembly.MobilizeAccrualEffectName),
            new KeywordEffectBinding(KeywordEffectAssembly.MobilizeLossPrefabId, KeywordEffectAssembly.MobilizeLossEffectName),
        });

        RegisterBuiltInCardComponents();
    }

    /// <summary>
    /// 内置插件·**进程级**注册（批次 2）：六个内置数据组件（注册序即第一段加载顺序：
    /// <c>typeCategory → factionCost → battleStats → tagData → keywords → effects</c>）——内容自游戏层迁出。
    /// </summary>
    private static void RegisterBuiltInCardComponents()
    {
        // 构造期段（顺序：类别 → 阵营/花费 → 对战数值）。
        CardComponentRegistry.Register(
            TypeCategoryDefinition.DefinitionName,
            CardComponentPhase.Construction,
            static (_, _, _) => Task.CompletedTask, // 空操作：数据经 CardDefinition 派生读面消费
            TypeCategoryDefinition.Read,
            isBuiltIn: true,
            writer: TypeCategoryDefinition.Write);

        CardComponentRegistry.Register(
            FactionCostDefinition.DefinitionName,
            CardComponentPhase.Construction,
            static (card, definition, _) =>
            {
                var typed = (FactionCostDefinition)definition;
                card.AddData(new FactionCostData(typed.Faction, typed.DeployCost));
                return Task.CompletedTask;
            },
            FactionCostDefinition.Read,
            isBuiltIn: true,
            writer: FactionCostDefinition.Write);

        CardComponentRegistry.Register(
            BattleStatsDefinition.DefinitionName,
            CardComponentPhase.Construction,
            static (card, definition, _) =>
            {
                var typed = (BattleStatsDefinition)definition;
                card.AddData(new BattleStatsData(typed.OperateCost, typed.Attack, typed.Defense));
                return Task.CompletedTask;
            },
            BattleStatsDefinition.Read,
            isBuiltIn: true,
            writer: BattleStatsDefinition.Write);

        // 加载期段（顺序：元数据 → 词条 → 效果）。
        CardComponentRegistry.Register(
            TagDataDefinition.DefinitionName,
            CardComponentPhase.Load,
            static (card, definition, _) =>
            {
                var typed = (TagDataDefinition)definition;
                var data = new TagData(typed.Rarity);
                foreach (var tag in typed.Tags)
                {
                    data.AddTag(tag);
                }

                card.AddData(data);
                return Task.CompletedTask;
            },
            TagDataDefinition.Read,
            isBuiltIn: true,
            writer: TagDataDefinition.Write);

        CardComponentRegistry.Register(
            KeywordsDefinition.DefinitionName,
            CardComponentPhase.Load,
            static (card, definition, context) =>
            {
                var typed = (KeywordsDefinition)definition;
                var loadContext = context.KeywordLoadContext;
                foreach (var declaration in typed.Keywords)
                {
                    card.Keywords.GrantCore(declaration.Id, declaration.Value, loadContext);
                }

                return Task.CompletedTask;
            },
            KeywordsDefinition.Read,
            isBuiltIn: true,
            writer: KeywordsDefinition.Write);

        CardComponentRegistry.Register(
            EffectsDefinition.DefinitionName,
            CardComponentPhase.Load,
            static (_, _, _) => Task.CompletedTask, // 空操作：声明已在载入期转为效果注册项，装载走框架步骤
            EffectsDefinition.Read,
            isBuiltIn: true,
            writer: EffectsDefinition.Write);
    }
}
