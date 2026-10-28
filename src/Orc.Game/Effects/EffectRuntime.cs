using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Damage;
using Orc.Game.Judicators;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Effects;

/// <summary>无头选靶描述（效果运行期）：sel/side/zone/unitType/keyword/count 的最小集。</summary>
/// <param name="Sel">选靶方式（all/one/any/random/self——<c>self</c>＝视角卡自身，E1-41）。</param>
/// <param name="Side">阵营（friendly/enemy/both；null＝both）。</param>
/// <param name="Zone">区域（frontline/support；null＝两者）。</param>
/// <param name="UnitType">兵种过滤（null＝不过滤）。</param>
/// <param name="Keyword">词条过滤（null＝不过滤）。</param>
/// <param name="Count">数量（null＝按 sel 默认）。</param>
public sealed record EffectSelector(
    string Sel,
    string? Side = null,
    string? Zone = null,
    string? UnitType = null,
    string? Keyword = null,
    int? Count = null,
    EffectThreshold? Threshold = null,
    bool IncludeCovert = false);

/// <summary>
/// **目标阈值过滤**（E1-57）：目标卡的属性须满足 字段 算子 取值（如 花费不大干 3 的单位）。
/// </summary>
/// <param name="Field">字段（ttack／defense／opCost／deployCost）。</param>
/// <param name="Op">算子（gte／lte／gt／lt／eq）。</param>
/// <param name="Value">取值。</param>
public sealed record EffectThreshold(string Field, string Op, int Value);

/// <summary>
/// **光环受益谓词描述**（E1-56）：由游戏层据此构造 <see cref="Orc.Game.Cards.AuraDeclaration"/> 的受益谓词
/// （csx 侧只描述"谁受益"，谓词本体不落到脚本）。
/// </summary>
/// <param name="Side">阵营面（friendly/enemy/both；null＝不限）。</param>
/// <param name="Faction">阵营（<see cref="Cards.Faction"/> 枚举名；null＝不限）。</param>
/// <param name="UnitType">兵种（<see cref="Cards.UnitType"/> 枚举名；null＝不限）。</param>
/// <param name="Keyword">词条标识（null＝不限）。</param>
/// <param name="ExcludeSelf">排除宿主自身（`其他/其它…`）。</param>
/// <param name="Zone">区域（frontline/support；null＝不限）。</param>
public sealed record EffectAuraFilter(
    string? Side = null,
    string? Faction = null,
    string? UnitType = null,
    string? Keyword = null,
    bool ExcludeSelf = false,
    string? Zone = null);

/// <summary>
/// 修饰期限（E1-41；csx 侧经 <c>buff</c>/<c>costMod</c> 的 <c>until</c> 参数传入）：
/// 持续态修饰器的**到期相位**——<see cref="Permanent"/>＝随效果存续（无期限）。
/// </summary>
public enum EffectDuration
{
    /// <summary>无期限（修饰器随效果存续／随效果卸载撤销）。</summary>
    Permanent = 0,

    /// <summary>本回合结束（`直到回合结束`）。</summary>
    TurnEnd = 1,

    /// <summary>下个己方回合开始（`直到下个友方回合开始`）。</summary>
    NextOwnerTurnStart = 2,
}

/// <summary>
/// 效果运行时门面（csx handler 的**唯一游戏层受控入口**）：把「消灭（死亡链）／伤害／属性修饰／词条授予（含参值）／
/// 词条撤销／词条参值改写／内容型词条授予／抽牌／无头选靶／指挥点槽加·减／指挥点加·减（E1-25）」收敛到一处集中暴露——不给各服务零散加 public 面（便于审计与替换）。
/// <para>批 4 加性（csx 动态效果能力）：动态效果的**受控挂载/卸载/编译挂载**（<c>AttachPrefabAsync</c>／
/// <c>AttachSnapshotAsync</c>／<c>CompileAttachAsync</c>／<c>DetachEffectAsync</c>——统一结果体系）见
/// <c>EffectRuntime.DynamicEffects.cs</c>。</para>
/// <para>批 6 加性（卡组定向取卡）：<c>FetchFromDeckAsync</c>——从目标卡归属玩家卡组定向取出该实例并移动到其手牌
/// （主场景＝「（触发条件）后从卡组抽取此牌」；结果对象 <c>DeckFetchResult</c>——三态＋类别，两层同型）。</para>
/// <para>解析路径与既有服务同构：<see cref="ResolveFor"/>（卡 → 玩家 → 服务）。</para>
/// <para>服务不可达（未装配/脱局）＝**降级不抛错**：返回 false／空集（沿用「功能不可用＝不失败」口径）。</para>
/// <para>R9 加性（来源透传）：动作方法在 <c>ct</c> 之后追加可选 <c>Card? source</c>——只做两件事：
/// 写入下游信号的 <c>Source</c> 载荷、作为下游动作（伤害来源/击杀者/弃置施动方）的显式入参；
/// **不做猜测性回退**（不拿 target/viewer 当 source），不传＝null。`DamageAsync`／`KillAsync` 沿用既有
/// `source` 形参（位于 ct 前、位置不变）。本期已接下游消费方＝伤害/击杀＋弃置路径；其余动作的 `source`
/// 为下游服务接线预留（下游服务不在本步改动文件清单内）。</para>
/// </summary>
public sealed partial class EffectRuntime
{
    private readonly Func<CommandManager?> _commands;
    private readonly Func<PlayerManager?> _players;
    private readonly Func<JudicatorRegistry?> _judicators;
    private readonly Func<ResourceManager?> _resources;
    private readonly Func<Orc.Game.Board.Battlefield?>? _battlefield;
    private readonly Func<bool>? _allowRandom;
    private readonly Func<LogicEngine?>? _engine;
    private readonly Func<bool>? _isActionAllowed;

