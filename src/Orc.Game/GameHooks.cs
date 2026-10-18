using Orc.Game.Judicators;
using Orc.Game.Triggers;

namespace Orc.Game;

/// <summary>
/// 接入途径（03-hook定义 S4 分类枚举）：效果/外部介入对局局内逻辑的三类受控途径。
/// </summary>
public enum GameHookAccessPath
{
    /// <summary>订阅（<c>Bus.Mount</c>——被动触发器挂载更新总线）。</summary>
    Subscribe = 0,

    /// <summary>band 注入（<c>Effect.Inject&lt;TView&gt;</c>——效果注册到通用流程触发器的具名 band）。</summary>
    BandInject = 1,

    /// <summary>判定器替换（<c>JudicatorRegistry.RegisterReplaceWrapper</c>——逻辑服务的受控改写）。</summary>
    JudicatorWrapper = 2,
}

/// <summary>
/// 观察面（03-hook定义 S4 分类枚举）：外部只读获取对局进展的三类接口。
/// </summary>
public enum GameHookObservation
{
    /// <summary>即时更新监听（<c>LogicEngine.Subscribe</c> / <c>OnImmediateUpdate</c>）。</summary>
    ImmediateUpdate = 0,

    /// <summary>段轮询（<c>LogicEngine.TakeSegments</c> / <c>TryTakeSegment</c>）。</summary>
    SegmentPoll = 1,

    /// <summary>快照/事件流导出（<c>src/Orc/Output/*</c> 静态 <c>Serialize</c>）。</summary>
    Snapshot = 2,
}

/// <summary>
/// 扩展点类别（03-hook定义 S4 分类枚举）：装配期注入面的三类形态。
/// </summary>
public enum GameHookExtensionKind
{
    /// <summary>注册表（<c>HookRegistry</c> / <c>PrefabManager</c> / 判定器注册表等）。</summary>
    Registry = 0,

    /// <summary>提供器（<c>CardLibrary</c> 延迟提供器 / <c>IScriptEvaluator</c> 等）。</summary>
    Provider = 1,

    /// <summary>构造注入（<c>Match</c> 构造注入点 / <c>Initialize</c> 接线点）。</summary>
    ConstructorInjection = 2,
}

/// <summary>
/// 待补层状态（03-hook定义 S7；三值——「已具备/后置/不做」）。
/// </summary>
public enum GameHookPendingStatus
{
    /// <summary>已具备：存在同名/同义对局信号或专用流程触发器可直接介入。</summary>
    Available = 0,

    /// <summary>后置：语义可对位，但缺信号/流程位（留待后续批次）。</summary>
    Deferred = 1,

    /// <summary>不做：依赖未实现机制（压制/沉默/撤退/存活/充能），本批明确排除。</summary>
    NotPlanned = 2,
}

/// <summary>
/// 待补层条目（kards-diy 27 项监听触发器对齐表的一行；仅登记，不接线）。
/// </summary>
/// <param name="KardsTrigger">kards-diy 触发器名（权威源＝<c>docs/kards-diy-可参考语料报告.md</c> §2.1）。</param>
/// <param name="Status">结论（已具备/后置/不做）。</param>
/// <param name="OrcCounterparts">OrC 对位（<see cref="GameHooks.Signals"/> 中的信号名，或流程位标识）。</param>
public sealed record GameHookPendingTrigger(
    string KardsTrigger,
    GameHookPendingStatus Status,
    IReadOnlyList<string> OrcCounterparts)
{
    /// <summary>状态中文名（与文档对齐表一致）。</summary>
    public string StatusText => Status switch
    {
        GameHookPendingStatus.Available => "已具备",
        GameHookPendingStatus.Deferred => "后置",
        GameHookPendingStatus.NotPlanned => "不做",
        _ => throw new ArgumentOutOfRangeException(nameof(Status), Status, "未定义的待补层状态。"),
    };
}

