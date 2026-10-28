using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Model;

namespace Orc.Game.Players;

/// <summary>
/// 总部实体（Hq；W3-3 G11 实体化——「非卡实体」）：
/// Entity 身份（<see cref="Orc.Core.Entity.Ref"/> 目标承载）＋组件容器（<see cref="Orc.Cards.Card"/> 薄容器面——
/// AddData/GetData）＋词条/效果装载面（AddEffect/Effects/MountPassiveEffects）；不入死亡/销毁链
/// （不发 card.died / card.destroyed、不置已毁、不清槽位、归零不注销效果/修饰；「两条链均不接」）。
/// 归属：随 Player 创建并互持（<see cref="Player.Hq"/>——「未绑定」在结构上不可达）；占位（支援线槽 2，居中）
/// 由对局/战场装配完成（布局语义——邻位/守护/轰炸机拦截基准；槽位关系不出现在目标承载面）。
/// 数值模型（单一当前值；「防御力」＝血量域）：本体＝<see cref="HqStateData.Health"/>（受控写入），
/// 有效值＝修饰机制链输出（缓存；「获得 +X 防御力」＝挂 +X 修饰）；数值改变一律走「通用数据改变管线」：
/// 伤害性扣减经 <see cref="ApplyDamageAsync"/>（改写段→致命介入段→应用→跑链→集中触发），
/// 修饰增删经 <see cref="Modifiers"/>（同一管线）；「HQ 获得防御力时」可经 card.stat.changed 线监听
/// （变化字段含 <see cref="CardStatFields.HqHealth"/>）。
/// 终局响应＝数值路径下游统一判定（归零检查响应 <see cref="RespondToZero"/>——经装配期注入的对局服务执行
/// 〔延迟读取〕；脱局＝防御降级〔读面可用、引擎相关能力静默跳过，不发射、不抛错〕）。
/// 管线挂载面（改写/介入）：<see cref="AddDamageRewriter"/> / <see cref="AddLethalIntervention"/>
/// （「受伤 -1」与「致命前 +6 防」在此挂；可挂可卸、按来源撤销 <see cref="RemovePipelineHooksBySource"/>）。
/// </summary>
public sealed class Hq : Card
{
    private readonly List<HqDamageRewriter> _damageRewriters = new();
    private readonly List<HqLethalIntervention> _lethalInterventions = new();
    private Func<Player?>? _opponentProvider;
    private Func<MatchLifecycle?>? _lifecycleProvider;
    private readonly LogicEngine _damageEngine; // E1-33：受伤害信号发射用（构造期绑定）

    /// <summary>创建总部实体（随 Player 创建；对局装配路径——引擎绑定于构造期）。</summary>
    internal Hq(Player owner, LogicEngine engine)
        : base(engine, NameOf(owner))
    {
        Owner = owner;
        _damageEngine = engine;

        // 组件容器：HQ 状态数据（血量本体＋占位槽）＋修饰机制容器（链/检测/集中触发）＋HQ 血量检测组件。
        AddData(new HqStateData());
        Modifiers = new CardModifierComponent(this, engine);
        Modifiers.RegisterDetector(new HqHealthUpdateDetector(this));

        // A2 加性：HQ 词条面（同族承载——「友方总部具有免疫」类文本的授予/查询基座；
        // 宿主＝引擎薄容器 Card，与卡牌侧同一管理组件与同一挂载/卸载机制；空管理面零行为负担）。
        Keywords = new KeywordManager(this, engine);

        // 通用接入点：指向触发器（与单位卡同一承载；HQ 不继承 CardBase，故自带具名触发面）。
        // S8（批次 3）：成员表由类型预制体 `card.hq` 声明——本类不自建执行体、不再手写显示名登记（批次 7 起只持成员地址）。
        MemberTable = Orc.Game.Model.CardTypeBinder.Bind(
            this,
            HostKind.Hq,
            engine,
            definition: null,
            validationJudicatorResolver: null);
        TargetingTrigger = MemberTable.AddressOf(CardProcesses.Targeting);
    }

    /// <summary>HQ 的成员表（S8：由类型预制体 <c>card.hq</c> 声明；与卡牌类同一绑定面）。</summary>
    public CardMemberTable MemberTable { get; }

    /// <summary>指向成员地址（<c>host#targeting</c>；「某方指向本总部」的最终确定面）。</summary>
    public MemberAddress TargetingTrigger { get; }

