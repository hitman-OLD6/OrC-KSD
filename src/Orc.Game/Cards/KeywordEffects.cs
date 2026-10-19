using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Commanding;
using Orc.Game.Damage;
using Orc.Game.Players;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 批 1 词条效果化（A 档试点）：「壳＋效果行为」模式的词条效果制品集（动员打样／压制、情报推广）。
// 模式（口径依据《词条效果化·批 1 需求文档》与本批实现记录「模式设计点」）：
// ①词条组件收薄为壳（标识／参值／授予-移除／读取面）；行为（触发订阅／时序决策／效果语义）由本文件
//   的效果承载——效果＝效果体系正式对象（PassiveEffect 子类），构造期经组件 EmbedEffect 注入内嵌效果通道；
//   装载／卸载／死亡注销／回滚／复装随词条生灭（内嵌效果通道既有语义；亡计先例）。
// ②信号订阅（订阅建立/撤销随效果生命周期）：效果在 OnMount 经宿主卡的**词条装载上下文提供器**
//   （KeywordManager.LoadContextProvider——与词条组件运行逻辑同一上下文来源）解析对局引擎并建立订阅；
//   在 OnUnmount 退订（订阅挂点＝装载/卸载回调）。独立构造（脱离对局）＝上下文不可达＝不订阅
//   （功能不可用、加载不失败——沿既有语境判定口径）。
// ③参值一律**运行期**经词条读取面读取（KeywordRules.GetKeywordValue——不构造期捕获；参值改写后行为随动）。
// ④行为等价：语义与收薄前的组件实现逐一对应（含过滤/时序/幂等）；差异清单见批 1 实现记录与汇报。
//
// 批 2 词条效果化（B 档扩展）：伤害改写族（伏击／重甲／免疫）的效果制品集。模式（延续批 1）：
// ①词条组件收薄为壳（标识／参值〔重甲〕／授予-移除／读取面）；行为（伤害改写 handler 的注册/撤销与
//   改写判定）由本文件批 2 区域效果承载——构造期经组件 EmbedEffect 注入内嵌效果通道；装载/卸载/
//   死亡注销/回滚/复装随词条生灭（内嵌效果通道既有语义）。
// ②注册（订阅）建立于 OnMount、撤销于 OnUnmount：上下文解析＝宿主卡 → 词条管理面 →
//   词条装载上下文提供器（与词条组件运行逻辑同一上下文来源）→ 「造成攻击伤害」触发器注册口
//   （AttackDamageTrigger——Register/Unregister）。
// ③无装载上下文或判定器通道缺失＝不注册（防御、不抛错、无半态——单源约束；不回退直调）；
//   独立构造（脱离对局）＝上下文不可达＝不注册（功能不可用、加载不失败）。
// ④参值（重甲）一律运行期经词条读取面读取（KeywordRules.GetKeywordValue——不构造期捕获；参值改写后行为随动）。
// ⑤行为等价：语义与收薄前的组件实现逐一对应（含方向/防御边界）；差异与改签清单见批 2 实现记录与汇报。
//
// 批 3 词条效果化（C 档首迁）：闪击（能力型）行为迁效果承载（组件收薄为壳——标识／授予-移除／读取面；
// 「部署置位」（unit.deployed → CanMove/CanAttack 置位）由效果承载——订阅建立/撤销随效果生命周期；仅部署路径生效）。
// （批 3 时制品与装配口径同批 1/2——构造期 EmbedEffect；批 4 起闪击改经「数据壳＋行为引用」，见下批 4 段。）
//
// 批 4 数据化（路线 A·混合形态最小试点）：闪击／动员（含双效果）由「C# 效果注入」迁「数据壳＋行为引用」形态——
// ①数据壳＝词条效果库制品（Cards/KeywordPrefabs/*.prefab.json：触发器/hooks 结构面＋assemblyKey 行为引用）；
// ②绑定声明＝注册面（KeywordRegistry.DeclareEffectBindings——词条 → 效果清单）；
// ③装载期实例化＝授予链（KeywordManager 的装载期实例化点——context.Engine 可用时实例化数据效果并接入内嵌效果通道）；
// ④行为引用生产注册＝KeywordEffectAssembly（对局装配段——行为引用注册＋库装载＋绑定核验）。
// 本文件中三个试点类型（BlitzDeployEffect／MobilizeAccrualEffect／MobilizeLossEffect）现为**行为引用目标**
// （不再作为 Effect 装配——原效果实例订阅路径退役；行为经数据效果 hooks 触发器驱动——「回调先 vs hooks 后」语义差异
// 见批 4 实现记录与汇报）；其余批 1/2 效果（压制/情报/伏击/重甲/免疫）保持「C# 效果注入」形态不变。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 词条信号效果（批 1；词条行为的效果承载基类）：「解析对局上下文 → 装载钩子 → 订阅信号」与
/// 「卸载退订」的统一承载——订阅建立/撤销随效果装载/卸载（生命周期）自动发生（效果作者不手写外部管理）。
/// 上下文解析路径＝宿主卡 → 词条管理面 → 词条装载上下文提供器（与词条组件运行逻辑同一来源）；
/// 不可达（独立构造/未装配）＝不订阅（功能不可用、加载不失败——沿既有「无装配源＝跳过」口径）。
/// 信号处理＝派生类覆写 <see cref="HandleUpdateAsync"/>（按更新标识过滤、行为语义自定义）；
/// 装载钩子＝派生类覆写 <see cref="OnLoaded"/>（订阅建立**前**调用——如装载时点判定）。
/// 实例可多次成对装载/卸载（复装）——每次装载各自解析/订阅、每次卸载退订（幂等）。
/// </summary>
public abstract class KeywordSignalEffect : PassiveEffect
{
    private IDisposable? _subscription;
    private KeywordLoadContext? _context;

