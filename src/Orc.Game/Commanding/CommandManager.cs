using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Orc.Game.Commanding;

// ─────────────────────────────────────────────────────────────────────────────
// 2C 指挥管理器（指挥系统承载；后置项 A/B/C 加性：反击豁免 / 胜负判定 / 前线阻挡）：
// ①内置流程触发器（引擎侧创建——指挥触发器 / 单位移动触发器 / 单位攻击触发器 / 造成攻击伤害触发器；
//   由对局装配期注册为底层触发器〔进注册表、可查询〕）；
// ②指挥入口（BeginCommandAsync）：一次拖拽＝一次调用链、单一公开入口方法驱动；输入＝被拖动单位引用；
//   发起判定（非己方回合 / 归属无效 / 已死亡 / 两动作均不可用 / 对局已结束）→ 指挥触发器（流程编排：读判定 → 交互 →
//   分派〔空槽→移动；敌方单位/敌方 HQ 实体引用→攻击〕→ 嵌套执行 → 外层收尾〔扣费＋两 bool 更新〕）；
// ③动作可用性聚合判定公开面（GetCommandAvailability）：bool＋原因＋候选；与流程内部同源、纯查询；
//   攻击目标筛选含轰炸机拦截（后置项 C：目标所在战线存在敌方战斗机时该战线非战斗机目标置黑——含 HQ）；
// ④移动执行（仅推进：支援线→前线；前置＝前线不存在存活敌方单位〔后置项 C；候选＋执行复验双保险〕；
//   候选＝前线空槽；无后撤/横移候选；发射 unit.position.changed——恰一次）；
// ⑤攻击执行（单位 vs 单位必经「造成攻击伤害」触发器中转；HQ 简路＝伤害经 HQ 数值路径与管线
//   〔改写→介入→应用→跑链集中触发〕、不反击、不走「造成攻击伤害」）；攻击结算＝按反击豁免判定表
//   （后置项 A：目标轰炸机永不反击 / 攻击者炮兵不受反击 / 攻击者轰炸机不受反击〔目标战斗机例外〕/
//   攻击者具冲击不受反击〔批 5——豁免族最高优先〕/ 其余正常；
//   多类型逐条适用、豁免优先；同时互伤框架——豁免方不结算反击伤害）→ 执行段公共尾部＝冲击消耗
//   （批 5；X1＝攻击即消耗——含 HQ 攻击）→ HP≤0 死亡（统一死亡流程：清位＋置毁＋
//   效果注销＋Position 置空＋card.died 恰一次）；HQ≤0 → HQ 侧统一响应（W3-3：终局判定迁至 HQ——归零检查
//   〔响应 card.stat.changed〕执行终局记录：状态置结束＋胜者＝HQ 归零方之对手；其后动作入口拒绝，
//   当次结算收尾照常完成）；
// ⑥守护维护（被守护状态：相邻〔同线槽位索引差 1〕守护者 → 获被守护；仅能被炮/轰攻击；守护者自身不可被守护；
//   由位置/入场/死亡/升级更新与词条授予-移除完成点驱动重算——底层链负责〔批 4 加性：升级与词条变更纳入驱动；
//   守护者判定双通道＝IsGuard 代码通道 / 守护词条〕）；⑦回合恢复（turn.start 处理段：行动方在场单位重置两 bool）。
// 「两 bool 只在外层更新」：内层（移动/攻击/伤害/词条）一律不写——法定写入点＝指挥收尾 / 部署链〔闪击效果——unit.deployed 发射时〕/ 回合恢复。
// W2b G3 接线（加性）：数值读取面接改——攻击力/防御力/行动费结算读点改读「有效值」（修饰机制缓存；
//   伤害值/HQ 伤害＝攻击有效值、扣减经门户〔ApplyDefenseDamageAsync〕、死亡判定＝防御有效值、可用性/扣费/复验＝行动费有效值〔K4：行动费取值经 OperateCosts 共享单元——判定/扣费同源〕）；
//   防御归零统一死亡衔接——被动触发器（响应 card.stat.changed；外部订阅通知完成之后检查）「防御有效值 ≤0 且未死亡」
//   → 统一死亡流程〔死亡清理＝效果/修饰器注销＋数值整合零新发射；先数值变化、后死亡信号〕（含修饰撤销/到期等
//   非伤害来源的 ≤0 边界；伤害路径判定为防护兜底——已死亡跳过，死亡恰一次）；互伤结算含死亡冻结防护（尸体不结算）。
// W2c X1/X2 加性：
// ①X1 触发者卡牌（TriggerCard）——4 个内置流程触发器视图承载「引发该操作的效果宿主卡引用」；填充规则：
//   主动指挥（含其嵌套调用）＝缺省空（不携带）；效果引发＝调用方显式携带（BeginCommandAsync 可选参数 / 触发器数据键）
//   且随同一效果链传递（指挥→移动/攻击；攻击→伤害链内一致）；复验只消费操作角色引用（前位、插入序），
//   触发者引用置于尾部、不参与复验（去重后计数允许多余项）。
// ②X2 效果卸载（死亡链）——修饰器清理之后、card.died 之前逐效果走统一卸载链（容器移除＋OnUnmount＋撤销登记
//   ＋总线卸载；托管清理幂等——修饰器已清、零发射），保持「先数值变化→清理→card.died」观察序。
// A4 加性（亡计与再触发）：
// ①亡计结算（死亡链新增步）——完整次序：槽位释放 → 置毁 → **亡计结算** → 词条死亡注销 → 修饰器清理 →
//   钳击失效通知 → 效果卸载 → 位置字段置空 → card.died；结算于「死亡前可观察状态」完整时执行（已置毁、
//   位置字段可读、词条行为态仍在、修饰仍生效、效果仍装载）；异常隔离（记录、不中断死亡流程）；
//   执行经再触发服务统一执行面（单源＋重入防护；无服务＝直调执行面降级）。
// ②「亡计再触发」不伴随死亡——由再触发机制（总线包装底层触发器）另行承载（见 RetriggerSystem.cs），
//   不属本死亡步语义。
// W3-3 G11 加性（HQ 实体化）：
// ①HQ 目标承载＝HQ 实体引用（hq.Ref）——候选/交互/执行以实体引用为准（槽位引用不再作为 HQ 目标产出；
//   槽位关系仅用于布局语义判定〔占位/邻位/守护/轰炸机拦截/范围矩阵基准〕）；
// ②攻击 HQ（简路骨架保持：不反击、不经过「造成攻击伤害」、不触发死亡/防御归零单位语义）：伤害数值改走
//   HQ 数值路径与管线（改写→介入→应用→跑链集中触发）；终局判定不内联——HQ 侧统一响应（归零检查触发器：
//   任何来源致 HQ 血量 ≤0 均经同一判定；「先数值变化、后终局记录」观察序）；
// ③被守护状态以 HQ 实体为单元维护（守护者/拦截基准仍为槽位关系与既有语义）；
// ④HQ 终局响应装配注入（延迟读取；胜者＝HQ 归零方之对手；脱局＝防御降级）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 指挥管理器（2C；对局级服务）：指挥流程入口与编排、KARDS 规则矩阵、
/// 攻击结算与统一死亡流程、守护维护、回合恢复。
/// 依赖：引擎（发射/触发）、战场（候选计算与布局）、目标选择管理器（交互承载）、
/// 玩家对（敌我判定）、当前行动方提供器（延迟读取——装配顺序：回合管理器后置）、
/// 对局生命周期（终局门禁与胜者记录——后置项 B；可空＝独立构造场景无门禁）。
/// </summary>
public sealed class CommandManager
{
    /// <summary>默认基础互伤在「造成攻击伤害」触发器内的事件优先级（数值大者后执行——伏击改写先判定）。</summary>
    private const int DefaultDamagePriority = 100;

    /// <summary>
    /// 「攻击执行」默认链在「单位攻击触发器」内的事件优先级（数值大者后执行）。
    /// 效果预制体 injects 默认优先级为 0，因此攻击前类效果（如 H39 sWG「抑制被本单位攻击的单位」）
    /// 先于伤害结算执行——否则目标的防御增益会先吸收一次伤害，剥光失去意义。
    /// </summary>
    private const int AttackExecutionPriority = 100;

    private readonly LogicEngine _engine;
    private readonly Battlefield _battlefield;
    private readonly TargeterManager _targeterManager;
    private readonly IReadOnlyList<Player> _players;
    private readonly Func<Player?> _currentPlayerProvider;
    private readonly MatchLifecycle? _lifecycle;
    private readonly Func<Player, Entity?, Entity?, bool> _inputAllowed;
    private readonly RetriggerSystem? _retrigger; // A4：再触发服务（亡计结算执行面/防护的统一入口；可空＝独立构造降级）
    private readonly Func<string, JudicatorBinding>? _validationJudicatorResolver; // J2：验证判定器解析器（复验绑定解析——对局＝注册表；缺省＝内置默认）
    private readonly Func<UnitCard, Ref<Entity>, bool> _combatTargetLegal; // K1：目标合法性判定通道（（攻击者, 目标引用）→ bool——对局＝combat.target.legal 条目句柄；缺省＝内置默认）
    private readonly Func<UnitCard, UnitCard, bool> _combatCounterEligibility; // K2：反击资格判定通道（（攻击者, 目标）→ bool——对局＝combat.counter.eligibility 条目句柄；缺省＝内置默认）
    private readonly Func<UnitCard, UnitCard, bool> _combatAmbushCondition; // K2：伏击条件判定通道（（被攻击单位, 攻击者）→ bool——对局＝combat.ambush.condition 条目句柄；缺省＝内置默认）
    private readonly Func<UnitCard, Slot?, LegEligibilityFailure?> _moveLegEligibility; // K3：move leg 资格判定通道（（单位, 源位置）→ 结果〔null＝通过〕——对局＝move.leg.eligibility 条目句柄；缺省＝内置默认）
    private readonly Func<UnitCard, LegEligibilityFailure?> _attackLegEligibility; // K3：attack leg 资格判定通道（（单位）→ 结果〔null＝通过〕——对局＝attack.leg.eligibility 条目句柄；缺省＝内置默认）
    private readonly Func<Player, bool> _moveFrontlineEnemy; // K3：推进前置判定通道（（所有者）→ bool——对局＝move.frontline-enemy 条目句柄；缺省＝内置默认）
    private readonly Trigger<CardTriggerView> _defenseDepletionTrigger; // W2b：防御归零检查（被动；挂载于更新总线）
    private readonly Trigger<CardTriggerView> _hqZeroTrigger; // W3-3：HQ 归零检查（被动；挂载于更新总线）

    private readonly HashSet<UnitCard> _guardedUnits = new();
    private readonly HashSet<Hq> _guardedHqs = new(); // W3-3：被守护 HQ 以 HQ 实体为单元
    private readonly PincerRegistry _pincers = new(); // A2：钳击关系注册表（对局级——一对一占用约束）