/// <summary>
/// 游戏层接入点／观察面／扩展点的**聚合索引**（03-hook定义 清单层的代码侧承载；纯声明、零行为）。
/// 信号常量／载荷键／判定器名／触发器分层**一律转发引用**既有定义（<see cref="GameUpdates"/>、
/// <see cref="JudicatorNames"/>、<see cref="TriggerLayer"/>），不复制字面值（防第二真源）；
/// 分类枚举与待补层为本类新定义。
/// 边界：本类不含内核信号 <c>card.placed</c>（<c>Updates.CardPlaced</c>，经 <c>GameUpdates.EmitCardPlaced</c> 转发）。
/// </summary>
public static class GameHooks
{
    // ---------- 对外信号（31 条；转发引用 GameUpdates） ----------

    /// <summary>回合开始前（<c>turn.start.before</c>）。</summary>
    public const string TurnStartBefore = GameUpdates.TurnStartBefore;

    /// <summary>回合开始（<c>turn.start</c>）。</summary>
    public const string TurnStart = GameUpdates.TurnStart;

    /// <summary>回合开始后（<c>turn.start.after</c>）。</summary>
    public const string TurnStartAfter = GameUpdates.TurnStartAfter;

    /// <summary>回合结束前（<c>turn.end.before</c>）。</summary>
    public const string TurnEndBefore = GameUpdates.TurnEndBefore;

    /// <summary>回合结束（<c>turn.end</c>）。</summary>
    public const string TurnEnd = GameUpdates.TurnEnd;

    /// <summary>打牌（<c>card.played</c>）。</summary>
    public const string CardPlayed = GameUpdates.CardPlayed;

    /// <summary>抽牌（<c>card.drawn</c>）。</summary>
    public const string CardDrawn = GameUpdates.CardDrawn;

    /// <summary>数值更新（<c>card.stat.changed</c>）。</summary>
    public const string CardStatChanged = GameUpdates.CardStatChanged;

    /// <summary>卡牌加载（<c>card.load</c>）。</summary>
    public const string CardLoad = GameUpdates.CardLoad;

    /// <summary>手牌加入（<c>card.hand.add</c>）。</summary>
    public const string CardHandAdd = GameUpdates.CardHandAdd;

    /// <summary>弃置（<c>card.discarded</c>）。</summary>
    public const string CardDiscarded = GameUpdates.CardDiscarded;

    /// <summary>爆牌（<c>card.burned</c>）。</summary>
    public const string CardBurned = GameUpdates.CardBurned;

    /// <summary>游戏层死亡（<c>card.died</c>）。</summary>
    public const string CardDied = GameUpdates.CardDied;

    /// <summary>死亡已确认、效果卸载前（<c>card.dying</c>）。</summary>
    public const string CardDying = GameUpdates.CardDying;

    /// <summary>单位即将离开战场（<c>card.leaving</c>）。</summary>
    public const string CardLeaving = GameUpdates.CardLeaving;

    /// <summary>单位加入（<c>unit.joined</c>）。</summary>
    public const string UnitJoined = GameUpdates.UnitJoined;

    /// <summary>单位部署（<c>unit.deployed</c>）。</summary>
    public const string UnitDeployed = GameUpdates.UnitDeployed;

    /// <summary>单位位置变化（<c>unit.position.changed</c>）。</summary>
    public const string UnitPositionChanged = GameUpdates.UnitPositionChanged;

    /// <summary>单位升级为老兵（<c>unit.upgraded</c>；S1）。</summary>
    public const string UnitUpgraded = GameUpdates.UnitUpgraded;

    /// <summary>单位被揭示（<c>unit.revealed</c>；S2）。</summary>
    public const string UnitRevealed = GameUpdates.UnitRevealed;

    /// <summary>卡组洗切（<c>deck.shuffled</c>）。</summary>
    public const string DeckShuffled = GameUpdates.DeckShuffled;

    /// <summary>单位类型变更（<c>unit.types.changed</c>）。</summary>
    public const string UnitTypesChanged = GameUpdates.UnitTypesChanged;