    /// <summary>创建词条信号效果。</summary>
    protected KeywordSignalEffect(string name)
        : base(name)
    {
    }

    /// <summary>
    /// 本效果的装载上下文（装载期解析——装载钩子与信号回调期可用；未装载/对局服务不可达＝null）。
    /// 派生类可经此读取对局服务面（如引擎引用——执行面调用所需）。
    /// </summary>
    protected KeywordLoadContext? Context => _context;

    /// <inheritdoc />
    protected sealed override void OnMount()
    {
        // 上下文解析：宿主卡 → 词条管理面 → 词条装载上下文提供器（与词条组件运行逻辑同一来源；
        // 独立构造＝提供器缺席/目标未就绪＝null＝不订阅——「功能不可用、加载不失败」）。
        var context = KeywordRules.TryGetKeywordManager(Host)?.LoadContextProvider?.Invoke();
        if (context is null)
        {
            return;
        }

        _context = context;
        OnLoaded(context); // 装载钩子（订阅建立前——此间不引入订阅残留风险）
        _subscription = context.Engine.Subscribe(HandleUpdateAsync); // 订阅建立（随效果装载）
    }

    /// <inheritdoc />
    protected sealed override void OnUnmount()
    {
        // 订阅撤销（随效果卸载；幂等——未订阅＝无操作）。
        var subscription = _subscription;
        _subscription = null;
        _context = null;
        subscription?.Dispose();
    }

    /// <summary>装载钩子（派生类可选覆写；订阅建立**前**调用——装载时点判定等；默认无操作）。</summary>
    protected virtual void OnLoaded(KeywordLoadContext context)
    {
    }

    /// <summary>信号处理（派生类覆写；按更新标识过滤——行为语义自定义）。</summary>
    protected abstract Task HandleUpdateAsync(
        string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct);
}

