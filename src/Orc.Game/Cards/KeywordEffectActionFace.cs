using Orc.Cards;
using Orc.Core;
using Orc.Game.Effects;
using Orc.Game.Effects.HandlerDsl;
using Orc.Game.Targeting;

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
    /// <c>choice</c> 的运行时实现：呈现选项并返回被选项的序号（**0 起**，供生成码的 <c>switch</c> 用）。
    /// <para>
    /// **走既有 targeter 提交通道**（R38／Q3／Q6／Q7）：复用 `option` 选择器
    /// （<see cref="SelectorTemplates.Option"/>，标识集＝选项文案），不新建子系统、不新增选择器类型。
    /// 有桥＝呈现 prompt ＋ N 个选项按钮，点选后返回该选项的 0 起下标；与 <c>ask</c> 共用
    /// <see cref="TargeterManager"/> 的全局 FIFO，同一效果内按调用顺序排队，不串序。
    /// </para>
    /// <para>
    /// **失败＝整条效果中断**（D33）：无桥／玩家取消／非法选择／对局已结束／超时…一律抛
    /// <see cref="InvalidOperationException"/>，消息以 <c>choice-failed[&lt;reason&gt;]:</c> 开头（D36）。
    /// <b>不返回哨兵、不选首项、不静默跳过</b>——<c>switch</c> 没有 default 兜底，错误下标＝生效到错误分支上
    /// （「宁可炸，不可错」）。因为没有 fallback（Q4），这里的失败**不降级**：<c>choice</c> 抛错 ⇒ 当前
    /// <c>order.cast</c> 中断 ⇒ <c>order.finalize</c> 不执行 ⇒ **这张牌仍在手牌区、未扣费**；同一次
    /// <c>card.played</c> 广播里排在其后的其它卡的边也被跳过（连坐，属信号广播 fail-fast 的既有性质，
    /// 非本条引入、本条也不消除）。本方法**不吞异常**（与 <c>AskAsync</c> 的 catch 形态刻意不同：
    /// 那是为了走 fallback，这里没有 fallback 可走）。
    /// </para>
    /// <para>
    /// **<paramref name="ct"/> 取消＝<see cref="OperationCanceledException"/> 原样穿透**，绝不折成
    /// <see cref="InvalidOperationException"/>——「玩家取消选择」与「对局取消」是两回事（Q3）。
    /// 已知边界：<c>ct</c> 不传给 <c>RunAsync</c>（后者无 <c>ct</c> 形参），故等待中取消**不会提前中断**，
    /// 最长到桥超时（MCP 桥默认 10 分钟）才以取消收场；但**进入通道前**会先
    /// <see cref="CancellationToken.ThrowIfCancellationRequested"/>——已取消的对局不呈现选项。
    /// </para>
    /// </summary>
    /// <param name="host">宿主卡（通道由其归属玩家的 targeter 管理器提供）。</param>
    /// <param name="prompt">提示文案（R38 Q7：经 <see cref="SelectorParameter.Prompt"/> 呈现给裁决者）。</param>
    /// <param name="labels">选项文案（按顺序；非空且唯一由校验期保证，R38 Q1）。</param>
    /// <param name="ct">取消令牌（对局取消＝原样上抛 <see cref="OperationCanceledException"/>）。</param>
    /// <returns>被选项的 0 起下标（生成码 <c>switch</c> 的分支号）。</returns>
    /// <exception cref="ArgumentNullException">host 或 labels 为 null。</exception>
    /// <exception cref="InvalidOperationException">未取得裁决（无通道／取消／非法选择／失败／终局）——整条效果中断。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 已取消。</exception>
    /// <remarks>
    /// **本方法在排除清单里**（只由生成码调用、不进目录），所以「choice 是预览态」不能标在这里——
    /// 扫描器根本扫不到它。该事实登记在 <c>HandlerDslContract.PreviewOps</c>（R36；R38 已撤销该条）。
    /// </remarks>
    public static async Task<int> ChooseAsync(
        Card host, string prompt, string[] labels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(labels);

        // 已取消的对局不该再向玩家呈现选项（否则会弹一个点了也没用的面板，且要等桥超时才收场）。
        // 这是唯一一处主动检查 ct——它只挡「进来时就已经取消」，不改变「等待中被取消」的形态。
        ct.ThrowIfCancellationRequested();

        var owner = OwnerOf(host);
        var targeterManager = owner?.TargeterManager;
        if (targeterManager?.Bridge is null)
        {
            // 与 ask 的无通道口径一致：明确失败（引擎绝不代选）。不 catch、不兜底。
            throw ChoiceFailed(
                ReasonName(TargeterFailureReason.BridgeNotAssembled), detail: null, prompt, labels.Length);
        }

        string? picked = null;
        var result = await targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Option,
                new IdentifierSetParameter(labels, 1, 1)
                {
                    Owner = owner,
                    // 与卡效果选靶同类别（这是一次由卡效果发起的交互，不是指挥动作）。
                    Action = SelectorActions.Effect,
                    Prompt = prompt,
                }).ConfigureAwait(false);

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                // 不重试（D34）：`InvalidSelection` 直接映射为失败，不静默取首项。
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            picked = step.Value;
            return TargeterResult.Ok();
        }).ConfigureAwait(false);

        // D36：对局取消必须原样穿透，且判定在**因子映射之前**——
        // `TargeterManager.RunLoopAsync` 的 catch(Exception) 会把 ct 取消折成 Failed(Other)，
        // 不前置判断就会把「对局取消」伪装成「效果故障」，与 Q3 刻意区分两者的理由正好相反。
        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (result.IsOk && picked is not null)
        {
            var index = Array.IndexOf(labels, picked);
            if (index < 0)
            {
                // 选择器回传了不在候选文案集内的标识：属**异常路径**（抛），不得记一条 picked=0。
                throw new InvalidOperationException(
                    $"choice-failed[{ReasonName(TargeterFailureReason.Fault)}]: "
                    + $"选择器回传的标识 '{picked}' 不在本次选项文案集内，无法定位分支"
                    + $"（prompt=\"{prompt}\"，选项数={labels.Length}）；效果中断。");
            }

            // R38 Q5／D35：只记成功裁决；picked 为 **1 起**（与 target.submit 的 picks 同口径）。
            // 落账在 `host.Engine`（与 RandomSelections 同址）——动作面类不该知道运行时内部持有谁（D34）。
            host.Engine.ChoiceSelections.Add(
                new ChoiceSelectionRecord(host.Name, prompt, labels.ToArray(), index + 1, picked));

            return index;
        }

        if (result.IsCancelled)
        {
            // D36⑤：FailAll（房间轮换／终局）与玩家取消在此不可区分——接受同形，但**消息必须中性**。
            throw ChoiceFailed("cancelled", "可能来自玩家取消或对局收束（要分辨来源请查 TargetingTrace）",
                prompt, labels.Length);
        }

        throw ChoiceFailed(
            ReasonName(result.Reason ?? TargeterFailureReason.Other), result.Detail, prompt, labels.Length);
    }

    /// <summary>
    /// `choice` 失败的统一落点（D36）：**类型只有 <see cref="InvalidOperationException"/> 一种**，排障靠消息。
    /// <para>
    /// 对外只有一种语义——「这个节点没拿到裁决、效果必须中断」；类型分化会逼每个调用方维护一张 catch 表，
    /// 而它们对每种因子的处理动作完全相同。
    /// </para>
    /// </summary>
    private static InvalidOperationException ChoiceFailed(
        string reason, string? detail, string prompt, int labelCount)
    {
        var message = $"choice-failed[{reason}]: 未取得裁决，整条效果中断"
            + $"（prompt=\"{prompt}\"，选项数={labelCount}）";

        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += $"，detail={detail}";
        }

        return new InvalidOperationException(message);
    }

    /// <summary>
    /// 失败因子 → 消息里的稳定词（D36②：`<reason>`＝枚举名小写连字符形态）。
    /// <para>
    /// **七个因子全部显式列支**（含本路径当前不可达的 <see cref="TargeterFailureReason.RetryLimitExceeded"/>／
    /// <see cref="TargeterFailureReason.NoAvailableCandidates"/>）：「不可达」是实现判断、不是
    /// <see cref="TargeterFailureReason"/> 的契约——显式列支保证 targeter 语义将来变化时至少有一行要改，
    /// 而不是静默落进兜底。`_ =>` 兜底保留，但归入 <c>other</c> 并附枚举原值。
    /// </para>
    /// </summary>
    private static string ReasonName(TargeterFailureReason reason) => reason switch
    {
        TargeterFailureReason.InvalidSelection => "invalid-selection",
        TargeterFailureReason.RetryLimitExceeded => "retry-limit-exceeded",
        TargeterFailureReason.NoAvailableCandidates => "no-available-candidates",
        TargeterFailureReason.BridgeNotAssembled => "bridge-not-assembled",
        TargeterFailureReason.GameEnded => "game-ended",
        TargeterFailureReason.Fault => "fault",
        TargeterFailureReason.Other => "other",
        _ => $"other({reason})",
    };

    /// <summary>
    /// 卡 → 归属玩家（本类自带一份 5 行 switch；**不复用** <see cref="EffectRuntime"/> 的私有同名面——
    /// 跨类取用要么提升可见性〔＝动冻结面〕，要么绕道，两条都不划算，D34）。
    /// </summary>
    private static Orc.Game.Players.Player? OwnerOf(Card card) => card switch
    {
        Orc.Game.Players.Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };

    /// <summary>
    /// <c>unitsOf</c> 节点的运行时实现：列出某阵营在某区域的单位。
    /// <para>
    /// **存在的理由**：<c>iterate.over</c> 此前只能来自 <c>ask</c>（必须弹窗问玩家）或视图里恰好声明了
    /// 实体列表，于是「每个我方单位 +1 攻击」这种最基础的 DIY 写法根本表达不出来——
    /// 界面怎么设计都造不出一个不存在的原语。
    /// </para>
    /// <para>
    /// 标 <see cref="HandlerDslPureAttribute"/>：它只读世界状态、不改任何东西。默认规则会把
    /// 一切 <c>Task</c> 方法判成有副作用，那会让前端以为这是个动作。
    /// </para>
    /// </summary>
    /// <param name="host">宿主卡（隐式宿主；用于解析运行时与阵营归属）。</param>
    /// <param name="side">阵营：<c>self</c>／<c>enemy</c>／<c>any</c>。</param>
    /// <param name="zone">区域过滤（省略＝不限）。</param>
    /// <param name="ct">取消令牌。</param>
    [HandlerDslNode("unitsOf")]
    [HandlerDslPure]
    public static async Task<IReadOnlyList<Card>> UnitsOfAsync(
        Card host,
        [HandlerDslEnumValues("self", "enemy", "any")] string side,
        string? zone = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (EffectRuntime.ResolveFor(host) is not { } runtime)
        {
            return Array.Empty<Card>();
        }

        // `any` 与省略同义：EffectSelector 里 side 为空就是「不限定阵营」。
        var normalizedSide = string.IsNullOrEmpty(side) || string.Equals(side, "any", StringComparison.Ordinal)
            ? null
            : side;

        return await runtime.SelectAsync(host, new EffectSelector("all", normalizedSide, zone), ct)
            .ConfigureAwait(false);
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
