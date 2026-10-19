using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 2C 词条系统（对战词条；A1 组件化体系）：
// 词条＝独立组件（KeywordComponent：自含数据〔含参值〕＋运行逻辑＋授予/移除回调＋可内嵌效果）；
// 卡上「词条管理组件」（CardBase.Keywords → KeywordManager）负责注册与索引（有无/参值查询）与
// 授予/移除/参值改写统一读写口；注册面（KeywordRegistry）为合法标识集与组件构造的唯一来源。
// 装载（加载时＝CardBase.LoadAsync 的词条装载步骤）：从定义声明逐条经管理组件授予链挂载
// （存在性置位 → 运行逻辑装载＋内嵌效果装载 → OnGrant）——先于 card.load，卡组/手牌即可被读取查询。
// 无词条卡＝无副作用（不注册词条组件、不装载逻辑；词条面可寻址、空内容）。
// 本文件承载：标识常量（KeywordIds）／词条装载上下文（KeywordLoadContext）／静态便利读取与奋战记账面（KeywordRules）。
// 四词条组件实现见 KeywordComponents.cs；注册面见 KeywordRegistry.cs；管理组件见 KeywordManager.cs。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 词条标识常量集（2C；需求指定固定字面值、精确匹配；对外读取契约）。
/// 生产内建标识经 <see cref="KeywordRegistry"/> 注册面装配（2C 首批四枚：<see cref="Blitz"/> / <see cref="Fury"/> /
/// <see cref="SmokeScreen"/> / <see cref="Ambush"/>；其后随批次逐步扩展——当前全集见登记序清单 <see cref="All"/>）；
/// 合法标识集＝注册面内容（含开放扩展注册的标识）。
/// 词条声明含未注册标识＝定义期明确错误（fail-fast；校验承载于 <see cref="CardDefinition"/>）。
/// </summary>
public static class KeywordIds
{
    /// <summary>闪击（能力型）：部署（unit.deployed）时置单位可移动＋可攻击。</summary>
    public const string Blitz = "闪击";

    /// <summary>奋战（能力型）：攻击后记账——首次攻击后仍可攻、二次后不可（「本轮已攻次数」由词条组件自维护）。</summary>
    public const string Fury = "奋战";

    /// <summary>烟幕（标记型）：不可被攻击（对一切攻击者生效；攻击 targeting 中被筛除、不入候选）——轻量组件形态。</summary>
    public const string SmokeScreen = "烟幕";

    /// <summary>伏击（能力型）：「造成攻击伤害」内改写——被攻击单位实时攻击力＞攻击者实时防御力时，改为攻击者死亡、被攻击者不受伤。</summary>
    public const string Ambush = "伏击";

    // ---------- A2 新增（第 2 批·9 项词条；标识字面＝对外数据契约基线——Q&A-13） ----------

    /// <summary>被压制（状态标记型）：不能移动或攻击；拥有者回合结束时按剩余回合数递减解除（默认 1；「额外压制一回」＝＋1）。</summary>
    public const string Suppressed = "被压制";

    /// <summary>被抑制（状态标记型）：抑制清空处置后保留的标记（供条件查询——「被抑制或被压制」类消费）。</summary>
    public const string Inhibited = "被抑制";

    /// <summary>动员（能力型）：友方回合开始 +1/+1（累积保留）；受到实际伤害（净伤害＞0）后失去（词条移除链）。</summary>
    public const string Mobilize = "动员";

    /// <summary>钳击（能力型）：部署时可选另一友方单位形成一对一配对（既有单选交互）；双方获钳击效果；一方离场另一方即时失去。</summary>
    public const string Pincer = "钳击";

    /// <summary>预报（骨架）：仅标识声明/装配/读取——行为面待澄清（官方语义不可考，见 Q&A-1；申报）。</summary>
    public const string Forecast = "预报";

    /// <summary>免疫（能力型）：不会受到伤害——伤害归零（不限制索敌；非伤害效果照常；单位与 HQ 同族承载）。</summary>
    public const string Immune = "免疫";

