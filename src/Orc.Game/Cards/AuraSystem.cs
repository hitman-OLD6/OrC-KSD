using Orc.Cards;
using Orc.Game.Board;
using Orc.Game.Players;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// W3-1 G4（持续/条件静态能力）光环系统（声明 + 场级收集面）：
// 贡献节三形态之「光环收集」（他条件）：光环源卡（效果装载时）向场级收集面注册「光环声明」；
// 受益卡每轮链重跑时经「现收集」（GameEnvironment.CollectAuras——过滤〔字段/通用门禁/谓词〕→ 合成）
// 把命中声明的贡献叠加进本轮有效值——受益卡侧零结构增删（收集是链重跑的固定步骤、非卡上条目）。
// 「注册≠生效」：注册完成＝进入收集面读取范围（可被收集可见）；「生效」＝收集过滤命中后、经一轮重跑
// 合成进有效值。通用门禁（宿主在场/存活/未离场）为必经兜底——卡组期注册的声明被拦截（未在场），
// 宿主进场后经全域重跑（环境事件）生效；「失效即消失」两路径均合法：门禁不命中（零增删主路径）
// 或声明注销（效果卸载链托管收口——见 CardEffectLoader 托管清理）。
// 生命周期：同来源的声明整组注册/注销（按来源撤销＝引用相等）；注册/注销幂等（对齐装载链既有口径）；
// 各声明独立参与合成（不合并、不去重——同来源多条/多来源多条均各自独立）；合成稳定序＝收集面登记序。
// 贡献形态：与既有修饰器类型家族同构（纯变换回调——加法型为本批验收形态；更强形态属加性扩展）；
// 字段范围＝既有字段体系（开放集合、机制字段无关）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 光环声明（W3-1 G4）：一条「光环源 → 受益卡字段」的贡献声明（注册进场级收集面 <see cref="AuraRegistry"/>）。
/// 组成：① 宿主 <see cref="Host"/>（源卡——通用门禁依据：在场/存活/未离场）；
/// ② <see cref="Field"/>（贡献目标字段——既有字段体系）；
/// ③ <see cref="Transform"/>（贡献变换——与修饰器链节同构的纯变换；加法型经 <see cref="Add"/> 便捷构造）；
/// ④ <see cref="Source"/>（来源标记——整组注销依据；相等性判据＝引用相等，通常＝注册方效果实例）；
/// ⑤ <see cref="Predicate"/>（受益谓词——附加条件，如「相邻」「源在前线且受益方在源方支援线」；null＝恒真）。
/// 「来源在不在场」由收集步骤的通用门禁（<see cref="IsHostActive"/>）承担——声明谓词不重复实现在场判定。
/// </summary>
public sealed class AuraDeclaration
{
    /// <summary>
    /// 创建光环声明。
    /// </summary>
    /// <param name="host">宿主（源卡——通用门禁依据：已进场/未死亡/未离场；非 null）。</param>
    /// <param name="field">贡献目标字段（非 null/空白；既有字段体系）。</param>
    /// <param name="transform">贡献变换（参数＝受益卡引用＋当前累积值；须为纯变换——不得产生持久副作用；非 null）。</param>
    /// <param name="source">来源标记（整组注销依据；非 null；相等性判据＝引用相等）。</param>
    /// <param name="predicate">受益谓词（可选；null＝恒真——仅受通用门禁约束；形如 <c>(env, beneficiary) =&gt; ...</c>）。</param>
    /// <exception cref="ArgumentNullException">host / transform / source 为 null。</exception>
    /// <exception cref="ArgumentException">field 为 null/空白。</exception>
    public AuraDeclaration(
        Card host,
        string field,
        Func<Card, int, int> transform,
        object source,
        Func<GameEnvironment, Card, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(source);

        Host = host;
        Field = field;
        Transform = transform;
        Source = source;
        Predicate = predicate;
    }

    /// <summary>宿主（源卡——通用门禁依据；声明不改变宿主状态，仅读取其在场事实）。</summary>
    public Card Host { get; }

    /// <summary>贡献目标字段（既有字段体系；与修饰器目标字段同一标识体系、无映射层）。</summary>
    public string Field { get; }

