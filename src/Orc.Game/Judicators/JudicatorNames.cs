namespace Orc.Game.Judicators;

/// <summary>
/// 判定器名常量集（受控常量集合；J1 机制骨架）：注册源与消费引用的唯一称谓来源（防散落字面——沿用 KeywordIds 先例）。
/// 命名规范（J1 定型；J2/J3 引用基准）：英文点分式、「域.用途」两级起（必要时多级）、全小写、ordinal 逐字敏感；
/// 判定器名系代码级引用标识（机制性标识体系——与信号名 card.played 等同族），非卡面词汇域（KeywordIds 中文词属卡面词汇域）。
/// 实验性成分不在字面标注（字面保持干净）：以常量注释＋实现记录申报承载。
/// 冻结性：名称一经 J3 引用视为冻结契约（更名需申报迁移说明）。
/// 机制骨架阶段暂无条目——具体名字随各单（J2/J3/后续）提报加入。
/// </summary>
public static class JudicatorNames
{
    public const string ActionInput = "action.input.allowed";

    /// <summary>回合归属准入（签名＝（玩家）→ bool）：该玩家是否为当前行动方；三处入口同源（打牌/移动攻击/反制）。</summary>
    public const string ActionTurnOwner = "action.turn.owner";
    // ---------- J2（合法性验证替换）：默认验证判定器（四条；内置注册段固定注册——无条件可用） ----------

    /// <summary>费用检查（费用验证点专属；预打出/打出复用同一判定器——moding 改写即其全部引用点生效）。</summary>
    public const string CostCheck = "validation.cost.check";

    /// <summary>反制使用检查（反制使用验证点专属；触发器验证与入口预检同一判定源——改写两侧同步生效）。</summary>
    public const string CounterUse = "validation.counter.use";

    /// <summary>移动复验（单位移动验证点专属；执行前兜底复验）。</summary>
    public const string MoveRecheck = "validation.move.recheck";

    /// <summary>攻击复验（单位攻击验证点专属；执行前兜底复验——与移动复验相互独立改写）。</summary>
    public const string AttackRecheck = "validation.attack.recheck";

    /// <summary>抑制资格判定（签名＝（目标单位）→ <c>InhibitEligibilityFailure?</c>〔null＝可抑制〕）：
    /// 默认只判死亡与内建「无法被抑制」；固守等词条经 <c>judicatorWrappers</c> 自注册改写（返回 <c>Immune</c>）。</summary>
    public const string InhibitLegEligibility = "inhibit.leg.eligibility";

    // ---------- J3（两示范落地）：示范判定器（两条；注册经既有装配期注册面〔外部装配段〕——见 J3 实现记录） ----------

    /// <summary>卡组顶特点判定（示范①；签名＝（玩家，特点标识）→ bool）：默认按真实卡组顶求值（TagData 开放 tag）；
    /// 卡组空＝不满足（false 降级）；恒真/恒假改写经 moding（全局生效）、注销回退。</summary>
    public const string DeckTopTag = "deck.top.tag";

    /// <summary>目标候选合法性判定（示范②；签名＝（候选引用）→ bool）：默认规则＝在场单位可选**且隐蔽单位不可选**
    /// （S2 加性——隐蔽机制·豁免剔除）；改写用例＝允许/禁止某类目标可选（经 moding 整体更换选择规则）。
    /// 装配归属（S2 受控适配）＝**生产内置注册段**（<c>Match.Initialize</c> 固定注册——原「示范类经外部装配段」随 S2 提入生产装配）。</summary>
    public const string TargetCandidateEligibility = "targeting.candidate.eligibility";

    // ---------- K1（A 档 C1-C4：交战合法性判定族）：交战判定器（四条；内置注册段固定注册——无条件可用） ----------

    /// <summary>目标合法性判定（组合判定器；签名＝（攻击者, 目标引用）→ bool）：编排六重＝归属→存活/在场→烟幕→守护资格→拦截→范围
    /// （HQ 分支＝归属→占位槽→守护→拦截→范围）；候选链与复验链共用同一条目——改写后全部调用点同步生效。</summary>
    public const string CombatTargetLegal = "combat.target.legal";

    /// <summary>范围矩阵判定（签名＝（攻击者, 目标槽位）→ bool）：炮/战/轰任意线、步/坦/零类型仅相邻；类型组单源共表。</summary>
    public const string CombatRange = "combat.range";

    /// <summary>被守护攻击资格判定（签名＝（攻击者）→ bool）：炮/轰组——不含战斗机；类型组与 combat.range 共表。</summary>
    public const string CombatGuardEligibility = "combat.guard.eligibility";

    /// <summary>轰炸机拦截判定（签名＝（攻击者, 目标槽位, 目标是否战斗机）→ bool）：轰炸机∧非战斗机目标∧同线存活敌方战斗机＝拦截。</summary>
    public const string CombatInterception = "combat.interception";

    // ---------- K2（A 档 C5/C6：反击豁免与伏击条件）：反击/伏击判定器（两条；内置注册段固定注册——无条件可用） ----------

    /// <summary>反击资格判定（签名＝（攻击者, 目标）→ bool）：反击豁免表五条款、豁免优先（攻击者具冲击不受反击〔豁免族最高优先〕／目标轰炸机永不反击／
    /// 攻击者炮兵不受任何反击／攻击者轰炸机不受反击〔例外＝目标战斗机〕／其余正常）；默认互伤区与伏击资格共用同一条目——改写后全部调用点同步生效。</summary>
    public const string CombatCounterEligibility = "combat.counter.eligibility";