    /// <summary>
    /// 创建指挥管理器（创建四个内置流程触发器并装配默认链；订阅位置/入场/死亡更新以维护被守护状态）。
    /// </summary>
    /// <param name="engine">对局引擎（发射更新 / 触发子触发器）。</param>
    /// <param name="battlefield">战场（候选计算与布局真源）。</param>
    /// <param name="targeterManager">目标选择管理器（指挥交互承载——一次拖拽＝一次请求）。</param>
    /// <param name="players">双玩家（敌我判定；行动方＋其对手）。</param>
    /// <param name="currentPlayerProvider">当前行动方提供器（延迟读取；缺省＝null＝回合未就绪——发起校验将拒绝）。</param>
    /// <param name="lifecycle">对局生命周期（终局门禁＋胜者记录——后置项 B；缺省＝null＝独立构造场景无门禁）。</param>
    /// <param name="retrigger">再触发服务（A4；亡计结算执行面/防护的统一入口——缺省＝null＝独立构造降级：死亡链直调亡计执行面〔无重入防护〕）。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（J2；按名解析——对局路径＝注册表解析〔复验判定器经固定内置注册段注册〕；
    /// 缺省＝null＝独立构造路径——内置默认解析〔构造即可用：注入本管理器对局级只读设施引用〕）。</param>
    /// <param name="combatTargetLegal">目标合法性判定通道（K1；（攻击者, 目标引用）→ bool——对局路径＝固定内置注册段注册的
    /// combat.target.legal 条目〔经条目句柄调用——moding 动态生效〕；缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <param name="combatCounterEligibility">反击资格判定通道（K2；（攻击者, 目标）→ bool——对局路径＝固定内置注册段注册的
    /// combat.counter.eligibility 条目〔经条目句柄调用——moding 动态生效；默认互伤区与伏击资格共用〕；
    /// 缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <param name="combatAmbushCondition">伏击条件判定通道（K2；（被攻击单位, 攻击者）→ bool——对局路径＝固定内置注册段注册的
    /// combat.ambush.condition 条目〔经条目句柄调用——moding 动态生效；伏击效果经装载上下文取用〕；
    /// 缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <param name="moveLegEligibility">move leg 资格判定通道（K3；（单位, 源位置）→ 结果〔null＝通过〕——对局路径＝固定内置注册段注册的
    /// move.leg.eligibility 条目〔经条目句柄调用——moding 动态生效；可用性与移动复验共用〕；
    /// 缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <param name="attackLegEligibility">attack leg 资格判定通道（K3；（单位）→ 结果〔null＝通过〕——对局路径＝固定内置注册段注册的
    /// attack.leg.eligibility 条目〔经条目句柄调用——moding 动态生效；可用性与攻击复验共用〕；
    /// 缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <param name="moveFrontlineEnemy">推进前置判定通道（K3；（所有者）→ bool——对局路径＝固定内置注册段注册的
    /// move.frontline-enemy 条目〔经条目句柄调用——moding 动态生效；移动可用性与移动复验共用〕；
    /// 缺省＝null＝独立构造路径——内置默认判定器〔构造即可用〕）。</param>
    /// <exception cref="ArgumentNullException">engine / battlefield / targeterManager / players 为 null。</exception>
    /// <exception cref="ArgumentException">players 不是两名玩家。</exception>
    /// <exception cref="KeyNotFoundException">解析未注册名（解析动作即校验——装配期 fail-fast）。</exception>
    public CommandManager(
        LogicEngine engine,
        Battlefield battlefield,
        TargeterManager targeterManager,
        IReadOnlyList<Player> players,
        Func<Player?>? currentPlayerProvider = null,
        MatchLifecycle? lifecycle = null,
        RetriggerSystem? retrigger = null,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null,
        Func<UnitCard, Ref<Entity>, bool>? combatTargetLegal = null,
        Func<UnitCard, UnitCard, bool>? combatCounterEligibility = null,
        Func<UnitCard, UnitCard, bool>? combatAmbushCondition = null,
        Func<UnitCard, Slot?, LegEligibilityFailure?>? moveLegEligibility = null,
        Func<UnitCard, LegEligibilityFailure?>? attackLegEligibility = null,
        Func<Player, bool>? moveFrontlineEnemy = null,
        Func<Player, Entity?, Entity?, bool>? inputAllowed = null,
        KeywordSnapshot? keywordSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(targeterManager);
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count != 2)
        {
            throw new ArgumentException("指挥管理器需要恰两名玩家（敌方判定依据）。", nameof(players));
        }

        _engine = engine;
        _battlefield = battlefield;
        _targeterManager = targeterManager;
        _players = players;
        _currentPlayerProvider = currentPlayerProvider ?? (() => null);
        _lifecycle = lifecycle;
        _inputAllowed = inputAllowed ?? ((_, _, _) => true);
        _retrigger = retrigger;
        _validationJudicatorResolver = validationJudicatorResolver;

        // K1 加性（A 档 C1-C4：交战合法性判定族）：目标合法性判定通道——对局路径＝注入的条目调用委托
        // （经注册表条目句柄——每次调用经统一解析点，moding 动态生效、候选/复验两链同步）；
        // 独立构造路径＝内置默认判定器（构造即可用——注入本管理器对局级只读设施引用；子规则内置实例经实例主方法调用——不内联）。
        _combatTargetLegal = combatTargetLegal ?? CreateBuiltInCombatTargetLegal();

        // K2 加性（A 档 C5/C6：反击豁免与伏击条件）：反击资格与伏击条件判定通道——对局路径＝注入的条目调用委托
        // （经注册表条目句柄——每次调用经统一解析点，moding 动态生效；反击资格＝默认互伤区与伏击资格共用）；
        // 独立构造路径＝内置默认判定器（构造即可用——零注入实例经实例主方法调用——不内联）。
        _combatCounterEligibility = combatCounterEligibility ?? CreateBuiltInCombatCounterEligibility();
        _combatAmbushCondition = combatAmbushCondition ?? CreateBuiltInCombatAmbushCondition();

        // K3 加性（A 档 C7/C8：复验消重）：leg 资格与推进前置判定通道——对局路径＝注入的条目调用委托
        // （经注册表条目句柄——每次调用经统一解析点，moding 动态生效、可用性/复验四调用点同源）；
        // 独立构造路径＝内置默认判定器（构造即可用——注入本管理器对局级只读设施引用；条目化调用形态同构——不内联）。
        _moveLegEligibility = moveLegEligibility ?? CreateBuiltInMoveLegEligibility();
        _attackLegEligibility = attackLegEligibility ?? CreateBuiltInAttackLegEligibility();
        _moveFrontlineEnemy = moveFrontlineEnemy ?? CreateBuiltInMoveFrontlineEnemy();

        // ① 指挥触发器（内置；默认链＝指挥流程编排）
        CommandTrigger = new Trigger<CommandTriggerView>("指挥触发器");
        CommandTrigger.Register("指挥流程", HandleCommandFlowAsync);

        // ② 单位移动触发器（执行前复验＝验证判定器承载——按名绑定移动复验判定器〔J2〕；默认链＝移动执行）
        //    解析动作即校验：对局路径＝注册表解析（装配期已就绪——注册段先于本创建位）；独立构造＝内置默认（构造即可用）。
        UnitMoveTrigger = new Trigger<UnitMoveTriggerView>("单位移动触发器");
        UnitMoveTrigger.BindValidation(
            ResolveRecheckBinding(JudicatorNames.MoveRecheck),
            refs => refs.Count > 0 ? refs[0] : null,
            refs => refs.Count > 0 && refs[0].Value is CardBase card ? card.Owner?.Index : null);
        UnitMoveTrigger.Register("移动执行", HandleUnitMoveAsync);

        // ③ 单位攻击触发器（执行前复验＝验证判定器承载——按名绑定攻击复验判定器〔J2〕；默认链＝攻击执行）
        UnitAttackTrigger = new Trigger<UnitAttackTriggerView>("单位攻击触发器");
        UnitAttackTrigger.BindValidation(
            ResolveRecheckBinding(JudicatorNames.AttackRecheck),
            refs => refs.Count > 0 ? refs[0] : null,
            refs => refs.Count > 0 && refs[0].Value is CardBase card ? card.Owner?.Index : null);
        UnitAttackTrigger.Register("攻击执行", HandleUnitAttackAsync, AttackExecutionPriority);

        // ④ 造成攻击伤害触发器（与对局同生的内置共享流程触发器；伏击加载时在此注册改写逻辑）
        //    默认基础互伤＝高优先级（后执行）——伏击改写（注册默认优先级）先判定；改写生效＝替代默认。
        AttackDamageTrigger = new Trigger<AttackDamageTriggerView>("造成攻击伤害触发器");
        AttackDamageTrigger.Register("默认基础互伤", HandleDefaultAttackDamageAsync, DefaultDamagePriority);

        // 词条装载上下文（卡牌加载时取用；伏击注册「造成攻击伤害」改写）。
        // A2 加性：随带对局服务——钳击（同伴选择交互/候选枚举/关系注册表）与被压制（当前行动方）效果的运行逻辑取用；
        // K2 加性：随带判定器通道——伏击效果资格（combat.counter.eligibility）与条件（combat.ambush.condition）判定取用
        // （缺通道＝组件静默不注册——防御；对局/独立构造路径下本管理器两通道恒非空）。
        // 批 4 加性（守护词条化）：随带词条变更观察者——词条链真实授予/移除完成点驱动守护维护重算
        // （授予-移除-复装动作完成即随动；同步执行、变更驱动——无对局上下文＝跳过、不抛错）。
        // lambda 免——直接引用（构造时点这些字段均已赋值）。
        KeywordLoadContext = new KeywordLoadContext(
            engine, AttackDamageTrigger, _targeterManager, _battlefield, _pincers, _currentPlayerProvider,
            _combatCounterEligibility, _combatAmbushCondition,
            keywordChangeObserver: MaintainGuardState,
            // S5：注入本局词条快照——授予链/绑定查询读快照，运行期覆盖不影响进行中对局。
            keywordSnapshot: keywordSnapshot);

        // 守护维护：由位置/入场/死亡/升级更新驱动（凡影响占用布局或守护者身份的变动均等效触达——
        // 批 4 加性：升级可能经词条集替换改变守护者身份，unit.upgraded 纳入订阅；词条授予-移除-复装
        // 经词条链完成点观察者驱动〔见下方 KeywordLoadContext 注入〕——同步、变更驱动）
        _engine.Subscribe((updateType, _, _) =>
        {
            if (updateType == GameUpdates.UnitPositionChanged
                || updateType == GameUpdates.UnitJoined
                || updateType == GameUpdates.UnitDeployed
                || updateType == GameUpdates.CardDied
                || updateType == GameUpdates.UnitUpgraded)
            {
                MaintainGuardState();
            }

            return Task.CompletedTask;
        });

        // G14补（S10）在场回合数：回合事件驱动计数——单位归属玩家的回合正式开始信号（turn.start）时递增
        // （单方步进：仅「回合开始方==单位归属玩家」时递增；静默数据变更——不新增信号/不发更新）。
        // 锚点：turn.start＝正式开始（递增点）——turn.start.after 读到递增后的新值（与既有锚点约定衔接）。
        _engine.Subscribe((updateType, payload, _) =>
        {
            if (updateType == GameUpdates.TurnStart
                && payload is not null
                && payload.TryGetValue(GameUpdates.PayloadPlayer, out var value)
                && value is Player turnPlayer)
            {
                AdvanceTurnsInPlay(turnPlayer);
            }

            return Task.CompletedTask;
        });