    /// <summary>额外获得指挥点槽（<c>slot.gained</c>）。</summary>
    public const string SlotGained = GameUpdates.SlotGained;

    /// <summary>失去指挥点槽（<c>slot.lost</c>）。</summary>
    public const string SlotLost = GameUpdates.SlotLost;

    /// <summary>指挥点槽改变（<c>slot.changed</c>）。</summary>
    public const string SlotChanged = GameUpdates.SlotChanged;

    /// <summary>额外获得点数（<c>point.gained</c>）。</summary>
    public const string PointGained = GameUpdates.PointGained;

    /// <summary>失去点数（<c>point.lost</c>）。</summary>
    public const string PointLost = GameUpdates.PointLost;

    /// <summary>指挥点改变（<c>point.changed</c>）。</summary>
    public const string PointChanged = GameUpdates.PointChanged;

    /// <summary>受到伤害（<c>card.damaged</c>；E1-33）。</summary>
    public const string CardDamaged = GameUpdates.CardDamaged;

    /// <summary>单位行动后（<c>unit.acted</c>；E1-33）。</summary>
    public const string UnitActed = GameUpdates.UnitActed;

    /// <summary>交战并存活（<c>unit.combat.survived</c>；E1-39）。</summary>
    public const string UnitCombatSurvived = GameUpdates.UnitCombatSurvived;

    /// <summary>造成伤害（<c>unit.damage.dealt</c>；E1-47）。</summary>
    public const string UnitDamageDealt = GameUpdates.UnitDamageDealt;

    /// <summary>反制触发（<c>counter.triggered</c>；E1-53）。</summary>
    public const string CounterTriggered = GameUpdates.CounterTriggered;

    /// <summary>词条临时增益到期相位（<c>keyword.attack.finished</c>）。</summary>
    public const string KeywordAttackFinished = GameUpdates.KeywordAttackFinished;

    /// <summary>对外信号全量（34 条；稳定序＝定义序）。</summary>
    public static IReadOnlyList<string> Signals { get; } = new[]
    {
        TurnStartBefore, TurnStart, TurnStartAfter, TurnEndBefore, TurnEnd,
        CardPlayed, CardDrawn, CardStatChanged,
        CardLoad, CardHandAdd, CardDiscarded, CardBurned,
        CardDied, CardDying, CardLeaving, UnitJoined, UnitDeployed, UnitPositionChanged, UnitUpgraded, UnitRevealed,
        DeckShuffled, UnitTypesChanged,
        SlotGained, SlotLost, SlotChanged,
        PointGained, PointLost, PointChanged,
        CardDamaged, UnitActed, UnitCombatSurvived, UnitDamageDealt, CounterTriggered,
        KeywordAttackFinished,
    };

    // ---------- 载荷键（17 条；转发引用 GameUpdates） ----------

    /// <summary>载荷键：玩家。</summary>
    public const string PayloadPlayer = GameUpdates.PayloadPlayer;

    /// <summary>载荷键：回合数。</summary>
    public const string PayloadTurnNumber = GameUpdates.PayloadTurnNumber;

    /// <summary>载荷键：卡牌实例。</summary>
    public const string PayloadCard = GameUpdates.PayloadCard;

    /// <summary>载荷键：单位。</summary>
    public const string PayloadUnit = GameUpdates.PayloadUnit;

    /// <summary>载荷键：词条标识。</summary>
    public const string PayloadKeyword = GameUpdates.PayloadKeyword;

    /// <summary>载荷键：位置。</summary>
    public const string PayloadPosition = GameUpdates.PayloadPosition;

    /// <summary>载荷键：原位置。</summary>
    public const string PayloadOldPosition = GameUpdates.PayloadOldPosition;

    /// <summary>载荷键：新位置。</summary>
    public const string PayloadNewPosition = GameUpdates.PayloadNewPosition;

    /// <summary>载荷键：变化字段集合。</summary>
    public const string PayloadChangedFields = GameUpdates.PayloadChangedFields;