    /// <summary>
    /// 发动指向通知（由 <see cref="TargetingRules.NotifyAsync"/> 调用）。
    /// <para>
    /// **HQ 没有编排实体**（它是玩家侧的薄容器，不参与卡牌装载链），因此没有成员边可跑；
    /// 旧实现也只在这里跑一条"只暴露动作缝、无业务逻辑"的 handler，而该缝的包装器由效果注入挂载——
    /// 效果家族已删除后缝上恒无包装器。本方法因此只保留**调用点语义**（谁被指向、什么来源），
    /// 不再伪装成一次成员执行。若将来 HQ 需要被订阅，给它建实体即可（地址已在
    /// <see cref="TargetingTrigger"/> 上就位）。
    /// </para>
    /// </summary>
    internal Task InvokeTargetingTriggerAsync(Card? source, string kind, Card? triggerCard, CancellationToken ct)
    {
        _ = source;
        _ = kind;
        _ = triggerCard;
        _ = ct;
        return Task.CompletedTask;
    }

    /// <summary>归属玩家（HQ 随 Player 创建——恒非空）。</summary>
    public Player Owner { get; }

    /// <summary>卡侧修饰器组件（W2a G3 机制；W3-3 起 HQ 亦持有——「链/检测/集中触发」的挂载与读取面）。</summary>
    public CardModifierComponent Modifiers { get; }

    /// <summary>
    /// 词条管理组件（A2 加性：HQ 词条面——单位与 HQ 同族承载「免疫」等词条；宿主＝引擎薄容器 Card）。
    /// 运行时授予/移除/查询经本面（如「友方总部具有免疫」＝授予「免疫」）；装载上下文提供器经装配期注入
    /// （延迟读取——HQ 免疫的归零改写器挂载不经上下文，缺省亦可用；注入保持与卡牌侧一致的可扩展性）。
    /// </summary>
    public KeywordManager Keywords { get; }

    /// <summary>有效血量（读面；跑链落定后的缓存——「始终读有效值」；未跑过链＝本体值）。</summary>
    public int Health => Modifiers.GetEffectiveValue(CardStatFields.HqHealth);

    /// <summary>所在阵线（布局语义——邻位/守护/轰炸机拦截基准；入线由对局/战场装配完成；未入线＝null）。</summary>
    public BattleLine? Line => GetData<HqStateData>().Line;

    /// <summary>装配入线（对局/战场装配调用——布局语义；记录所在线；不改线内容，入列由装配方完成）。</summary>
    internal void AttachToLine(BattleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        GetData<HqStateData>().Line = line;
    }

    // ---------- 管线挂载面（改写 / 介入；可挂可卸、按来源撤销） ----------

    /// <summary>伤害改写挂钩集（挂载序——改写段应用序；只读枚举）。</summary>
    public IReadOnlyList<HqDamageRewriter> DamageRewriters => _damageRewriters;

    /// <summary>致命介入挂钩集（挂载序——介入段触发序；只读枚举）。</summary>
    public IReadOnlyList<HqLethalIntervention> LethalInterventions => _lethalInterventions;

    /// <summary>挂载伤害改写挂钩（幂等：已挂载同一实例＝无操作）。</summary>
    /// <exception cref="ArgumentNullException">rewriter 为 null。</exception>
    public void AddDamageRewriter(HqDamageRewriter rewriter)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        if (_damageRewriters.Contains(rewriter))
        {
            return; // 幂等：已挂载同一实例（不重复贡献）
        }