/// <summary>
/// 动员·回合累积行为（批 4 数据化：由「C# 效果注入」迁「数据壳＋行为引用」形态——本类型为**行为引用目标**
/// 〔数据壳 <c>keyword.mobilize.accrual</c> 的 assemblyKey 指向 <see cref="HandleAccrualAsync"/>〕；
/// 原效果实例订阅路径退役，行为现由数据效果 hooks 触发器（<c>turn.start</c>）驱动〔订阅者广播段——语义差异申报〕）。
/// 语义（等价基线）：友方回合开始（拥有者回合开始相位）→ +1/+1（累积——修饰器来源无撤销路径：失去/再获得不清理既得；
/// 来源标识改签＝宿主卡〔原词条组件〕——黑盒等价＝既得 +1/+1 保留、不随效果卸载撤销，见批 4 申报）。
/// 仅在场（已单位化、未死亡、有位置）单位获得加成（卡在卡组/手牌/未在场时跳过）。
/// </summary>
public sealed class MobilizeAccrualEffect
{
    private MobilizeAccrualEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.mobilize.accrual</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：条件/过滤/守卫保留在行为内部
    /// （数据壳仅承载「触发器/hooks＋行为引用」结构面）——触发过滤（turn.start）由数据壳 hooks 承载、
    /// 归属/在场过滤与加成逐条等价于原效果处理。
    /// </summary>
    internal static async Task HandleAccrualAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Player is not Player current
            || view.Host is not CardBase ownerCard
            || !ReferenceEquals(ownerCard.Owner, current))
        {
            return; // 非该单位拥有者的回合开始：不处理
        }

        if (view.Host is not UnitCard unit
            || !unit.TryGetData<UnitStateData>(out var state)
            || state.IsDestroyed
            || state.Position is null)
        {
            return; // 未在场（未单位化/已死亡/无位置）：不获得（「单位在其回合开始时获得」）
        }

        await unit.Modifiers.AddModifiersAsync(
            new Modifier[]
            {
                new AddModifier(CardStatFields.Attack, 1, ownerCard), // 来源＝宿主卡（批 4 改签——既得保留、无撤销路径）
                new AddModifier(CardStatFields.Defense, 1, ownerCard),
            },
            ct);
    }
}

/// <summary>
/// 动员·受伤失去行为（批 4 数据化：由「C# 效果注入」迁「数据壳＋行为引用」形态——本类型为**行为引用目标**
/// 〔数据壳 <c>keyword.mobilize.loss</c> 的 assemblyKey 指向 <see cref="HandleLossAsync"/>〕；
/// 原效果实例订阅路径退役，行为现由数据效果 hooks 触发器（<c>card.damaged</c>）驱动〔订阅者广播段——语义差异申报〕）。
/// 语义（等价基线）：监听 <see cref="GameUpdates.CardDamaged"/>（「受到伤害」——伤害结算后、实际扣减＞0 恰一次、
/// 先落定后发射；伤害被完全吸收/归零＝不发）→ 失去动员（词条移除链——走移除链、既得 +1/+1 保留）。
/// 「受伤监听类信号 → RevokeAsync 自我撤销」——门户直调路径已退役（门户不再感知动员）。
/// </summary>
public sealed class MobilizeLossEffect
{
    private MobilizeLossEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.mobilize.loss</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：触发过滤（card.damaged）由数据壳 hooks 承载、
    /// 本卡过滤与自我撤销逐条等价于原效果处理。
    /// </summary>
    internal static async Task HandleLossAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Host is not Card card)
        {
            return;
        }

        if (view.Card is not Card damaged || !ReferenceEquals(damaged, card))
        {
            return; // 非本卡受到伤害：不处理
        }

        if (KeywordRules.TryGetKeywordManager(card) is not { } manager)
        {
            return;
        }

        await manager.RevokeAsync(KeywordIds.Mobilize); // 失去＝词条移除（走移除链；幂等防御——词条不在＝无操作）
    }
}

/// <summary>
/// 被压制·生命周期效果（批 1；由 <see cref="SuppressedKeywordComponent"/> 内嵌装配）：
/// 状态管理（递减/自解除）——拥有者回合结束驱动：递减剩余回合数（参值运行期读取——改写后随动）；
/// 到 0＝到期解除（走标记移除路径）。「下一个回合」不含当前回合：装载时若正处于该单位的拥有者回合，
/// 本回合结束不递减（skip 消费一次、不进参值面）。订阅挂点＝装载（OnMount）/卸载（OnUnmount）。
/// </summary>
public sealed class SuppressionLifecycleEffect : KeywordSignalEffect
{
    private bool _skipOwnerTurnEnd;

    /// <summary>创建被压制·生命周期效果。</summary>
    internal SuppressionLifecycleEffect()
        : base("被压制·生命周期")
    {
    }

    /// <inheritdoc />
    protected override void OnLoaded(KeywordLoadContext context)
    {
        // 「下一个回合」不含当前回合：施加（装载）时若正处于该单位的拥有者回合，本回合结束不递减（skip 消费一次）
        var current = context.CurrentPlayerProvider?.Invoke();
        _skipOwnerTurnEnd = current is not null
            && Host is CardBase ownerCard
            && ReferenceEquals(ownerCard.Owner, current);
    }