    /// <summary>载荷键：卡组。</summary>
    public const string PayloadDeck = GameUpdates.PayloadDeck;

    /// <summary>载荷键：新增类型。</summary>
    public const string PayloadAddedType = GameUpdates.PayloadAddedType;

    /// <summary>载荷键：数量（实际变化量 Δ）。</summary>
    public const string PayloadAmount = GameUpdates.PayloadAmount;

    /// <summary>载荷键：原槽值。</summary>
    public const string PayloadOldSlots = GameUpdates.PayloadOldSlots;

    /// <summary>载荷键：新槽值。</summary>
    public const string PayloadNewSlots = GameUpdates.PayloadNewSlots;

    /// <summary>载荷键：原点数。</summary>
    public const string PayloadOldPoints = GameUpdates.PayloadOldPoints;

    /// <summary>载荷键：新点数。</summary>
    public const string PayloadNewPoints = GameUpdates.PayloadNewPoints;

    /// <summary>载荷键：击杀者（E1-47）。</summary>
    public const string PayloadKiller = GameUpdates.PayloadKiller;

    /// <summary>载荷键全量（17 条；稳定序＝定义序）。</summary>
    public static IReadOnlyList<string> PayloadKeys { get; } = new[]
    {
        PayloadPlayer, PayloadTurnNumber, PayloadCard, PayloadUnit, PayloadPosition,
        PayloadKeyword,
        PayloadOldPosition, PayloadNewPosition, PayloadChangedFields, PayloadDeck, PayloadAddedType,
        PayloadAmount, PayloadOldSlots, PayloadNewSlots, PayloadOldPoints, PayloadNewPoints,
        PayloadKiller,
    };

    // ---------- 判定器名（22 条；转发引用 JudicatorNames） ----------

    /// <summary>判定器名：费用检查（<c>validation.cost.check</c>）。</summary>
    public const string JudicatorCostCheck = JudicatorNames.CostCheck;

    public const string JudicatorActionInput = JudicatorNames.ActionInput;

    /// <summary>判定器名：回合归属准入（<c>action.turn.owner</c>；R4——三处入口同源）。</summary>
    public const string JudicatorActionTurnOwner = JudicatorNames.ActionTurnOwner;

    /// <summary>判定器名：反制使用检查（<c>validation.counter.use</c>）。</summary>
    public const string JudicatorCounterUse = JudicatorNames.CounterUse;

    /// <summary>判定器名：移动复验（<c>validation.move.recheck</c>）。</summary>
    public const string JudicatorMoveRecheck = JudicatorNames.MoveRecheck;

    /// <summary>判定器名：攻击复验（<c>validation.attack.recheck</c>）。</summary>
    public const string JudicatorAttackRecheck = JudicatorNames.AttackRecheck;

    /// <summary>判定器名：卡组顶特点判定（<c>deck.top.tag</c>；示范①）。</summary>
    public const string JudicatorDeckTopTag = JudicatorNames.DeckTopTag;

    /// <summary>判定器名：目标候选合法性判定（<c>targeting.candidate.eligibility</c>；示范②——S2 起经生产内置段注册）。</summary>
    public const string JudicatorTargetCandidateEligibility = JudicatorNames.TargetCandidateEligibility;

    /// <summary>判定器名：目标合法性判定（<c>combat.target.legal</c>；K1 组合判定器）。</summary>
    public const string JudicatorCombatTargetLegal = JudicatorNames.CombatTargetLegal;

    /// <summary>判定器名：范围矩阵判定（<c>combat.range</c>；K1 子规则）。</summary>
    public const string JudicatorCombatRange = JudicatorNames.CombatRange;

    /// <summary>判定器名：被守护攻击资格判定（<c>combat.guard.eligibility</c>；K1 子规则）。</summary>
    public const string JudicatorCombatGuardEligibility = JudicatorNames.CombatGuardEligibility;

    /// <summary>判定器名：轰炸机拦截判定（<c>combat.interception</c>；K1 子规则）。</summary>
    public const string JudicatorCombatInterception = JudicatorNames.CombatInterception;