        _damageRewriters.Add(rewriter);
    }

    /// <summary>卸载伤害改写挂钩（幂等：未挂载＝false 无操作、不抛错）。</summary>
    /// <exception cref="ArgumentNullException">rewriter 为 null。</exception>
    public bool RemoveDamageRewriter(HqDamageRewriter rewriter)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        return _damageRewriters.Remove(rewriter);
    }

    /// <summary>挂载致命介入挂钩（幂等：已挂载同一实例＝无操作）。</summary>
    /// <exception cref="ArgumentNullException">intervention 为 null。</exception>
    public void AddLethalIntervention(HqLethalIntervention intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        if (_lethalInterventions.Contains(intervention))
        {
            return; // 幂等：已挂载同一实例（不重复贡献）
        }

        _lethalInterventions.Add(intervention);
    }

    /// <summary>卸载致命介入挂钩（幂等：未挂载＝false 无操作、不抛错）。</summary>
    /// <exception cref="ArgumentNullException">intervention 为 null。</exception>
    public bool RemoveLethalIntervention(HqLethalIntervention intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        return _lethalInterventions.Remove(intervention);
    }

    /// <summary>
    /// 按来源撤销管线挂载物（改写＋介入；统一机制——与修饰器按来源撤销对称，效果托管撤销适用）：
    /// 撤销该来源在本 HQ 上的全部挂钩条目（相等性判据＝引用相等）；返回撤销条目数（无命中＝0、幂等）。
    /// </summary>
    /// <exception cref="ArgumentNullException">source 为 null。</exception>
    public int RemovePipelineHooksBySource(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var removed = _damageRewriters.RemoveAll(r => ReferenceEquals(r.Source, source));
        removed += _lethalInterventions.RemoveAll(i => ReferenceEquals(i.Source, source));
        return removed;
    }

    // ---------- 数值门户（伤害性扣减） ----------

    /// <summary>
    /// 数值门户：伤害性扣减（HQ 数值路径入口——「受伤」的受控变更；「不建专用 HQ 伤害流程」＝
    /// 攻击伤害同经本路径，不再有内联特例）：
    /// ①改写段——伤害改写挂钩按挂载序改写扣减量（「受伤 -1」在此挂）；仅作用于伤害性扣减
    ///   （不影响加血、不影响非伤害变化）；改写后 ≤0 钳制为 0，终量为 0＝无变化
    ///   （不应用、零发射、不判定——P1「改变才传播」）。
    /// ②介入段——「将致命」预判（当前有效值 − 扣减量 ≤ 0）时逐介入挂钩触发一轮
    ///   （单次结算一轮、不递归——介入后仍将致命亦不再介入；「致命前 +6 防」在此挂）；介入量并入应用算式
    ///   （走 HQ 数值路径、参与统一判定）。
    /// ③应用——本体受控写入（不钳制——数值表现钳制在快照层；≤0 由归零响应收敛）。
    /// ④数值落定——跑链（修饰机制管线：链/检测/集中触发；有变更才发 card.stat.changed）。
    /// （终局判定不在本方法内联——归零统一响应 <see cref="RespondToZero"/> 在数值路径下游承接。）
    /// </summary>
    /// <param name="amount">伤害性扣减量（≥0；0＝合法——零发射）。</param>
    /// <exception cref="ArgumentOutOfRangeException">amount 为负。</exception>
    /// <remarks>R11 起为 <c>internal</c> 受控面：唯一调用方＝伤害处理端（<c>MatchActionServices.ApplyDamageAsync</c>）。</remarks>
    internal async Task ApplyDamageAsync(int amount, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var state = GetData<HqStateData>();

        // ① 改写段（挂载序；改写后 ≤0 钳制为 0——「受伤 -1」不产生治疗、不允许负值）
        var rewritten = amount;
        foreach (var rewriter in _damageRewriters.ToArray())
        {
            rewritten = rewriter.Rewrite(rewritten);
        }

        rewritten = Math.Max(0, rewritten);
        if (rewritten == 0)
        {
            return; // 无变化：不应用、零发射、不判定
        }

        // ② 介入段（「将致命」预判：本次数值变化将致 HQ ≤0；单次结算一轮、不递归——不重新预判/不循环）
        var adjustment = 0;
        if (Health - rewritten <= 0)
        {
            foreach (var intervention in _lethalInterventions.ToArray())
            {
                adjustment += intervention.Adjust(this);
            }
        }

        // ③ 应用（本体受控写入；不钳制——数值表现钳制在快照层〔「表现面不出现负数」，与单位侧损伤量模型同构：
        //   修饰贡献与伤害差额不被吞掉；≤0 由归零响应收敛〕）
        var healthBefore = Health; // E1-33：受伤害信号按「改变才传播」
        state.Health += adjustment - rewritten;

        // ④ 数值落定（跑链 → 检测 → 集中触发——有变更才发；无变化＝整体不动、零发射）
        await Modifiers.RequestRerunAsync(ct);

        if (Health != healthBefore)
        {
            await GameUpdates.EmitCardDamaged(_damageEngine, this, rewritten, ct); // E1-33：HQ 侧受伤害（改写后实际扣减）
        }
    }

    // ---------- 终局响应（装配期注入；延迟读取） ----------

    /// <summary>
    /// 装配期注入（延迟读取）：终局响应所需对局服务——对手提供器（胜者＝HQ 归零方之对手）＋
    /// 对局生命周期提供器（终局记录）。由指挥管理器在对局装配期注入；
    /// 脱局场景（未注入）＝防御降级（归零不记录、不抛错——「对局能力以引擎/对局装配为前提」）。
    /// </summary>
    internal void ConfigureTerminalResponse(Func<Player?> opponentProvider, Func<MatchLifecycle?> lifecycleProvider)
    {
        ArgumentNullException.ThrowIfNull(opponentProvider);
        ArgumentNullException.ThrowIfNull(lifecycleProvider);
        _opponentProvider = opponentProvider;
        _lifecycleProvider = lifecycleProvider;
    }

    /// <summary>
    /// 归零统一响应（HQ 数值路径下游——「终局判定迁至 HQ」的落点）：有效值 ≤0 时执行终局记录
    /// （状态置结束＋胜者＝HQ 归零方之对手）；任何来源（攻击伤害/效果/修饰撤销等）使 HQ 归零均经此响应
    /// （数值变化统一判定的 HQ 侧承接；不内联于攻击流程）。有效值 &gt;0＝无操作；
    /// 对局侧重复调用幂等（生命周期 End 自身幂等）。
    /// </summary>
    internal void RespondToZero()
    {
        if (Health > 0)
        {
            return;
        }

        var lifecycle = _lifecycleProvider?.Invoke();
        var winner = _opponentProvider?.Invoke();
        if (lifecycle is null || winner is null)
        {
            return; // 防御降级：脱局（无对局服务）＝静默跳过（不发射、不抛错）
        }

        lifecycle.End(winner, MatchEndReason.HqZero);
    }

    private static string NameOf(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.Index == 0 ? "玩家A总部" : "玩家B总部";
    }
}

