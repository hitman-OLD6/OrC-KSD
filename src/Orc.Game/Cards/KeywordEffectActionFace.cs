using Orc.Cards;
using Orc.Game.Effects;
using Orc.Game.Effects.HandlerDsl;

namespace Orc.Game.Cards;

/// <summary>
/// **csx 的死亡动作门面**（A2 加性）：词条行为资产（<c>Cards/KeywordPrefabs/*.component.json</c>）的 csx 成员
/// 需要「改写方自己执行死亡」时经本类调用——等价于旧效果时代经 <c>KeywordLoadContext.DeathAction</c> 的调用，
/// 目标单位经死亡动作走**完整两端**（触发端 <c>engine#death</c> → 处理端 <c>engine#death.handle</c>）。
/// <para>
/// **为什么单独一个类型而不是 <see cref="EffectRuntime"/> 的方法**：<c>EffectRuntime</c> 的公共成员集是
/// **契约冻结面**（<c>ContractFreezeTests</c> 逐条比对基线，变更需人工确认并重生成基线）。
/// 动作面（消灭／战斗／位移…）将来还会增加，逐个塞进冻结面会让每次加一条动作面都变成一次契约变更；
/// 本类不属于冻结面的任何一段，因此是动作面的**稳定落点**（新增方法不触碰契约门）。
/// </para>
/// <para>
/// 服务不可达／目标非单位＝<c>false</c>（零副作用、不抛错——沿「功能不可用＝不失败」口径）。
/// </para>
/// </summary>
[HandlerDslActionFace]
public static class KeywordEffectActionFace
{
    /// <summary>
    /// 以死亡动作消灭目标（完整两端；<paramref name="host"/> 只用于解析对局服务）。
    /// </summary>
    /// <param name="host">服务解析宿主（行为组件所在卡；取自 csx 的 <c>view.Host</c>）。</param>
    /// <param name="target">被消灭的单位（其它实体类型＝false）。</param>
    /// <param name="killer">击杀者归属（<c>card.died</c> 的 <c>Killer</c>）。</param>
    /// <param name="triggerCard">触发者卡（链路传递；可空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">host／target 为 null。</exception>
    public static Task<bool> DeathAsync(
        Card host, Card target, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);