    /// <summary>判定器名：反击资格判定（<c>combat.counter.eligibility</c>；K2——反击豁免表）。</summary>
    public const string JudicatorCombatCounterEligibility = JudicatorNames.CombatCounterEligibility;

    /// <summary>判定器名：伏击条件判定（<c>combat.ambush.condition</c>；K2——伏击条件）。</summary>
    public const string JudicatorCombatAmbushCondition = JudicatorNames.CombatAmbushCondition;

    /// <summary>判定器名：move leg 资格判定（<c>move.leg.eligibility</c>；K3——复验消重共享条件）。</summary>
    public const string JudicatorMoveLegEligibility = JudicatorNames.MoveLegEligibility;

    /// <summary>判定器名：attack leg 资格判定（<c>attack.leg.eligibility</c>；K3——复验消重共享条件）。</summary>
    public const string JudicatorAttackLegEligibility = JudicatorNames.AttackLegEligibility;

    /// <summary>判定器名：推进前置判定（<c>move.frontline-enemy</c>；K3——前线存活敌方）。</summary>
    public const string JudicatorMoveFrontlineEnemy = JudicatorNames.MoveFrontlineEnemy;

    /// <summary>判定器名：效果无头选靶判定（<c>effect.target.resolve</c>；E1——csx handler 选择面）。</summary>
    public const string JudicatorEffectTargetResolve = JudicatorNames.EffectTargetResolve;

    /// <summary>判定器名：回合开始的槽递增（<c>resource.slot.increment</c>；E1-25）。</summary>
    public const string JudicatorPointSlotIncrement = JudicatorNames.PointSlotIncrement;

    /// <summary>判定器名：额外获得槽数的数字包裹（<c>resource.slot.gain</c>；E1-25）。</summary>
    public const string JudicatorPointSlotGain = JudicatorNames.PointSlotGain;

    /// <summary>判定器名：失去槽数的数字包裹（<c>resource.slot.lose</c>；E1-25）。</summary>
    public const string JudicatorPointSlotLose = JudicatorNames.PointSlotLose;

    /// <summary>判定器名：额外获得点数的数字包裹（<c>resource.point.gain</c>；E1-25 后续）。</summary>
    public const string JudicatorPointGain = JudicatorNames.PointGain;

    /// <summary>判定器名：失去点数的数字包裹（<c>resource.point.lose</c>；E1-25 后续）。</summary>
    public const string JudicatorPointLose = JudicatorNames.PointLose;

    /// <summary>判定器名全量（23 条；稳定序＝定义序）。</summary>
    public static IReadOnlyList<string> JudicatorNameList { get; } = new[]
    {
        JudicatorCostCheck, JudicatorCounterUse, JudicatorMoveRecheck, JudicatorAttackRecheck, JudicatorActionInput,
        JudicatorActionTurnOwner,
        JudicatorDeckTopTag, JudicatorTargetCandidateEligibility,
        JudicatorCombatTargetLegal, JudicatorCombatRange, JudicatorCombatGuardEligibility, JudicatorCombatInterception,
        JudicatorCombatCounterEligibility, JudicatorCombatAmbushCondition,
        JudicatorMoveLegEligibility, JudicatorAttackLegEligibility, JudicatorMoveFrontlineEnemy,
        JudicatorEffectTargetResolve,
        JudicatorPointSlotIncrement, JudicatorPointSlotGain, JudicatorPointSlotLose,
        JudicatorPointGain, JudicatorPointLose,
    };