    /// <summary>重甲（参值型）：受到的对战伤害 -X（攻击/反击均含；指令伤害不减免）；参值域 0..3（值封顶）。</summary>
    public const string Armor = "重甲";

    /// <summary>情报（参值型）：具有情报的卡被使用时触发（触发结构就位；「明牌 X 张」执行面待接线——G18 未就绪）；参值域 0..3。</summary>
    public const string Intelligence = "情报";

    /// <summary>无法被压制（防护标记型）：施加压制的资格判定拒绝（施加前查对位防护标记；可授予、可经移除路径清除）。</summary>
    public const string CannotBeSuppressed = "无法被压制";

    /// <summary>无法被抑制（防护标记型）：施加抑制的资格判定拒绝（施加前查对位防护标记；可授予、可经移除路径清除）。</summary>
    public const string CannotBeInhibited = "无法被抑制";

    // ---------- A4 新增（第 2 批·亡计；标识字面＝对外数据契约基线） ----------

    /// <summary>
    /// 亡计（内容型）：统一死亡流程「亡计结算」步与「亡计再触发」共用的执行面承载（单源）。
    /// 内部效果＝死亡结算动作（内容经内嵌效果通道、实例为基准；空内容＝合法静默）；参值位无语义；不打对战词条标。
    /// </summary>
    public const string Deathrattle = "亡计";

    // ---------- S1 新增（老兵机制；标识字面＝对外数据契约基线） ----------

    /// <summary>
    /// 老兵（标记型）：单位当前形态内容为「老兵版本」的标记（单一真源——老兵读取面的唯一判据）。
    /// 由数据映射 `VeteranOf:<基础卡id>`（老兵卡定义）随内容落地；升级动作经「全清→换新→授予老兵版本词条集」落地；
    /// 老兵卡以任意途径在场的实例同样成立（内容即真源——不以「是否经历过升级动作」为判据）。
    /// 无参值；不打对战词条标；行为面＝无（纯标记，消费方经 <see cref="VeteranRules.IsVeteran"/> 读取）。
    /// </summary>
    public const string Veteran = "老兵";

    // ---------- S2 新增（隐蔽机制；标识字面＝对外数据契约基线） ----------

    /// <summary>
    /// 隐蔽（标记型）：单位数据声明 `covert` 映射的「隐蔽」标记（单一真源——隐蔽读取面的唯一判据）。
    /// 语义＝单位「无法被卡牌效果影响」（指令效果索敌经判定器剔除／无头选靶候选剔除／光环受益剔除／钳击配对剔除；
    /// 攻击路径不受限——可被攻击、会被揭示）；无参值；不打对战词条标；
    /// 行为面＝无（纯标记，消费方经 <see cref="CovertRules.IsCovert"/> 读取）；揭示＝标记移除（<see cref="CovertRules.RevealAsync"/>）。
    /// </summary>
    public const string Covert = "隐蔽";

    // ---------- 批 4 新增（守护词条化——新词条纳入；标识字面＝对外数据契约基线） ----------

    /// <summary>
    /// 守护（标记型）：相邻（同线槽位索引差 1）同方存活「守护者」使该单位/HQ 获「被守护」
    /// （被守护者仅能被炮/轰攻击）。守护者判定＝双通道并行——代码通道（<see cref="CardDefinition.IsGuard"/>）
    /// 或本词条（现势词条面；数据体 `guard` 映射落地），两者行为等价（判定/保护/豁免规则零变化）。
    /// 无参值；打对战词条标（wiki『属于对战词条』）；行为面＝无（纯标记——守护维护重算由
    /// 位置/入场/死亡/升级更新与词条授予-移除完成点驱动，见 CommandManager）。
    /// </summary>
    public const string Guard = "守护";

    // ---------- 批 5 新增（冲击词条化——新词条纳入；标识字面＝对外数据契约基线） ----------