        // G3 修饰机制（W2b 加性）：防御归零的统一死亡衔接——被动触发器（挂载更新总线、响应 card.stat.changed）：
        // 检查在「外部订阅通知完成之后」（总线触发器阶段）执行——「先数值变化、后死亡信号」：
        // card.stat.changed（致死结算段、数值落定）→ 死亡清理 → card.died（清理就绪后、恰一次）。
        _defenseDepletionTrigger = new Trigger<CardTriggerView>(
            "防御归零检查触发器",
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardTriggerView>("防御归零检查", HandleDefenseDepletionAsync) },
            hooks: new[] { GameUpdates.CardStatChanged });
        _engine.Bus.Mount(_defenseDepletionTrigger);

        // W3-3（G11 HQ 实体化）：HQ 归零的统一响应——被动触发器（挂载更新总线、响应 card.stat.changed）：
        // 「终局判定迁至 HQ」的触发侧——与防御归零检查同构（检查在「外部订阅通知完成之后」〔总线触发器阶段〕执行；
        // 「先数值变化（通用数据改变更新）、后终局记录」观察序）；任何来源（攻击伤害/效果/修饰撤销等）使 HQ 血量 ≤0
        // 均经同一判定：执行主体＝HQ 侧统一响应（RespondToZero——不内联于攻击流程）。
        _hqZeroTrigger = new Trigger<CardTriggerView>(
            "HQ 归零检查触发器",
            TriggerKind.Passive,
            events: new[] { new TriggerEvent<CardTriggerView>("HQ 归零检查", HandleHqZeroCheckAsync) },
            hooks: new[] { GameUpdates.CardStatChanged });
        _engine.Bus.Mount(_hqZeroTrigger);

        // W3-3：终局响应装配注入（延迟读取）——HQ 随玩家创建，对局服务在此绑定：
        // 胜者＝HQ 归零方之对手；脱局场景（本对象独立构造、无生命周期提供）＝HQ 侧防御降级（归零不记录、不抛错）。
        foreach (var player in players)
        {
            player.Hq.ConfigureTerminalResponse(() => EnemyOf(player), () => _lifecycle);
        }
    }

    // ---------- 内置流程触发器（公开只读；对局装配期注册为底层触发器） ----------

    /// <summary>指挥触发器（内置；一次指挥流程的编排承载——读判定 → 交互 → 分派 → 嵌套执行 → 外层收尾）。</summary>
    public Trigger<CommandTriggerView> CommandTrigger { get; }

    /// <summary>单位移动触发器（内置；执行段＝槽位变更＋发射 unit.position.changed；复验＝执行前兜底）。</summary>
    public Trigger<UnitMoveTriggerView> UnitMoveTrigger { get; }

    /// <summary>单位攻击触发器（内置；执行段＝HQ 简路〔伤害经 HQ 数值路径〕/ 单位互伤经「造成攻击伤害」）。</summary>
    public Trigger<UnitAttackTriggerView> UnitAttackTrigger { get; }

    /// <summary>「造成攻击伤害」共享流程触发器（内置；单位 vs 单位攻击结算必经；伏击效果的改写注册口）。</summary>
    public Trigger<AttackDamageTriggerView> AttackDamageTrigger { get; }

    /// <summary>词条装载上下文（卡牌加载时取用；经对局装配注入卡牌库）。
    /// A2 加性：随带对局服务面（目标选择管理器／战场／钳击关系注册表／当前行动方提供器——钳击与被压制效果的运行逻辑取用）。</summary>
    public KeywordLoadContext KeywordLoadContext { get; }

    /// <summary>钳击关系注册表（A2 加性；对局级服务——一对一占用约束的查询与登记；只读转发面）。</summary>
    public PincerRegistry Pincers => _pincers;

    // ---------- 离场受控入口（S9 加性；转换等「非死亡离场」组合的公共面） ----------

    /// <summary>
    /// 离场（S9；单位「不再在场」——转换等「非死亡离场」组合的公共受控入口）：
    /// 槽位释放（变空槽）→ <see cref="UnitStateData.Position"/> 置空（「有位置」判据失效）→
    /// 离场失效通知（关系类观察者——钳击配对失效、对侧即时失去；「不再在场」判据与死亡路径同源）。
    /// 非死亡路径：不发 card.died（非死亡——不得误导「被消灭」类观察）、不发 card.discarded（非弃置）、
    /// 不发 unit.position.changed（专属移动语义）；不执行死亡链其余步骤（亡计/词条注销/修饰器清理/效果卸载）——
    /// 销毁经配套销毁面（<see cref="LogicEngine.DestroyCard"/>——杀＋card.destroyed＋资源清理）由调用方组合完成
    /// （转换组合＝离场＋销毁＋Create＋Place；顺序与失败一致性由组合承载方〔效果语义/测试组合〕设计与申报）。
    /// 光环/门禁类为「读取时判定」（未在场自然不命中），无需额外通知。
    /// 终局门禁：对局已结束＝拒绝。独立构造路径（无生命周期）＝无门禁。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    /// <exception cref="InvalidOperationException">对局已结束；或单位尚未单位化／已死亡已毁／不在场（前置契约不符——明确拒绝）。</exception>
    public async Task LeaveBattlefieldAsync(UnitCard unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (_lifecycle is not null && !_lifecycle.IsActionAllowed)
        {
            throw new InvalidOperationException(_lifecycle.IsEnded
                ? "对局已结束（终局），离场动作被拒绝。"
                : "对局不在进行相位（准备/换牌），离场动作被拒绝。");
        }

        if (!unit.TryGetData<UnitStateData>(out var state))
        {
            throw new InvalidOperationException(
                $"单位 '{unit.Name}' 尚未单位化（离场动作被拒绝——前置契约不符）。");
        }

        if (state.IsDestroyed)
        {
            throw new InvalidOperationException(
                $"单位 '{unit.Name}' 已死亡/已毁（离场动作被拒绝——前置契约不符）。");
        }

        if (state.Position is not { } slot)
        {
            throw new InvalidOperationException(
                $"单位 '{unit.Name}' 当前不在场（离场动作被拒绝——前置契约不符）。");
        }

        await GameUpdates.EmitCardLeaving(_engine,unit,"leave",ct);
        // ① 槽位释放（变空槽）→ ② 不再在场（Position 置空——「有位置」判据失效）
        slot.Clear();
        state.Position = null;

        // ③ 离场失效通知（关系类观察者——既有「离场失效通知」面；死亡路径之外的第二接入点，转换路径接入）
        await PincerRules.OnUnitLeftBattlefieldAsync(unit, _pincers, ct);
    }

    /// <summary>防御归零检查触发器（内置；被动——挂载更新总线、响应 card.stat.changed；公开只读）。
    /// 用途：内置事件（「防御归零检查」）的寻址面——注册项句柄经 <see cref="Trigger{TView}.InitialRegistrations"/> 供给（moding（逻辑替换）等场景）。</summary>
    public Trigger<CardTriggerView> DefenseDepletionTrigger => _defenseDepletionTrigger;

    /// <summary>HQ 归零检查触发器（内置；被动——挂载更新总线、响应 card.stat.changed；公开只读）。
    /// 用途：内置事件（「HQ 归零检查」）的寻址面——注册项句柄经 <see cref="Trigger{TView}.InitialRegistrations"/> 供给（moding（逻辑替换）等场景）。</summary>
    public Trigger<CardTriggerView> HqZeroTrigger => _hqZeroTrigger;

    // ---------- ① 指挥入口（一次拖拽＝一次调用链、单一公开入口方法） ----------

    /// <summary>
    /// 指挥单位（一次调用链：发起判定 → 指挥触发器〔交互 → 分派 → 嵌套执行 → 收尾〕→ 结果）：
    /// 输入＝被拖动单位引用（行动方从单位归属与对局状态推导——无需额外上下文参数）。
    /// 发起拒绝（非己方回合 / 归属无效 / 已死亡 / 两动作均不可用）＝不进入交互、零副作用、不发射更新；
    /// 取消＝零副作用；成功＝完整执行（含以攻击者死亡告终的攻击结算——照常扣费、照常清位）。
    /// X1 加性：<paramref name="triggerCard"/>＝触发者卡牌（效果引发情形——引发本次操作的效果宿主卡引用；
    /// 显式携带、缺省＝null＝玩家主动操作）；携带后随指挥链透传（含嵌套的移动/攻击与伤害调用）。
    /// 装配性错误（未单位化）＝明确异常（fail-fast）。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    /// <exception cref="InvalidOperationException">单位尚未单位化（装配性错误、fail-fast）。</exception>
    public async Task<CommandResult> BeginCommandAsync(
        UnitCard unit, CancellationToken ct = default, Ref<Entity>? triggerCard = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RequireUnitized(unit);

        // 终局门禁（后置项 B）：对局已结束＝动作入口拒绝（零副作用、状态不推进；只读查询面不经此入口）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return CommandResult.Failure(CommandFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return CommandResult.Failure(CommandFailureReason.PhaseBlocked);
            }
        }

        // 发起判定（纯查询、与流程内部同源）
        var availability = GetCommandAvailability(unit);
        if (availability.IneligibleReason is { } ineligible)
        {
            return CommandResult.Failure(MapIneligible(ineligible));
        }

        if (!availability.AnyActionAvailable)
        {
            return CommandResult.Failure(CommandFailureReason.NoActionAvailable);
        }

        // 动作作用域（UI 消费桥接）：一次拖拽指挥链产生的事件聚合为一段。
        await using var _actionScope = _engine.BeginAction();

        var box = new CommandFlowBox();
        var data = new Dictionary<string, object?>
        {
            [CommandDataKeys.Card] = unit.Ref,
            [CommandDataKeys.Player] = unit.Owner,
            [CommandDataKeys.FlowBox] = box,
        };
        if (triggerCard is not null)
        {
            data[CommandDataKeys.TriggerCard] = triggerCard; // X1：效果引发情形显式携带（玩家主动操作＝缺省空）
        }

        await CommandTrigger.InvokeAsync(_engine, data, ct);

        return box.Result ?? CommandResult.Failure(CommandFailureReason.CommandFlowFault);
    }

    // ---------- ①′ 独立移动/攻击入口（I3-a：各自跑限定候选交互、复用同一执行链） ----------

    /// <summary>
    /// 独立移动入口（I3-a；与拖拽入口 <see cref="BeginCommandAsync"/> 共用执行真源）：
    /// 发起判定（与 <see cref="GetCommandAvailability"/> 同源）→ 限定候选交互（候选＝移动候选＝前线空槽；
    /// 槽位参数＝本单位〔被拖动单位〕）→ 确认＝复用同一移动执行链（验证→执行→扣费→清位）；
    /// 取消/发起拒绝＝零副作用（不发更新、不扣费、不清位）；执行复验拒绝＝不消耗行动（不扣费、不清位）。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    /// <exception cref="InvalidOperationException">单位尚未单位化（装配性错误、fail-fast）。</exception>
    public async Task<CommandResult> BeginMoveAsync(UnitCard unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RequireUnitized(unit);

        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return CommandResult.Failure(CommandFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return CommandResult.Failure(CommandFailureReason.PhaseBlocked);
            }
        }

        var availability = GetCommandAvailability(unit);
        if (availability.IneligibleReason is { } ineligible)
        {
            return CommandResult.Failure(MapIneligible(ineligible));
        }

        if (!availability.Move.CanUse)
        {
            return CommandResult.Failure(CommandFailureReason.NoActionAvailable);
        }

        await using var _actionScope = _engine.BeginAction();

        var (kind, selected, targeting) = await RunSelectorAsync(unit, availability.Move.Candidates, ct);
        if (kind == SelectorOutcomeKind.Cancelled)
        {
            return CommandResult.Cancelled(targeting); // 取消＝零副作用
        }

        if (kind != SelectorOutcomeKind.Confirmed || selected is null)
        {
            return CommandResult.Failure(CommandFailureReason.TargetingFailed, targeting);
        }

        var box = new CommandFlowBox();
        await DispatchMoveAsync(unit, selected, box, new Context(), ct, triggerCard: null);
        return box.Result ?? CommandResult.Failure(CommandFailureReason.CommandFlowFault);
    }

    /// <summary>
    /// 独立攻击入口（I3-a；与拖拽入口 <see cref="BeginCommandAsync"/> 共用执行真源）：
    /// 发起判定（与 <see cref="GetCommandAvailability"/> 同源）→ 限定候选交互（候选＝攻击候选＝合法敌方单位/敌方 HQ；
    /// 槽位参数＝本单位〔被拖动单位〕）→ 确认＝复用同一攻击执行链（验证→执行→扣费→清位）；
    /// 取消/发起拒绝＝零副作用；执行复验拒绝＝不消耗行动（不扣费、不清位）。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    /// <exception cref="InvalidOperationException">单位尚未单位化（装配性错误、fail-fast）。</exception>
    public async Task<CommandResult> BeginAttackAsync(UnitCard unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RequireUnitized(unit);

        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return CommandResult.Failure(CommandFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return CommandResult.Failure(CommandFailureReason.PhaseBlocked);
            }
        }

        var availability = GetCommandAvailability(unit);
        if (availability.IneligibleReason is { } ineligible)
        {
            return CommandResult.Failure(MapIneligible(ineligible));
        }

        if (!availability.Attack.CanUse)
        {
            return CommandResult.Failure(CommandFailureReason.NoActionAvailable);
        }

        await using var _actionScope = _engine.BeginAction();

        var (kind, selected, targeting) = await RunSelectorAsync(unit, availability.Attack.Candidates, ct);
        if (kind == SelectorOutcomeKind.Cancelled)
        {
            return CommandResult.Cancelled(targeting); // 取消＝零副作用
        }

        if (kind != SelectorOutcomeKind.Confirmed || selected is null)
        {
            return CommandResult.Failure(CommandFailureReason.TargetingFailed, targeting);
        }

        var box = new CommandFlowBox();
        await DispatchAttackAsync(unit, selected, box, new Context(), ct, triggerCard: null);
        return box.Result ?? CommandResult.Failure(CommandFailureReason.CommandFlowFault);
    }

    /// <summary>
    /// 限定候选交互（独立入口共用；场景＝场上单位指向）：单选槽（槽位参数＝被拖动单位）＋候选集合粗筛；
    /// 一次 Begin 一次 Complete 按槽位名回填（沿用既有桥接契约）。取消/失败经三态类别表达。
    /// </summary>
    private async Task<(SelectorOutcomeKind Kind, Ref<Entity>? Selected, TargeterResult? Result)> RunSelectorAsync(
        UnitCard unit, IReadOnlyList<Ref<Entity>> candidates, CancellationToken ct)
    {
        Ref<Entity>? selected = null;
        var result = await _targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                new ReferenceSetParameter(candidates, 1, 1, tag: unit));

            // 非法选择＝同一选择器重入（Q8a：内部处理、支持重试）。
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<Ref<Entity>>();
            }

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            selected = step.Value;
            return TargeterResult.Ok();
        });

        if (result.IsCancelled)
        {
            return (SelectorOutcomeKind.Cancelled, null, result);
        }

        if (!result.IsOk)
        {
            return (SelectorOutcomeKind.Failed, null, result);
        }

        if (selected is null || !selected.IsAlive)
        {
            return (SelectorOutcomeKind.Failed, null, result);
        }

        return (SelectorOutcomeKind.Confirmed, selected, result);
    }

    /// <summary>独立入口选择结局（内部；三态）。</summary>
    private enum SelectorOutcomeKind
    {
        /// <summary>确认（携带选中引用）。</summary>
        Confirmed,

        /// <summary>取消（前端主动；零副作用）。</summary>
        Cancelled,

        /// <summary>失败（无可用候选/交互异常等；细节经结果对象透传）。</summary>
        Failed,
    }

    // ---------- ② 动作可用性聚合判定（公开面；纯查询、与流程内部同源） ----------

    /// <summary>
    /// 动作可用性聚合判定（纯查询——无副作用、不发更新、不启动交互、不改变任何状态）：
    /// 流程级资格（己方回合 ∧ Owner＝当前行动方 ∧ 在场未死亡）＋ 动作级（bool ∧ 被压制 ∧ 费用 ∧ 候选可行性——一次聚合）。
    /// K3：leg 资格（流程级＋动作级前置：owner==current／!destroyed／CanMove·CanAttack／被压制／行动费／位置〔源∈支援线〕）
    /// 经共享 leg 条目单源取用（原内联逐条条件迁入——与复验判定器同源）；流程级原因由 leg 结果分类提取（读取路由——非第二判定源）。
    /// 移动候选＝前线任意空槽引用；攻击候选＝合法敌方单位/敌方 HQ 引用（范围矩阵 ∧ 烟幕 ∧ 守护筛选后）。
    /// 矩阵断言可直接经本公开面进行（不必驱动完整拖拽交互）。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    /// <exception cref="InvalidOperationException">单位尚未单位化（装配性错误、fail-fast）。</exception>
    public CommandAvailability GetCommandAvailability(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RequireUnitized(unit);

        if (unit.Owner is { } inputOwner && !_inputAllowed(inputOwner, unit, null))
            return new CommandAvailability(CommandBlockReason.InteractionBlocked,
                CommandActionAvailability.Blocked(CommandBlockReason.InteractionBlocked),
                CommandActionAvailability.Blocked(CommandBlockReason.InteractionBlocked));

        // K3：leg 资格先行（共享条件源——可用性与复验四调用点同源）；移动的位置参数＝当前位置（仅推进语义）。
        var moveLegFailure = _moveLegEligibility(unit, unit.GetData<UnitStateData>().Position);
        var attackLegFailure = _attackLegEligibility(unit);

        // 流程级资格原因＝leg 结果的分类提取（NonOwnerTurn/OwnerInvalid/UnitDead——两侧 leg 流程段同源、结果一致；
        // 分类为读取路由，不重新判断条件——无第二判定源）。
        var ineligible = FlowLevelIneligibleReason(moveLegFailure) ?? FlowLevelIneligibleReason(attackLegFailure);
        return new CommandAvailability(
            ineligible,
            ComputeMoveAvailability(unit, moveLegFailure),
            ComputeAttackAvailability(unit, attackLegFailure));
    }

    private CommandActionAvailability ComputeMoveAvailability(UnitCard unit, LegEligibilityFailure? legFailure)
    {
        // K3：leg 结果→动作级阻断原因（映射为纯读取；leg 内部顺序＝现状优先级：can→suppressed→cost→position）。
        if (legFailure is { } failure)
        {
            return CommandActionAvailability.Blocked(MapLegFailure(failure));
        }

        var owner = unit.Owner!; // leg 资格通过 ⇒ owner 非 null（归属检查见 leg 条件序列）

        // 推进前置（后置项 C；K3：经 move.frontline-enemy 条目——与复验同源）：前线不存在存活敌方单位方可推进
        //（空前线或己方已占；敌方清空后实时恢复）
        if (!CommandMovementRules.IsRetreat(_battlefield, unit, unit.GetData<UnitStateData>().Position) && _moveFrontlineEnemy(owner))
        {
            return CommandActionAvailability.Blocked(CommandBlockReason.NoCandidates);
        }

        // 移动候选＝前线任意空槽（不被邻位动态规则约束；无后撤/横移候选）
        var candidates = new List<Ref<Entity>>();
        foreach (var slot in CommandMovementRules.Destinations(_battlefield, unit, unit.GetData<UnitStateData>().Position))
        {
            if (slot.IsEmpty)
            {
                candidates.Add(slot.Ref);
            }
        }

        return candidates.Count == 0
            ? CommandActionAvailability.Blocked(CommandBlockReason.NoCandidates)
            : CommandActionAvailability.Available(candidates);
    }

    private CommandActionAvailability ComputeAttackAvailability(UnitCard unit, LegEligibilityFailure? legFailure)
    {
        // K3：leg 结果→动作级阻断原因（映射为纯读取；leg 内部顺序＝现状优先级：can→suppressed→cost）。
        if (legFailure is { } failure)
        {
            return CommandActionAvailability.Blocked(MapLegFailure(failure));
        }

        var candidates = CollectAttackCandidates(unit).Where(r => _inputAllowed(unit.Owner!, unit, r.Value)).ToList();
        return candidates.Count == 0
            ? CommandActionAvailability.Blocked(CommandBlockReason.NoCandidates)
            : CommandActionAvailability.Available(candidates);
    }

    /// <summary>
    /// 攻击候选收集（合法敌方单位/敌方 HQ 引用；索引序＝敌支援线 → 前线 → 敌 HQ）：
    /// 收集/枚举保留于本管理器（遍历结构与顺序＝应用流程逻辑）；逐候选合法性经目标合法性判定器
    /// （K1：combat.target.legal——六重编排〔归属/存活-在场/烟幕/守护资格/拦截/范围〕由判定器承载；本处只调用不判断）。
    /// </summary>
    private List<Ref<Entity>> CollectAttackCandidates(UnitCard attacker)
    {
        var result = new List<Ref<Entity>>();
        var owner = attacker.Owner;
        if (owner is null)
        {
            return result;
        }

        var enemy = EnemyOf(owner);
        if (enemy is null)
        {
            return result;
        }

        var enemyLine = _battlefield.GetSupportLine(enemy);
        for (var i = 0; i < enemyLine.Count; i++)
        {
            if (enemyLine[i].Occupant is UnitCard target && _combatTargetLegal(attacker, target.Ref))
            {
                result.Add(target.Ref);
            }
        }

        var frontLine = _battlefield.FrontLine;
        for (var i = 0; i < frontLine.Count; i++)
        {
            if (frontLine[i].Occupant is UnitCard target && _combatTargetLegal(attacker, target.Ref))
            {
                result.Add(target.Ref);
            }
        }

        // HQ（W3-3 实体化）：目标以 HQ 实体引用承载——候选产出 hq.Ref（不留双承载：槽位引用不再作为
        // HQ 目标产出；槽位关系仅用于布局语义判定〔守护/轰炸机拦截/范围矩阵——经 HQ 占位槽〕）。
        // HQ 的槽位以棋盘为准查询（W3-4：HQ 居中占槽 2，不再假定 0）。
        var hqIndex = Battlefield.IndexOfHq(enemyLine);
        if (hqIndex >= 0 && enemyLine[hqIndex].Occupant is Hq hq && _combatTargetLegal(attacker, hq.Ref))
        {
            result.Add(hq.Ref);
        }

        return result;
    }

    // K1：原 IsLegalUnitTarget / IsLegalHqTarget / IsLineInRange 的规则编排已迁至交战合法性判定器族——
    // combat.target.legal 组合器承载前两者之编排（归属→存活/在场→烟幕→守护资格→拦截→范围；HQ 分支＝
    // 归属→占位槽→守护→拦截→范围），combat.range 子规则承载 IsLineInRange 范围矩阵；
    // 本管理器候选/复验调用点经 _combatTargetLegal 单源调用（只调用不判断——无第二真源）。

    // ---------- ③ 守护状态（维护型；由更新/词条变更驱动重算） ----------

    /// <summary>
    /// 被守护状态查询（单位）：相邻（同线槽位索引差 1）存在同方存活守护者 ＝ 被守护
    /// （仅能被炮/轰攻击；守护者自身永不获被守护；多守护源不叠加＝存在性判定；
    /// 守护者判定＝双通道并行〔IsGuard 代码通道 / 「守护」词条〕——批 4）。
    /// 读取形式＝维护型状态查询（由位置/入场/死亡/升级更新与词条授予-移除完成点驱动维护）。
    /// </summary>
    /// <exception cref="ArgumentNullException">unit 为 null。</exception>
    public bool IsUnitGuarded(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _guardedUnits.Contains(unit);
    }

    /// <summary>被守护状态查询（HQ；W3-3：HQ 实体承载——查询以 HQ 实体为单元）。</summary>
    /// <exception cref="ArgumentNullException">hq 为 null。</exception>
    public bool IsHqGuarded(Hq hq)
    {
        ArgumentNullException.ThrowIfNull(hq);
        return _guardedHqs.Contains(hq);
    }

    /// <summary>被守护状态查询（HQ；按玩家转发——转发读面，读 player.Hq 的守护状态）。</summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    public bool IsHqGuarded(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return IsHqGuarded(player.Hq);
    }

    /// <summary>
    /// 被守护状态重算（全量；存在性判定——「至少一个相邻守护者」）：
    /// 逐线（双方支援线＋前线）对每个在场单位检查物理邻位；HQ 检查两侧邻位（W3-4：HQ 居中占槽、两侧均可放单位）；
    /// 守护者判定＝双通道并行（IsGuard 代码通道 / 「守护」词条——现势词条面，批 4；判定等价）；
    /// 守护者存活状态计入（死亡即不构成保护来源）；守护者自身跳过（永不获被守护）；同方限定。
    /// </summary>
    private void MaintainGuardState()
    {
        _guardedUnits.Clear();
        _guardedHqs.Clear();

        MaintainGuardForLine(_battlefield.PlayerASupportLine);
        MaintainGuardForLine(_battlefield.FrontLine);
        MaintainGuardForLine(_battlefield.PlayerBSupportLine);

        MaintainHqGuard(_battlefield.PlayerASupportLine);
        MaintainHqGuard(_battlefield.PlayerBSupportLine);
    }

    private void MaintainGuardForLine(BattleLine line)
    {
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Occupant is not UnitCard unit || IsDead(unit))
            {
                continue;
            }

            if (unit.Definition.IsGuard || KeywordRules.HasKeyword(unit, KeywordIds.Guard))
            {
                continue; // 守护者自身永不获被守护（任何情形，含相邻另一守护者；双通道判定——代码通道/守护词条）
            }

            if (unit.Owner is not { } owner)
            {
                continue; // 无归属单位不获（同方限定不成立）
            }

            if (IsGuardianAt(line, i - 1, owner) || IsGuardianAt(line, i + 1, owner))
            {
                _guardedUnits.Add(unit);
            }
        }
    }

    private void MaintainHqGuard(BattleLine supportLine)
    {
        var index = Battlefield.IndexOfHq(supportLine);
        if (index < 0 || supportLine[index].Occupant is not Hq hq)
        {
            return;
        }

        // W3-4：HQ 居中占槽（HqSlotIndex＝2），两侧都是可放单位的邻位——任一邻位有己方守护者即计入保护。
        if (IsGuardianAt(supportLine, index + 1, hq.Owner) || IsGuardianAt(supportLine, index - 1, hq.Owner))
        {
            _guardedHqs.Add(hq); // HQ 计入保护（守护者在相邻槽 → HQ 获被守护）
        }
    }

    private static bool IsGuardianAt(BattleLine line, int index, Player owner)
    {
        if (index < 0 || index >= line.Count)
        {
            return false;
        }

        // 守护者判定＝双通道并行（批 4）：IsGuard 代码通道或「守护」词条（现势词条面——加载/授予-移除-复装/升级随动）。
        return line[index].Occupant is UnitCard guardian
            && (guardian.Definition.IsGuard || KeywordRules.HasKeyword(guardian, KeywordIds.Guard))
            && !IsDead(guardian)
            && ReferenceEquals(guardian.Owner, owner);
    }

    // ---------- ④ 回合恢复（turn.start 处理段接线：行动方在场单位重置两 bool） ----------

    /// <summary>
    /// 单位行动状态恢复（回合开始处理段调用；turn.start.after 前完成）：
    /// 对当前行动方在场单位（HQ 除外）重置两 bool＝true ＋ 词条运行态清零（「本轮已攻次数」随回合恢复一并清零——词条侧）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    public void RefreshActionStates(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        RefreshLine(_battlefield.PlayerASupportLine, player);
        RefreshLine(_battlefield.FrontLine, player);
        RefreshLine(_battlefield.PlayerBSupportLine, player);
    }

    private static void RefreshLine(BattleLine line, Player player)
    {
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Occupant is not UnitCard unit || IsDead(unit))
            {
                continue;
            }

            if (!ReferenceEquals(unit.Owner, player))
            {
                continue;
            }

            var command = unit.GetData<CommandData>();
            command.CanMove = true;
            command.CanAttack = true;
            KeywordRules.ResetTurnCounters(unit);
        }
    }

    // ---------- ④b 在场回合数递增（G14补 S10；turn.start 更新驱动——单方步进） ----------

    /// <summary>
    /// 在场回合数递增（G14补 S10；turn.start 更新驱动，回合事件驱动计数）：
    /// 对「归属==回合开始方」的三线在场单位逐一递增（单方步进——对方回合不递增；
    /// 死亡单位不在场、自然跳过——方法内另设防御）；静默数据变更（不发射/不通知）。
    /// </summary>
    private void AdvanceTurnsInPlay(Player player)
    {
        AdvanceLine(_battlefield.PlayerASupportLine, player);
        AdvanceLine(_battlefield.FrontLine, player);
        AdvanceLine(_battlefield.PlayerBSupportLine, player);
    }

    private static void AdvanceLine(BattleLine line, Player player)
    {
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Occupant is UnitCard unit && !IsDead(unit) && ReferenceEquals(unit.Owner, player))
            {
                unit.AdvanceTurnsInPlay(); // 单位侧内部防御（未单位化/已死亡＝无操作）
            }
        }
    }

    // ---------- ⑤ 指挥流程（指挥触发器默认链） ----------

    private async Task HandleCommandFlowAsync(CommandTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not { IsAlive: true } unitRef || unitRef.Value is not UnitCard unit
            || view.FlowBox is not CommandFlowBox box)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：链中止、结局不发生
            return;
        }

        // 读判定（复算；同源）
        var availability = GetCommandAvailability(unit);
        if (availability.IneligibleReason is { } ineligible)
        {
            box.Result = CommandResult.Failure(MapIneligible(ineligible));
            return;
        }

        if (!availability.AnyActionAvailable)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.NoActionAvailable);
            return;
        }

        // 交互：一次拖拽＝一次请求；候选＝可用动作的候选并集（候选由后端给出——Q19＝a）
        var candidates = new List<Ref<Entity>>(availability.Move.Candidates.Count + availability.Attack.Candidates.Count);
        candidates.AddRange(availability.Move.Candidates);
        candidates.AddRange(availability.Attack.Candidates);

        Ref<Entity>? selected = null;
        var targeting = await _targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                new ReferenceSetParameter(candidates, 1, 1, tag: unit));

            // 非法选择＝同一选择器重入（Q8a：内部处理、支持重试）。
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<Ref<Entity>>();
            }

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            selected = step.Value;
            return TargeterResult.Ok();
        });

        if (targeting.IsCancelled)
        {
            box.Result = CommandResult.Cancelled(targeting); // 取消＝零副作用
            return;
        }

        if (!targeting.IsOk)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.TargetingFailed, targeting);
            return;
        }

        if (selected is null || !selected.IsAlive)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.TargetingFailed);
            return;
        }

        // 分派：选中空槽 → 移动触发器；选中敌方单位 / 敌方 HQ → 攻击触发器（X1：触发者随指挥链透传）
        await DispatchSelectedAsync(unit, selected!, box, ctx, ct, view.TriggerCard);
    }

    private async Task DispatchSelectedAsync(
        UnitCard unit, Ref<Entity> selected, CommandFlowBox box, Context ctx,
        CancellationToken ct, Ref<Entity>? triggerCard)
    {
        var owner = unit.Owner;
        var enemy = owner is null ? null : EnemyOf(owner);
        var selectedEntity = selected.Value;
        if (owner is not null && !_inputAllowed(owner, unit, selectedEntity))
        { box.Result = CommandResult.Failure(CommandFailureReason.InteractionBlocked); return; }

        // 分派：选中敌方 HQ 实体引用 / 敌方单位 → 攻击触发器；选中空槽 → 移动触发器
        // （X1：触发者随指挥链透传）。W3-3：HQ 目标以实体引用承载——不再有「槽位→HQ」分派
        // （槽位引用不再作为 HQ 目标产出；HQ 占位槽引用落入无效目标分派）。
        if (selectedEntity is Hq hq)
        {
            if (enemy is not null && ReferenceEquals(hq.Owner, enemy))
            {
                await DispatchAttackAsync(unit, selected, box, ctx, ct, triggerCard);
                return;
            }
        }
        else if (selectedEntity is UnitCard)
        {
            await DispatchAttackAsync(unit, selected, box, ctx, ct, triggerCard);
            return;
        }
        else if (selectedEntity is Slot slot && slot.IsEmpty)
        {
            await DispatchMoveAsync(unit, selected, box, ctx, ct, triggerCard);
            return;
        }

        // 无效目标分派（防御；正常流程不可达——候选面之外者不产生终局）
        box.Result = CommandResult.Failure(CommandFailureReason.TargetingFailed);
    }

    private async Task DispatchMoveAsync(
        UnitCard unit, Ref<Entity> newPositionRef, CommandFlowBox box, Context ctx,
        CancellationToken ct, Ref<Entity>? triggerCard)
    {
        var state = unit.GetData<UnitStateData>();
        var oldSlot = state.Position;
        if (oldSlot is null)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 嵌套执行（复验＝移动触发器验证承载——拒绝＝仅本次取消、零副作用＋留痕）
        var data = new Dictionary<string, object?>
        {
            [CommandDataKeys.Unit] = unit.Ref,
            [CommandDataKeys.OldPosition] = oldSlot.Ref,
            [CommandDataKeys.NewPosition] = newPositionRef,
        };
        if (triggerCard is not null)
        {
            data[CommandDataKeys.TriggerCard] = triggerCard; // X1：效果引发情形随指挥链透传（主动指挥＝缺省空）
        }

        var stream = await UnitMoveTrigger.InvokeAsync(_engine, data, ct);

        if (ctx.Interrupted || stream.Outcome != ExecutionOutcome.Normal)
        {
            box.Result = stream.Outcome == ExecutionOutcome.ValidationRejected
                ? CommandResult.Failure(CommandFailureReason.ExecutionRejected)
                : CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 外层收尾（执行成功后）：扣费 → 两 bool 更新（恰一次、只在外层）
        await FinalizeMoveAsync(unit, ct);
        box.Result = CommandResult.Success();

        if (!unit.GetData<UnitStateData>().IsDestroyed)
        {
            await GameUpdates.EmitUnitActed(_engine, unit, ct); // E1-33：行动后（移动；已阵亡不豁免语义＝不为亡者发射）
        }
    }

    private async Task DispatchAttackAsync(
        UnitCard unit, Ref<Entity> targetRef, CommandFlowBox box, Context ctx,
        CancellationToken ct, Ref<Entity>? triggerCard)
    {
        // 嵌套执行（复验＝攻击触发器验证承载——拒绝＝仅本次取消、零副作用＋留痕）
        var data = new Dictionary<string, object?>
        {
            [CommandDataKeys.Attacker] = unit.Ref,
            [CommandDataKeys.Target] = targetRef,
        };
        if (triggerCard is not null)
        {
            data[CommandDataKeys.TriggerCard] = triggerCard; // X1：效果引发情形随指挥链透传（主动指挥＝缺省空）
        }

        var stream = await UnitAttackTrigger.InvokeAsync(_engine, data, ct);

        if (ctx.Interrupted || stream.Outcome != ExecutionOutcome.Normal)
        {
            box.Result = stream.Outcome == ExecutionOutcome.ValidationRejected
                ? CommandResult.Failure(CommandFailureReason.ExecutionRejected)
                : CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 外层收尾（执行成功后；攻击者死亡不豁免）：扣费 → 两 bool 更新（恰一次、只在外层）
        await FinalizeAttackAsync(unit, ct);
        box.Result = CommandResult.Success();

        if (!unit.GetData<UnitStateData>().IsDestroyed)
        {
            await GameUpdates.EmitUnitActed(_engine, unit, ct); // E1-33：行动后（攻击；已阵亡＝不为亡者发射）
        }
    }

    /// <summary>移动收尾（外层）：扣费（行动方扣除、恰一次；W2b：读行动费有效值；K4：经 OperateCosts 共享单元；E1-25 后续：经点数通用入口发 point.changed）→ 两 bool 更新（非坦克＝二选一：另一动作同被清）。</summary>
    private static async Task FinalizeMoveAsync(UnitCard unit, CancellationToken ct)
    {
        var command = unit.GetData<CommandData>();

        await OperateCosts.DeductAsync(unit, ct);
        command.CanMove = false;
        if (!HasUnitType(unit, UnitType.Tank))
        {
            command.CanAttack = false;
        }
    }

    /// <summary>攻击收尾（外层）：扣费（行动方扣除、恰一次；W2b：读行动费有效值；K4：经 OperateCosts 共享单元；E1-25 后续：经点数通用入口发 point.changed）→ CanAttack 更新（奋战读取记账）→ 非坦克二选一清 CanMove。</summary>
    private static async Task FinalizeAttackAsync(UnitCard unit, CancellationToken ct)
    {
        var command = unit.GetData<CommandData>();

        await OperateCosts.DeductAsync(unit, ct);
        command.CanAttack = KeywordRules.ResolveCanAttackAfterAttack(unit)
            || KeywordProgramRuntime.Components(unit).Any(c=>c.Attacks<KeywordProgramRuntime.Resolve(unit,"attack.limit",1));
        if (!HasUnitType(unit, UnitType.Tank))
        {
            command.CanMove = false;
        }
    }

    // ---------- ⑥ 单位移动触发器默认链（执行段） ----------

    private async Task HandleUnitMoveAsync(UnitMoveTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Unit is not { IsAlive: true } unitRef || unitRef.Value is not UnitCard unit
            || view.OldPosition is not { IsAlive: true } oldRef || oldRef.Value is not Slot oldSlot
            || view.NewPosition is not { IsAlive: true } newRef || newRef.Value is not Slot newSlot)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：链中止、移动不发生
            return;
        }

        // 执行段：槽位变更（旧槽移除 → 放入目标空槽；原槽变为空槽、参与后续邻位计算）→ 发射更新（恰一次；先于收尾段）
        await ApplyPositionChangeAsync(unit, oldSlot, newSlot, ct);
        // 守护维护由本更新驱动（订阅重算——布局已变更、收尾未发生）
    }

    /// <summary>槽位变更与位置更新的**单一执行面**（指挥移动与效果位移共用；不扣费、不发 unit.acted）。</summary>
    private async Task ApplyPositionChangeAsync(UnitCard unit, Slot oldSlot, Slot newSlot, CancellationToken ct)
    {
        var state = unit.GetData<UnitStateData>();
        oldSlot.Clear();
        newSlot.Place(unit);
        state.Position = newSlot;
        await GameUpdates.EmitUnitPositionChanged(_engine, unit, oldSlot, newSlot, ct);
    }

    // ---------- ⑦ 单位攻击触发器默认链（执行段） ----------

    private async Task HandleUnitAttackAsync(UnitAttackTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard attacker
            || view.Target is not { IsAlive: true } targetRef)
        {
            ctx.Interrupt();
            return;
        }

        var targetEntity = targetRef.Value;
        var combatCompleted=false;
        try
        {
        await KeywordProgramRuntime.AttackEvent(attacker,targetEntity as Card,true,ct);

        // S2（隐蔽机制·揭示触发点）：攻击执行段入口（复验后、伤害结算前——E2-Q3a 裁定）——
        // 参与攻击的隐蔽单位各自揭示：先攻击者（动作发起方）、后被攻击者（受动作方——单位目标）；
        // HQ 不作被揭示方（攻击 HQ 时仅攻击者揭示）；载荷缺失/结构性中断＝未进入本段、不揭示。
        await CovertRules.RevealAsync(attacker, ct);
        if (targetEntity is UnitCard targetUnit)
        {
            await CovertRules.RevealAsync(targetUnit, ct);
        }

        if (targetEntity is Hq hq && !ReferenceEquals(hq.Owner, attacker.Owner))
        {
            // HQ 简路（W3-3 实体化）：伤害＝攻击者攻击力有效值；HQ 不反击；不走「造成攻击伤害」（无单位互伤链）；
            // 伤害数值改走 HQ 数值路径与管线（改写→介入→应用→跑链集中触发——不建专用 HQ 伤害流程）。
            // 归零→终局：不内联于攻击流程——HQ 侧统一响应（数值变化下游；与「先数值变化、后终局记录」
            // 观察序一致）。当次结算收尾照常完成（内部步骤不经门禁）。
            var damage = attacker.Modifiers.GetEffectiveValue(CardStatFields.Attack);
            var healthBefore = hq.Health;
            await hq.ApplyDamageAsync(damage, ct);
            var dealt = healthBefore - hq.Health;
            if (dealt > 0)
            {
                await GameUpdates.EmitUnitDamageDealt(_engine, attacker, hq, dealt, ct); // E1-47：HQ 路径的来源侧
            }
        }
        else if (targetEntity is UnitCard)
        {
            // 单位 vs 单位：必经「造成攻击伤害」（伏击效果的改写注册口；同一目标承载）
            // X1：触发者随链路传递（同一效果链内一致——攻击链读数透传至伤害链；主动指挥＝空、照常透传）
            var damageData = new Dictionary<string, object?>
            {
                [CommandDataKeys.Attacker] = attackerRef,
                [CommandDataKeys.Target] = targetRef,
                [CommandDataKeys.Resolution] = new AttackDamageResolution(),
            };
            if (view.TriggerCard is not null)
            {
                damageData[CommandDataKeys.TriggerCard] = view.TriggerCard;
            }

            await AttackDamageTrigger.InvokeAsync(_engine, damageData, ct);
        }
        else
        {
            ctx.Interrupt();
            return;
        }

        combatCompleted=true;
        }
        finally
        {
            try
            {
                if(combatCompleted)await KeywordProgramRuntime.AttackEvent(attacker,targetEntity as Card,false,ct);
                else foreach(var component in KeywordProgramRuntime.Components(attacker))await component.ClearTemporary(ct);
            }
            finally { if(KeywordProgramRuntime.TakeTemporary(attacker))await GameUpdates.EmitKeywordAttackFinished(_engine,attacker,CancellationToken.None); }
        }
        // 执行段记账（词条侧）：奋战——本轮已攻次数 +1（先执行（含记账）→ 后更新）
        KeywordRules.RecordAttack(attacker);

        // 冲击消耗（批 5；X1＝攻击即消耗）：到达执行段公共尾部即消耗——与对象类型（单位/HQ/伏击者）、
        // 反击/伏击是否起效均无关（「到达尾部」为唯一判据）；「未抵达尾部」（发起前拒绝/复验拒绝/
        // 执行段载荷中断）＝不消耗、未触发不失去。存活防御：攻击者已死亡＝防御性跳过（真实对局不可达——
        // 带冲击攻击者因 C5 豁免不被反击致死；终态卡移除链会被拒绝——防御）；幂等（无冲击＝零操作）。
        // 顺序口径（Q&A-6(b) 裁量）：先记账（RecordAttack）后消耗——既有记账块收口保持不动，
        // 消耗作为执行段最后副作用落地（消耗触发词条变更通知——如守护维护随动重算）。
        if (KeywordRules.HasKeyword(attacker, KeywordIds.Shock)
            && !attacker.GetData<UnitStateData>().IsDestroyed)
        {
            await attacker.Keywords.RevokeAsync(KeywordIds.Shock);
        }
    }

    // ---------- ⑧ 「造成攻击伤害」默认链（基础互伤；伏击改写的替代面） ----------

    private async Task HandleDefaultAttackDamageAsync(AttackDamageTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard attacker
            || view.Target is not { IsAlive: true } targetRef || targetRef.Value is not UnitCard target
            || view.Resolution is not AttackDamageResolution resolution)
        {
            ctx.Interrupt(); // 载荷缺失（HQ 不走本流程——防御）
            return;
        }

        // 死亡冻结（W2b）：任一方已死亡＝尸体不参与数值结算（对已死单位的伤害门户亦将明确拒绝——此处为流程侧防护；
        // 真实对局不可达〔候选过滤〕——防御于测试/异常驱动路径）。
        if (attacker.GetData<UnitStateData>().IsDestroyed || target.GetData<UnitStateData>().IsDestroyed)
        {
            return;
        }

        if (resolution.IsRewritten)
        {
            // 伏击改写（已判定成立）：替代默认——攻击者死亡（统一死亡流程、无 HP 逐步扣减语义）、被攻击者不受伤
            await ProcessDeathAsync(attacker, ct, killer: target); // E1-47：被攻击者为击杀者（伏击反杀）
            await EmitCombatSurvivedAsync(_engine, target, ct); // E1-39：被攻击者确经交战且未阵亡 ⇒ 幸存
            return;
        }

        // 默认基础互伤：按反击豁免判定表（后置项 A；K2：经 combat.counter.eligibility 判定器条目——单源、moding 动态生效）
        // ——同时结算、以互扣前有效值为基准；豁免方不结算反击伤害
        // （判定表：目标轰炸机永不反击 / 攻击者炮兵不受反击 / 攻击者轰炸机不受反击〔目标战斗机例外〕/
        //  攻击者具冲击不受反击〔批 5——豁免族最高优先〕/ 其余正常）
        // W2b：伤害值与扣减一律接改——伤害读「攻击力有效值」；扣减经门户（损伤量→跑链→有变更集中触发）。
        // A2：伤害修正读取（handler 链介入——免疫归零／重甲减伤在默认结算 handler 之前登记；修正后照常走
        // 「0 伤害」路径——净伤害＝0 不算「受到伤害」，与动员失去等消费一致）。
        var damageToTarget = ResolveIncomingDamage(resolution, attacker, target);
        var damageToAttacker = ResolveIncomingDamage(resolution, target, attacker);
        var counterAttacks = _combatCounterEligibility(attacker, target); // K2：反击资格判定通道（对局＝条目句柄；独立构造＝内置默认）

        var targetDefenseBefore = target.Modifiers.GetEffectiveValue(CardStatFields.Defense);
        // E1-47：结算窗口内设置**伤害来源游标**——门户触发的「防御归零统一死亡衔接」在同一同步调用链内执行，
        // 故游标可把"谁造成了这次伤害"带给死亡衔接（游标外恒 null＝非归属驱动）。
        await WithDamageSourceAsync(
            attacker, () => target.ApplyDefenseDamageAsync(damageToTarget, ct));
        await EmitDamageDealtAsync(_engine, attacker, target, targetDefenseBefore, ct); // E1-47：**来源侧**实际造成量

        if (counterAttacks)
        {
            var attackerDefenseBefore = attacker.Modifiers.GetEffectiveValue(CardStatFields.Defense);
            await WithDamageSourceAsync(
                target, () => attacker.ApplyDefenseDamageAsync(damageToAttacker, ct));
            await EmitDamageDealtAsync(_engine, target, attacker, attackerDefenseBefore, ct); // E1-47：反击方向
        }

        // 死亡判定（互扣后防御有效值 ≤0；数值表现钳制后 ≤0 即 ==0）；同归于尽＝两枚 card.died 均发射，顺序：被攻击者在前、攻击者在后。
        // 防御归零的统一死亡衔接由 stat.changed 订阅先行处理（含修饰撤销/到期等非伤害来源）；此处判定＝结算路径内防护兜底——
        // 已死亡＝跳过（保证死亡恰一次、发射顺序稳定）。
        if (!target.GetData<UnitStateData>().IsDestroyed
            && target.Modifiers.GetEffectiveValue(CardStatFields.Defense) <= 0)
        {
            await ProcessDeathAsync(target, ct, killer: attacker); // E1-47：对战致死 ⇒ 攻击者胜出
        }

        if (counterAttacks && !attacker.GetData<UnitStateData>().IsDestroyed
            && attacker.Modifiers.GetEffectiveValue(CardStatFields.Defense) <= 0)
        {
            await ProcessDeathAsync(attacker, ct, killer: target); // E1-47：反击致死 ⇒ 被攻击者胜出
        }

        // E1-47：来源侧伤害信号（发射点＝各处扣减之后——实际变化量 > 0 才发）。
        // E1-39：交战存活信号（死亡判定**之后**才发——已阵亡者不发射，不误导"幸存"）。
        // 双方**皆参战**（反击豁免只影响"是否互伤"，不影响"是否参战"），故各自按存活与否发射。
        // 观察序＝被攻击者在前、攻击者在后（与同归于尽的 card.died 观察序一致）。
        await EmitCombatSurvivedAsync(_engine, target, ct);
        await EmitCombatSurvivedAsync(_engine, attacker, ct);
    }

    /// <summary>
    /// 受方伤害解析（A2 加性）：基准＝来源方攻击力有效值；修正＝结算记录中登记的归零（免疫——置 0）与
    /// 减伤（重甲——扣减）。归零优先（结果 0）；减伤下限 0 自然收敛（不足减则归 0；与免疫叠加结果一致）。
    /// 归零/减伤均在默认结算 handler 之前由对应词条 handler 登记（「伤害结算 handler 之前把伤害设为 0」）。
    /// </summary>
    private static int ResolveIncomingDamage(
        AttackDamageResolution resolution, UnitCard source, UnitCard receiver)
    {
        if (resolution.IsDamageZeroed(receiver))
        {
            return 0; // 免疫归零
        }

        var raw = source.Modifiers.GetEffectiveValue(CardStatFields.Attack);
        return Math.Max(0, raw - resolution.GetDamageReduction(receiver)); // 重甲减伤（下限 0）
    }

    /// <summary>
    /// **伤害来源游标**（E1-47）：伤害门户触发的「防御归零统一死亡衔接」与本调用**同一同步调用链**，
    /// 故以结算窗口内的临时游标承载"本次伤害的施动方"（窗口外恒 null ⇒ 死亡无击杀者归属）。
    /// 用**恢复式**赋值（try/finally 还原上一值）——嵌套伤害（如伤害链内的再触发）亦保持作用域正确。
    /// </summary>
    private UnitCard? _damageSourceCursor;

    private async Task WithDamageSourceAsync(UnitCard source, Func<Task> action)
    {
        var previous = _damageSourceCursor;
        _damageSourceCursor = source;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _damageSourceCursor = previous;
        }
    }

    /// <summary>
    /// 发射 unit.damage.dealt（E1-47；**来源侧**）：实际防御变化量 &gt; 0 才发（改变才传播）。
    /// 受方为 HQ 时亦适用（调用侧另行给出 HQ 路径的发射点）。
    /// </summary>
    private static Task EmitDamageDealtAsync(
        LogicEngine engine, UnitCard dealer, UnitCard target, int defenseBefore, CancellationToken ct)
    {
        var dealt = defenseBefore - target.Modifiers.GetEffectiveValue(CardStatFields.Defense);
        return dealt > 0
            ? GameUpdates.EmitUnitDamageDealt(engine, dealer, target, dealt, ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// 发射 unit.combat.survived（E1-39）：仅对**未阵亡**的参战单位发射（恰一次；已死亡＝零发射）。
    /// </summary>
    private static Task EmitCombatSurvivedAsync(LogicEngine engine, UnitCard unit, CancellationToken ct)
        => unit.GetData<UnitStateData>().IsDestroyed
            ? Task.CompletedTask
            : GameUpdates.EmitUnitCombatSurvived(engine, unit, ct);

    /// <summary>
    /// 死亡链替换口（消灭确认前、槽位释放与卸载前）：返回 <c>true</c> ＝ 取消本次消灭。
    /// 与词条程序的 <c>death.cancel</c> 串联——注册面在前、内置词条面在后，任一取消即终止。
    /// </summary>
    public Func<UnitCard, Card?, CancellationToken, Task<bool>>? DeathInterceptor { get; set; }

    /// <summary>
    /// 统一死亡流程（攻击结算判定死亡后调用；伏击改写的攻击者死亡同走此流程）。
    /// 完整次序（A4 定稿·唯一权威口径——《需求文档》Q&A-1）：
    /// ①槽位释放（变空槽）→ ②IsDestroyed 置位 → ③**亡计结算**（新增步：仅含亡计词条的卡驱动其内部效果动作一次；
    /// 「死亡前可观察状态」完整时执行——位置字段可读、词条行为态仍在、修饰仍生效、效果仍装载、IsDestroyed 已置位；
    /// 异常隔离〔记录、不中断死亡流程〕；恰一次保证＝流程内步骤位次＋执行面行为态解析〔死亡注销后天然跳过〕＋执行中重入防护）→
    /// ④词条死亡注销（2C-A1：仅行为撤销——登记/参值保留）→ ⑤修饰器清理
    /// （W2b：含期限订阅随销；数值整合至最终态、零新发射——死亡清理为内部特殊路径）→ ⑥钳击失效通知（A2；位置保持现状）→
    /// ⑦效果卸载（X2：统一卸载链收口——容器移除＋OnUnmount＋撤销登记＋总线卸载；托管清理幂等）→
    /// ⑧UnitStateData.Position 置空（实例保留可查询）→ ⑨card.died 发射（恰一次；死亡状态就绪后）。
    /// 不发 unit.position.changed（该更新专属移动语义）。HQ 不入死亡/销毁链——本流程不适用（边界）。
    /// </summary>
    internal async Task KillUnitAsync(UnitCard unit, UnitCard? killer = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        await ProcessDeathAsync(unit, ct, killer).ConfigureAwait(false);
    }

    /// <summary>Effect-origin combat: no turn/action/point payment, but normal target legality and combat resolution.</summary>
    internal async Task<bool> AttackByEffectAsync(UnitCard unit,Orc.Cards.Card target,CancellationToken ct,bool bypassRange=false)
    {
        if(_lifecycle?.IsActionAllowed==false||!unit.TryGetData<UnitStateData>(out var state)||state.IsDestroyed||state.Position is null)return false;
        if(KeywordRules.HasKeyword(unit,KeywordIds.Suppressed))return false;
        using var origin=KeywordProgramRuntime.BeginEffectAttack(unit,bypassRange);
        if(!IsAttackTargetLegal(unit,target.Ref))return false;
        await using var scope=_engine.BeginAction();
        var result=await UnitAttackTrigger.InvokeAsync(_engine,new Dictionary<string,object?>
        {
            [CommandDataKeys.Attacker]=unit.Ref,[CommandDataKeys.Target]=target.Ref,[CommandDataKeys.TriggerCard]=unit.Ref
        },ct);
        return result.Outcome==ExecutionOutcome.Normal;
    }

    /// <summary>
    /// 效果发起的位移（`retreat` 等；与 <see cref="AttackByEffectAsync"/> 同构的门户）：
    /// 不扣行动费、不清行动位、不做推进资格判定（目标为支援线空槽，方向与"推进"相反）；
    /// 槽位变更与位置更新走与指挥移动共用的单一执行面。返回是否实际位移。
    /// </summary>
    public async Task<bool> MoveByEffectAsync(UnitCard unit, Slot destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(destination);
        if (_lifecycle?.IsEnded == true) return false;
        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed || state.Position is not { } oldSlot) return false;
        if (ReferenceEquals(oldSlot, destination) || !destination.IsEmpty) return false;
        if (unit.Owner is not { } owner || !_battlefield.GetSupportLine(owner).Contains(destination)) return false;

        await using var scope = _engine.BeginAction();
        await ApplyPositionChangeAsync(unit, oldSlot, destination, ct);
        return true;
    }

    /// <summary>
    /// 无头伤害（效果运行期受控入口；经 <c>Orc.Game.Effects.EffectRuntime.DamageAsync</c> 在有**来源**时调用，E1-50）：
    /// 在**伤害来源游标**作用下执行单位伤害门户（⇒ 致死时死亡衔接拿到击杀者）→ 发射 <c>unit.damage.dealt</c>。
    /// </summary>
    internal async Task DealDamageAsync(UnitCard target, int amount, UnitCard source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var before = target.Modifiers.GetEffectiveValue(CardStatFields.Defense);
        await WithDamageSourceAsync(source, () => target.ApplyDefenseDamageAsync(amount, ct)).ConfigureAwait(false);
        await EmitDamageDealtAsync(_engine, source, target, before, ct).ConfigureAwait(false);
    }

    /// <summary>无头 HQ 伤害（效果运行期；经 <c>EffectRuntime.DamageAsync</c> 在有来源时调用，E1-50）。</summary>
    internal async Task DealDamageToHqAsync(Hq hq, int amount, UnitCard source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hq);
        ArgumentNullException.ThrowIfNull(source);

        var healthBefore = hq.Health;
        await hq.ApplyDamageAsync(amount, ct).ConfigureAwait(false);
        var dealt = healthBefore - hq.Health;
        if (dealt > 0)
        {
            await GameUpdates.EmitUnitDamageDealt(_engine, source, hq, dealt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 无头移动（效果运行期受控入口；经 <c>Orc.Game.Effects.EffectRuntime.MoveAsync</c> 暴露——internal 以免零散扩张 public 面）：
    /// 依目标区域取**首个空槽**，走与指挥同一执行链（<see cref="DispatchMoveAsync"/>：复验触发器 → 外层收尾）。
    /// 不可移动（无位置 / 无空槽 / 区域未知）＝false（不抛错）。
    /// </summary>
    internal async Task<bool> MoveUnitAsync(UnitCard unit, string zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);

        if (unit.GetData<UnitStateData>().Position is null)
        {
            return false;
        }

        var line = zone switch
        {
            "frontline" => _battlefield.FrontLine,
            "support" => unit.Owner is { } owner ? _battlefield.GetSupportLine(owner) : null,
            _ => null,
        };
        if (line is null)
        {
            return false;
        }

        Slot? target = null;
        foreach (var slot in line)
        {
            if (slot.IsEmpty)
            {
                target = slot;
                break;
            }
        }

        if (target is null)
        {
            return false;
        }

        var ctx = new Context();
        var box = new CommandFlowBox();
        await DispatchMoveAsync(unit, target.Ref, box, ctx, ct, triggerCard: null).ConfigureAwait(false);
        return box.Result is { IsSuccess: true };
    }

    /// <param name="killer">击杀者（E1-47 加性；null＝非归属驱动来源——如修饰到期致防御归零）。</param>
    private async Task ProcessDeathAsync(UnitCard unit, CancellationToken ct, UnitCard? killer = null)
    {
        var state = unit.GetData<UnitStateData>();
        if (state.IsDestroyed) return;

        // 死亡链（消灭确认前、卸载前）：注册面（moding/插件）在前，内置词条面（death.cancel）在后；
        // 任一取消即终止，且都发生在 IsDestroyed 置位与任何清理之前。
        if (DeathInterceptor is not null && await DeathInterceptor(unit, killer, ct)) return;
        if (await KeywordProgramRuntime.BeforeDestroy(unit,killer,ct)) return;
        state.Position?.Clear();
        state.IsDestroyed = true;

        // Host-bound death effects must settle while their registrations still exist.
        // The completed-death observation remains after cleanup in card.died.
        await GameUpdates.EmitCardDying(_engine, unit, killer, ct);

        // A4：亡计结算（新增步——置毁之后、词条死亡注销之前；清理前结算）。
        // 经再触发服务统一执行面（单源＋重入防护）；独立构造（无服务）＝直调执行面降级（无重入防护）。
        if (_retrigger is not null)
        {
            await _retrigger.ExecuteDeathrattleAsync(unit, ct);
        }
        else
        {
            await DeathrattleRules.ExecuteAsync(unit, _engine, ct);
        }

        KeywordRules.RevokeAllOnDeath(unit); // 2C-A1：词条死亡注销（经静态助手转发——仅行为撤销：OnRevoke 序列＋运行逻辑注销＋内嵌效果卸载；登记/参值保留可查询）

        await unit.Modifiers.ClearAllForDeathAsync(ct); // W2b：注销全部修饰器（含期限订阅随销）＋数值整合至最终态（零新发射）

        // A2：钳击关系失效通知（一方离场〔不再在场〕——另一方即时失去；同事务内落定）。置于死亡清理之后：
        // 离场方自身修饰器已清（本通知对其幂等、零新发射——「发射契约以致死变更为界」保持）；对侧撤销正常落定。
        await PincerRules.OnUnitLeftBattlefieldAsync(unit, _pincers, ct);

        // X2：效果卸载（统一卸载链收口——容器移除＋OnUnmount＋撤销登记＋总线卸载；含托管清理〔幂等：
        // 修饰器已清、零操作〕）——清理就绪后方发 card.died（「先数值变化→清理→card.died」观察序保持）。
        foreach (var effect in unit.Effects.ToArray())
        {
            unit.RemoveEffect(effect);
        }

        state.Position = null;
        await GameUpdates.EmitCardDied(_engine, unit, killer, ct); // E1-47：死亡 ＋ **归属**（Killer 可为 null）
        // 守护维护由本更新驱动（清位后等效触达——被守护状态可观测变化）
    }

    /// <summary>
    /// 防御归零的统一死亡衔接（W2b；card.stat.changed 更新驱动的被动检查——总线触发器阶段）：
    /// 任一单位的防御有效值经数值变更（伤害/修饰/撤销/到期）降至 ≤0（数值表现钳制后 ≤0 即 ==0）且尚未死亡
    /// → 走既有统一死亡流程（无攻击者归因）。执行时机＝card.stat.changed 的外部订阅通知完成之后——
    /// 「先数值变化、后死亡信号」（card.stat.changed → 死亡清理 → card.died）。
    /// 已死亡/已毁＝跳过（幂等防护——死亡恰一次）；非单位卡（无单位数据组件）＝跳过。
    /// </summary>
    private async Task HandleDefenseDepletionAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit
            || !unit.TryGetData<UnitStateData>(out var state)
            || state.IsDestroyed)
        {
            return;
        }

        if (unit.Modifiers.GetEffectiveValue(CardStatFields.Defense) <= 0)
        {
            // E1-47：击杀者归属＝结算窗口内的**伤害来源游标**（窗口外/非伤害来源＝null）。
            await ProcessDeathAsync(unit, ct, killer: _damageSourceCursor);
        }
    }

    /// <summary>
    /// HQ 归零的统一响应（W3-3 G11；card.stat.changed 更新驱动的被动检查——总线触发器阶段）：
    /// 任一 HQ 的有效血量经数值变更（伤害/修饰/撤销等）降至 ≤0 → 执行 HQ 侧统一响应
    /// （终局记录：状态置结束＋胜者＝HQ 归零方之对手——「终局判定迁至 HQ」的落点：判定为 HQ 侧响应，
    /// 不内联于攻击流程）。执行时机＝card.stat.changed 的外部订阅通知完成之后——
    /// 「先数值变化（通用数据改变更新）、后终局记录」。非 HQ 卡＝跳过；有效值 &gt;0＝无操作；
    /// 生命周期侧重复调用幂等（End 自身幂等）。
    /// </summary>
    private Task HandleHqZeroCheckAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is Hq hq)
        {
            hq.RespondToZero(); // HQ 侧统一响应（≤0 才生效；内部幂等；脱局防御降级在 HQ 侧）
        }

        return Task.CompletedTask;
    }

    // ---------- ⑨ 执行前复验（分派后、执行前兜底；J2：验证判定器承载——ValidationRejected、零副作用＋留痕） ----------
    // 复验逻辑迁至逐点专属判定器（MoveRevalidationJudicator / AttackRevalidationJudicator——验证点装配期按名绑定）；
    // K3：复验经共享 leg 条目与 move.frontline-enemy 条目取用条件（与可用性侧同源）；本区保留攻击目标合法性转发面
    //（IsAttackTargetLegal——与可用性侧单源、供攻击复验判定器装配期注入转发）。

    /// <summary>
    /// 复验判定器绑定解析（J2）：对局路径＝注册表解析（固定内置注册段已注册——装配期已就绪）；
    /// 独立构造路径＝内置默认（构造即可用——注入本管理器的对局级只读设施引用；leg/推进前置经本管理器内置默认通道——单源）。
    /// 解析动作即校验（未注册名＝装配期 fail-fast）。
    /// </summary>
    private JudicatorBinding ResolveRecheckBinding(string name)
        => _validationJudicatorResolver is not null
            ? _validationJudicatorResolver(name)
            : JudicatorBinding.FromStandalone(name, CreateRecheckJudicator(name));

    /// <summary>创建内置默认复验判定器（独立构造路径；装配期注入本管理器对局级只读设施引用——仅只读使用）。
    /// K3：leg 资格与推进前置经本管理器内置默认通道注入（与对局路径「经条目句柄」形态同构——不内联副本）。</summary>
    private ValidationJudicator CreateRecheckJudicator(string name) => name switch
    {
        JudicatorNames.MoveRecheck => new MoveRevalidationJudicator(
            _lifecycle, _moveLegEligibility, _battlefield, _moveFrontlineEnemy),
        JudicatorNames.AttackRecheck => new AttackRevalidationJudicator(
            _lifecycle, _attackLegEligibility, (attacker, targetRef) => IsAttackTargetLegal(attacker, targetRef)),
        _ => throw new KeyNotFoundException(
            $"判定器 '{name}' 未注册（独立构造路径无注册表——内置默认仅含复验两项；未注册引用＝配置错误）。"),
    };

    /// <summary>
    /// 创建内置默认目标合法性判定通道（K1；独立构造路径——构造即可用、零配置）：
    /// 子规则（范围/资格/拦截）以内置默认实例承载、经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）；对局级只读设施（战场布局/敌我判定/守护查询）经构造注入（仅只读使用）。
    /// </summary>
    private Func<UnitCard, Ref<Entity>, bool> CreateBuiltInCombatTargetLegal()
    {
        var range = new CombatRangeJudicator(_battlefield, EnemyOf);
        var guardEligibility = new CombatGuardEligibilityJudicator();
        var interception = new CombatInterceptionJudicator(_battlefield, EnemyOf);
        var targetLegal = new CombatTargetLegalJudicator(
            EnemyOf,
            IsUnitGuarded,
            hq => IsHqGuarded(hq),
            (attacker, targetSlot) => CombatJudicatorInvoker.InvokeBool(range, attacker, targetSlot),
            attacker => CombatJudicatorInvoker.InvokeBool(guardEligibility, attacker),
            (attacker, targetSlot, targetIsFighter) => CombatJudicatorInvoker.InvokeBool(interception, attacker, targetSlot, targetIsFighter));
        return (attacker, targetRef) => CombatJudicatorInvoker.InvokeBool(targetLegal, attacker, targetRef);
    }

    /// <summary>
    /// 创建内置默认反击资格判定通道（K2；独立构造路径——构造即可用、零配置）：
    /// 内置默认实例经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）。
    /// </summary>
    private static Func<UnitCard, UnitCard, bool> CreateBuiltInCombatCounterEligibility()
    {
        var counterEligibility = new CombatCounterEligibilityJudicator();
        return (attacker, target) => CombatJudicatorInvoker.InvokeBool(counterEligibility, attacker, target);
    }

    /// <summary>
    /// 创建内置默认伏击条件判定通道（K2；独立构造路径——构造即可用、零配置）：
    /// 内置默认实例经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）。
    /// </summary>
    private static Func<UnitCard, UnitCard, bool> CreateBuiltInCombatAmbushCondition()
    {
        var ambushCondition = new CombatAmbushConditionJudicator();
        return (self, attacker) => CombatJudicatorInvoker.InvokeBool(ambushCondition, self, attacker);
    }

    /// <summary>
    /// 创建内置默认 move leg 资格判定通道（K3；独立构造路径——构造即可用、零配置）：
    /// 内置默认实例经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）。
    /// </summary>
    private Func<UnitCard, Slot?, LegEligibilityFailure?> CreateBuiltInMoveLegEligibility()
    {
        var judicator = new LegEligibilityJudicator(_currentPlayerProvider, _battlefield, isMove: true);
        return (unit, position) => LegJudicatorInvoker.InvokeLeg(judicator, unit, position);
    }

    /// <summary>
    /// 创建内置默认 attack leg 资格判定通道（K3；独立构造路径——构造即可用、零配置）：
    /// 内置默认实例经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）。
    /// </summary>
    private Func<UnitCard, LegEligibilityFailure?> CreateBuiltInAttackLegEligibility()
    {
        var judicator = new LegEligibilityJudicator(_currentPlayerProvider, battlefield: null, isMove: false);
        return unit => LegJudicatorInvoker.InvokeLeg(judicator, unit, null);
    }

    /// <summary>
    /// 创建内置默认推进前置判定通道（K3；独立构造路径——构造即可用、零配置）：
    /// 内置默认实例经实例主方法调用（独立路径无注册表——无改写通道；
    /// 与对局路径「经条目句柄」形态同构、不内联副本）。
    /// </summary>
    private Func<Player, bool> CreateBuiltInMoveFrontlineEnemy()
    {
        var judicator = new MoveFrontlineEnemyJudicator(_battlefield, EnemyOf);
        return owner => CombatJudicatorInvoker.InvokeBool(judicator, owner);
    }

    /// <summary>攻击目标有效性复验（覆盖「目标引用有效性」：单位仍存活在场 / HQ 引用仍为敌方 HQ——W3-3 实体承载）。
    /// K1：规则编排迁至 CombatTargetLegalJudicator（combat.target.legal——六重编排＋引用契约）；本面保留为转发壳
    /// （候选链与复验链同源单点）；可见性保持 internal（J2 口径——供复验判定器装配期注入转发）。</summary>
    internal bool IsAttackTargetLegal(UnitCard attacker, Ref<Entity> targetRef)
        => _combatTargetLegal(attacker, targetRef);

    // ---------- 辅助 ----------

    /// <summary>敌我判定转发（K1；交战判定族装配期注入转发——与指挥流程单源；可见性提为 internal 供装配段注入使用）。</summary>
    internal Player? EnemyOfPlayer(Player player) => EnemyOf(player);

    private Player? EnemyOf(Player player)
    {
        foreach (var candidate in _players)
        {
            if (!ReferenceEquals(candidate, player))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasUnitType(UnitCard unit, string type)
        => unit.TryGetData<UnitStateData>(out var state) && state.UnitTypes.Contains(type);

    // K1：原 HasAnyLineRange / CountsAsBombard（类型组判定）已迁至 CombatTypeGroups（C2/C3/C4 共表——单源定义处）。

    // K3：原 HasLivingEnemyOnFrontLine（推进前置读取面）已迁至 MoveFrontlineEnemyJudicator（move.frontline-enemy 条目——
    // 移动可用性与移动复验两调用点经条目句柄单源取用；旧装配期注入转发路径退役）。

    // K1：原 IsBlockedByEnemyFighter（轰炸机拦截判定）与 ResolveLineOf（槽位→战线解析）已迁至
    // CombatInterceptionJudicator（combat.interception 子规则——逐字迁移、含防御分支）。

    private static bool IsDead(UnitCard unit)
        => !unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed;

    private static CommandFailureReason MapIneligible(CommandBlockReason reason) => reason switch
    {
        CommandBlockReason.NonOwnerTurn => CommandFailureReason.NonOwnerTurn,
        CommandBlockReason.OwnerInvalid => CommandFailureReason.OwnerInvalid,
        CommandBlockReason.UnitDead => CommandFailureReason.UnitDead,
        CommandBlockReason.InteractionBlocked => CommandFailureReason.InteractionBlocked,
        _ => CommandFailureReason.CommandFlowFault,
    };

    /// <summary>leg 结果→流程级资格原因（K3；分类读取——NonOwnerTurn/OwnerInvalid/UnitDead 三项流程级；其余＝null）。
    /// 读取路由（leg 条件→原因），不重新判断条件——无第二判定源。</summary>
    private static CommandBlockReason? FlowLevelIneligibleReason(LegEligibilityFailure? failure) => failure switch
    {
        LegEligibilityFailure.NonOwnerTurn => CommandBlockReason.NonOwnerTurn,
        LegEligibilityFailure.OwnerInvalid => CommandBlockReason.OwnerInvalid,
        LegEligibilityFailure.UnitDead => CommandBlockReason.UnitDead,
        _ => null,
    };

    /// <summary>leg 结果→动作级阻断原因映射（K3；逐项：owner/can/suppressed/cost 同名映射、
    /// 位置不在支援线→NoCandidates——映射为纯读取、非条件重判）。</summary>
    private static CommandBlockReason MapLegFailure(LegEligibilityFailure failure) => failure switch
    {
        LegEligibilityFailure.NonOwnerTurn => CommandBlockReason.NonOwnerTurn,
        LegEligibilityFailure.OwnerInvalid => CommandBlockReason.OwnerInvalid,
        LegEligibilityFailure.UnitDead => CommandBlockReason.UnitDead,
        LegEligibilityFailure.FlagFalse => CommandBlockReason.FlagFalse,
        LegEligibilityFailure.Suppressed => CommandBlockReason.Suppressed,
        LegEligibilityFailure.PointShortage => CommandBlockReason.PointShortage,
        LegEligibilityFailure.PositionNotInSupportLine => CommandBlockReason.NoCandidates,
        _ => throw new InvalidOperationException(
            $"leg 资格结果 '{failure}' 超出已知值域（映射不可用——fail-fast）。"),
    };

    private static void RequireUnitized(UnitCard unit)
    {
        if (!unit.TryGetData<UnitStateData>(out _) || !unit.TryGetData<CommandData>(out _))
        {
            throw new InvalidOperationException(
                $"单位 '{unit.Name}' 尚未单位化（缺少单位/指挥组件），不能发起指挥流程（装配性错误）。");
        }
    }
}

