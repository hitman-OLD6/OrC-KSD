using Orc.Cards;
using Orc.Core;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Triggers;

namespace Orc.Game.Cards;

/// <summary>反制使用评估的拒绝类别（internal；验证与入口映射同源的判定结果，见 <see cref="CounterCard.EvaluateUse"/>）。</summary>
internal enum CounterUseRejection
{
    /// <summary>非己方回合（该卡所属玩家不是当前行动方）。</summary>
    NotOwnerTurn,

    /// <summary>指挥点不足（未激活时检查）。</summary>
    NotEnoughPoints,
}

/// <summary>
/// 反制卡（三大类之一）：激活/取消两态（数据组件 <see cref="CounterActivationData"/>）。
/// 数据组件装配（E 区差异化）：＝阵营〔国籍〕＋部署费合并组件（基类）＋激活状态组件。
/// <para>
/// 触发器（P4）：<see cref="UseCounterTrigger"/> **只做激活/取消验证＋状态翻转**（扣点/退点、置激活、登记/注销
/// <c>OrderFlow</c> 的 Counter 段 handler）；不再承载效果 handler 本体。
/// </para>
/// <para>
/// 效果 handler 集经 <see cref="AddEffectHandler"/> 装配（签名保持不变）；激活时包装为
/// <c>OrderFlowView</c> handler 注册到 <c>OrderFlow.Trigger</c> 的 <see cref="OrderFlowBands.Counter"/> 段
/// （按激活序＝注册序执行），取消时注销。包装器构造 <see cref="CardTriggerView"/>（Card＝反制卡、Player＝归属、
/// Source＝指令卡、Target＝施放目标、Argument＝数值）后调用原 handler。
/// </para>
/// <para>
/// **一次性消耗**（第 15 轮裁决）：handler 调用 <c>ctx.Stop()</c>（默认：指令照常扣费/离手、效果不触发）或
/// <c>ctx.Interrupt()</c>（牌留手/不扣费）＝命中；命中后本卡从手牌移除并走弃置路径（<c>card.discarded</c>，不退费）、
/// 注销全部 Counter 段 handler、置未激活。未命中（未调用 Stop/Interrupt）保持激活；多张反制中未触发者保持激活。
/// </para>
/// 使用反制不执行 targeter 交互、不发任何游戏更新；位置语义＝不做激活区——激活/取消＝状态翻转＋费用
/// （卡保持手牌位置不变）；可重复激活（激活↔取消可反复、无次数上限，费用各恰一次；仅限己方回合；消耗后不可再激活）。
/// 加载模板与其余装配沿用基类（<see cref="CardBase"/>）。
/// </summary>
public class CounterCard : CardBase
{
    private readonly List<CounterEffectHandler> _effectHandlers = new();
    private readonly List<TriggerRegistration> _activeRegistrations = new();

    /// <summary>激活实扣额（W3-2 G5）：激活时写入、取消时按此退还、取消后清空——「扣点与退点同额」跨调用守恒（与期间修饰漂移解耦）。</summary>
    private int? _chargedCost;

    /// <summary>一次性消耗守卫（S14 第 7 条）：同一张卡只消耗一次；后续包装器见之直接跳过。</summary>
    private bool _consumed;

    /// <summary>创建反制卡（激活状态组件＋触发器与默认链事件在构造期装配；
    /// 使用反制触发器按名绑定反制使用检查判定器——解析器缺省＝内置默认〔独立构造即可用〕）。</summary>
    /// <param name="engine">引擎（发射/触发）。</param>
    /// <param name="definition">卡牌定义。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（按名解析——对局路径＝注册表解析；
    /// 缺省＝null＝独立构造路径——内置默认解析）。</param>
    /// <exception cref="ArgumentNullException">engine 或 definition 为 null。</exception>
    public CounterCard(
        LogicEngine engine,
        CardDefinition definition,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null)
        : base(engine, definition)
    {
        // 激活状态组件（E 区：「反制＝激活状态组件」；2B 挂载落实——初始未激活）。
        AddData(new CounterActivationData());

        UseCounterTrigger = new CounterUseTrigger(this, validationJudicatorResolver);
        // R9：内置链 handler 归因于本卡（registrant: this）——链内发信号以本卡为 Source。
        UseCounterTrigger.Register("使用反制", HandleUseCounterAsync, registrant: this);
    }