    /// <inheritdoc />
    protected override async Task HandleUpdateAsync(
        string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
    {
        if (updateType != GameUpdates.TurnEnd)
        {
            return;
        }

        var card = Host;
        if (payload?[GameUpdates.PayloadPlayer] is not Player ended
            || card is not CardBase ownerCard
            || !ReferenceEquals(ownerCard.Owner, ended))
        {
            return; // 非该单位拥有者的回合结束：不处理
        }

        if (_skipOwnerTurnEnd)
        {
            _skipOwnerTurnEnd = false; // 当前（拥有者）回合的结束：跳过——尚未开始递减
            return;
        }

        if (KeywordRules.TryGetKeywordManager(card) is not { } manager)
        {
            return;
        }

        var remaining = KeywordRules.GetKeywordValue(card, KeywordIds.Suppressed)
            ?? SuppressRules.SuppressedDefaultTurns;
        var next = remaining - 1;
        if (next <= 0)
        {
            await manager.RevokeAsync(KeywordIds.Suppressed); // 到期：解除（标记移除路径）
            return;
        }

        manager.SetValue(KeywordIds.Suppressed, next); // 剩余回合数递减
    }
}

/// <summary>
/// 情报·被使用时触发效果（批 1；由 <see cref="IntelligenceKeywordComponent"/> 内嵌装配）：
/// 「具有情报的卡被使用时」触发（card.played 时点接线；推断口径、官方规则文本未直述）——触发点调用
/// 情报执行待办环节（<see cref="IntelligenceRules.InvokePendingRevealAsync"/>；「明牌 X 张」执行面 G18
/// 就绪后接线、本批不发明）。参值运行期读取（改写后触发随动；缺省 0）。
/// 订阅挂点＝装载（OnMount）/卸载（OnUnmount）——行为随词条生灭。
/// </summary>
public sealed class IntelligenceTriggerEffect : KeywordSignalEffect
{
    /// <summary>创建情报·被使用时触发效果。</summary>
    internal IntelligenceTriggerEffect()
        : base("情报·被使用时触发")
    {
    }

