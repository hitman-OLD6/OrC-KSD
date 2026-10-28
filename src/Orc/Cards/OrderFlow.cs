#pragma warning disable CS8618 // 视图属性值由框架在绑定时提供；声明期不做初始化（视图类不应写属性初始化器）。

using Orc.Core;
using Orc.Core.Model;

namespace Orc.Cards;

/// <summary>
/// 指令流程 band 方案（R3 公共名兼容：8 个动作中唯一保留专属 band 方案者）：
/// Counter＝反制检查（扩展位，无默认处理器；反制类效果经既有装载/注入机制接入此处）／
/// Resolve＝结算（priority 0＝宣告点 <c>card.played</c>；priority 100＝默认「施放结算」——按登记序执行全部主动效果）／
/// Finalize＝收尾（独立阶段；游戏层 priority 100 注册扣费/离手）。
/// <para>
/// 段序：<see cref="Counter"/> → <see cref="Resolve"/> → <see cref="Finalize"/>。
/// <c>card.played</c> 位于 <see cref="Resolve"/> 之首（priority 0）：反制 <c>Stop</c> 使整个 Resolve 不执行，
/// 因此被反制的指令**不发** <c>card.played</c>（视为未成功打出）——这是「反制后效果不触发」的必要前提
/// （现有指令卡效果＝被动效果 hook <c>card.played</c>，宣告点若早于反制则效果会绕过反制）。
/// </para>
/// </summary>
public enum OrderFlowBands
{
    /// <summary>反制检查（扩展位；默认无处理器，供反制类效果注入）。</summary>
    Counter = 1,

    /// <summary>结算（priority 0＝宣告点；priority 100＝默认施放结算）。</summary>
    Resolve = 2,

    /// <summary>收尾（独立阶段；<c>ctx.Stop()</c> 不阻止本段，<c>ctx.Interrupt()</c> 阻止）。</summary>
    Finalize = 3,
}

/// <summary>
/// 指令流程视图（S5；P4 多指令化）：一次施放的执行时信息通道——来源 / 目标 / 数值 / 当前施放的指令集（非泛型接线面）。
/// 反制等检查位 handler 经本视图获得「当前施放的指令 / 来源 / 目标」（执行时数据），而非装配时预绑具体指令引用。
/// </summary>
[ContextView]
public class OrderFlowView
{
    /// <summary>来源（主动效果的持有卡）。</summary>
    [Read]
    public virtual Card Source { get; set; }

    /// <summary>施放目标（承受方；可空——无目标指令）。</summary>
    [Optional]
    [Read]
    public virtual Card? Target { get; set; }

    /// <summary>数值（如伤害量；流程内可改写）。</summary>
    [Mutate]
    public virtual int Amount { get; set; }

    /// <summary>当前施放的指令集（登记序；多主动效果按此序全部执行）。空集＝无主动效果。</summary>
    [Read]
    public virtual IReadOnlyList<ICastAction> Orders { get; set; }
}

/// <summary>
/// 指令流程（S5；引擎级共享；P5/S17 起为内核唯一流程——
/// 攻击／伤害流程类 <c>AttackFlow</c>／<c>DamageFlow</c> 已删除，动作实现归游戏层动作实体）：
/// 编排「反制检查（外来边）→ 结算（自有边）→ 收尾（独立成员）」。
/// <para>
/// P4 两阶段执行（S13）：阶段 1＝<c>engine#order.cast</c>（外来检查边先跑、结算自有边最后跑）；
/// 阶段 2＝<c>engine#order.finalize</c>。
/// <c>ExecuteAsync</c> 先跑阶段 1；<c>Interrupt</c>（链级中断）则跳过阶段 2；否则无论阶段 1 是否 <c>Stop</c> 都跑阶段 2。
/// 因此 <c>ctx.Stop()</c>＝被反制但仍消耗（结算停、收尾跑；反制默认语义），
/// <c>ctx.Interrupt()</c>＝牌留手/不扣费（两阶段全停；保留为可选路径）。
/// </para>
/// <para>
/// **位次等价（§27）**：旧实现用 band 表达三段（Counter 1／Resolve 2／Finalize 3）；
/// 新模型用"外来边的声明序优先级 ＋ 自有边最后"表达同一顺序——
/// 反制检查（外来边，优先级 0）→ 宣告点（外来边，优先级 100）→ 施放结算（自有边）。
/// </para>
/// <para>
/// 游戏层经**外来边**注入语义：宣告点（<c>card.played</c>，优先级 100）、收尾（扣费/离手，挂在 <c>order.finalize</c>）。
/// 内核只提供位点，不认识具体卡类型。
/// </para>
/// </summary>
public sealed class OrderFlow
{
    /// <summary>成员名：指令施放（宣告＋结算）。与 <c>EngineProcesses.OrderCast</c> 同源。</summary>
    public const string CastMember = "order.cast";

    /// <summary>成员名：指令收尾（扣费／离手）。与 <c>EngineProcesses.OrderFinalize</c> 同源。</summary>
    public const string FinalizeMember = "order.finalize";

    /// <summary>「施放结算」在声明序里的优先级（旧 Resolve 段默认事件的 100；宣告点同值、按接入序在前）。</summary>
    private const int ResolveDefaultPriority = 100;