    /// <summary>伏击条件判定（签名＝（被攻击单位, 攻击者）→ bool）：被攻击单位攻击有效值 ＞ 攻击者防御有效值（严格大于、相等＝不命中）。</summary>
    public const string CombatAmbushCondition = "combat.ambush.condition";

    // ---------- K3（A 档 C7/C8：复验消重）：leg 资格与推进前置判定器（三条；内置注册段固定注册——无条件可用） ----------

    /// <summary>move leg 资格判定（签名＝（单位, 源位置）→ LegEligibilityFailure?〔null＝通过〕）：共享『leg 资格』条件序列
    /// （owner==current／!destroyed／CanMove／被压制／行动费／位置〔源∈支援线〕）；可用性聚合与移动复验共用同一条目——
    /// 改写后两调用点同步生效（分区条目：不影响 attack 侧）。</summary>
    public const string MoveLegEligibility = "move.leg.eligibility";

    /// <summary>attack leg 资格判定（签名＝（单位, 位置〔忽略〕）→ LegEligibilityFailure?〔null＝通过〕）：共享『leg 资格』条件序列
    /// （owner==current／!destroyed／CanAttack／被压制／行动费）；可用性聚合与攻击复验共用同一条目——
    /// 改写后两调用点同步生效（分区条目：不影响 move 侧）。</summary>
    public const string AttackLegEligibility = "attack.leg.eligibility";

    /// <summary>推进前置判定（签名＝（所有者）→ bool）：前线是否存在存活敌方单位（空前线或己方已占＝false）；
    /// 移动可用性与移动复验共用同一条目——改写后两调用点同步生效。</summary>
    public const string MoveFrontlineEnemy = "move.frontline-enemy";

    // ---------- E1（效果运行期）：无头选靶判定器（一条；内置注册段固定注册——无条件可用） ----------

    /// <summary>效果无头选靶判定（签名＝（视角卡，<c>EffectSelector</c>）→ <c>IReadOnlyList&lt;Card&gt;</c>）：
    /// 供 csx handler 经 <c>EffectRuntime.SelectAsync</c> 求值（side/zone/keyword/count；unitType 过滤与真随机待补）。
    /// moding 改写＝选择规则整体更换（全局生效）。</summary>
    public const string EffectTargetResolve = "effect.target.resolve";

    // ---------- E1-25（指挥点槽事件改进）：资源判定器（三条；内置注册段固定注册——无条件可用） ----------

    /// <summary>回合开始的槽递增（签名＝（玩家）→ int）：默认返回 <c>1</c>——<c>SettleAsync</c> 的递增来源；
    /// moding 改写＝改写「回合开始的递增」（全局生效、注销回退 1）。</summary>
    public const string PointSlotIncrement = "resource.slot.increment";

    /// <summary>额外获得槽数的**数字包裹**（签名＝（玩家，请求值）→ int）：默认返回入参**原值**（恒等）；
    /// moding 改写＝改写「额外获得 n 个指挥点槽」的实际数字（全局生效、注销回退原值）。</summary>
    public const string PointSlotGain = "resource.slot.gain";

    /// <summary>失去槽数的**数字包裹**（签名＝（玩家，请求值）→ int）：默认返回入参**原值**（恒等）；
    /// moding 改写＝改写「失去 n 个指挥点槽」的实际数字（全局生效、注销回退原值）。</summary>
    public const string PointSlotLose = "resource.slot.lose";

    // ---------- E1-25 后续（指挥点事件改造）：点数数字包裹（两条；内置注册段固定注册——无条件可用） ----------

    /// <summary>额外获得点数的**数字包裹**（签名＝（玩家，请求值）→ int）：默认返回入参**原值**（恒等）；
    /// moding 改写＝改写「获得 n 个指挥点」的实际数字（全局生效、注销回退原值）。</summary>
    public const string PointGain = "resource.point.gain";

    /// <summary>失去点数的**数字包裹**（签名＝（玩家，请求值）→ int）：默认返回入参**原值**（恒等）；
    /// moding 改写＝改写「失去 n 个指挥点」的实际数字（全局生效、注销回退原值）。</summary>
    public const string PointLose = "resource.point.lose";

    // ---------- 通用关系判定族（卡面效果可组合的原子判定；内置注册段固定注册——无条件可用） ----------

    /// <summary>相邻关系判定（签名＝（卡A, 卡B）→ bool）：两者均在场、同一条线、槽位索引差 1；任一方无位置＝false。
    /// 纯几何关系（不含归属/兵种过滤）；口径与 <c>GameEnvironment.AreAdjacent</c> 同源（同一实现）。</summary>
    public const string UnitAdjacent = "unit.adjacent";

    /// <summary>兵种判定（签名＝（卡, 兵种标识）→ bool）：卡持有单位数据且兵种集含该标识（序数不敏感）；
    /// 非单位／标识空白／无兵种＝false。</summary>
    public const string UnitTypeMatch = "unit.type";

    /// <summary>归属判定（签名＝（视角卡, 目标卡）→ bool）：两者归属同一玩家（「友方」）；
    /// 任一归属不可达（未装载/独立构造/非卡实体）＝false。</summary>
    public const string UnitSide = "unit.side";
}