        return EffectRuntime.ResolveFor(host) is { } runtime
            ? runtime.DeathAsync(target, killer, triggerCard, ct)
            : Task.FromResult(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // handler DSL 三个**运行时入口**（内建 op 的实现落点，不是给作者连线的原语）。
    //
    // 它们在 EXCLUSIONS.md 里（不进节点目录）：ask／choice／read 是**内建 op**，
    // 图上的节点由 10 个内建 op 承担，运行期实现只能由生成码调用——本类不在冻结面，
    // 故新增这三个入口不需要动 contract-freeze.json。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ask</c> 的运行时实现（R23／D18）：发起玩家选靶，返回 <c>list&lt;entity&gt;</c>（0..n）。
    /// <para>
    /// **<paramref name="fallback"/> 只管「无交互通道」这一种情况**（`empty`／`first`）：
    /// 无通道时 <c>SelectAsync</c> 明确失败（抛 <see cref="InvalidOperationException"/>），此时才按 fallback 处理；
    /// **有通道、玩家主动取消＝返回空集、不走 fallback**（取消是玩家的意图表达，不是环境缺失）。
    /// </para>
    /// <para>
    /// <paramref name="prompt"/>／<paramref name="kind"/> 是契约侧字段：v1 的候选集由选择器与判定器给出，
    /// 提示文案的呈现面属前端桥接（S10），实体种类过滤在 DSL 层做静态子类型判定。
    /// </para>
    /// </summary>
    /// <param name="host">宿主卡（服务解析与视角卡）。</param>
    /// <param name="kind">候选实体种类（<c>unit</c>／<c>hq</c>／<c>card</c>；v1 只用于静态判定）。</param>
    /// <param name="side">阵营限定（null＝不限）。</param>
    /// <param name="zone">区域限定（null＝不限）。</param>
    /// <param name="prompt">提示文案（呈现面见 S10）。</param>
    /// <param name="fallback">无交互通道时的降级策略：<c>empty</c>（缺省）／<c>first</c>。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">host 为 null。</exception>
    public static async Task<IReadOnlyList<Card>> AskAsync(
        Card host, string kind, string? side, string? zone, string prompt, string fallback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = kind;
        _ = prompt;

        if (EffectRuntime.ResolveFor(host) is not { } runtime)
        {
            return Array.Empty<Card>();
        }

        try
        {
            return await runtime.SelectAsync(host, new EffectSelector("one", side, zone), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // 无交互通道（环境缺失）：作者才有权预先指定替代策略；`first` 必须由作者显式选择。
            if (!string.Equals(fallback, "first", StringComparison.Ordinal))
            {
                return Array.Empty<Card>();
            }

            var candidates = await runtime.SelectAsync(host, new EffectSelector("all", side, zone), ct)
                .ConfigureAwait(false);
            return candidates.Count > 0 ? new[] { candidates[0] } : Array.Empty<Card>();
        }
    }

    /// <summary>
    /// <c>choice</c> 的运行时实现：呈现选项并返回被选项的序号（0 起）。
    /// <para>
    /// **v1 抛 <see cref="NotSupportedException"/>**：选项呈现面属前端桥接（S10/R24 的交互面），尚未接入。
    /// 这里**不能**返回 −1 之类的「无选择」哨兵——那会让 <c>switch</c> 走 default 或越界，
    /// 变成「生效到错误分支上」，比「不生效」更糟（Constraints：不得留「装上了但不生效」的静默路径）。
    /// **宁可炸，不可错**：需要 <c>choice</c> 的图在接通前端桥接前一律当场失败。
    /// </para>
    /// </summary>
    /// <param name="host">宿主卡。</param>
    /// <param name="prompt">提示文案。</param>
    /// <param name="labels">选项文案（按顺序）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">host 或 labels 为 null。</exception>
    /// <exception cref="NotSupportedException">总是抛出：选项呈现需前端桥接（S10）。</exception>
    public static Task<int> ChooseAsync(Card host, string prompt, string[] labels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(labels);
        _ = prompt;
        _ = ct;
        throw new NotSupportedException(
            "choice 节点的选项呈现需前端桥接（S10）：本机没有交互通道时不做替代选择。");
    }

    /// <summary>
    /// <c>read</c> 变体的运行时读取入口（SPEC §12.8 的可读项）：返回**实时值**（含修饰链）。
    /// <para>
    /// 未知可读项＝<see cref="NotSupportedException"/>（fail-fast：宁可当场失败，也不返回一个「看起来正常」的 0
    /// 让作者以为条件成立）。<paramref name="of"/> 取值集：<c>self</c>（宿主卡本身）／<c>viewer</c>（宿主归属玩家）。
    /// </para>
    /// </summary>
    /// <param name="host">宿主卡（self 的读取对象／viewer 的归属来源）。</param>
    /// <param name="of">读取对象：<c>self</c>／<c>viewer</c>。</param>
    /// <param name="path">可读项（由目录的 <c>readables</c> 声明）。</param>
    /// <exception cref="ArgumentNullException">host 为 null。</exception>
    public static object? ReadValue(Card host, string of, string path)
    {
        ArgumentNullException.ThrowIfNull(host);

        var cardBase = host as CardBase;
        var viewer = cardBase?.Owner;

        if (string.Equals(of, "viewer", StringComparison.Ordinal))
        {
            if (viewer is null)
            {
                throw new InvalidOperationException($"read.of=viewer 不可用：宿主 '{host.Name}' 未归属任何玩家。");
            }

            return path switch
            {
                "kredits" => viewer.Points,
                "maxKredits" => viewer.PointSlots,
                "hqHealth" => viewer.HqHealth,
                _ => throw new NotSupportedException(
                    $"read.path '{path}' 在 of=viewer 上没有定义（可用：kredits／maxKredits／hqHealth）。"),
            };
        }

        if (!string.Equals(of, "self", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"read.of '{of}' 超出封闭集：只允许 self／viewer。");
        }

        switch (path)
        {
            case "attack" when cardBase is not null:
                return cardBase.Modifiers.GetEffectiveValue(CardStatFields.Attack);
            case "defense" when cardBase is not null:
                return cardBase.Modifiers.GetEffectiveValue(CardStatFields.Defense);
            case "opCost" when cardBase is not null:
                return cardBase.Definition.OperateCost;
            case "deployCost" when cardBase is not null:
                return cardBase.Definition.DeployCost;
            case "hqHealth" when host is Orc.Game.Players.Hq hq:
                return hq.Health;
            case "name":
                return host.Name;
            case "zone" when host is UnitCard unit && unit.TryGetData<UnitStateData>(out var state):
                return state.Line?.ToString() ?? string.Empty;
            default:
                throw new NotSupportedException(
                    $"read.path '{path}' 在 of=self 上没有定义（可用：attack／defense／opCost／deployCost／hqHealth／name／zone）。");
        }
    }
}