    /// <summary>判定器名装配面：21 条内置（5 条验证类＋6 条交战类＋3 条动作资格类＋1 条效果选靶类＋5 条资源类
    /// ＋1 条目标候选合法性〔S2 提入生产装配〕）由 <c>Match.Initialize</c> 固定注册段注册；1 条示范类（DeckTopTag）经外部装配段（<c>judicatorAssembly</c>）可选注入。</summary>
    public static IReadOnlyList<string> BuiltInJudicatorNames { get; } = new[]
    {
        JudicatorCostCheck, JudicatorCounterUse, JudicatorMoveRecheck, JudicatorAttackRecheck, JudicatorActionInput,
        JudicatorActionTurnOwner,
        JudicatorCombatTargetLegal, JudicatorCombatRange, JudicatorCombatGuardEligibility, JudicatorCombatInterception,
        JudicatorCombatCounterEligibility, JudicatorCombatAmbushCondition,
        JudicatorMoveLegEligibility, JudicatorAttackLegEligibility, JudicatorMoveFrontlineEnemy,
        JudicatorEffectTargetResolve,
        JudicatorPointSlotIncrement, JudicatorPointSlotGain, JudicatorPointSlotLose,
        JudicatorPointGain, JudicatorPointLose,
        JudicatorTargetCandidateEligibility,
    };

    /// <summary>判定器名装配面：示范类（经 <c>judicatorAssembly</c> 外部装配段注入）。</summary>
    public static IReadOnlyList<string> ExternalJudicatorNames { get; } = new[]
    {
        JudicatorDeckTopTag,
    };

    // ---------- 触发器分层（转发引用 TriggerLayer 成员） ----------

    /// <summary>触发器分层全量（转发 <see cref="TriggerLayer"/> 成员；名称由 <c>ToString</c> 派生）。</summary>
    public static IReadOnlyList<TriggerLayer> TriggerLayers { get; } = new[]
    {
        TriggerLayer.LowLevel, TriggerLayer.External,
    };

    // ---------- 流程位标识（非信号；待补层对位用） ----------

    /// <summary>流程位：单位攻击触发器（<c>UnitAttackTrigger</c>「攻击执行」——非信号、可直接介入的专用流程触发器）。</summary>
    public const string FlowUnitAttack = "UnitAttackTrigger";

    // ---------- 待补层：kards-diy 27 项监听触发器（仅登记，不接线） ----------

    /// <summary>kards trigger：<c>turnStart</c>。</summary>
    public const string KardsTurnStart = "turnStart";

    /// <summary>kards trigger：<c>turnEnd</c>。</summary>
    public const string KardsTurnEnd = "turnEnd";

    /// <summary>kards trigger：<c>unitDeployed</c>。</summary>
    public const string KardsUnitDeployed = "unitDeployed";

    /// <summary>kards trigger：<c>attack</c>。</summary>
    public const string KardsAttack = "attack";

    /// <summary>kards trigger：<c>orderPlayed</c>。</summary>
    public const string KardsOrderPlayed = "orderPlayed";

    /// <summary>kards trigger：<c>shuffle</c>。</summary>
    public const string KardsShuffle = "shuffle";

    /// <summary>kards trigger：<c>unitMobilized</c>。</summary>
    public const string KardsUnitMobilized = "unitMobilized";

    /// <summary>kards trigger：<c>attacked</c>（被攻击）。</summary>
    public const string KardsAttacked = "attacked";

    /// <summary>kards trigger：<c>afterAttack</c>。</summary>
    public const string KardsAfterAttack = "afterAttack";

    /// <summary>kards trigger：<c>afterAttackHQ</c>。</summary>
    public const string KardsAfterAttackHq = "afterAttackHQ";

    /// <summary>kards trigger：<c>friendlyDeath</c>。</summary>
    public const string KardsFriendlyDeath = "friendlyDeath";

    /// <summary>kards trigger：<c>friendlyAttacked</c>。</summary>
    public const string KardsFriendlyAttacked = "friendlyAttacked";

    /// <summary>kards trigger：<c>friendlyDamaged</c>。</summary>
    public const string KardsFriendlyDamaged = "friendlyDamaged";

    /// <summary>kards trigger：<c>hqDamaged</c>。</summary>
    public const string KardsHqDamaged = "hqDamaged";

    /// <summary>kards trigger：<c>enemyKilled</c>。</summary>
    public const string KardsEnemyKilled = "enemyKilled";