    /// <summary>创建运行时门面（协作者以延迟读取注入——装配链时序无关）。</summary>
    /// <param name="commands">指挥管理器访问器。</param>
    /// <param name="players">玩家管理器访问器。</param>
    /// <param name="judicators">判定器注册表访问器。</param>
    /// <param name="resources">资源管理器访问器。</param>
    /// <param name="engine">对局引擎访问器（批 4 加性；动态效果受控面用——实例化/预制体库；缺省 null＝动态效果面按「服务不可用」降级）。</param>
    /// <param name="isActionAllowed">可操作相位门禁访问器（批 4 加性；终局/非进行相位的受控面拒斥——缺省 null＝无门禁）。</param>
    /// <param name="battlefield">战场访问器（加性）。</param>
    /// <param name="allowRandom">
    /// 随机选择器开关访问器（R2c／A-5）：缺省 null＝**禁止**随机取样（生产口径）；
    /// 测试／沙盒／简单 AI 装配方显式返回 true。
    /// </param>
    /// <exception cref="ArgumentNullException">任一必填访问器为 null。</exception>
    public EffectRuntime(
        Func<CommandManager?> commands,
        Func<PlayerManager?> players,
        Func<JudicatorRegistry?> judicators,
        Func<ResourceManager?> resources,
        Func<LogicEngine?>? engine = null,
        Func<bool>? isActionAllowed = null,
        Func<Orc.Game.Board.Battlefield?>? battlefield = null,
        Func<bool>? allowRandom = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(judicators);
        ArgumentNullException.ThrowIfNull(resources);
        _commands = commands;
        _players = players;
        _judicators = judicators;
        _resources = resources;
        _engine = engine;
        _isActionAllowed = isActionAllowed;
        _battlefield = battlefield;
        _allowRandom = allowRandom;
    }

    /// <summary>
    /// 宿主所在方在战场上的单位（**同步只读面**）：条件表达式要渲染成同步 csx 守卫，
    /// 不能走 <see cref="SelectAsync"/>（那是交互选择器，需要 await）。未装配战场＝空集。
    /// </summary>
    /// <exception cref="ArgumentNullException">host 为 null。</exception>
    public IReadOnlyList<Card> OwnerUnits(Orc.Game.Cards.CardBase host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Owner is not { } owner || _battlefield?.Invoke() is not { } battlefield)
        {
            return Array.Empty<Card>();
        }