    /// <inheritdoc />
    protected override Task HandleUpdateAsync(
        string updateType, IReadOnlyDictionary<string, object?>? payload, CancellationToken ct)
    {
        if (updateType != GameUpdates.CardPlayed)
        {
            return Task.CompletedTask;
        }

        var card = Host;
        if (payload?[GameUpdates.PayloadCard] is not Card played || !ReferenceEquals(played, card))
        {
            return Task.CompletedTask; // 非本卡被使用：不处理
        }

        var amount = KeywordRules.GetKeywordValue(card, KeywordIds.Intelligence) ?? 0;
        return IntelligenceRules.InvokePendingRevealAsync(card, amount, Context?.Engine, ct);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 批 2：伤害改写族效果（伏击／重甲／免疫）——模式说明见文件头「批 2 词条效果化」段。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 词条伤害改写效果基类（批 2；伤害改写族〔伏击／重甲／免疫单位侧〕的统一承载）：
/// 「解析对局上下文 → 装载钩子 → 向『造成攻击伤害』触发器注册改写 handler」与「卸载撤销」的统一承载——
/// 注册建立/撤销随效果装载/卸载（生命周期）自动发生（效果作者不手写外部管理）。
/// 上下文解析路径＝宿主卡 → 词条管理面 → 词条装载上下文提供器（与词条组件运行逻辑同一来源）；
/// 不可达（独立构造/未装配）＝不注册（功能不可用、加载不失败——沿既有「无装配源＝跳过」口径）。
/// 装载准备检查＝派生类覆写 <see cref="OnLoading"/>（false＝不注册——如判定器通道缺失；防御、不抛错、无半态）；
/// 装载钩子＝派生类覆写 <see cref="OnLoaded"/>（注册建立**前**调用）；卸载清位＝派生类覆写 <see cref="OnUnloading"/>。
/// 实例可多次成对装载/卸载（复装）——每次装载各自解析/注册、每次卸载撤销（幂等）。
/// </summary>
public abstract class KeywordDamageRewriteEffect : PassiveEffect
{
    private readonly List<Action> _detach = new();
    private KeywordLoadContext? _context;

    /// <summary>创建词条伤害改写效果。</summary>
    protected KeywordDamageRewriteEffect(string name)
        : base(name)
    {
    }

    /// <summary>本效果的装载上下文（装载期解析——装载钩子与处理器期可用；未装载/对局服务不可达＝null）。</summary>
    protected KeywordLoadContext? Context => _context;

    /// <summary>本效果在「造成攻击伤害」触发器内的注册名。</summary>
    protected abstract string HandlerName { get; }

    /// <inheritdoc />
    protected override void OnMount()
    {
        // 上下文解析：宿主卡 → 词条管理面 → 词条装载上下文提供器（与词条组件运行逻辑同一来源；
        // 独立构造＝提供器缺席/目标未就绪＝null＝不注册——「功能不可用、加载不失败」）。
        var context = KeywordRules.TryGetKeywordManager(Host)?.LoadContextProvider?.Invoke();
        if (context is null || !OnLoading(context))
        {
            return; // 无装载上下文 / 准备检查未通过（如判定器通道缺失）：不注册（防御、不抛错、无半态）
        }

        _context = context;
        OnLoaded(context); // 装载钩子（注册建立前——此间不引入注册残留风险）
        AttachHandlers(context); // 注册（R11：伏击→unit.attack；重甲→combat.damage；免疫→damage.apply）
    }

    /// <inheritdoc />
    protected override void OnUnmount()
    {
        // 注册撤销（随效果卸载；幂等——未注册＝无操作）；上下文/通道引用清位（与注册状态一致）。
        var detach = _detach.ToArray();
        _detach.Clear();
        _context = null;
        OnUnloading();
        foreach (var action in detach)
        {
            action();
        }
    }

    /// <summary>
    /// 注册改写 handler（默认：攻击处理触发器 <see cref="ActionHandleBands.Rewrite"/> 段——伏击；
    /// 派生类可按 R11 改挂到伤害三端/处理端）。
    /// 注册随效果装载建立、随卸载撤销（顺序与装载相反）。
    /// </summary>
    protected virtual void AttachHandlers(KeywordLoadContext context)
        => AttachTo(context.AttackDamageTrigger, HandlerName, ActionHandleBands.Rewrite, HandleAttackDamageAsync);

    /// <summary>向指定触发器注册 handler 并登记撤销动作（registrant＝本效果——R9 执行归属）。</summary>
    protected void AttachTo<TView>(
        Trigger<TView> trigger, string name, Func<TView, Context, CancellationToken, Task> handler)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var registration = trigger.Register(name, handler, registrant: this);
        _detach.Add(() => trigger.Unregister(registration));
    }

    /// <summary>向声明了专门 band 方案的触发器注册 handler（须显式 band——触发器约定）。</summary>
    protected void AttachTo<TView>(
        Trigger<TView> trigger, string name, Enum band, Func<TView, Context, CancellationToken, Task> handler)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(band);
        var registration = trigger.Register(name, handler, band, registrant: this);
        _detach.Add(() => trigger.Unregister(registration));
    }

    /// <summary>装载准备检查（派生类可选覆写；false＝不注册——防御语义〔如判定器通道缺失〕；默认通过）。</summary>
    protected virtual bool OnLoading(KeywordLoadContext context) => true;

    /// <summary>装载钩子（派生类可选覆写；注册建立**前**调用——通道引用保存等；默认无操作）。</summary>
    protected virtual void OnLoaded(KeywordLoadContext context)
    {
    }

    /// <summary>卸载清位钩子（派生类可选覆写；注册撤销**前**调用——派生类专属引用清位；默认无操作）。</summary>
    protected virtual void OnUnloading()
    {
    }