    /// <summary>
    /// 冲击（标记型）：攻击后不再受到反击（反击资格判定条款——豁免族最高优先、先于轰炸机/炮兵豁免；
    /// 伏击先资格随之翻转）；攻击一次后永久移除（消耗＝攻击执行段公共尾部——攻击即消耗、含 HQ 攻击；
    /// 未触发不失去；消耗后经「获得」渠道可再授予）。无参值；打对战词条标（wiki『属于对战词条』）；
    /// 行为面＝无（纯标记——免反击＝C5 条款现势读取〔静态读〕、消耗＝攻击执行段尾部词条移除链，均不内嵌逻辑）。
    /// </summary>
    public const string Shock = "冲击";

    /// <summary>生产内建词条标识清单（登记序；注册面静态装配依据与诊断用——合法集真源＝注册面内容）。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Blitz, Fury, SmokeScreen, Ambush,
        Suppressed, Inhibited, Mobilize, Pincer, Forecast, Immune, Armor, Intelligence,
        CannotBeSuppressed, CannotBeInhibited,
        Deathrattle,
        Veteran,
        Covert,
        Guard,
        Shock,
    };

    /// <summary>标识是否已注册（存在性判定，转发注册面——单源；null/空白＝false）。</summary>
    public static bool IsDefined(string keyword) => KeywordRegistry.IsDefined(keyword);
}

/// <summary>
/// 词条装载上下文（2C；装载词条组件运行逻辑所需的对局级服务面；A2 加性扩展：钳击/压制所需的对局服务）：
/// 经卡牌库注入（延迟读取）、<see cref="CardBase.LoadAsync"/> 的词条装载步骤与运行时授予按需取用；独立构造（脱离对局）＝null
/// （词条组件运行逻辑装载跳过注册、不抛错——功能不可用、加载不失败）。
/// A2 服务面（均可缺省——独立构造/部分装配＝null，消费侧防御跳过）：
/// 目标选择管理器（钳击同伴选择交互）、战场（钳击候选枚举——己方在场单位）、
/// 钳击关系注册表（一对一占用查询/登记）、当前行动方提供器（压制施加时的「拥有者回合」判定）。
/// K2 判定器通道（可缺省——缺省＝无判定器通道：伏击效果静默不注册〔与「无上下文」同构〕、不抛错、不回退直调）：
/// 反击资格（combat.counter.eligibility——伏击资格判定取用）、伏击条件（combat.ambush.condition——伏击条件判定取用）。
/// 批 4 加性：词条变更观察者（可缺省——缺省＝不通知〔跳过〕；对局侧随动驱动的接入面，如守护维护重算）。
/// </summary>
public sealed class KeywordLoadContext
{
    internal KeywordLoadContext(
        LogicEngine engine,
        Trigger<AttackDamageTriggerView> attackDamageTrigger,
        TargeterManager? targeterManager = null,
        Battlefield? battlefield = null,
        PincerRegistry? pincerRegistry = null,
        Func<Player?>? currentPlayerProvider = null,
        Func<UnitCard, UnitCard, bool>? counterEligibility = null,
        Func<UnitCard, UnitCard, bool>? ambushCondition = null,
        Action? keywordChangeObserver = null,
        KeywordSnapshot? keywordSnapshot = null,
        Orc.Game.Actions.IDamageAction? damageAction = null,
        Orc.Game.Actions.IDeathAction? deathAction = null)
    {
        Engine = engine;
        AttackDamageTrigger = attackDamageTrigger;
        TargeterManager = targeterManager;
        Battlefield = battlefield;
        PincerRegistry = pincerRegistry;
        CurrentPlayerProvider = currentPlayerProvider;
        CounterEligibility = counterEligibility;
        AmbushCondition = ambushCondition;
        KeywordChangeObserver = keywordChangeObserver;
        Keywords = keywordSnapshot;
        DamageAction = damageAction;
        DeathAction = deathAction;
    }

    /// <summary>对局引擎（发射更新 / 触发子触发器所需）。</summary>
    public LogicEngine Engine { get; }

    /// <summary>
    /// 本局**词条快照**（前端资产迁移 S5）：对局装配开始时由 <see cref="Match"/> 冻结并注入，
    /// 此后授予链的组件构造与词条效果绑定查询一律读本快照——运行期"覆盖"不影响进行中的对局。
    /// 独立构造（无对局）＝null ＝回退进程级静态面（既有行为）。
    /// </summary>
    public KeywordSnapshot? Keywords { get; }

