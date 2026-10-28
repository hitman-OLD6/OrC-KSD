using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Actions;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Orc.Game.Model;
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
// ⑤攻击执行（S11：攻击动作＝「单位攻击触发器」触发端 +「造成攻击伤害触发器」处理端——两端的默认链在
//   AttackAction 内；HQ／单位目标分支都在处理端，HQ 不反击、不发 unit.combat.survived）；攻击结算＝按反击豁免判定表
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
    /// <summary>S8：内置「词条程序取消消灭」在死亡处理端 <c>Rewrite</c> 段的优先级（注册面/插件默认 0＝在前）。</summary>
    private const int DeathRewritePriority = 100;

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
    private readonly Func<UnitCard, BoardPosition?, LegEligibilityFailure?> _moveLegEligibility; // K3：move leg 资格判定通道（（单位, 源位置）→ 结果〔null＝通过〕——对局＝move.leg.eligibility 条目句柄；缺省＝内置默认）
    private readonly Func<UnitCard, LegEligibilityFailure?> _attackLegEligibility; // K3：attack leg 资格判定通道（（单位）→ 结果〔null＝通过〕——对局＝attack.leg.eligibility 条目句柄；缺省＝内置默认）
    private readonly Func<Player, bool> _moveFrontlineEnemy; // K3：推进前置判定通道（（所有者）→ bool——对局＝move.frontline-enemy 条目句柄；缺省＝内置默认）

    private readonly HashSet<UnitCard> _guardedUnits = new();
    private readonly HashSet<Hq> _guardedHqs = new(); // W3-3：被守护 HQ 以 HQ 实体为单元
    private readonly PincerRegistry _pincers = new(); // A2：钳击关系注册表（对局级——一对一占用约束）
    private readonly OperateFinalizer _operateFinalizer; // S5：行动入口收尾的统一调用者（扣费/额度/unit.acted）

    /// <summary>行动额度成员地址（<c>engine#action-budget</c>；额度改写的挂接点是该成员上的外来边）。</summary>
    public MemberAddress ActionQuotaTrigger { get; }
    private readonly MatchActionServices _actionServices; // S4：动作服务面（对局级单实例——五个动作共用）

    /// <summary>
    /// 动作服务面（**内部只读面**；沙盒原语复用效果侧唯一写点：<c>LandMoveAsync</c> / <c>ApplyDeathAsync</c>）。
    /// 不新增写路径——原语经同一对局级实例、同一原子落地。
    /// </summary>
    internal MatchActionServices ActionServices => _actionServices;

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
        Func<UnitCard, BoardPosition?, LegEligibilityFailure?>? moveLegEligibility = null,
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

        // S5：行动状态（费用/额度/unit.acted）只经本收尾单元写；入口只算差异（OperateProfile）。
        // 行动额度触发器：额度消耗的可改写点（效果经 injects 注入 handler——如「机动」使攻击额度消耗减一）。
        ActionQuotaTrigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.ActionBudget);
        _operateFinalizer = new OperateFinalizer(new OperateEntryServices(engine));

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

        // ① 指挥成员（内置；自有边＝指挥流程编排）。批次 7（§27）：端＝引擎成员，链＝成员上的自有边。
        CommandTrigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.Command);
        UnitMoveTrigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.UnitMove);
        UnitAttackTrigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.UnitAttack);
        AttackDamageTrigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.AttackDamage);

        // ② 移动／攻击的**执行前复验**（J2：判定器承载）：位次＝自有边首条（链之前），不合法＝本级停止
        //    （与旧 BindValidation 的"仅本次取消"同义）。
        ActionEdges.Attach<UnitMoveTriggerView>(_engine, EngineProcesses.UnitMove, RecheckMoveAsync);
        ActionEdges.Attach<UnitMoveTriggerView>(_engine, EngineProcesses.UnitMove, HandleUnitMoveAsync);
        ActionEdges.Attach<UnitAttackTriggerView>(_engine, EngineProcesses.UnitAttack, RecheckAttackAsync);
        ActionEdges.Attach<CommandTriggerView>(_engine, EngineProcesses.Command, HandleCommandFlowAsync);

        // ⑤ 伤害动作（R11/S5a）：三触发端＋一处理端；处理端默认执行体＝唯一门户调用点
        //    （DamageAction → 动作服务面 MatchActionServices.ApplyDamageAsync）。
        //    触发端默认转发与处理端默认落地都在 DamageAction 构造内注册（自有边＝链上末步）——
        //    外来改写边（重甲／免疫）挂在其前，因此「改写永远在默认之前」仍由位次表达。
        //
        // S4/S6–S8：动作服务面对局级单实例（伤害/移动/抽牌/加入手牌/死亡共用）；
        // S15 起动作实例不在本类构造——由下方动作目录按依赖序统一构造。
        _actionServices = new MatchActionServices(
            engine,
            (dealer, action) => WithDamageSourceAsync(dealer, action),
            _pincers,
            _retrigger);

        // S15：动作实例一律由目录按依赖序构造（本类不再 new）；目录每对局一份、随本管理器销毁。
        // P5b：工厂经 ActionImplementationRegistry.Resolve() 解析（默认实现集 + 单动作覆盖）；
        // 未注册任何实现集时在此 fail-fast（错误信息含「未注册动作实现」）。
        Actions = new MatchActionCatalog(
            engine,
            _actionServices,
            (attacker, targetRef) => IsAttackTargetLegal(attacker, targetRef),
            _combatCounterEligibility);

        AddToHandAction = Actions.AddToHand;
        DrawAction = Actions.Draw;
        MoveAction = Actions.Move;
        DeathAction = Actions.Death;
        JoinBattlefieldAction = Actions.JoinBattlefield;
        AttackAction = Actions.Attack;

        // S8：死亡处理端的改写位是**唯一**改写口——旧的「拦截器属性 + 词条 death.cancel」两个并列取消口
        // 收敛为同一条外来挂载边。内置词条 handler 用较高 priority：插件/注册面（默认 0）先判定，
        // 保持「注册面在前、内置词条面在后、任一取消即终止」的既有裁决次序。
        // §27：改用**外来挂载边**表达（R1i 迁移规则 `Rewrite → wrap before`）——位次＝自有边（免死守卫/死亡链）之前；
        // priority 语义不变。
        _engine.AttachMemberEdge(
            EngineProcesses.DeathHandle,
            "词条程序取消消灭",
            this,
            MemberEdges.For<DeathHandleView>(HandleKeywordDeathCancelAsync),
            priority: DeathRewritePriority);

        // S5a/P5：管道只是目录中伤害动作的门面（同一实例；既不自建扣数值实体，也不接受目录外实例）。
        DamagePipeline = new DamagePipeline(Actions.Damage);

        // 词条装载上下文（卡牌加载时取用；伏击注册「造成攻击伤害」改写）。
        // A2 加性：随带对局服务——钳击（同伴选择交互/候选枚举/关系注册表）与被压制（当前行动方）效果的运行逻辑取用；
        // K2 加性：随带判定器通道——伏击效果资格（combat.counter.eligibility）与条件（combat.ambush.condition）判定取用
        // （缺通道＝组件静默不注册——防御；对局/独立构造路径下本管理器两通道恒非空）。
        // 批 4 加性（守护词条化）：随带词条变更观察者——词条链真实授予/移除完成点驱动守护维护重算
        // （授予-移除-复装动作完成即随动；同步执行、变更驱动——无对局上下文＝跳过、不抛错）。
        // lambda 免——直接引用（构造时点这些字段均已赋值）。
        KeywordLoadContext = new KeywordLoadContext(
            engine, _targeterManager, _battlefield, _pincers, _currentPlayerProvider,
            _combatCounterEligibility, _combatAmbushCondition,
            keywordChangeObserver: MaintainGuardState,
            // S5：注入本局词条快照——授予链/绑定查询读快照，运行期覆盖不影响进行中对局。
            keywordSnapshot: keywordSnapshot,
            // R11：伤害三层管道——重甲挂 combat.damage、免疫挂 damage.apply（U3：经门面给实例，
            // 动作契约只报成员地址；band 挂接点与段语义不变）。
            damageAction: DamagePipeline,
            // S11：死亡动作——伏击改写经它走**完整两端**（触发端 → 处理端）执行「以死代伤」。
            deathAction: DeathAction);

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
        _engine.SubscribeMember(
            GameUpdates.CardStatChanged,
            "防御归零检查触发器",
            this,
            MemberEdges.For<CardTriggerView>(HandleDefenseDepletionAsync));

        // W3-3（G11 HQ 实体化）：HQ 归零的统一响应——被动触发器（挂载更新总线、响应 card.stat.changed）：
        // 「终局判定迁至 HQ」的触发侧——与防御归零检查同构（检查在「外部订阅通知完成之后」〔总线触发器阶段〕执行；
        // 「先数值变化（通用数据改变更新）、后终局记录」观察序）；任何来源（攻击伤害/效果/修饰撤销等）使 HQ 血量 ≤0
        // 均经同一判定：执行主体＝HQ 侧统一响应（RespondToZero——不内联于攻击流程）。
        _engine.SubscribeMember(
            GameUpdates.CardStatChanged,
            "HQ 归零检查触发器",
            this,
            MemberEdges.For<CardTriggerView>(HandleHqZeroCheckAsync));

        // W3-3：终局响应装配注入（延迟读取）——HQ 随玩家创建，对局服务在此绑定：
        // 胜者＝HQ 归零方之对手；脱局场景（本对象独立构造、无生命周期提供）＝HQ 侧防御降级（归零不记录、不抛错）。
        foreach (var player in players)
        {
            player.Hq.ConfigureTerminalResponse(() => EnemyOf(player), () => _lifecycle);
        }
    }

    // ---------- 内置流程触发器（公开只读；对局装配期注册为底层触发器） ----------

    /// <summary>指挥成员地址（<c>engine#command</c>；自有边＝一次指挥流程的编排：读判定 → 交互 → 分派 → 收尾）。</summary>
    public MemberAddress CommandTrigger { get; }

    /// <summary>单位移动成员地址（<c>engine#unit-move</c>；复验＝首条自有边，执行段＝槽位变更＋发射）。</summary>
    public MemberAddress UnitMoveTrigger { get; }

    /// <summary>单位攻击成员地址（<c>engine#unit-attack</c>；结构校验与转发是自有边，外部效果是外来挂载边）。</summary>
    public MemberAddress UnitAttackTrigger { get; }

    /// <summary>「造成攻击伤害」成员地址（<c>engine#attack-damage</c>；伏击改写＝外来边，默认结算＝自有边）。</summary>
    public MemberAddress AttackDamageTrigger { get; }

    /// <summary>
    /// 伤害三层管道（R11；对局级，与攻击伤害触发器同层）：<c>combat/effect/battle</c> 三触发端 ＋
    /// <c>damage.apply</c> 处理端。处理端是**唯一**调用数值门户的地方——外部程序集不能旁路（门户为 internal）。
    /// </summary>
    public DamagePipeline DamagePipeline { get; }

    /// <summary>
    /// 动作目录（S15；对局级、本管理器持有）：八个动作实例与其两端触发器的装配真源。
    /// 内部面——宿主经 <c>IMatchBindingServices.RegisterFlowTriggers</c> 按目录装配，不向插件交出目录本体。
    /// </summary>
    internal MatchActionCatalog Actions { get; }

    /// <summary>卡牌移动动作（S6）：三入口（被指挥／被控／撤退）共用的触发端 ＋ 唯一落地处理端。
    /// P5b：实现可由插件程序集提供，本属性只暴露宿主契约 <see cref="IMoveAction"/>。</summary>
    public IMoveAction MoveAction { get; }

    /// <summary>加入战场动作（S9）：部署／加入两触发端 ＋ 共享处理端（单位化 + 落位 + <c>card.placed</c>）。</summary>
    public IJoinBattlefieldAction JoinBattlefieldAction { get; }

    /// <summary>攻击动作（S11）：<see cref="UnitAttackTrigger"/> 触发端 ＋ <see cref="AttackDamageTrigger"/> 处理端。</summary>
    public IAttackAction AttackAction { get; }

    /// <summary>抽牌动作（S7）：卡组顶／指定牌两触发端 ＋ 共享处理端。</summary>
    public IDrawAction DrawAction { get; }

    /// <summary>加入手牌动作（S7）：独立可复用（爆牌＝其失败分支）。</summary>
    public IAddToHandAction AddToHandAction { get; }

    /// <summary>
    /// 死亡动作（S8）：处理端的**外来挂载边**是唯一改写口（是否死亡／击杀者／处置指向；自有边＝免死守卫 + 死亡链）。
    /// 插件经 <c>DeathAction.Handle.RegisterMounted(...)</c> 注入。
    /// </summary>
    public IDeathAction DeathAction { get; }

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

        if (state.Line is not { } line)
        {
            throw new InvalidOperationException(
                $"单位 '{unit.Name}' 当前不在场（离场动作被拒绝——前置契约不符）。");
        }

        await GameUpdates.EmitCardLeaving(_engine,unit,"leave",ct);
        // ① 槽位释放（变空槽）→ ② 不再在场（Position 置空——「有位置」判据失效）
        line.TryRemove(unit);
        state.Line = null;

        // ③ 离场失效通知（关系类观察者——既有「离场失效通知」面；死亡路径之外的第二接入点，转换路径接入）
        await PincerRules.OnUnitLeftBattlefieldAsync(unit, _pincers, ct);
    }

    /// <summary>
    /// 移动复验（执行前兜底；J2 判定器承载）：不合法＝本级停止（等价旧 <c>BindValidation</c> 的"仅本次取消"）。
    /// </summary>
    private Task RecheckMoveAsync(UnitMoveTriggerView view, Context ctx, CancellationToken ct)
        => RecheckAsync(
            JudicatorNames.MoveRecheck,
            new[] { view.Unit, view.OldPosition, view.NewPosition },
            SubjectOf(view.Unit),
            ctx);

    /// <summary>复验主体解析：视图字段是引用（Ref）或卡对象，统一成"被判定主体"。</summary>
    private static Card? SubjectOf(object? candidate) => candidate switch
    {
        Card card => card,
        Ref<Entity> { Value: Card card } => card,
        _ => null,
    };

    /// <summary>攻击复验（执行前兜底；J2 判定器承载）。</summary>
    private Task RecheckAttackAsync(UnitAttackTriggerView view, Context ctx, CancellationToken ct)
        => RecheckAsync(
            JudicatorNames.AttackRecheck,
            new[] { view.Attacker, view.Target },
            SubjectOf(view.Attacker),
            ctx);

    /// <summary>
    /// 复验的公共执行体：<paramref name="refs"/>＝**操作角色引用**（视图声明序，与旧触发器的
    /// "载荷第一层引用收集"同序同口径：移动＝单位/原槽位/目标空隙，攻击＝攻击者/目标）。
    /// <para>
    /// 判定器按该序列取位（复验判定器读 <c>refs[0..n]</c>），因此这里必须给出**完整角色序列**、
    /// 不能只给主体：少给＝判定器一律判非法（复验恒拒 ⇒ 攻击/移动被本级停止、零结算）。
    /// 空项＝载荷缺该角色（不补位——缺项本身即"结构不完整"，判定器按 count 判非法）。
    /// </para>
    /// </summary>
    private Task RecheckAsync(
        string judicator, IReadOnlyList<Ref<Entity>?> refs, Card? subject, Context ctx)
    {
        if (subject is null)
        {
            return Task.CompletedTask; // 载荷缺主体：结构错误由后续边判定（不在此伪造拒绝）
        }

        var collected = new List<Ref<Entity>>(refs.Count);
        foreach (var reference in refs)
        {
            if (reference is not null)
            {
                collected.Add(reference);
            }
        }

        var binding = ResolveRecheckBinding(judicator);
        var playerIndex = (subject as CardBase)?.Owner?.Index;
        var verdict = binding.Validate(collected, subject.Ref, playerIndex);
        if (!verdict.IsValid)
        {
            // 拒绝＝本级停止（等价旧 BindValidation 的"仅本次取消"：链上其后的默认执行边不再跑、零副作用）。
            // 同时把拒绝写进数据面：成员执行结果只回答"停了"，而入口需要**可辨识的失败原因**
            // （ExecutionRejected 而非兜底码 CommandFlowFault）——与卡牌守卫边同一口径（MemberGuardKeys）。
            ctx.Set(MemberGuardKeys.Rejected, true);
            ctx.Set(MemberGuardKeys.Reason, verdict.RejectionReason);
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>本次执行是否被守卫边（复验）拒绝（数据面标记；调用方据此映射失败原因）。</summary>
    private static bool IsGuardRejected(Context ctx)
        => ctx.TryGet(MemberGuardKeys.Rejected, out var rejected) && rejected is true;

    /// <summary>防御归零检查（内置；订阅 <c>card.stat.changed</c> 的边；诊断读面＝触发点恒为该信号）。</summary>
    public string DefenseDepletionTrigger => "防御归零检查触发器";

    /// <summary>HQ 归零检查（内置；订阅 <c>card.stat.changed</c> 的边）。</summary>
    public string HqZeroTrigger => "HQ 归零检查触发器";

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
        // X1 入口初值（R9）：唯一归一化点——Ref 在写入边界转 Card；失效/错型降级 null 并留痕（不抛）。
        // 玩家主动操作（缺省 null）＝空来源；效果引发＝调用方显式携带。
        data[CommandDataKeys.TriggerCard] = SourceNormalizer.ToCard(triggerCard, _engine, "command.triggerCard");

        await _engine.InvokeMemberAsync(EngineProcesses.Command, new Context(data), ct);

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

        var (kind, selected, targeting) = await RunSelectorAsync(
            unit, availability.Move.Candidates, SelectorActions.Move, ct);
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

        var (kind, selected, targeting) = await RunSelectorAsync(
            unit, availability.Attack.Candidates, SelectorActions.Attack, ct);
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
    /// <para>
    /// <paramref name="action"/>＝动作类别（R8 的 <c>pending.action</c>）——由调用方声明（移动/攻击），
    /// 助手内**不**按候选 kind 推断：候选可能是并集或异常项，推断会失真并引入第二套规则。
    /// </para>
    /// </summary>
    private async Task<(SelectorOutcomeKind Kind, Ref<Entity>? Selected, TargeterResult? Result)> RunSelectorAsync(
        UnitCard unit, IReadOnlyList<Ref<Entity>> candidates, string action, CancellationToken ct)
    {
        Ref<Entity>? selected = null;
        var result = await _targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                // R2 加性：归属座位＝发起选靶的单位归属（此前未填 → 待决请求 Seat 恒 null → 广播给所有会话）。
                new ReferenceSetParameter(candidates, 1, 1, tag: unit) { Owner = unit.Owner, Action = action });

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
        var moveLegFailure = _moveLegEligibility(unit, PositionOf(unit));
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
        if (!CommandMovementRules.IsRetreat(_battlefield, unit, PositionOf(unit)) && _moveFrontlineEnemy(owner))
        {
            return CommandActionAvailability.Blocked(CommandBlockReason.NoCandidates);
        }

        // 移动候选＝前线任意空槽（不被邻位动态规则约束；无后撤/横移候选）
        var candidates = new List<Ref<Entity>>();
        foreach (var gap in CommandMovementRules.Destinations(_battlefield, unit, PositionOf(unit)))
        {
            candidates.Add(gap.Ref);
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
        for (var i = 0; i < enemyLine.Size; i++)
        {
            if (enemyLine[i] is UnitCard target && _combatTargetLegal(attacker, target.Ref))
            {
                result.Add(target.Ref);
            }
        }

        var frontLine = _battlefield.FrontLine;
        for (var i = 0; i < frontLine.Size; i++)
        {
            if (frontLine[i] is UnitCard target && _combatTargetLegal(attacker, target.Ref))
            {
                result.Add(target.Ref);
            }
        }

        // HQ（W3-3 实体化）：目标以 HQ 实体引用承载——候选产出 hq.Ref（不留双承载：槽位引用不再作为
        // HQ 目标产出；槽位关系仅用于布局语义判定〔守护/轰炸机拦截/范围矩阵——经 HQ 占位槽〕）。
        // HQ 的槽位以棋盘为准查询（W3-4：HQ 居中占槽 2，不再假定 0）。
        var hqIndex = Battlefield.IndexOfHq(enemyLine);
        if (hqIndex >= 0 && enemyLine[hqIndex] is Hq hq && _combatTargetLegal(attacker, hq.Ref))
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
        for (var i = 0; i < line.Size; i++)
        {
            if (line[i] is not UnitCard unit || IsDead(unit))
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
        if (index < 0 || supportLine[index] is not Hq hq)
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
        if (index < 0 || index >= line.Size)
        {
            return false;
        }

        // 守护者判定＝双通道并行（批 4）：IsGuard 代码通道或「守护」词条（现势词条面——加载/授予-移除-复装/升级随动）。
        return line[index] is UnitCard guardian
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
        for (var i = 0; i < line.Size; i++)
        {
            if (line[i] is not UnitCard unit || IsDead(unit))
            {
                continue;
            }

            if (!ReferenceEquals(unit.Owner, player))
            {
                continue;
            }

            KeywordRules.ResetActionQuotas(unit);
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
        for (var i = 0; i < line.Size; i++)
        {
            if (line[i] is UnitCard unit && !IsDead(unit) && ReferenceEquals(unit.Owner, player))
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
                // R2/R8：归属座位＋合并类别（移动 ∪ 攻击并集——发布时无法确定单一类别，故标 command）。
                new ReferenceSetParameter(candidates, 1, 1, tag: unit)
                {
                    Owner = unit.Owner,
                    Action = SelectorActions.Command,
                });

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
        CancellationToken ct, Card? triggerCard)
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
        else if (selectedEntity is FormationGap)
        {
            await DispatchMoveAsync(unit, selected, box, ctx, ct, triggerCard);
            return;
        }

        // 无效目标分派（防御；正常流程不可达——候选面之外者不产生终局）
        box.Result = CommandResult.Failure(CommandFailureReason.TargetingFailed);
    }

    private async Task DispatchMoveAsync(
        UnitCard unit, Ref<Entity> newPositionRef, CommandFlowBox box, Context ctx,
        CancellationToken ct, Card? triggerCard)
    {
        var state = unit.GetData<UnitStateData>();
        var oldPosition = PositionOf(unit);
        if (oldPosition is null)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 嵌套执行（复验＝移动触发器验证承载——拒绝＝仅本次取消、零副作用＋留痕）
        var data = new Dictionary<string, object?>
        {
            [CommandDataKeys.Unit] = unit.Ref,
            [CommandDataKeys.OldPosition] = oldPosition.Ref,
            [CommandDataKeys.NewPosition] = newPositionRef,
        };
        // X1 兼容入口（R9）：逐层手工透传保留；帧继承已覆盖（不写键时子帧继承父帧值），
        // 此处仅在非空时显式写入，值已是归一化后的 Card。
        if (triggerCard is not null)
        {
            data[CommandDataKeys.TriggerCard] = triggerCard;
        }

        var flowCtx = new Context(data);
        await _engine.InvokeMemberAsync(EngineProcesses.UnitMove, flowCtx, ct);

        // 复验拒绝（守卫边在数据面留标记并本级停止）：零收尾、失败原因＝ExecutionRejected。
        // 不能落到下面的 CommandFlowFault——那是"链被中断／结构不完整"的兜底码，不是"本次操作不合法"。
        if (IsGuardRejected(flowCtx))
        {
            box.Result = CommandResult.Failure(CommandFailureReason.ExecutionRejected);
            return;
        }

        if (ctx.Interrupted || flowCtx.Interrupted)
        {
            box.Result = CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 外层收尾（执行成功后）：扣费 → 两 bool 更新 → unit.acted（恰一次、只在外层；经 S5 统一收尾单元）
        var isTank = HasUnitType(unit, UnitType.Tank);
        var profile = new OperateProfile(
            DeductCost: true,
            IsMove: true,
            IsTank: isTank,
            EmitActed: true);

        await _operateFinalizer.FinalizeAsync(unit, profile, ct);
        box.Result = CommandResult.Success();
        await _operateFinalizer.EmitActedAsync(unit, profile, ct); // E1-33：已阵亡不发射（不为亡者发射）
    }

    private async Task DispatchAttackAsync(
        UnitCard unit, Ref<Entity> targetRef, CommandFlowBox box, Context ctx,
        CancellationToken ct, Card? triggerCard)
    {
        // 嵌套执行（复验＝攻击触发端验证承载——拒绝＝仅本次取消、零副作用＋留痕）。
        // S11：指挥入口只调**攻击动作**（触发端 → 处理端），不在入口内做结算。
        var action = await AttackAction.CommandAsync(unit, targetRef, triggerCard, ct).ConfigureAwait(false);

        var outcome = action.Targets[0];
        if (outcome.Outcome != ActionOutcomeKind.Applied)
        {
            box.Result = outcome.Reason == ActionFailureKind.ValidationRejected
                ? CommandResult.Failure(CommandFailureReason.ExecutionRejected)
                : CommandResult.Failure(CommandFailureReason.CommandFlowFault);
            return;
        }

        // 指向：指挥攻击成功发起后，对目标各触发一次（复验拒绝/取消不触发；效果发起的攻击不走本路径）。
        // 目标引用可能**已随本次攻击失效**（攻击后 handler 把目标转换/移出战场——如"虎视"转溃军）：
        // 指向通知只对仍在场的卡有意义，失效引用不可解引用（Ref.Value 会抛 StaleReferenceException）。
        if (targetRef.IsAlive && targetRef.Value is Card targeted)
        {
            await TargetingRules.NotifyAsync(
                _engine, targeted, unit, TargetingRules.KindCommand, triggerCard, ct);
        }

        // 外层收尾（执行成功后；攻击者死亡不豁免）：扣费 → 两 bool 更新 → unit.acted（恰一次、只在外层；经 S5 统一收尾单元）
        var isTank = HasUnitType(unit, UnitType.Tank);
        var profile = new OperateProfile(
            DeductCost: true,
            IsMove: false,
            IsTank: isTank,
            EmitActed: true);

        await _operateFinalizer.FinalizeAsync(unit, profile, ct);
        box.Result = CommandResult.Success();
        await _operateFinalizer.EmitActedAsync(unit, profile, ct); // E1-33：已阵亡＝不为亡者发射
    }

    // ---------- ⑥ 单位移动触发器默认链（执行段） ----------

    private async Task HandleUnitMoveAsync(UnitMoveTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Unit is not { IsAlive: true } unitRef || unitRef.Value is not UnitCard unit
            || view.NewPosition is not { IsAlive: true } newRef || newRef.Value is not FormationGap newSlot
            || view.OldPosition is not { IsAlive: true })
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：链中止、移动不发生
            return;
        }

        // 执行段＝**底层移动动作**（S6）：指挥入口只负责前置（复验绑定）与收尾（OperateFinalizer）；
        // 槽位变更与位置更新全部落在移动动作的处理端（唯一落地口）。此处不再自行写槽位。
        var result = await MoveAction.CommandAsync(unit, newSlot, view.TriggerCard, ct).ConfigureAwait(false);
        if (result.Targets.Count == 0 || result.Targets[0].Outcome != ActionOutcomeKind.Applied)
        {
            ctx.Interrupt(); // 底层动作未落地：本次指挥移动失败、零收尾（不扣费、不改行动位）
        }

        // 守护维护由 unit.position.changed 更新驱动（订阅重算——布局已变更、收尾未发生）
    }

    // ---------- ⑦ 攻击结算（S11：触发端/处理端的默认链在 AttackAction 内；此处只保留指挥入口可见性） ----------

    /// <summary>
    /// **伤害来源游标**（E1-47）：伤害门户触发的「防御归零统一死亡衔接」与本调用**同一同步调用链**，
    /// 故以结算窗口内的临时游标承载"本次伤害的施动方"（窗口外恒 null ⇒ 死亡无击杀者归属）。
    /// 用**恢复式**赋值（try/finally 还原上一值）——嵌套伤害（如伤害链内的再触发）亦保持作用域正确。
    /// 唯一使用点＝处理端核心 <see cref="MatchActionServices.ApplyDamageAsync"/>（R11 门户收口后）。
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
    /// 统一死亡流程（攻击结算判定死亡后调用；伏击改写的攻击者死亡同走此流程）——S8 起收敛为**死亡动作**入口：
    /// 触发端 → 处理端（<c>Rewrite</c> 唯一改写位 → <c>Veto</c> 免死守卫 → <c>Apply</c> 死亡链）。
    /// 完整次序（A4 定稿·唯一权威口径——《需求文档》Q&A-1，落地实现见 <see cref="MatchActionServices.ApplyDeathAsync"/>）：
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

    /// <summary>
    /// 效果发起的攻击（R16）：**零行动经济面**（不扣指挥点、不写行动位、不发 <c>unit.acted</c>）——
    /// 代价属于「指挥」，不属于攻击动作；**不受压制阻断**：压制只影响「能否被指挥」
    /// （<c>attack.leg.eligibility</c> 与候选名单），被压制单位仍可被效果强制攻击。
    /// </summary>
    internal async Task<bool> AttackByEffectAsync(UnitCard unit,Orc.Cards.Card target,CancellationToken ct,bool bypassRange=false)
    {
        if(_lifecycle?.IsActionAllowed==false||!unit.TryGetData<UnitStateData>(out var state)||state.IsDestroyed||state.Line is null)return false;
        await using var scope=_engine.BeginAction();
        var result=await AttackAction.EffectAsync(unit,target.Ref,bypassRange,triggerCard:unit,ct).ConfigureAwait(false);
        return result.Targets[0].Outcome==ActionOutcomeKind.Applied;
    }

    /// <summary>
    /// 效果发起的位移（`retreat`、被控移动等；S6 效果入口）：**零资源消耗**——不扣指挥点、不写行动位、
    /// 不发 <c>unit.acted</c>、不要求 <c>CanMove</c>、**不受压制阻断**（压制只决定「能否被指挥」，R16）。
    /// 前置只判「单位在场未毁、目标槽存在/为空/非原槽、方向约束＝己方支援线」；落地经底层移动动作。
    /// </summary>
    internal async Task<bool> MoveUnitAsync(UnitCard unit, FormationGap destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(destination);
        if (unit.Owner is not { } owner || !ReferenceEquals(destination.Line, _battlefield.GetSupportLine(owner))) return false;

        return await EffectMoveAsync(unit, destination, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 效果移动的**共用执行体**（S6）：两个入口重载都走这里 → <see cref="MoveAction.EffectAsync"/>（同一底层动作）。
    /// 前置只判「单位在场未毁、目标槽存在/为空/非原槽」；方向约束由各重载自己表达。
    /// </summary>
    private async Task<bool> EffectMoveAsync(UnitCard unit, FormationGap destination, CancellationToken ct)
    {
        if (_lifecycle?.IsEnded == true) return false;
        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed || state.Line is null) return false;
        if (!destination.IsCurrent) return false;

        await using var scope = _engine.BeginAction();
        var result = await MoveAction.EffectAsync(unit, destination, triggerCard: null, ct).ConfigureAwait(false);
        return result.Targets.Count > 0 && result.Targets[0].Outcome == ActionOutcomeKind.Applied;
    }

    /// <summary>
    /// 无头伤害（效果运行期受控入口；经 <c>Orc.Game.Effects.EffectRuntime.DamageAsync</c> 调用）：
    /// 走 <see cref="DamagePipeline.EffectAsync"/>（<c>effect.damage</c> → <c>damage.apply</c>），
    /// 与对战/战斗共用同一处理端（免疫、重甲语义一致）。
    /// </summary>
    internal Task<bool> DamageByEffectAsync(Card? source, Card target, int amount, CancellationToken ct = default)
        => DamagePipeline.EffectAsync(source, target, amount, ct);

    /// <summary>
    /// 战斗（R10；经 <c>Orc.Game.Effects.EffectRuntime.BattleAsync</c> 调用）：双方快照攻击力后各打一次
    /// <c>battle.damage</c>（同时结算——先死者不影响反向输出）；不发攻击类信号、不扣行动代价。
    /// </summary>
    internal async Task<bool> BattleByEffectAsync(UnitCard first, UnitCard second, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var firstAttack = Math.Max(0, first.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var secondAttack = Math.Max(0, second.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        var forward = await DamagePipeline.BattleAsync(first, second, firstAttack, ct).ConfigureAwait(false);
        var backward = await DamagePipeline.BattleAsync(second, first, secondAttack, ct).ConfigureAwait(false);
        return forward && backward;
    }

    /// <summary>
    /// 无头移动（效果运行期受控入口；经 <c>Orc.Game.Effects.EffectRuntime.MoveAsync</c> 暴露——internal 以免零散扩张 public 面）：
    /// 依目标区域取**首个空槽**后走指定槽效果入口（S6：zone 重载只是选择器，不新增语义入口）。
    /// <para>
    /// P2 有意变更：本入口不再走指挥移动链（不扣费、不清行动位、不发 <c>unit.acted</c>）——
    /// 效果移动是动作的后果而非行动，代价属于「指挥」（R16）。
    /// </para>
    /// 不可移动（无位置 / 无空槽 / 区域未知）＝false（不抛错）。
    /// </summary>
    internal async Task<bool> MoveUnitAsync(UnitCard unit, string zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);

        if (unit.GetData<UnitStateData>().Line is null)
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

        FormationGap? target = null;
        foreach (var gap in line.GetInsertionGaps())
        {
            target = gap;
            break;
        }

        if (target is null)
        {
            return false;
        }

        // zone 重载只是选择器：不经「己方支援线」方向约束（推进到前线由 zone 语义表达）。
        return await EffectMoveAsync(unit, target, ct).ConfigureAwait(false);
    }

    /// <param name="killer">击杀者（E1-47 加性；null＝非归属驱动来源——如修饰到期致防御归零）。</param>
    private async Task ProcessDeathAsync(UnitCard unit, CancellationToken ct, Card? killer = null)
        => await DeathAction.ExecuteAsync(unit, killer, triggerCard: null, ct).ConfigureAwait(false);

    /// <summary>
    /// 死亡处理端 <c>Rewrite</c> 段的内置 handler（S8）：把旧「词条程序 <c>death.cancel</c>」取消口
    /// 改为改写位——取消＝<c>ShouldDie=false</c>，并按既有次序中止本段后续（免死守卫在 <c>Veto</c> 段收口留痕）。
    /// </summary>
    private async Task HandleKeywordDeathCancelAsync(DeathHandleView view, Context ctx, CancellationToken ct)
    {
        if (await KeywordProgramRuntime.BeforeDestroy(view.Unit, view.Killer, ct).ConfigureAwait(false))
        {
            view.ShouldDie = false;
            ctx.Stop();
        }
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
    /// <summary>璇诲彇鍗曚綅褰撳墠浣嶇疆锛堢嚎锛嬪簭鍙凤紱鏈湪鍦猴紳null锛夈€?/summary>
    private static BoardPosition? PositionOf(UnitCard unit)
    {
        if (!unit.TryGetData<UnitStateData>(out var state) || state.Line is not { } line)
        {
            return null;
        }

        var index = line.IndexOf(unit);
        return index < 0 ? null : new BoardPosition(line, index);
    }

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
    private Func<UnitCard, BoardPosition?, LegEligibilityFailure?> CreateBuiltInMoveLegEligibility()
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