    /// <summary>伤害改写处理（派生类覆写；伏击＝攻击伤害触发器内、默认互伤之前执行——由注册优先级与改写标志保证顺序）。</summary>
    protected virtual Task HandleAttackDamageAsync(
        AttackDamageTriggerView view, Context ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// 伏击·伤害改写效果（批 2；由 <see cref="AmbushKeywordComponent"/> 内嵌装配）：
/// 「造成攻击伤害」处理端 <see cref="ActionHandleBands.Rewrite"/> 段注册改写逻辑（注册随效果装载、撤销随效果卸载）——
/// 改写判定（先资格、后条件）：
/// 被攻击单位（目标侧，＝本卡）含伏击 ∧ 目标方按反击豁免判定表具有反击资格
/// （K2：经 <c>combat.counter.eligibility</c> 判定器通道——豁免约束改写，无资格＝不发生反击、改写不成立、按表单方结算）
/// ∧ 条件命中（K2：经 <c>combat.ambush.condition</c> 判定器通道——被攻击单位攻击力有效值 ＞ 攻击者防御力有效值，互扣前）
/// → **由效果自己执行死亡**：经 <see cref="KeywordLoadContext.DeathAction"/> 走 `IDeathAction.ExecuteAsync`
/// **完整两端**（触发端 → 处理端；不是直调处理端），随后置 <c>IsRewritten</c> 与 <c>Handled</c>
/// （攻击者死亡、被攻击者不受伤；处理端据此跳过伤害）。
/// 判定器通道经装载上下文取用（<c>KeywordLoadContext.CounterEligibility</c>／<c>KeywordLoadContext.AmbushCondition</c>）；
/// 无装载上下文或通道缺失＝不注册（防御、不抛错、不回退直调——单源约束）。不区分攻击者类型；HQ 目标不改写。
/// </summary>
public sealed class AmbushRewriteEffect : KeywordDamageRewriteEffect
{
    private Func<UnitCard, UnitCard, bool>? _counterEligibility; // K2：反击资格判定通道（combat.counter.eligibility——资格→C5）
    private Func<UnitCard, UnitCard, bool>? _ambushCondition;    // K2：伏击条件判定通道（combat.ambush.condition——条件→C6）
    private Orc.Game.Actions.IDeathAction? _deathAction;         // S11：死亡动作（改写方自己走完整两端）

    /// <summary>创建伏击·伤害改写效果。</summary>
    internal AmbushRewriteEffect()
        : base("伏击·伤害改写")
    {
    }

    /// <inheritdoc />
    protected override string HandlerName => "伏击改写";

    /// <inheritdoc />
    protected override bool OnLoading(KeywordLoadContext context)
    {
        if (context.CounterEligibility is null || context.AmbushCondition is null
            || context.DeathAction is null)
        {
            return false; // K2/S11：通道缺失：不注册（防御、不抛错、不回退直调——与「无装载上下文」同构）
        }

        _counterEligibility = context.CounterEligibility;
        _ambushCondition = context.AmbushCondition;
        _deathAction = context.DeathAction;
        return true;
    }

    /// <inheritdoc />
    protected override void OnUnloading()
    {
        _counterEligibility = null; // K2：通道引用清位（与注册状态一致；重复卸载幂等）
        _ambushCondition = null;
        _deathAction = null;
    }

    /// <inheritdoc />
    protected override async Task HandleAttackDamageAsync(
        AttackDamageTriggerView view, Context ctx, CancellationToken ct)
    {
        if (Host is not UnitCard self || !self.TryGetData<UnitStateData>(out var selfState) || selfState.IsDestroyed)
        {
            return; // 死亡后不再改写（双保险：装载已注销 + 存活判定）
        }

        if (view.Target is not { IsAlive: true } targetRef
            || targetRef.Value is not UnitCard target
            || !ReferenceEquals(target, self))
        {
            return; // 仅处理「本卡被攻击」的目标侧（HQ 目标不进入）
        }

        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard attacker
            || !attacker.TryGetData<UnitStateData>(out _)) // 攻击者须已单位化（未单位化＝不改写、不抛错）
        {
            return;
        }

        if (view.Resolution is not AttackDamageResolution resolution || resolution.IsRewritten)
        {
            return; // 目标侧单命中（多源不叠加；已改写＝跳过）
        }

        var counterEligibility = _counterEligibility;
        var ambushCondition = _ambushCondition;
        var deathAction = _deathAction;
        if (counterEligibility is null || ambushCondition is null || deathAction is null)
        {
            return; // K2/S11：通道缺失（防御：装载时已不注册——双保险；不回退直调）
        }

        // 先资格（K2：经 combat.counter.eligibility 判定器通道——豁免约束改写）：目标方（本卡）具有反击资格。
        if (!counterEligibility(attacker, self))
        {
            return;
        }

        // 后条件（K2：经 combat.ambush.condition 判定器通道——单源）：伏击条件命中＝改写成立。
        if (!ambushCondition(self, attacker))
        {
            return;
        }

        resolution.MarkRewritten();

        // S11：改写方**自己执行死亡**——经死亡动作走**完整两端**（触发端 → 处理端），不复用默认互伤、
        // 不直调处理端。随后置 Handled＝已完整处置（处理端据此跳过伤害；免死时攻击者仍存活 → 另发幸存信号）。
        await deathAction.ExecuteAsync(attacker, killer: self, view.TriggerCard, ct).ConfigureAwait(false);
        resolution.MarkHandled();
    }
}

/// <summary>
/// 重甲·减伤效果（批 2；由 <see cref="ArmorKeywordComponent"/> 内嵌装配）：
/// 受到的对战伤害 -X（攻击伤害与反击伤害均含；指令伤害不减免——非对战路径不经本注册口）。
/// 参值运行期经词条读取面读取（<c>KeywordRules.GetKeywordValue</c>——改写后减伤量随动；参值 null／0＝无减伤）。
/// 减伤经「造成攻击伤害」handler 链介入（默认结算 handler 之前登记减伤量；结果下限 0 自然收敛）；
/// 判定读点（攻击者侧「无视重甲」——默认不无视）经 <c>ArmorRules.IgnoresArmor</c> 保持不变。
/// 注册随效果装载、撤销随效果卸载（无装载上下文＝不注册——防御、不抛错、无半态）。
/// </summary>
public sealed class ArmorReductionEffect : KeywordDamageRewriteEffect
{
    /// <summary>创建重甲·减伤效果。</summary>
    internal ArmorReductionEffect()
        : base("重甲·减伤")
    {
    }