/// <summary>
/// 伤害改写挂钩（W3-3 G11；管线改写段挂载物——「受伤 -1」类效果的组合表达）：
/// 对「伤害性扣减量」的改写（纯变换——使用处注入逻辑；仅作用于伤害、不影响加血/非伤害变化）；
/// 挂载序＝改写应用序；改写后 ≤0 由管线统一钳制为 0。
/// 来源标记＝任意引用对象（相等性判据＝引用相等；按来源撤销经 <see cref="Hq.RemovePipelineHooksBySource"/>）。
/// </summary>
public sealed class HqDamageRewriter
{
    private readonly Func<int, int> _rewrite;

    /// <summary>创建伤害改写挂钩。</summary>
    /// <param name="source">来源标记（施加方标识；任意引用对象，非 null）。</param>
    /// <param name="rewrite">改写回调（参数＝扣减量；返回改写后量；须为纯变换——不得产生副作用）。</param>
    /// <exception cref="ArgumentNullException">source / rewrite 为 null。</exception>
    public HqDamageRewriter(object source, Func<int, int> rewrite)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rewrite);
        Source = source;
        _rewrite = rewrite;
    }

    /// <summary>来源标记（施加方标识；相等性判据＝引用相等）。</summary>
    public object Source { get; }

    /// <summary>改写执行（管线改写段调用；机制内部）。</summary>
    internal int Rewrite(int amount) => _rewrite(amount);
}

/// <summary>
/// 致命介入挂钩（W3-3 G11；管线介入段挂载物——「致命前 +6 防」类效果的组合表达）：
/// 「将致命」时被调用（单次结算内一轮、不递归）；用法＝返回对当前值的调整量（如 +6）；
/// 调整量并入应用算式（走 HQ 数值路径、参与统一判定）。
/// 来源标记＝任意引用对象（相等性判据＝引用相等；按来源撤销经 <see cref="Hq.RemovePipelineHooksBySource"/>）。
/// </summary>
public sealed class HqLethalIntervention
{
    private readonly Func<Hq, int> _adjust;

    /// <summary>创建致命介入挂钩。</summary>
    /// <param name="source">来源标记（施加方标识；任意引用对象，非 null）。</param>
    /// <param name="adjust">介入回调（参数＝HQ 引用；返回当前值调整量；须为纯计算——不得产生副作用）。</param>
    /// <exception cref="ArgumentNullException">source / adjust 为 null。</exception>
    public HqLethalIntervention(object source, Func<Hq, int> adjust)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adjust);
        Source = source;
        _adjust = adjust;
    }

    /// <summary>来源标记（施加方标识；相等性判据＝引用相等）。</summary>
    public object Source { get; }

    /// <summary>介入执行（管线介入段调用；机制内部）。</summary>
    internal int Adjust(Hq hq) => _adjust(hq);
}
