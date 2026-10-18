using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
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
/// </summary>
public sealed partial class EffectRuntime
{
    private readonly Func<CommandManager?> _commands;
    private readonly Func<PlayerManager?> _players;
    private readonly Func<JudicatorRegistry?> _judicators;
    private readonly Func<ResourceManager?> _resources;
    private readonly Func<Orc.Game.Board.Battlefield?>? _battlefield;

    /// <summary>创建运行时门面（协作者以延迟读取注入——装配链时序无关）。</summary>
    /// <param name="commands">指挥管理器访问器。</param>
    /// <param name="players">玩家管理器访问器。</param>
    /// <param name="judicators">判定器注册表访问器。</param>
    /// <param name="resources">资源管理器访问器。</param>
    /// <param name="engine">对局引擎访问器（批 4 加性；动态效果受控面用——实例化/预制体库；缺省 null＝动态效果面按「服务不可用」降级）。</param>
    /// <param name="isActionAllowed">可操作相位门禁访问器（批 4 加性；终局/非进行相位的受控面拒斥——缺省 null＝无门禁）。</param>
    /// <exception cref="ArgumentNullException">任一必填访问器为 null。</exception>
    public EffectRuntime(
        Func<CommandManager?> commands,
        Func<PlayerManager?> players,
        Func<JudicatorRegistry?> judicators,
        Func<ResourceManager?> resources,
        Func<LogicEngine?>? engine = null,
        Func<bool>? isActionAllowed = null,
        Func<Orc.Game.Board.Battlefield?>? battlefield = null)
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
            foreach (var slot in line)
            {
                if (slot.Occupant is Orc.Game.Cards.CardBase unit && ReferenceEquals(unit.Owner, owner))
                {
                    units.Add(unit);
                }
            }
        }

        return units;
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

    /// <summary>造成伤害（游戏语义：单位扣防御、总部扣血）。</summary>
    /// <param name="source">施动方（E1-50；提供时归属：发 <c>unit.damage.dealt</c>、致死时作 <c>Killer</c>）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> DamageAsync(Card target, int amount, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dealer = source as UnitCard;
        switch (target)
        {
            case UnitCard unit when dealer is not null && _commands() is { } commands:
                // E1-50：有来源 ⇒ 走指挥管理器的受控面（**伤害来源游标** ＋ unit.damage.dealt 归属）
                await commands.DealDamageAsync(unit, amount, dealer, ct).ConfigureAwait(false);
                return true;
            case UnitCard unit:
                await unit.ApplyDefenseDamageAsync(amount, ct).ConfigureAwait(false);
                return true;
            case Hq hq when dealer is not null && _commands() is { } commands:
                await commands.DealDamageToHqAsync(hq, amount, dealer, ct).ConfigureAwait(false);
                return true;
            case Hq hq:
                await hq.ApplyDamageAsync(amount, ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>属性修饰（攻击力/防御力增减；来源＝本门面实例——撤销请经 <c>RemoveBySourceAsync</c>）。</summary>
    /// <param name="duration">期限（E1-41；<see cref="EffectDuration.Permanent"/>＝随效果存续）。</param>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> BuffAsync(
        Card target, int attack, int defense,
        EffectDuration duration = EffectDuration.Permanent, CancellationToken ct = default)
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
    public Task<bool> GrantAsync(Card target, string keyword, CancellationToken ct = default)
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
    public Task<bool> GrantAsync(Card target, string keyword, int? value, CancellationToken ct = default)
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
    public Task<bool> RevokeAsync(Card target, string keyword, CancellationToken ct = default)
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
    public Task<bool> SetKeywordValueAsync(Card target, string keyword, int? value, CancellationToken ct = default)
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

    /// <summary>
    /// 内容型词条授予（词条效果化·批 0）：词条面统一判定（CardBase＋Hq 同族）；沿用既有内容签名
    /// （<c>keyword ＋ 内容对象</c>——如「获得亡计」的 <see cref="Effect"/> 载荷）；内容生命周期随词条组件生灭
    /// （授予＝内容装载；撤销/死亡注销＝内容卸载；授予失败回滚＝无残留）；空内容（null）＝合法静默；
    /// 幂等/终态/异常语义完全沿用既有标识授予。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> GrantWithContentAsync(Card target, string keyword, Effect? content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        return KeywordRules.TryGetKeywordManager(target) is { } manager
            ? manager.GrantWithContentAsync(keyword, content)
            : Task.FromResult(false);
    }

    /// <summary>抽牌（走玩家管理器——含满手爆牌裁决）；返回请求的抽牌次数（服务不可达＝0）。</summary>
    /// <exception cref="ArgumentNullException">viewer 为 null。</exception>
    public async Task<int> DrawAsync(Card viewer, int count, CancellationToken ct = default)
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
    public async Task<DeckFetchResult> FetchFromDeckAsync(Card target, CancellationToken ct = default)
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
    public async Task<int> AddToHandAsync(Card viewer, string cardName, int count, CancellationToken ct = default)
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
    public async Task<int> ShuffleInAsync(Card viewer, string cardName, int count, CancellationToken ct = default)
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
    public async Task<bool> PinAsync(Card target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit && await SuppressRules.ApplyAsync(unit).ConfigureAwait(false);
    }

    /// <summary>抑制（游戏层抑制服务——清空处置全链；资格拒绝/非单位＝false）。</summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public Task<bool> SilenceAsync(Card target, CancellationToken ct = default)
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
    public Task<VeteranPromotionResult> UpgradeAsync(Card target, CancellationToken ct = default)
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
    public Task<RevealOutcome> RevealAsync(Card target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target is UnitCard unit
            ? CovertRules.RevealAsync(unit, ct)
            : Task.FromResult(RevealOutcome.NoOp);
    }

    /// <summary>移动（无头；zone＝<c>frontline</c>／<c>support</c>；不可用＝false）。</summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    public async Task<bool> MoveAsync(Card target, string zone, CancellationToken ct = default)
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
    public async Task<bool> GainPointSlotsAsync(Card viewer, int amount, CancellationToken ct = default)
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
    public async Task<bool> LosePointSlotsAsync(Card viewer, int amount, CancellationToken ct = default)
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
    public async Task<bool> GainPointsAsync(Card viewer, int amount, CancellationToken ct = default)
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
    public async Task<bool> LosePointsAsync(Card viewer, int amount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (amount <= 0 || _resources() is not { } resources || OwnerOf(viewer) is not { } player)
        {
            return false;
        }

        return await resources.LosePointsAsync(player, amount, ct).ConfigureAwait(false);
    }

    /// <summary>无头选靶（经游戏层判定器 <see cref="JudicatorNames.EffectTargetResolve"/> 求值；不可用＝空集）。</summary>
    /// <exception cref="ArgumentNullException">viewer 或 selector 为 null。</exception>
    public Task<IReadOnlyList<Card>> SelectAsync(Card viewer, EffectSelector selector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);

        if (_judicators() is not { } judicators)
        {
            return Task.FromResult<IReadOnlyList<Card>>(Array.Empty<Card>());
        }

        var result = judicators.Invoke(
            JudicatorNames.EffectTargetResolve, new object[] { viewer, selector }, OwnerOf(viewer)?.Index);
        return Task.FromResult(
            result is { Length: > 0 } && result[0] is IReadOnlyList<Card> cards
                ? cards
                : (IReadOnlyList<Card>)Array.Empty<Card>());
    }

    /// <summary>
    /// 交互式选靶（首选单目标）：候选来自**目标候选名单判定器**（玩家域＝viewer 归属域）——
    /// "不可被敌方指令指向"等可选性声明经该判定器的包装器生效；取消/失败/无候选＝空集。
    /// 一次调用＝一次 targeter 请求（经玩家侧 TargeterManager → 前端桥接）。
    /// </summary>
    /// <exception cref="ArgumentNullException">viewer 或 selector 为 null。</exception>
    public async Task<IReadOnlyList<Card>> BeginTargetSelectionAsync(
        Card viewer,
        EffectSelector selector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(selector);

        if (_judicators() is not { } judicators)
        {
            return Array.Empty<Card>();
        }

        var targeterManager = OwnerOf(viewer)?.TargeterManager;
        if (targeterManager is null)
        {
            return Array.Empty<Card>();
        }

        var rule = new JudicatorSelectionRule(
            judicators.Resolve(JudicatorNames.TargetCandidateEligibility), viewer, selector);
        var candidates = rule.Candidates();
        if (candidates.Count == 0)
        {
            return Array.Empty<Card>();
        }

        // 单次请求内用名单快照复核（避免逐候选重算）；候选本身也是快照（D15）。
        var validator = rule.Snapshot();
        Ref<Entity>? selected = null;
        var result = await targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.FieldUnit,
                new ReferenceSetParameter(candidates, 1, 1, domainValidator: validator, tag: viewer));

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

        return result.IsOk && selected is { IsAlive: true } reference && reference.Value is Card card
            ? new[] { card }
            : Array.Empty<Card>();
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
            foreach (var slot in line)
            {
                if (slot.Occupant is UnitCard unit)
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