    /// <inheritdoc />
    protected override string HandlerName => "重甲减伤";

    /// <summary>R11：重甲只挂 <c>combat.damage</c>——效果伤害与战斗伤害不减免。</summary>
    protected override void AttachHandlers(KeywordLoadContext context)
    {
        if (context.DamageAction is { } pipeline)
        {
            AttachTo(pipeline.CombatDamage, HandlerName, ActionTriggerBands.Initiate, HandleCombatDamageAsync);
        }
    }

    private Task HandleCombatDamageAsync(DamageEventView view, Context ctx, CancellationToken ct)
    {
        // 参值运行期读取（每次伤害结算时读取当前参值——非装载时快照；null／0＝无减伤效果〔「重甲 0」语义等效〕）。
        if (Host is not { } self
            || KeywordRules.GetKeywordValue(self, KeywordIds.Armor) is not { } armor
            || armor <= 0)
        {
            return Task.CompletedTask;
        }

        // 只减「打在本卡身上」的那一向（反击方向是另一次 CombatAsync，Target 同样指向本卡）。
        if (!ReferenceEquals(view.Target, self))
        {
            return Task.CompletedTask;
        }

        // 判定读点：攻击者侧「无视重甲」——默认不无视。
        if (view.Source is UnitCard attacker && ArmorRules.IgnoresArmor(attacker))
        {
            return Task.CompletedTask;
        }

        view.Amount = Math.Max(0, view.Amount - armor);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 免疫·归零效果（批 2；由 <see cref="ImmuneKeywordComponent"/> 内嵌装配）：
/// 不会受到伤害——伤害归零（不限制索敌：目标候选/校验不查免疫；非伤害效果照常）。
/// 单位侧＝「造成攻击伤害」触发器内注册（默认结算 handler 之前把对本卡的伤害设为 0——攻击伤害与反击伤害两方向；
/// 注册随效果装载、撤销随效果卸载；无装载上下文＝不注册——防御、不抛错、无半态）；
/// HQ 侧＝HQ 伤害路径改写段挂「归零改写器」（机制同一、接入点各按路径对齐）——HQ 宿主即挂、不依赖装载上下文
/// （双宿主上下文依赖差异＝现状语义，逐条保持）；来源标记＝本效果实例（原组件实例改签——见批 2「内部改签清单」）。
/// 归零后按「0 伤害」正常走（净伤害＝0 不算「受到伤害」——动员等消费自然一致）。
/// </summary>
public sealed class ImmuneZeroingEffect : KeywordDamageRewriteEffect
{
    private HqDamageRewriter? _hqRewriter;

    /// <summary>创建免疫·归零效果。</summary>
    internal ImmuneZeroingEffect()
        : base("免疫·归零")
    {
    }

    /// <inheritdoc />
    protected override string HandlerName => "免疫归零";

    /// <summary>R11：单位免疫挂 <c>damage.apply</c>（全伤害种类——对战/效果/战斗）。</summary>
    protected override void AttachHandlers(KeywordLoadContext context)
    {
        if (context.DamageAction is { } pipeline)
        {
            AttachTo(pipeline.Apply, HandlerName, ActionHandleBands.Rewrite, HandleApplyAsync);
        }
    }

    private Task HandleApplyAsync(DamageApplyView view, Context ctx, CancellationToken ct)
    {
        if (Host is { } self && ReferenceEquals(view.Target, self))
        {
            view.IsZeroed = true; // 目标方向：本卡（免疫）不受伤
            view.Amount = 0;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnMount()
    {
        if (Host is Hq hq)
        {
            // HQ 侧（「机制同一、接入点各按路径对齐」）：HQ 宿主即挂归零改写器——不依赖装载上下文
            // （不得因「上下文不可达＝不处理」跳过 HQ 侧——现状语义保持）；来源标记＝本效果实例（改签）。
            _hqRewriter = new HqDamageRewriter(this, _ => 0);
            hq.AddDamageRewriter(_hqRewriter);
            return;
        }

        base.OnMount(); // 单位侧：上下文注册路径（无上下文＝不注册——防御、不抛错、无半态）
    }

    /// <inheritdoc />
    protected override void OnUnmount()
    {
        var rewriter = _hqRewriter;
        _hqRewriter = null;
        if (Host is Hq hq && rewriter is not null)
        {
            hq.RemoveDamageRewriter(rewriter); // 幂等（未挂载＝无操作、不抛错）
        }

        base.OnUnmount();
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// 批 3/批 4：闪击（能力型；部署置位）——批 3 迁效果承载、批 4 迁「数据壳＋行为引用」；
// 模式说明见文件头「批 3 词条效果化」与「批 4 数据化」段。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 闪击·部署置位行为（批 4 数据化：由「C# 效果注入」迁「数据壳＋行为引用」形态——本类型为**行为引用目标**
/// 〔数据壳 <c>keyword.blitz.deploy-set</c> 的 assemblyKey 指向 <see cref="HandleDeploySetAsync"/>〕；
/// 原效果实例订阅路径退役，行为现由数据效果 hooks 触发器（<c>unit.deployed</c>）驱动〔订阅者广播段——语义差异申报〕）。
/// 行为（等价基线）：监听 <see cref="GameUpdates.UnitDeployed"/>（「单位部署」——单位化完成后发射、恰一次；
/// 载荷＝{ Unit, Position }）→ 过滤 Host==载荷单位 → 置位指挥组件 CanMove/CanAttack＝true（覆盖部署初值 false/false）。
/// 仅部署路径生效：加入链（unit.joined）/部署重放（不发射链级信号）/升级替换（unit.upgraded）等路径
/// 不发射 unit.deployed——不置位（与现状一致）。防御：载荷缺失/非本单位/无 CommandData＝跳过、不抛错（沿既有口径）。
/// 差异申报（沿批 3/批 4 口径）：①置位时点迁移——「部署链收尾（扣费后）」→「unit.deployed 发射时」（批 3）
/// →「unit.deployed 订阅者广播段」（批 4 数据效果 hooks——外部通道通知段之后；仍在本信号分发流程内、先于可指挥读取）；
/// ②扣费异常边界（unit.deployed 与扣费之间链中断时「已置位」——对局不可达：费用校验先于链执行、
/// 收尾扣费为常规扣减无失败分支）；③独立构造（无上下文）＝不实例化、不订阅、不置位（「独立构造＝功能不可用」
/// 既定口径；加载不失败、无半态）。
/// </summary>
public sealed class BlitzDeployEffect
{
    private BlitzDeployEffect()
    {
    }

    /// <summary>
    /// 行为处理器（数据壳 <c>keyword.blitz.deploy-set</c> 的 assemblyKey 目标；签名＝
    /// <c>Func&lt;CardEventView, Context, CancellationToken, Task&gt;</c>）：触发过滤（unit.deployed）由数据壳
    /// hooks 承载、主机过滤与置位逐条等价于原效果处理。
    /// </summary>
    internal static Task HandleDeploySetAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Unit is not Card deployed || !ReferenceEquals(deployed, view.Host))
        {
            return Task.CompletedTask; // 非本单位部署：不处理（Host==载荷过滤——其他单位部署不误置位）
        }

        if (deployed.TryGetData<CommandData>(out var command))
        {
            command.CanMove = true; // 置位（覆盖部署初值 false/false；重复信号幂等——已是 true 再置 true 无副作用）
            command.CanAttack = true;
        }

        return Task.CompletedTask; // 无 CommandData（防御——非单位/未单位化）：跳过、不抛错
    }
}