    private readonly LogicEngine _engine;
    private bool _bound;

    internal OrderFlow(LogicEngine engine) => _engine = engine;

    /// <summary>指令流程阶段 1 的成员地址（<c>engine#order.cast</c>）。</summary>
    public MemberAddress Trigger => MemberAddress.Create(AddressScope.Engine, CastMember);

    /// <summary>指令流程阶段 2 的成员地址（<c>engine#order.finalize</c>）。</summary>
    public MemberAddress FinalizeTrigger => MemberAddress.Create(AddressScope.Engine, FinalizeMember);

    /// <summary>
    /// 往阶段 1（<c>order.cast</c>）挂一条**反制检查边**；返回的句柄 Dispose＝撤销。
    /// <para>位次：优先级 0（旧 Counter 段）——恒早于宣告点（优先级 100）。</para>
    /// </summary>
    /// <param name="displayName">来源名（留痕/审查用，如"反制检查"）。</param>
    /// <param name="owner">所有者（反制卡实例；撤销依据）。</param>
    /// <param name="edge">检查体（视图 <see cref="OrderFlowView"/>；拒绝＝<c>ctx.Stop()</c>）。</param>
    public IDisposable AttachCounterGuard(string displayName, object owner, OwnedEdge edge)
        => _engine.AttachMemberEdge(CastMember, displayName, owner, edge, priority: 0);

    /// <summary>
    /// 执行指令流程（单指令便捷重载）：<paramref name="order"/> 为 null＝无主动效果。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
    public Task<bool> ExecuteAsync(
        Card source, ICastAction? order, Card? target, int amount, CancellationToken ct = default)
        => ExecuteAsync(
            source,
            order is null ? Array.Empty<ICastAction>() : new[] { order },
            target,
            amount,
            ct);

    /// <summary>
    /// 执行指令流程（规范施放入口）：反制检查 → 结算（宣告点 + 按登记序执行全部主动效果）→〔未中断时〕收尾。
    /// 顶层调用时指令流挂总流；嵌套调用（如成员内）时挂当前执行者流。
    /// 只要求 <paramref name="source"/> 非空；目标可空（无目标指令）。
    /// </summary>
    /// <returns>链是否被中断（<c>ctx.Interrupt()</c>＝true：牌留手、不扣费）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 或 <paramref name="orders"/> 为 null。</exception>
    public async Task<bool> ExecuteAsync(
        Card source, IReadOnlyList<ICastAction> orders, Card? target, int amount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(orders);

        EnsureBound();

        var data = new Dictionary<string, object?>
        {
            [PayloadKeys.Source] = source,
            [PayloadKeys.Target] = target,
            [PayloadKeys.Amount] = amount,
            [PayloadKeys.Orders] = orders,
        };

        // 阶段 1：反制检查（外来边）→ 宣告点（外来边）→ 施放结算（自有边）。
        var ctx = new Context(data);
        await _engine.InvokeMemberAsync(CastMember, ctx, ct).ConfigureAwait(false);

        // Interrupt＝两阶段全停（牌留手/不扣费）；Stop＝只停 Resolve、Finalize 照常。
        if (ctx.Interrupted)
        {
            return true;
        }

        // 阶段 2：Finalize（独立阶段；扣费/离手）。同一份数据面（收尾要读 Source／Amount）。
        await _engine.InvokeMemberAsync(FinalizeMember, ctx, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// 施放结算（默认自有边）：按登记序依次执行全部主动效果（**禁止** <c>FirstOrDefault</c>——多主动效果必须全跑）。
    /// 施放信息经执行时数据传递；不预绑具体指令引用。
    /// </summary>
    private async Task OnResolve(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        foreach (var order in view.Orders)
        {
            if (ctx.Interrupted)
            {
                break; // 链级中断：后续主动效果不再执行
            }

            var castData = new Dictionary<string, object?>
            {
                [PayloadKeys.Source] = view.Source,
                [PayloadKeys.Target] = view.Target,
                [PayloadKeys.Amount] = view.Amount,
            };
            await order.CastAsync(_engine, castData, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 首次使用时把「施放结算」接成 <c>order.cast</c> 的自有边。
    /// <para>
    /// 为什么是懒接入：<see cref="OrderFlow"/> 在 <c>new LogicEngine()</c> 里构造，此时对局还没把编排运行时挂上
    /// （<c>Match</c> 在构造期才 <c>Engine.Model = EngineRuntime</c>）；成员边只能挂到**已装配**的引擎实体上。
    /// 缺运行时＝明确失败（不静默跳过：那会让指令卡"打出去什么都没发生"）。
    /// </para>
    /// </summary>
    private void EnsureBound()
    {
        if (_bound)
        {
            return;
        }

        var runtime = _engine.Model
            ?? throw new InvalidOperationException(
                "指令流程需要已装配的编排运行时（engine#order.cast／engine#order.finalize 是引擎成员）："
                + "本引擎未挂 Model，指令无法施放。");

        runtime.Engine.AttachOwnedEdge(CastMember, MemberEdges.For<OrderFlowView>(OnResolve));
        _bound = true;
    }
}