    /// <summary>「造成攻击伤害」共享流程触发器（伏击/免疫/重甲效果的改写注册口；引擎侧创建、与对局同生）。</summary>
    public Trigger<AttackDamageTriggerView> AttackDamageTrigger { get; }

    /// <summary>目标选择管理器（A2 加性；钳击同伴选择交互的承载——既有单选交互形态；可空＝无对局服务）。</summary>
    public TargeterManager? TargeterManager { get; }

    /// <summary>战场（A2 加性；钳击候选枚举「己方战场上单位」的布局真源；可空＝无对局服务）。</summary>
    public Battlefield? Battlefield { get; }

    /// <summary>钳击关系注册表（A2 加性；一对一占用约束的查询/登记面；可空＝无对局服务）。</summary>
    public PincerRegistry? PincerRegistry { get; }

    /// <summary>当前行动方提供器（A2 加性；压制施加时「是否处于拥有者回合」判定〔「下一个回合」不含当前回合〕；可空＝视为非拥有者回合）。</summary>
    public Func<Player?>? CurrentPlayerProvider { get; }

    /// <summary>反击资格判定通道（K2 加性；combat.counter.eligibility——伏击资格判定取用〔资格→C5〕；
    /// 可空＝无判定器通道：伏击效果静默不注册、不抛错、不回退直调）。</summary>
    public Func<UnitCard, UnitCard, bool>? CounterEligibility { get; }

    /// <summary>伏击条件判定通道（K2 加性；combat.ambush.condition——伏击条件判定取用〔条件→C6〕；
    /// 可空＝无判定器通道：伏击效果静默不注册、不抛错、不回退直调）。</summary>
    public Func<UnitCard, UnitCard, bool>? AmbushCondition { get; }

    /// <summary>
    /// 词条变更观察者（批 4 加性；守护词条化的随动驱动通道）：词条链在**真实授予/移除完成点**通知恰一次
    /// （幂等重复＝零通知、失败回滚＝零通知——「改变才传播」）；对局侧据此随动重算（如守护维护——
    /// 「授予-移除-复装动作完成即重算」，与位置/入场/死亡/升级更新驱动同模式、同步执行）。
    /// 可空＝无对局上下文（独立构造/部分装配）＝跳过、不抛错（操作不失败）。
    /// </summary>
    public Action? KeywordChangeObserver { get; }

    /// <summary>
    /// 伤害动作（R11/P5b 接口化）：重甲挂 <c>combat.damage</c>、免疫单位侧挂 <c>damage.apply</c>；
    /// 可空＝无对局伤害面（独立构造）＝不注册（防御、不抛错）。
    /// </summary>
    public Orc.Game.Actions.IDamageAction? DamageAction { get; }

    /// <summary>
    /// 死亡动作（S11 加性）：伏击改写经它走**完整两端**（触发端 → 处理端）执行「以死代伤」；
    /// 可空＝无对局死亡面（独立构造）＝伏击效果不注册（防御、不抛错）。
    /// </summary>
    public Orc.Game.Actions.IDeathAction? DeathAction { get; }
}

/// <summary>
/// 词条规则助手（2C；静态便利口——卡上词条面的转发面，调用形态保持；A2 泛化：宿主类型 CardBase → <see cref="Card"/>——
/// 单位与 HQ 同族读取，其它实体＝动态查询 false/null、不抛错）：
/// 读取（有无/参值）与奋战记账语义转发至词条组件体系（无第二套实现；无词条卡＝动态查询 false/null、不抛错）。
/// </summary>
public static class KeywordRules
{
    /// <summary>是否含指定词条（标识查询；无词条卡＝false、不抛错——转发卡上词条面）。</summary>
    public static bool HasKeyword(Card card, string keyword)
        => TryGetKeywordManager(card)?.Has(keyword) ?? false;