    /// <summary>kards trigger：<c>afterKill</c>。</summary>
    public const string KardsAfterKill = "afterKill";

    /// <summary>kards trigger：<c>targetedByOrder</c>。</summary>
    public const string KardsTargetedByOrder = "targetedByOrder";

    /// <summary>kards trigger：<c>kreditsGained</c>。</summary>
    public const string KardsKreditsGained = "kreditsGained";

    /// <summary>kards trigger：<c>unitLeft</c>（非消灭离场）。</summary>
    public const string KardsUnitLeft = "unitLeft";

    /// <summary>kards trigger：<c>friendlyPinned</c>。</summary>
    public const string KardsFriendlyPinned = "friendlyPinned";

    /// <summary>kards trigger：<c>friendlySuppressed</c>。</summary>
    public const string KardsFriendlySuppressed = "friendlySuppressed";

    /// <summary>kards trigger：<c>friendlySilenced</c>。</summary>
    public const string KardsFriendlySilenced = "friendlySilenced";

    /// <summary>kards trigger：<c>friendlyRetreated</c>。</summary>
    public const string KardsFriendlyRetreated = "friendlyRetreated";

    /// <summary>kards trigger：<c>friendlySurvived</c>。</summary>
    public const string KardsFriendlySurvived = "friendlySurvived";

    /// <summary>kards trigger：<c>unpinned</c>。</summary>
    public const string KardsUnpinned = "unpinned";

    /// <summary>kards trigger：<c>impactUsed</c>。</summary>
    public const string KardsImpactUsed = "impactUsed";

    /// <summary>kards trigger：<c>chargeNow</c>。</summary>
    public const string KardsChargeNow = "chargeNow";

    /// <summary>
    /// 待补层对齐表（27 项；权威源＝<c>docs/kards-diy-可参考语料报告.md</c> §2.1／<c>invariants.json:96-124</c>）。
    /// 稳定序＝上方清单定义序（已具备 → 后置 → 不做）。仅登记，不接线。
    /// </summary>
    public static IReadOnlyList<GameHookPendingTrigger> PendingTriggers { get; } = new[]
    {
        new GameHookPendingTrigger(KardsTurnStart, GameHookPendingStatus.Available, [TurnStartBefore, TurnStart, TurnStartAfter]),
        new GameHookPendingTrigger(KardsTurnEnd, GameHookPendingStatus.Available, [TurnEndBefore, TurnEnd]),
        new GameHookPendingTrigger(KardsUnitDeployed, GameHookPendingStatus.Available, [UnitDeployed]),
        new GameHookPendingTrigger(KardsAttack, GameHookPendingStatus.Available, [FlowUnitAttack]),
        new GameHookPendingTrigger(KardsOrderPlayed, GameHookPendingStatus.Available, [CardPlayed]),
        new GameHookPendingTrigger(KardsShuffle, GameHookPendingStatus.Available, [DeckShuffled]),

        new GameHookPendingTrigger(KardsUnitMobilized, GameHookPendingStatus.Deferred, [UnitPositionChanged]),
        new GameHookPendingTrigger(KardsAttacked, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsAfterAttack, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsAfterAttackHq, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsFriendlyDeath, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsFriendlyAttacked, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsFriendlyDamaged, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsHqDamaged, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsEnemyKilled, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsAfterKill, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsTargetedByOrder, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsKreditsGained, GameHookPendingStatus.Deferred, []),
        new GameHookPendingTrigger(KardsUnitLeft, GameHookPendingStatus.Deferred, []),

        new GameHookPendingTrigger(KardsFriendlyPinned, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsFriendlySuppressed, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsFriendlySilenced, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsFriendlyRetreated, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsFriendlySurvived, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsUnpinned, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsImpactUsed, GameHookPendingStatus.NotPlanned, []),
        new GameHookPendingTrigger(KardsChargeNow, GameHookPendingStatus.NotPlanned, []),
    };
}