    /// <summary>贡献变换（纯变换——以受益卡引用＋当前累积值为参数；与修饰器链节同构）。</summary>
    public Func<Card, int, int> Transform { get; }

    /// <summary>来源标记（整组注销依据；相等性判据＝引用相等）。</summary>
    public object Source { get; }

    /// <summary>受益谓词（附加条件；null＝恒真）。参数＝（环境查询面、受益卡引用）。</summary>
    public Func<GameEnvironment, Card, bool>? Predicate { get; }

    /// <summary>
    /// 创建加法型光环声明（本批验收形态的便捷构造）：贡献＝受益卡字段当前累积值 + <paramref name="delta"/>。
    /// </summary>
    /// <param name="host">宿主（源卡）。</param>
    /// <param name="field">贡献目标字段。</param>
    /// <param name="delta">增量（可负）。</param>
    /// <param name="source">来源标记。</param>
    /// <param name="predicate">受益谓词（可选；null＝恒真）。</param>
    /// <exception cref="ArgumentNullException">host / source 为 null。</exception>
    /// <exception cref="ArgumentException">field 为 null/空白。</exception>
    public static AuraDeclaration Add(
        Card host, string field, int delta, object source, Func<GameEnvironment, Card, bool>? predicate = null)
        => new(host, field, (_, current) => current + delta, source, predicate);

    /// <summary>
    /// 通用门禁（必经兜底——收集过滤步骤内建）：宿主是否「在场/存活/未离场」。
    /// 单位卡（已单位化）＝未死亡 ∧ 位置非空；HQ 实体＝已入场（占位槽非空——不入死亡链、离场不适用）；
    /// 其余（未单位化——卡组/手牌中）＝false。判定为纯读（不构成任何结构变化——与「零增删」自洽）。
    /// </summary>
    internal bool IsHostActive()
    {
        if (Host is Hq hq)
        {
            return hq.Line is not null;
        }

        if (Host is CardBase card)
        {
            return card.TryGetData<UnitStateData>(out var state)
                && !state.IsDestroyed
                && state.Line is not null;
        }

        return false;
    }
}

/// <summary>
/// 场级收集面（W3-1 G4；单场唯一——随对局装配创建于 <see cref="GameEnvironment"/>，多对局相互独立）：
/// 光环声明的注册/注销结构面（同步、轻量——非数据组件、不进装配/加载/快照语义）＋登记序稳定读取面。
/// 注册时点＝效果装载时（效果装载链驱动）；注销时点＝效果卸载时（效果装载链托管收口——按来源整组撤销）。
/// 注册/注销幂等（对齐装载链既有口径）：同实例重复注册＝无操作；无命中注销＝无操作。
/// 稳定序＝登记序（合成施加序的来源——「确定变换」：同输入同序同输出）。
/// </summary>
public sealed class AuraRegistry
{
    private readonly List<AuraDeclaration> _declarations = new(); // 声明集（登记序——稳定合成序来源）

    /// <summary>声明全量列举（登记序——稳定合成序；集中查询面）。</summary>
    public IReadOnlyList<AuraDeclaration> All => _declarations;

    /// <summary>
    /// 注册光环声明（效果装载时点驱动；幂等：同一实例重复注册＝无操作）。
    /// 注册不触发重跑（「注册≠生效」——生效经后续一轮重跑合成；进场路径由全域重跑传播覆盖）。
    /// </summary>
    /// <returns>新增＝true；已注册（同一实例）＝false 无操作。</returns>
    /// <exception cref="ArgumentNullException">declaration 为 null。</exception>
    public bool Register(AuraDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (_declarations.Contains(declaration))
        {
            return false; // 幂等：同一实例重复注册＝无操作（不重复贡献）
        }

        _declarations.Add(declaration);
        return true;
    }

    /// <summary>
    /// 按来源注销（整组撤销——撤销该来源在收集面登记的全部声明；相等性判据＝引用相等）。
    /// 幂等：该来源无声明＝0、无操作（不抛错）。注销不触发重跑（衔接由调用方/事件驱动）。
    /// </summary>
    /// <returns>撤销条目数（无命中＝0）。</returns>
    /// <exception cref="ArgumentNullException">source 为 null。</exception>
    public int RemoveBySource(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _declarations.RemoveAll(declaration => ReferenceEquals(declaration.Source, source));
    }
}