        var units = new List<Card>();
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.PlayerBSupportLine, battlefield.FrontLine })
        {
            foreach (var slot in line.Members)
            {
                if (slot is Orc.Game.Cards.CardBase unit && ReferenceEquals(unit.Owner, owner))
                {
                    units.Add(unit);
                }
            }
        }

        return units;
    }

    /// <summary>
    /// 伤害管线（**逃生舱**；csx 可自注册改写 handler）：四个触发端（对战/效果/战斗伤害触发端 ＋ 处理端 Apply）。
    /// <para>
    /// 常规路径＝预制体 <c>injects</c> 按名注入（「伤害处理触发器」等，撤销随效果托管）；
    /// 本属性供需要自注册/动态改绑的场景（自行 <c>Register</c> 并自管 <c>Unregister</c>——
    /// 建议在 handler 内以「宿主是否在场」兜底，避免卸载后残留注册项产生行为）。
    /// </para>
    /// <para>不可用（未装配/脱局）＝null（功能不可用、不抛错）。</para>
    /// </summary>
    public DamagePipeline? DamagePipeline => _commands()?.DamagePipeline;

    /// <summary>
    /// 宿主所在对局的**场级光环收集面**（csx 声明持续态光环的入口）：
    /// csx 侧自行构造 <see cref="AuraDeclaration"/>（受益谓词可组合 <see cref="Judge"/>）后
    /// 经本面登记——避免脚本直接触达对局环境（脚本沙箱屏蔽 <c>Environment</c> 片段）。
    /// <para>语义：宿主未加载/脱局＝null（功能不可用、不抛错）；登记后须经
    /// <see cref="RerunAurasAsync"/> 衔接一轮使收益现算生效。</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">host 为 null。</exception>
    public AuraRegistry? AurasOf(Card host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return GameEnvironment.ResolveFor(host)?.Auras;
    }

    /// <summary>
    /// 衔接一轮全卡链重跑（光环声明/撤销后使收益现算生效；csx 声明光环的配套调用）。
    /// <para>宿主未加载/脱局＝false（功能不可用、不抛错）。</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">host 为 null。</exception>
    public async Task<bool> RerunAurasAsync(Card host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (GameEnvironment.ResolveFor(host) is not { } environment)
        {
            return false;
        }

        await environment.RerunAllCardsAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 求值判定器（**同步**；csx 侧组合卡面条件的统一入口）：按名调用判定器、取其布尔结果。
    /// <para>语义：结果序列首元素为 bool 且为 true＝true；判定器不可达／结果非布尔／结果为空＝false（降级不抛错，
    /// 对齐「功能不可用＝不失败」既有口径）。参数载荷由调用方按判定器签名给出（见 <see cref="JudicatorNames"/>）。</para>
    /// </summary>
    /// <param name="name">判定器名（受控常量；<see cref="JudicatorNames"/>）。</param>
    /// <param name="args">载荷（按该判定器声明的强类型签名顺序）。</param>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    public bool Judge(string name, params object[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_judicators() is not { } judicators)
        {
            return false;
        }

        object[]? result;
        try
        {
            result = judicators.Invoke(name, args, null);
        }
        catch (KeyNotFoundException)
        {
            return false; // 判定器未注册：降级（功能不可用、不失败）
        }

        return result is { Length: > 0 } && result[0] is bool flag && flag;
    }

    /// <summary>卡 → 运行时解析（「卡 → 玩家 → 服务」收敛点；脱局/未注入＝null）。</summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static EffectRuntime? ResolveFor(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card switch
        {
            Hq hq => hq.Owner.EffectRuntime,
            CardBase cardBase => cardBase.Owner?.EffectRuntime,
            _ => null,
        };
    }

    /// <summary>消灭（**游戏层死亡链**：亡计/词条注销/修饰清理/<c>card.died</c>；与总线 <c>card.destroyed</c> 语义不同）。</summary>
    /// <param name="source">施动方（E1-50；提供时作为 <c>card.died</c> 的 <c>Killer</c> 归属——"本单位消灭"可命中）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> KillAsync(Card target, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_commands() is not { } commands || target is not UnitCard unit)
        {
            return false;
        }

        await commands.KillUnitAsync(unit, source as UnitCard, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// **死亡动作**（A2 加性；csx 侧取用死亡动作的等价入口）：把目标单位交给死亡动作
    /// <see cref="Orc.Game.Actions.IDeathAction.ExecuteAsync"/> 走**完整两端**（触发端 <c>engine#death</c> →
    /// 处理端 <c>engine#death.handle</c>）——与词条效果时代经 <c>KeywordLoadContext.DeathAction</c> 的调用**同一路径**。
    /// <para>
    /// 与 <see cref="KillAsync"/> 的关系：两者最终都进死亡动作（<c>KillAsync</c> → <c>CommandManager.KillUnitAsync</c>
    /// → <c>DeathAction.ExecuteAsync</c>），差别只在 **<c>triggerCard</c> 透传**——伏击类效果必须把攻击链的触发者卡
    /// 带进死亡链（<c>card.died</c> 归因与链路追溯），故 csx 侧用本入口而不是 <see cref="KillAsync"/>。
    /// </para>
    /// <para>服务不可达／目标非单位＝<c>false</c>（零副作用、不抛错——沿本门面「功能不可用＝不失败」口径）。</para>
    /// </summary>
    /// <param name="target">被消灭的单位（其它实体类型＝false）。</param>
    /// <param name="killer">击杀者（<c>card.died</c> 的 <c>Killer</c> 归属）。</param>
    /// <param name="triggerCard">触发者卡（链路传递；可空）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <remarks>
    /// **internal**：本门面属**契约冻结面**（<c>ContractFreezeTests</c> 逐条比对 <c>effectRuntime</c> 段），
    /// 新增公开成员＝未授权契约变更；csx 侧统一走 <see cref="Orc.Game.Cards.KeywordEffectActionFace"/>（非冻结面）。
    /// </remarks>
    internal async Task<bool> DeathAsync(
        Card target, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_commands() is not { } commands || target is not UnitCard unit)
        {
            return false;
        }

        var result = await commands.DeathAction
            .ExecuteAsync(unit, killer, triggerCard, ct)
            .ConfigureAwait(false);
        return result.Targets.Count > 0
            && result.Targets[0].Outcome == ActionOutcomeKind.Applied;
    }

    /// <summary>
    /// 造成伤害（游戏语义：单位扣防御、总部扣血）。
    /// R11：经 <c>effect.damage</c> → <c>damage.apply</c>（免疫在此归零、重甲不减免——重甲只挂对战伤害）。
    /// </summary>
    /// <param name="source">施动方（E1-50；提供时归属：发 <c>unit.damage.dealt</c>、致死时作 <c>Killer</c>）。</param>
    /// <returns><c>true</c> ＝处理端已执行；服务不可达/目标无伤害语义＝<c>false</c>（零副作用）。</returns>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> DamageAsync(Card target, int amount, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_commands() is not { } commands)
        {
            return false; // 无对局伤害管道：服务不可用（不旁路门户）
        }

        return await commands.DamageByEffectAsync(source, target, amount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// **战斗**（R10）：两个在场存活单位互相对拼——快照双方攻击力后各走一次 <c>battle.damage</c>，
    /// 先死者不影响反向输出（同时结算语义）。
    /// <para>无视：射程 / 守护 / 烟幕 / 拦截 / 伏击改写 / 反击资格；不扣行动费、不清行动位；
    /// 不发 <c>unit.attack.*</c> / <c>unit.combat.survived</c> / <c>unit.acted</c>。</para>
    /// </summary>
    /// <param name="first">先手方（其对 <paramref name="second"/> 的伤害 <c>Source/Killer = first</c>）。</param>
    /// <param name="second">后手方（反向同理）。</param>
    /// <param name="source">发起这次战斗的卡（审计参数：只写日志，**不改变伤害归属**；可空）。</param>
    /// <returns><c>true</c> ＝两向结算均落到处理端；任一方非在场存活单位 / 服务不可达＝<c>false</c>（零副作用）。</returns>
    /// <exception cref="ArgumentNullException">first / second 为 null。</exception>
    public async Task<bool> BattleAsync(Card first, Card second, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first is not UnitCard a || second is not UnitCard b)
        {
            return false;
        }

        if (!IsBattleReady(a) || !IsBattleReady(b) || ReferenceEquals(a, b))
        {
            return false; // 非在场存活单位（含同一单位）：零副作用
        }

        if (_commands() is not { } commands)
        {
            return false;
        }

        // source 仅审计：不改变「first→second 的伤害归属＝first」这一事实。
        _engine?.Invoke()?.RootStream.WriteLog(
            "战斗",
            source is null
                ? $"'{a.Name}' 与 '{b.Name}' 强制战斗（无发起卡）。"
                : $"'{a.Name}' 与 '{b.Name}' 由 '{source.Name}' 发起强制战斗。",
            LogLevel.Info,
            new[] { "battle" });

        return await commands.BattleByEffectAsync(a, b, ct).ConfigureAwait(false);
    }

    private static bool IsBattleReady(UnitCard unit)
        => unit.TryGetData<UnitStateData>(out var state) && !state.IsDestroyed && state.Line is not null;

    /// <summary>属性修饰（攻击力/防御力增减；来源＝本门面实例——撤销请经 <c>RemoveBySourceAsync</c>）。</summary>
    /// <param name="duration">期限（E1-41；<see cref="EffectDuration.Permanent"/>＝随效果存续）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> BuffAsync(
        Card target, int attack, int defense,
        EffectDuration duration = EffectDuration.Permanent, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is Hq hq && attack == 0)
        {
            if (defense == 0) return false;
            await hq.Modifiers.AddModifiersAsync(new[]
            {
                new AddModifier(CardStatFields.HqHealth, defense, this, ExpiryFor(duration, target))
            }, ct).ConfigureAwait(false);
            return true;
        }
        if (target is not CardBase cardBase)
        {
            return false;
        }

        var expiry = ExpiryFor(duration, target);
        var modifiers = new List<Modifier>();
        if (attack != 0)
        {
            modifiers.Add(new AddModifier(CardStatFields.Attack, attack, this, expiry));
        }

        if (defense != 0)
        {
            modifiers.Add(new AddModifier(CardStatFields.Defense, defense, this, expiry));
        }

        if (modifiers.Count == 0)
        {
            return false;
        }

        await cardBase.Modifiers.AddModifiersAsync(modifiers, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 词条授予（无参值形态）：词条面**统一判定**（CardBase＋Hq 同族——经统一读口；其余实体＝降级 false）。
    /// 语义完全沿用既有词条组件（幂等＝已存在时无操作 false；终态拒绝/装载失败＝异常透传、不吞不降级）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> GrantAsync(Card target, string keyword, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return KeywordRules.TryGetKeywordManager(target) is { } manager
            ? manager.GrantAsync(keyword)
            : Task.FromResult(false);
    }

    /// <summary>
    /// 词条授予（**带参值形态**；词条效果化·批 0）：<paramref name="value"/>＝词条参值（「重甲3」的 3）；
    /// null＝**未提供参值**（与显式 0 为两种不同形态——0 为合法参值）；通道层不校验值域（非负/上限——
    /// 值域由词条组件既有机制钳制，如重甲/情报 [0,3]）。可达域与语义/降级同无参值形态（同族统一判定）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> GrantAsync(Card target, string keyword, int? value, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return KeywordRules.TryGetKeywordManager(target) is { } manager
            ? manager.GrantAsync(keyword, value)
            : Task.FromResult(false);
    }

    /// <summary>
    /// 词条撤销（词条效果化·批 0）：词条面统一判定（CardBase＋Hq 同族）；完整卸载（行为面先撤 →
    /// 运行逻辑注销＋内嵌效果卸载 → 存在性清除、参值不可读）。幂等（不存在＝无操作 false、不报错）；
    /// 对已死亡卡＝拒绝（明确异常透传）；不可达（无词条面）＝false 降级。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> RevokeAsync(Card target, string keyword, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return KeywordRules.TryGetKeywordManager(target) is { } manager
            ? manager.RevokeAsync(keyword)
            : Task.FromResult(false);
    }

    /// <summary>
    /// 词条参值改写（词条效果化·批 0）：词条面统一判定（CardBase＋Hq 同族）；纯存储改写（不重载行为面）、
    /// 允许置空参值（<paramref name="value"/>＝null 合法——「参值位可空」语义经本口保持）；前提＝词条存在
    /// （不存在＝明确异常透传）。不可达（无词条面）＝false（可判别「未执行」）；可达＝已改写 true（置空成功也为 true）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> SetKeywordValueAsync(Card target, string keyword, int? value, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (KeywordRules.TryGetKeywordManager(target) is not { } manager)
        {
            return Task.FromResult(false);
        }

        manager.SetValue(keyword, value); // 语义沿用既有：改写前提＝词条存在（不存在＝异常透传）；置空合法
        return Task.FromResult(true);
    }

    /// <summary>抽牌（走玩家管理器——含满手爆牌裁决）；返回请求的抽牌次数（服务不可达＝0）。</summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<int> DrawAsync(Card viewer, int count, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (count <= 0 || _players() is not { } players || OwnerOf(viewer) is not { } player)
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            await players.DrawCard(player, ct).ConfigureAwait(false);
        }

        return count;
    }

    // ---------- 定向取卡（批 6·Q7：csx 受控面） ----------

    /// <summary>
    /// **定向取卡**（批 6·Q7；csx 受控面）：从 <paramref name="target"/> 的归属玩家卡组把该卡实例定向取出并
    /// 移动到其手牌——主场景＝「（触发条件）后从卡组抽取此牌」（宿主自我取件：<c>FetchFromDeckAsync(self)</c>）。
    /// 能力范围：**任意指定实例**（引用可达即可——不设「仅宿主/仅己方」收窄；他方卡组经调用方组合合法可达）、
    /// **单张**（「取 N 张」＝调用方连续组合）、目标区域锁定手牌；落点＝目标卡归属玩家（＝「声明玩家」——从卡推导形态；
    /// 同玩家容器间移动，不支持跨玩家转移）。
    /// 语义＝**抽取**（对齐抽牌链路）：未满手＝<c>card.drawn</c> → <c>card.hand.add</c>（恰一次、顺序）；
    /// 满手＝爆牌裁决（<c>card.drawn</c> → 销毁 → <c>card.burned</c>；<c>card.hand.add</c> 零次）；失败＝零信号、零变更。
    /// 结局一律**结果化**（与动作层**同一对象**）；
    /// 判定优先级：参数层（异常）→ <see cref="DeckFetchFailureReason.InvalidTarget"/>（未加载/独立构造/非本局）
    /// → <see cref="DeckFetchFailureReason.Unavailable"/>（服务不可用/门禁：准备态/初始化加载未完成/终局后）
    /// → <see cref="DeckFetchFailureReason.CardDestroyed"/> → <see cref="DeckFetchFailureReason.NotInDeck"/>（后两者由动作层细化）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null（参数层）。</exception>
    public async Task<DeckFetchResult> FetchFromDeckAsync(Card target, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        // ① 引用契约（InvalidTarget）：
        if (OwnerOf(target) is not { } player)
        {
            // 未加载／独立构造（无归属——无法推导落点玩家）。
            return DeckFetchResult.Rejected(DeckFetchFailureReason.InvalidTarget, target);
        }

        if (ResolveFor(target) is not { } resolved || !ReferenceEquals(resolved, this))
        {
            // 非本局引用（归属到其它门面/未装配——不构成本门面的有效目标）。
            return DeckFetchResult.Rejected(DeckFetchFailureReason.InvalidTarget, target);
        }

        // ② 服务可用性与门禁（Unavailable——对齐批 4 服务不可用／门禁先例；零副作用、不抛）：
        if (_engine?.Invoke() is null)
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.Unavailable, target);
        }

        if (_isActionAllowed is { } allowed && !allowed())
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.Unavailable, target);
        }

        if (_players() is not { } players)
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.Unavailable, target);
        }

        // ③ 目标形态（HQ 等不入卡组——不在卡组；失败零副作用；判定置于门禁之后、对齐全局优先级）：
        if (target is not CardBase cardBase)
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.NotInDeck, target);
        }

        // ④ 委托动作层（CardDestroyed / NotInDeck 的精确分类在动作层——两层同型、结果透传）：
        return await players.FetchFromDeckAsync(player, cardBase, ct).ConfigureAwait(false);
    }

    /// <summary>行动花费修饰（+N 花费；缺省来源＝本门面实例）。</summary>
    /// <param name="duration">期限（E1-41；<see cref="EffectDuration.Permanent"/>＝随效果存续）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> CostModAsync(
        Card target, int delta,
        EffectDuration duration = EffectDuration.Permanent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is not CardBase cardBase || delta == 0)
        {
            return false;
        }

        await cardBase.Modifiers
            .AddModifierAsync(
                new AddModifier(CardStatFields.OperateCost, delta, this, ExpiryFor(duration, target)), ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 部署费用修饰（+N 花费）：手牌卡亦可——部署费基准＝定义值（装载即就绪，无需单位化）。
    /// 供「复制入手且费用 +X」类效果（复员等）使用；行动花费走 <see cref="CostModAsync"/>。
    /// </summary>
    public async Task<bool> DeployCostModAsync(Card target, int delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is not CardBase cardBase || delta == 0)
        {
            return false;
        }

        await cardBase.Modifiers
            .AddModifierAsync(new AddModifier(CardStatFields.DeployCost, delta, this), ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 期限 → 修饰器期限声明（E1-41）：<see cref="EffectDuration.TurnEnd"/>＝本回合结束相位（无需过滤——
    /// 挂载后遇到的第一个 <c>turn.end</c> 即"本回合结束"）；
    /// <see cref="EffectDuration.NextOwnerTurnStart"/>＝<c>turn.start</c> 相位 **且载荷玩家＝目标卡归属玩家**。
    /// </summary>
    private static ModifierExpiry? ExpiryFor(EffectDuration duration, Card target) => duration switch
    {
        EffectDuration.TurnEnd => new ModifierExpiry(GameUpdates.TurnEnd),
        EffectDuration.NextOwnerTurnStart => new ModifierExpiry(
            GameUpdates.TurnStart,
            payload => payload is not null
                && payload.TryGetValue(GameUpdates.PayloadPlayer, out var player)
                && ReferenceEquals(player, OwnerOf(target))),
        _ => null,
    };

    /// <summary>加入手牌（按**卡名**在卡池解析定义；不可用/未命中＝0）。</summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<int> AddToHandAsync(Card viewer, string cardName, int count, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        if (count <= 0 || MatchCardService.ResolveFor(viewer) is not { } service || OwnerOf(viewer) is not { } player)
        {
            return 0;
        }

        if (ResolveDefinitionIdByName(service, cardName) is not { } definitionId)
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            await service.CreateAndPlaceToHandAsync(definitionId, player, ct).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>洗入卡组（按**卡名**解析定义；发 <c>deck.shuffled</c>；不可用/未命中＝0）。</summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<int> ShuffleInAsync(Card viewer, string cardName, int count, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardName);
        if (count <= 0 || MatchCardService.ResolveFor(viewer) is not { } service || OwnerOf(viewer) is not { } player)
        {
            return 0;
        }

        if (ResolveDefinitionIdByName(service, cardName) is not { } definitionId)
        {
            return 0;
        }

        for (var i = 0; i < count; i++)
        {
            await service.CreateAndPlaceIntoDeckShuffledAsync(definitionId, player, ct).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>压制（游戏层压制服务；施加前资格判定，拒绝＝false）。</summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> PinAsync(Card target, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit && await SuppressRules.ApplyAsync(unit).ConfigureAwait(false);
    }

    /// <summary>抑制（游戏层抑制服务——清空处置全链；资格拒绝/非单位＝false）。</summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> SilenceAsync(Card target, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit ? InhibitRules.ApplyAsync(unit, ct) : Task.FromResult(false);
    }

    /// <summary>
    /// 升为老兵（S1；**经「老兵触发器」**——升级动作唯一标准发动入口的公共路径；csx handler 的受控接入面）：
    /// 返回三态结果（成功升级/幂等无操作/拒绝——可程序化区分）；执行段失败＝回滚＋异常上抛（原异常、可辨识）。
    /// 非单位（指令/反制/HQ 等）＝拒绝结果（「在场且存活」谓词不满足——降级不抛错，对齐本门面惯例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<VeteranPromotionResult> UpgradeAsync(Card target, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit
            ? unit.InvokeVeteranTriggerAsync(ct)
            : Task.FromResult(VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.NotOnField));
    }

    /// <summary>
    /// 揭示（S2；**经 RevealAsync 型服务**——揭示动作唯一标准发动入口的公共路径；csx handler 的受控接入面）：
    /// 先移除「隐蔽」标记 → 调揭示逻辑（卡侧「揭示触发器」）→ 广播 <c>unit.revealed</c>（恰一次）；
    /// 返回二态结果（揭示发生/无操作——可程序化区分）。
    /// 非单位（指令/反制/HQ 等）＝无操作结果（幂等语义——降级不抛错，对齐本门面惯例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<RevealOutcome> RevealAsync(Card target, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit
            ? CovertRules.RevealAsync(unit, ct)
            : Task.FromResult(RevealOutcome.NoOp);
    }

    /// <summary>
    /// 移动（无头；zone＝<c>frontline</c>／<c>support</c>；不可用＝false）。
    /// S6/R16：效果移动**零资源消耗**——不扣指挥点、不动行动位、不发 <c>unit.acted</c>、不受压制阻断；
    /// 落地经「卡牌移动」动作的唯一处理端。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> MoveAsync(Card target, string zone, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        if (_commands() is not { } commands || target is not UnitCard unit)
        {
            return false;
        }

        return await commands.MoveUnitAsync(unit, zone, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 额外获得指挥点槽（E1-25；卡 → 玩家 → 对局资源管理器；不可达＝false）。
    /// 槽实际变化才经由资源管理器发 <c>slot.gained</c>→<c>slot.changed</c>。
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<bool> GainPointSlotsAsync(Card viewer, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (amount <= 0 || _resources() is not { } resources || OwnerOf(viewer) is not { } player)
        {
            return false;
        }

        return await resources.GainSlotsAsync(player, amount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 失去指挥点槽（E1-25；卡 → 玩家 → 对局资源管理器；不可达＝false）。
    /// 槽实际变化才经由资源管理器发 <c>slot.lost</c>→<c>slot.changed</c>。
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<bool> LosePointSlotsAsync(Card viewer, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (amount <= 0 || _resources() is not { } resources || OwnerOf(viewer) is not { } player)
        {
            return false;
        }

        return await resources.LoseSlotsAsync(player, amount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 额外获得指挥点（E1-25 后续；卡效果语义路——卡 → 玩家 → 对局资源管理器；不可达＝false）。
    /// 点数实际增加才经由资源管理器发 <c>point.gained</c>→<c>point.changed</c>。
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<bool> GainPointsAsync(Card viewer, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (amount <= 0 || _resources() is not { } resources || OwnerOf(viewer) is not { } player)
        {
            return false;
        }

        return await resources.GainPointsAsync(player, amount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 失去指挥点（E1-25 后续；卡效果语义路——卡 → 玩家 → 对局资源管理器；不可达＝false）。
    /// 点数实际减少才经由资源管理器发 <c>point.lost</c>→<c>point.changed</c>。
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<bool> LosePointsAsync(Card viewer, int amount, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (amount <= 0 || _resources() is not { } resources || OwnerOf(viewer) is not { } player)
        {
            return false;
        }

        return await resources.LosePointsAsync(player, amount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 效果选靶（**唯一入口**，R2／A-6）：把「取样语义」与「裁定语义」拆开——
    /// <list type="bullet">
    /// <item><b>确定性</b>（<c>all</c>／<c>self</c>）：候选集即答案，经判定器求值、不产生 targeter（无往返）。</item>
    /// <item><b>引擎随机</b>（<c>random</c>）：归对局随机服务（种子可复现）；生产对局禁止，
    ///   未开启 <see cref="MatchOptions.AllowRandomTargeting"/>＝**明确错误**（不静默降级为"取首个"，R2c）。</item>
    /// <item><b>外部裁定</b>（其余，如 <c>one</c>／<c>any</c>／<c>choose</c>）：**必须经提交通道**
    ///   （玩家侧 <c>TargeterManager</c> → 前端桥接）；引擎永不代选（R2）。无前端接入＝明确失败。</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 或 selector 为 null。</exception>
    /// <exception cref="InvalidOperationException">
    /// 需要外部裁定时没有前端接入（无桥接），或生产对局执行到 <c>random</c> 选择器。
    /// </exception>
    public async Task<IReadOnlyList<Card>> SelectAsync(Card viewer, EffectSelector selector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);

        if (_judicators() is not { } judicators)
        {
            return Array.Empty<Card>();
        }

        var cards = selector.Sel switch
        {
            "all" or "self" => ResolveDeterministic(judicators, viewer, selector),
            "random" => ResolveRandom(judicators, viewer, selector),
            _ => await SubmitAsync(viewer, selector, judicators, ct).ConfigureAwait(false),
        };

        // 指向：最终名单确定后对每个目标各触发一次（候选枚举不算、后续失败不回滚）。
        await NotifyTargetedAsync(viewer, cards, ct).ConfigureAwait(false);
        return cards;
    }

    /// <summary>确定性取样（候选集即答案；不产生 targeter）。</summary>
    private IReadOnlyList<Card> ResolveDeterministic(JudicatorRegistry judicators, Card viewer, EffectSelector selector)
        => InvokeResolveJudicator(judicators, viewer, selector);

    /// <summary>
    /// 引擎随机取样（R2c）：<c>random</c> 归对局随机服务；生产对局未开启＝明确错误（不取首个兜底）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未开启随机选择器。</exception>
    private IReadOnlyList<Card> ResolveRandom(JudicatorRegistry judicators, Card viewer, EffectSelector selector)
    {
        if (_allowRandom is not null && !_allowRandom())
        {
            throw new InvalidOperationException(
                $"效果选择器 sel=random 被拒绝：生产对局禁止随机取样（R2c）。"
                + $"如为测试／沙盒／简单 AI 场景，请在对局配置中显式开启 {nameof(MatchOptions)}.AllowRandomTargeting。");
        }

        var cards = InvokeResolveJudicator(judicators, viewer, selector);

        // R2c／R7：本对局用过的随机选择器登记（审查导出 randomSelectors 段的数据源）。
        _engine?.Invoke()?.RandomSelections.Add(new RandomSelectionRecord(viewer.Name, Describe(selector)));

        return cards;
    }

    /// <summary>选择器的可读描述（审查导出用；不参与求值）。</summary>
    private static string Describe(EffectSelector selector)
        => $"sel={selector.Sel}"
           + (selector.Side is null ? string.Empty : $",side={selector.Side}")
           + (selector.Zone is null ? string.Empty : $",zone={selector.Zone}")
           + (selector.UnitType is null ? string.Empty : $",unitType={selector.UnitType}")
           + (selector.Keyword is null ? string.Empty : $",keyword={selector.Keyword}")
           + (selector.Count is null ? string.Empty : $",count={selector.Count}");

    private IReadOnlyList<Card> InvokeResolveJudicator(
        JudicatorRegistry judicators, Card viewer, EffectSelector selector)
    {
        var result = judicators.Invoke(
            JudicatorNames.EffectTargetResolve, new object[] { viewer, selector }, OwnerOf(viewer)?.Index);
        return result is { Length: > 0 } && result[0] is IReadOnlyList<Card> resolved
            ? resolved
            : Array.Empty<Card>();
    }

    /// <summary>「指向」通知（效果选靶路径；对最终选定的每个目标各一次）。</summary>
    private async Task NotifyTargetedAsync(Card viewer, IReadOnlyList<Card> targets, CancellationToken ct)
    {
        if (_engine?.Invoke() is not { } engine || targets.Count == 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            await TargetingRules.NotifyAsync(
                engine, target, viewer, TargetingRules.KindEffect, triggerCard: viewer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 外部裁定（提交通道）：候选来自**目标候选名单判定器**（玩家域＝viewer 归属域）——
    /// "不可被敌方指令指向"等可选性声明经该判定器的包装器生效；一次调用＝一次 targeter 请求
    /// （经玩家侧 TargeterManager → 前端桥接；请求 id 与提交结果沿用既有协议形态，S12 加性口径不变）。
    /// </summary>
    /// <exception cref="InvalidOperationException">需要裁定但没有前端接入（无 TargeterManager／无桥接）。</exception>
    private async Task<IReadOnlyList<Card>> SubmitAsync(
        Card viewer, EffectSelector selector, JudicatorRegistry judicators, CancellationToken ct)
    {
        var targeterManager = OwnerOf(viewer)?.TargeterManager;
        if (targeterManager?.Bridge is null)
        {
            // R2：引擎永不代选——没有前端就不是"取首个"，而是明确失败（不静默降级）。
            throw new InvalidOperationException(
                $"效果选靶 sel={selector.Sel} 需要外部裁定（提交通道），但当前对局没有前端接入"
                + $"（{TargeterFailureReason.BridgeNotAssembled}）：引擎不代选（R2）。"
                + "请装配 targeter 桥接（人类 UI／MCP／AI 前端），或把选择器改为确定性（all/self）。");
        }

        var rule = new JudicatorSelectionRule(
            judicators.Resolve(JudicatorNames.TargetCandidateEligibility), viewer, selector);
        var candidates = rule.Candidates();
        if (candidates.Count == 0)
        {
            Console.WriteLine($"[target-select] 空集：候选为 0（selector={selector} viewer={viewer.Name}）");
            return Array.Empty<Card>();
        }

        // 数量：显式 Count 优先；否则按旧取样口径取 1（sel=any 与 one 同口径——裁定权在前端，数量是声明）。
        var take = selector.Count is > 0 ? selector.Count.Value : 1;
        take = Math.Min(take, candidates.Count);

        // 单次请求内用名单快照复核（避免逐候选重算）；候选本身也是快照（D15）。
        var validator = rule.Snapshot();
        Console.WriteLine($"[target-select] 发起选靶：candidates={candidates.Count} " +
                          $"selector={selector} viewer={viewer.Name}");
        Ref<Entity>? selected = null;
        var result = await targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                new ReferenceSetParameter(candidates, take, take, domainValidator: validator, tag: viewer)
                {
                    // S6：选靶归属＝观察者的归属玩家（与 TargeterManager 的取用同源）。
                    Owner = OwnerOf(viewer),
                    // R8：卡效果发起的选靶一律 effect（不按效果种类细分——那是效果侧语义，不是选靶动作语义）。
                    Action = SelectorActions.Effect,
                });

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
        }).ConfigureAwait(false);

        if (result.IsOk && selected is { IsAlive: true } reference && reference.Value is Card card)
        {
            return new[] { card };
        }

        if (result.Reason == TargeterFailureReason.BridgeNotAssembled)
        {
            // 前端在请求处理期间缺失（桥接被拆／未装配）＝明确失败，不当作"没有目标"。
            throw new InvalidOperationException(
                $"效果选靶 sel={selector.Sel} 失败：{TargeterFailureReason.BridgeNotAssembled}（引擎不代选，R2）。");
        }

        return Array.Empty<Card>();
    }

    private static Player? OwnerOf(Card card) => card switch
    {
        Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };

    /// <summary>按**卡名**在卡池反查定义 id（首个同名；未命中＝null）。</summary>
    private static string? ResolveDefinitionIdByName(MatchCardService service, string cardName)
    {
        foreach (var pair in service.RegisteredDefinitions)
        {
            if (string.Equals(pair.Value.Name, cardName, StringComparison.Ordinal))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// **声明光环**（E1-56；持续态的正确机制）：把"某组卡**持续**获得 ±N 字段"登记为场级光环声明——
    /// 受益谓词由本门面按 <see cref="EffectAuraFilter"/> 构造（**不进 csx**），来源＝本门面实例
    /// （随效果卸载整组撤销）；登记后经 <c>RerunAllCardsAsync</c> 衔接一轮使收益现算生效。
    /// <para>与 <see cref="BuffAsync"/> 的分工：**静态/持续**文本（无触发）⇒ 光环（受益随进出/位置**实时重算**）；
    /// **触发体内**一次性动作 ⇒ 修饰器（尤其带期限）。</para>
    /// </summary>
    /// <param name="host">光环宿主（源卡；其「在场」为通用门禁）。</param>
    /// <param name="field">目标字段（`attack`／`defense`／`opCost`／`deployCost`）。</param>
    /// <param name="delta">增量（可负）。</param>
    /// <param name="filter">受益谓词描述。</param>
    /// <exception cref="ArgumentNullException">host 或 filter 为 null。</exception>
    public async Task<bool> DeclareAuraAsync(
        Card host, string field, int delta, EffectAuraFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(filter);
        if (delta == 0
            || StatFieldOf(field) is not { } statField
            || GameEnvironment.ResolveFor(host) is not { } environment)
        {
            return false;
        }

        environment.Auras.Register(AuraDeclaration.Add(
            host, statField, delta, this, (env, beneficiary) => MatchesAura(env, host, beneficiary, filter)));
        await environment.RerunAllCardsAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// **数值比较条件求值**（E1-57；**纯函数**——可安全参与 `&amp;&amp;` 合取，无需 `await`）。
    /// <para><paramref name="spec"/> 形如 `左度量:算子:右操作数`：度量＝`count=s=&lt;side&gt;`（单位数）／
    /// `points=s=friendly`（剩余指挥点数）／`stat=f=&lt;field&gt;;s=friendly;z=hq`（总部属性）；
    /// 算子＝`gte`／`lte`／`gt`／`lt`／`eq`；右操作数＝`#&lt;整数&gt;` 或**同类度量**（`count=s=enemy`）。</para>
    /// <para>不合法/不可解析/视角缺归属 ⇒ **false**（不抛错——与"占位条件"同观感但**不静默执行**）。</para>
    /// </summary>
    public static bool EvaluateCondition(Card? viewer, string spec)
    {
        if (viewer is null || string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var parts = spec.Split(':');
        if (parts.Length != 3
            || GameEnvironment.ResolveFor(viewer) is not { } environment
            || Measure(environment, viewer, parts[0]) is not { } left
            || RightOperand(environment, viewer, parts[2]) is not { } right)
        {
            return false;
        }

        return parts[1] switch
        {
            "gte" => left >= right,
            "lte" => left <= right,
            "gt" => left > right,
            "lt" => left < right,
            "eq" => left == right,
            _ => false,
        };
    }

    /// <summary>右操作数（`#n` 或度量引用）。</summary>
    private static int? RightOperand(GameEnvironment environment, Card viewer, string spec) =>
        spec.StartsWith('#') && int.TryParse(spec[1..], out var literal)
            ? literal
            : Measure(environment, viewer, spec);

    /// <summary>度量求值（`count:`／`points:`／`stat:`；未知＝null）。</summary>
    private static int? Measure(GameEnvironment environment, Card viewer, string spec)
    {
        var owner = OwnerOf(viewer);
        if (owner is null)
        {
            return null;
        }

        if (spec.StartsWith("count=", StringComparison.Ordinal))
        {
            var side = Attribute(spec, "s");
            var total = 0;
            foreach (var unit in EnumerateUnits(environment))
            {
                var unitOwner = OwnerOf(unit);
                var matches = side switch
                {
                    "enemy" => unitOwner is not null && !ReferenceEquals(unitOwner, owner),
                    "both" or null => true,
                    _ => ReferenceEquals(unitOwner, owner),
                };
                if (matches)
                {
                    total++;
                }
            }

            return total;
        }

        if (spec.StartsWith("points=", StringComparison.Ordinal))
        {
            // 视角玩家的**剩余指挥点数**（`s=enemy` 需对手对象——本轮不接，返回 null ⇒ 条件为假）。
            return Attribute(spec, "s") is "enemy" ? null : owner.Points;
        }

        if (spec.StartsWith("stat=", StringComparison.Ordinal))
        {
            // 仅支持"己方总部属性"（`z=hq`）——`敌方总部` 需对手对象（登记为待接）。
            if (Attribute(spec, "s") is "enemy" || Attribute(spec, "z") != "hq")
            {
                return null;
            }

            // `总部防御力`＝**HQ 生命值**（总部无防御修饰字段）；其余字段未接 ⇒ null。
            return Attribute(spec, "f") is "defense" ? owner.Hq.Health : null;
        }

        return null;
    }

    /// <summary>`key=value;key=value` 规范串取值。</summary>
    private static string? Attribute(string spec, string key)
    {
        var body = spec[(spec.IndexOf('=', StringComparison.Ordinal) + 1)..];
        foreach (var pair in body.Split(';'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(pair[..separator], key, StringComparison.Ordinal))
            {
                return pair[(separator + 1)..];
            }
        }

        return null;
    }

    /// <summary>场上单位枚举（双方支援线 ＋ 前线）。</summary>
    private static IEnumerable<UnitCard> EnumerateUnits(GameEnvironment environment)
    {
        var battlefield = environment.Battlefield;
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.PlayerBSupportLine, battlefield.FrontLine })
        {
            foreach (var slot in line.Members)
            {
                if (slot is UnitCard unit)
                {
                    yield return unit;
                }
            }
        }
    }

    /// <summary>DSL 字段名 → 机制字段标识（白名单；未知＝null）。</summary>
    private static string? StatFieldOf(string field) => field switch
    {
        "attack" => CardStatFields.Attack,
        "defense" => CardStatFields.Defense,
        "opCost" => CardStatFields.OperateCost,
        "deployCost" => CardStatFields.DeployCost,
        _ => null,
    };

    /// <summary>
    /// 光环受益谓词（E1-56）：按 <see cref="EffectAuraFilter"/> 逐项判定
    /// （自身排除 → 阵营面 → 阵营 → 兵种 → 词条 → 相邻 → 区域）。
    /// </summary>
    private static bool MatchesAura(
        GameEnvironment environment, Card host, Card beneficiary, EffectAuraFilter filter)
    {
        if (filter.ExcludeSelf && ReferenceEquals(host, beneficiary))
        {
            return false;
        }

        if (!MatchesAuraSide(host, beneficiary, filter.Side))
        {
            return false;
        }

        if (filter.Faction is { } faction
            && (beneficiary is not CardBase factionCard
                || !Faction.TryNormalize(faction, out var parsedFaction)
                || !string.Equals(factionCard.Definition.Faction, parsedFaction, StringComparison.Ordinal)))
        {
            return false;
        }

        if (filter.UnitType is { } unitType
            && (beneficiary is not UnitCard unit
                || !UnitType.TryNormalize(unitType, out var parsedType)
                || !unit.GetData<UnitStateData>().UnitTypes.Contains(parsedType)))
        {
            return false;
        }

        if (filter.Keyword is { } keyword
            && (beneficiary is not CardBase keywordCard || !keywordCard.Keywords.Has(keyword)))
        {
            return false;
        }

        return filter.Zone switch
        {
            "frontline" => environment.IsOnFrontLine(beneficiary),
            "support" => environment.GetLineOf(beneficiary) is not null && !environment.IsOnFrontLine(beneficiary),
            _ => true,
        };
    }

    /// <summary>光环的**阵营面**判定（friendly/enemy/both）。</summary>
    private static bool MatchesAuraSide(Card host, Card beneficiary, string? side)
    {
        if (side is null or "both" or "any")
        {
            return true;
        }

        var hostOwner = OwnerOf(host);
        var beneficiaryOwner = OwnerOf(beneficiary);
        if (hostOwner is null)
        {
            return false;
        }

        return side switch
        {
            "friendly" => ReferenceEquals(hostOwner, beneficiaryOwner),
            "enemy" => beneficiaryOwner is not null && !ReferenceEquals(hostOwner, beneficiaryOwner),
            _ => true,
        };
    }

}