    /// <summary>读取词条参值（加性扩展；无词条卡/无该词条/无参值＝null——与 <see cref="HasKeyword"/> 组合区分「有词条无参值」）。</summary>
    public static int? GetKeywordValue(Card card, string keyword)
        => TryGetKeywordManager(card)?.GetValue(keyword);

    /// <summary>
    /// 卡 → 词条管理面解析（A2 加性内部面）：CardBase 与 Hq 均携带词条面（同族承载）；
    /// 其它实体/未装载面＝null（查询自然产出 false/null、不抛错）。词条组件/服务面的统一入口。
    /// </summary>
    internal static KeywordManager? TryGetKeywordManager(Card card) => card switch
    {
        CardBase cardBase => cardBase.Keywords,
        Hq hq => hq.Keywords,
        _ => null,
    };

    /// <summary>
    /// 奋战记账（攻击动作成功执行后、由攻击执行段调用）：含奋战时「本轮已攻次数」+1；非奋战不记账。
    /// </summary>
    public static void RecordAttack(Card unit)
    {
        if (TryGetKeywordManager(unit) is { } keywords
            && keywords.TryGetComponent<FuryKeywordComponent>(KeywordIds.Fury, out var fury))
        {
            fury.RecordAttack();
        }
    }

    /// <summary>
    /// 攻击动作后的 CanAttack 收尾取值（外层收尾读取记账）：非奋战＝false（统一清位）；
    /// 奋战＝「本轮已攻次数」＜2（首次攻击后保持可攻、二次后不可）。
    /// </summary>
    public static bool ResolveCanAttackAfterAttack(Card unit)
        => TryGetKeywordManager(unit) is { } keywords
            && keywords.TryGetComponent<FuryKeywordComponent>(KeywordIds.Fury, out var fury)
            && fury.CanAttackAfterAttack();

    /// <summary>回合恢复：清零「本轮已攻次数」（随回合恢复一并清零；非奋战/无词条＝无操作）。</summary>
    public static void ResetTurnCounters(Card unit)
    {
        if (TryGetKeywordManager(unit) is { } keywords
            && keywords.TryGetComponent<FuryKeywordComponent>(KeywordIds.Fury, out var fury))
        {
            fury.ResetTurnCounters();
        }
    }

    /// <summary>
    /// 行动额度恢复（回合开始/部署置位共用）：移动额度＝1；攻击额度＝max(1, attack.limit, 奋战 2)。
    /// </summary>
    public static void ResetActionQuotas(UnitCard unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var attack = Math.Max(1, KeywordProgramRuntime.Resolve(unit, "attack.limit", 1));
        if (HasKeyword(unit, KeywordIds.Fury))
        {
            attack = Math.Max(attack, 2);
        }

        if (unit.TryGetData<CommandData>(out var command))
        {
            command.ResetQuotas(1, attack);
        }

        ResetTurnCounters(unit);
    }

    /// <summary>
    /// 部署链收尾驱动（扣费完成后、链返回前；仅部署路径调用）：遍历已挂载词条组件、逐组件执行收尾钩子
    /// （钳击＝同伴选择；闪击已迁效果承载〔批 3——部署时置位、不再经本钩子〕；其余默认空操作）——
    /// 收尾钩子的静态入口（消费点收口、细节在组件面）。
    /// </summary>
    public static async Task OnDeployChainFinalizedAsync(Card unit, CancellationToken ct)
    {
        if (TryGetKeywordManager(unit) is not { } keywords)
        {
            return;
        }

        foreach (var component in keywords.Components)
        {
            await component.OnDeployChainFinalizedAsync(unit, ct);
        }
    }

    /// <summary>
    /// 死亡注销驱动（统一死亡流程调用；转发至词条管理面——仅行为撤销：OnRevoke 序列＋运行逻辑注销＋内嵌效果卸载；
    /// 不执行存在性清除——登记/参值保留、照常可读）。重复调用＝无操作（幂等）。
    /// </summary>
    public static void RevokeAllOnDeath(Card unit) => TryGetKeywordManager(unit)?.RevokeOnDeath();
}