    /// <summary>使用反制的触发器（唯一；单入口状态翻转：未激活⇒激活流程、已激活⇒取消流程）。</summary>
    public Trigger<CardTriggerView> UseCounterTrigger { get; }

    /// <summary>
    /// 已注册的效果 handler 名称（登记序；激活期间＝登记集、未激活/已消耗＝空）——
    /// handler 注册/注销可观测面（激活后在册；取消或消耗后已注销）。
    /// </summary>
    public IReadOnlyList<string> RegisteredEffectHandlerNames
        => _activeRegistrations.Select(registration => registration.Name).ToArray();

    /// <summary>是否已被反制命中消耗（一次性；消耗后不可再激活）。</summary>
    public bool IsConsumed => _consumed;

    /// <summary>
    /// 效果 handler 集装配入口（装配/加载阶段注册；登记序保留）：
    /// 激活时按登记序注册进 <c>OrderFlow</c> 的 Counter 段、取消时注销。
    /// 签名保留 <see cref="CardTriggerView"/>（适配视图经 <see cref="CardTriggerView.Target"/> 加性字段携带施放目标）。
    /// </summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public void AddEffectHandler(
        string name, Func<CardTriggerView, Context, CancellationToken, Task> handler, object? registrant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        _effectHandlers.Add(new CounterEffectHandler(name, handler, registrant));
    }

    /// <summary>
    /// 反制使用评估（internal；验证与入口映射的单一判定源）：返回 null＝合法；否则＝拒绝类别。
    /// 规则：① 仅己方回合（owner 非空且＝当前行动方）；② 未激活时检查指挥点（≥ 花费）；已激活＝取消流程、无条件通过。
    /// </summary>
    internal CounterUseRejection? EvaluateUse(Player? currentPlayer)
    {
        var owner = Owner;
        if (owner is null || currentPlayer is null || !ReferenceEquals(owner, currentPlayer))
        {
            return CounterUseRejection.NotOwnerTurn;
        }

        // W3-2 G5：未激活时检查指挥点（≥「有效部署费」——修饰贡献叠加后的链输出；评估与翻转同口径）。
        if (!GetData<CounterActivationData>().IsActive
            && owner.Points < Modifiers.GetEffectiveValue(CardStatFields.DeployCost))
        {
            return CounterUseRejection.NotEnoughPoints;
        }

        return null;
    }

    /// <summary>使用反制默认事件（单入口状态翻转；按当前激活状态分派；E1-25 后续：扣点/退点经点数通用入口发 point.changed）。</summary>
    private async Task HandleUseCounterAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not CounterCard card || view.Player is not Player player)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：翻转不发生
            return;
        }

        if (card._consumed)
        {
            ctx.Stop(); // 已消耗（一次性）：不可再激活/取消——本级余下事件不执行
            return;
        }

        var activation = card.GetData<CounterActivationData>();

        if (!activation.IsActive)
        {
            // 激活流程：读「有效部署费」（W3-2 G5——修饰贡献叠加后的链输出；与评估同口径）→ 扣点 →
            // 记录实扣额（取消按此退还——「扣点与退点同额」跨调用守恒）→ 置激活 → 注册（效果）handler（登记序）。
            var cost = card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost);
            await ChangePointsAsync(card, player, -cost, ct);
            card._chargedCost = cost;
            activation.IsActive = true;

            // S14：注册目标是**内核 OrderFlow 的 Counter 段**（经适配视图）——反制因此有真实位点。
            foreach (var effect in card._effectHandlers)
            {
                card._activeRegistrations.Add(card.Engine.OrderFlow.Trigger.Register(
                    effect.Name, card.CreateCounterHandler(effect), OrderFlowBands.Counter, registrant: effect.Registrant));
            }

            // E1-53：**反制触发**信号（激活分支、恰一次；取消分支不发——退点撤销不属"触发"）。
            if (ctx.Engine is { } engine)
            {
                await GameUpdates.EmitCounterTriggered(engine, card, player, ct, view.TriggerCard ?? card).ConfigureAwait(false);
            }
        }
        else
        {
            // 取消流程：退点（无条件、按激活实扣额——与期间修饰漂移解耦、点数守恒）→ 清记录 → 取消激活 → 取消注册。
            var refund = card._chargedCost ?? throw new InvalidOperationException(
                $"反制 '{card.Name}' 处于激活态但无实扣额记录（结构性错误——激活须经使用流程；fail-fast、不静默）。");
            await ChangePointsAsync(card, player, refund, ct);
            card._chargedCost = null;
            activation.IsActive = false;
            foreach (var registration in card._activeRegistrations)
            {
                card.Engine.OrderFlow.Trigger.Unregister(registration);
            }

            card._activeRegistrations.Clear();

            // 本次执行快照含激活期间在册的效果 handler（取消前注册）：Stop 防其在本次触发中被执行
            // （「取消后触发不执行」的严谨落实——注销立即生效于后续触发）。
            ctx.Stop();
        }
    }

    /// <summary>
    /// 构造 Counter 段包装器（S14 第 2/7 条）：把 <see cref="OrderFlowView"/> 适配为 <see cref="CardTriggerView"/> 后调用原 handler；
    /// handler 调用 <c>Stop</c>/<c>Interrupt</c>（＝命中）后执行一次性消耗。
    /// </summary>
    private Func<OrderFlowView, Context, CancellationToken, Task> CreateCounterHandler(CounterEffectHandler effect)
        => async (view, ctx, ct) =>
        {
            if (_consumed)
            {
                return; // 同卡多 handler / 已消耗：不重复计算
            }

            if (Owner is not { } player)
            {
                return; // 未加载归属：结构不可用（零副作用）
            }

            var counterView = new CardTriggerView
            {
                Card = this,
                Player = player,
                Source = view.Source,
                Target = view.Target,
                Argument = view.Amount,
            };

            await effect.Handler(counterView, ctx, ct).ConfigureAwait(false);

            // 命中＝handler 主动停止（Stop＝默认反制；Interrupt＝可选强反制）；未命中保持激活、不消耗。
            if ((ctx.Stopped || ctx.Interrupted) && !_consumed)
            {
                await ConsumeAsync(player, ct).ConfigureAwait(false);
            }
        };

    /// <summary>
    /// 一次性消耗（命中即消耗，发生在 Counter 段执行过程中，不延后到 Finalize）：
    /// 从手牌移除 + 走弃置路径（<c>card.discarded</c>，复用 <c>PlayerManager.DiscardCardAsync</c> 语义）→
    /// 注销该卡在 Counter 段的全部 handler → 置未激活、清实扣额；**不退费**。
    /// </summary>
    private async Task ConsumeAsync(Player player, CancellationToken ct)
    {
        _consumed = true;

        // 弃置：手牌移除 → 销毁 → card.discarded 恰一次（不新建弃置路径）。
        if (player.Hand.Contains(this))
        {
            if (DiscardCardProvider is { } discard)
            {
                await discard(player, this, null, ct).ConfigureAwait(false);
            }
            else
            {
                // 脱局兜底（未装配对局弃置委托）：与 DiscardCardAsync 同序同信号。
                player.Hand.Remove(this);
                await HandLimitBurn.DestroyAndEmitDiscardedAsync(Engine, player, this, ct).ConfigureAwait(false);
            }
        }

        // 注销 + 未激活 + 清实扣额（不退费——不调 ChangePointsAsync）。
        foreach (var registration in _activeRegistrations)
        {
            Engine.OrderFlow.Trigger.Unregister(registration);
        }

        _activeRegistrations.Clear();
        GetData<CounterActivationData>().IsActive = false;
        _chargedCost = null;
    }

    /// <summary>点数变更归口（E1-25 后续）：经资源管理器通用入口（发 point.changed）；脱局＝直写兜底（保持既有语义）。</summary>
    private static async Task ChangePointsAsync(CounterCard card, Player player, int delta, CancellationToken ct)
    {
        if (ResourceManager.ResolveFor(card) is { } manager)
        {
            await manager.ChangePointsAsync(player, delta, PointChangeKind.Add, ct).ConfigureAwait(false);
            return;
        }

        player.Points += delta;
    }

    private sealed record CounterEffectHandler(
        string Name, Func<CardTriggerView, Context, CancellationToken, Task> Handler, object? Registrant);
}
