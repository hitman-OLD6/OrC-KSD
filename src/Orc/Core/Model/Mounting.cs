namespace Orc.Core.Model;

/// <summary>
/// 挂载声明（设计端编排通道，R1b）：把**自身的一个成员**挂到目标成员上。
/// <para>
/// 只表达"顺序与组合"：<see cref="Phase"/> 必须为 null——挂载声明携带相位（如 <c>replace</c>）＝两条通道混用，
/// 装载期拒绝（R1b／A-2：混用的装载期判定只看声明形态）。
/// </para>
/// </summary>
public sealed class MountDeclaration
{
    /// <summary>创建挂载声明。</summary>
    /// <param name="target">目标地址（<c>作用域#成员名</c>；装载期解析）。</param>
    /// <param name="member">被挂载的自身成员名。</param>
    /// <param name="phase">相位（仅用于识别混用；挂载声明必须为 null）。</param>
    /// <param name="source">边来源（默认作者显式声明）。</param>
    /// <exception cref="ArgumentException">target／member 为空或纯空白。</exception>
    public MountDeclaration(string target, string member, string? phase = null, EdgeSource source = EdgeSource.Explicit)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("挂载目标不能为空（形态＝作用域#成员名）。", nameof(target));
        }

        if (string.IsNullOrWhiteSpace(member))
        {
            throw new ArgumentException("挂载声明的成员名不能为空。", nameof(member));
        }

        Target = target;
        Member = member;
        Phase = phase;
        Source = source;
    }

    /// <summary>目标地址原文（<c>作用域#成员名</c>）。</summary>
    public string Target { get; }

    /// <summary>被挂载的自身成员名。</summary>
    public string Member { get; }

    /// <summary>相位（挂载通道禁止；非 null＝装载期拒绝）。</summary>
    public string? Phase { get; }

    /// <summary>边来源。</summary>
    public EdgeSource Source { get; }
}

/// <summary>
/// 包装目标（S2a）：包装的对象是**成员**（包裹该成员的整体执行），不是某一条边——
/// 因此**不需要**"成员＋来源"的二级地址；寻址仍只用 <c>作用域#成员名</c>。
/// <para>
/// 判定器包装器与本类型的成员目标**共用同一相位语义与同一注册面**（<see cref="WrapRegistry"/>），
/// 差别只在作用对象（成员 vs 判定器）。
/// </para>
/// </summary>
public readonly struct WrapTarget : IEquatable<WrapTarget>
{
    private WrapTarget(bool isMember, MemberAddress address, string? judicator)
    {
        IsMember = isMember;
        Address = address;
        Judicator = judicator;
    }

    /// <summary>目标是否为成员（false＝判定器）。</summary>
    public bool IsMember { get; }

    /// <summary>成员地址（<see cref="IsMember"/> 为 true 时有效）。</summary>
    public MemberAddress Address { get; }

    /// <summary>判定器名（<see cref="IsMember"/> 为 false 时有效）。</summary>
    public string? Judicator { get; }

    /// <summary>成员包装目标。</summary>
    public static WrapTarget ForMember(MemberAddress address) => new(true, address, null);

    /// <summary>判定器包装目标（判定器包装器同属一套相位语义）。</summary>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    public static WrapTarget ForJudicator(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new WrapTarget(false, default, name);
    }

    /// <summary>稳定键（审查导出／字典键用）。</summary>
    public string Key => IsMember ? $"member:{Address}" : $"judicator:{Judicator}";

    /// <inheritdoc />
    public bool Equals(WrapTarget other)
        => IsMember == other.IsMember && Address.Equals(other.Address)
           && string.Equals(Judicator, other.Judicator, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WrapTarget other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsMember, Address, Judicator is null ? 0 : StringComparer.Ordinal.GetHashCode(Judicator));

    /// <summary>相等运算符。</summary>
    public static bool operator ==(WrapTarget left, WrapTarget right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(WrapTarget left, WrapTarget right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Key;
}

/// <summary>
/// 包装声明（元效果端修改通道，R1b／R1i）：在目标成员的**外围**改写或拦截。
/// <para>
/// 相位语义（S2a／R1i 修订 2026-10-26）：<c>before</c>（改写输入，**FIFO**）／<c>after</c>（尾部副作用，**FIFO**）／
/// <c>replace</c>（替代整个边集合，**最后声明者生效**）。三者先后一律按声明序推出，不引入优先级数字。
/// </para>
/// <para>包装不得引入新的编排步骤（那是挂载的事）：本声明只携带目标＋相位＋包装器资产。</para>
/// </summary>
public sealed class WrapDeclaration
{
    /// <summary>创建包装声明。</summary>
    /// <param name="target">包装目标。</param>
    /// <param name="phase">相位。</param>
    /// <param name="handlerId">包装器 handler 资产 id。</param>
    /// <exception cref="ArgumentException">handlerId 为空或纯空白。</exception>
    public WrapDeclaration(WrapTarget target, WrapperPhase phase, string handlerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        Target = target;
        Phase = phase;
        HandlerId = handlerId;
    }

    /// <summary>包装目标。</summary>
    public WrapTarget Target { get; }

    /// <summary>相位。</summary>
    public WrapperPhase Phase { get; }

    /// <summary>包装器 handler 资产 id。</summary>
    public string HandlerId { get; }

    /// <summary>便捷构造：包装成员。</summary>
    public static WrapDeclaration ForMember(string memberAddress, WrapperPhase phase, string handlerId)
        => new(WrapTarget.ForMember(MemberAddress.Parse(memberAddress)), phase, handlerId);
}

/// <summary>
/// 挂载边（R1i／R1g）：某成员的执行序列里的一条外来边。
/// <para>
/// 边指向**一个成员**（不是裸 handler）——被挂载的成员保留自己的视图与合法性检验（R1）；
/// 执行时 owner 回填为 <see cref="Declaring"/>（它是被挂载成员所属的实体）。
/// </para>
/// </summary>
public sealed class MountEdge
{
    /// <summary>创建边。</summary>
    /// <exception cref="ArgumentNullException">declaring／member 为 null。</exception>
    public MountEdge(
        MemberAddress target,
        OrchestrationEntity declaring,
        MemberDefinition member,
        EdgeSource source,
        EdgeOrderSource orderSource,
        DeclarationOrder order,
        long sequence,
        string? declaration = null)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(member);

        Target = target;
        Declaring = declaring;
        Member = member;
        Source = source;
        OrderSource = orderSource;
        Order = order;
        Sequence = sequence;
        Declaration = declaration ?? target.ToString();
    }

    /// <summary>目标成员地址（这条边挂在谁身上；**边表视角**的地址，可能是 <c>self#…</c>）。</summary>
    public MemberAddress Target { get; }

    /// <summary>
    /// 声明原文地址（<c>作用域#成员名</c>，如 <c>host#targeting</c>）。
    /// <para>
    /// 与 <see cref="Target"/> 的区别（批次 2 遗留限制的修复）：边表按宿主实体的自身视角建表，
    /// 因此表地址恒为 <c>self#…</c>；若只保留表地址，<c>host#</c> 声明就无法还原成作者写下的原文。
    /// 审查面导出"这条边从哪条声明来"时以本属性为准（S10）。
    /// </para>
    /// </summary>
    public string Declaration { get; }

    /// <summary>被挂载成员所属实体（owner 回填目标）。</summary>
    public OrchestrationEntity Declaring { get; }

    /// <summary>被挂载的成员（保留自己的视图与合法性检验）。</summary>
    public MemberDefinition Member { get; }

    /// <summary>边来源（<c>explicit</c>／<c>injected</c>／<c>framework:disposal</c>／<c>framework:one-shot</c>）。</summary>
    public EdgeSource Source { get; }

    /// <summary>位次来源（<c>declared</c>／<c>appended</c>；**边级**属性，R1h）。</summary>
    public EdgeOrderSource OrderSource { get; }

    /// <summary>声明序（仅 <see cref="EdgeOrderSource.Declared"/> 边参与比较）。</summary>
    public DeclarationOrder Order { get; }

    /// <summary>建边序（同声明序时的稳定兜底；也是撤销句柄的身份）。</summary>
    public long Sequence { get; }
}

/// <summary>
/// 边的撤销句柄：**句柄即权限**（R1f：撤销按句柄、不按来源扫表——
/// 多组件并发卸载时只有句柄能区分"谁该撤哪条"，扫 owner 无法区分）。
/// </summary>
public sealed class MountHandle
{
    internal MountHandle(MemberEdgeTable table, MountEdge edge)
    {
        Table = table;
        Edge = edge;
    }

    internal MemberEdgeTable Table { get; }

    /// <summary>句柄对应的边。</summary>
    public MountEdge Edge { get; }

    /// <summary>是否已撤销。</summary>
    public bool IsRevoked { get; private set; }

    /// <summary>撤销（幂等）。</summary>
    public bool Revoke()
    {
        if (IsRevoked)
        {
            return false;
        }

        if (!Table.Detach(this))
        {
            IsRevoked = true;
            return false;
        }

        IsRevoked = true;
        return true;
    }
}

/// <summary>
/// 单个成员上的边表（R1i／R1g）：执行序＝**声明序边**（按 <see cref="MountEdge.Order"/> 有序排列）
/// → **追加序边**（运行期挂边，按建边序）。
/// <para>
/// 有序插入是硬要求（R1g）：<c>placed</c> 批的边若其组件声明序更靠前，必须插到已存在的 <c>loaded</c> 边之前；
/// 重建实例产生的边必须回填原位。因此本表**不采用"只追加"实现**——快照时按 (声明序, 建边序) 全排序。
/// </para>
/// <para>框架注销边（<c>framework:disposal</c>）的声明序＝该组件的"最后一条边"（<see cref="DeclarationOrder.FrameworkLast"/>）。</para>
/// </summary>
public sealed class MemberEdgeTable
{
    private readonly List<MountEdge> _edges = new();
    private long _sequence;

    /// <summary>创建边表。</summary>
    public MemberEdgeTable(MemberAddress address)
    {
        Address = address;
    }

    /// <summary>本表所属成员地址。</summary>
    public MemberAddress Address { get; }

    /// <summary>边数。</summary>
    public int Count => _edges.Count;

    /// <summary>挂一条边并返回撤销句柄。</summary>
    /// <exception cref="ArgumentNullException">edge 为 null。</exception>
    public MountHandle Attach(
        OrchestrationEntity declaring,
        MemberDefinition member,
        EdgeSource source = EdgeSource.Explicit,
        EdgeOrderSource orderSource = EdgeOrderSource.Declared,
        DeclarationOrder order = default,
        string? declaration = null)
    {
        var edge = new MountEdge(Address, declaring, member, source, orderSource, order, _sequence++, declaration);
        _edges.Add(edge);
        return new MountHandle(this, edge);
    }

    /// <summary>撤销一条边（句柄不属本表＝false，幂等）。</summary>
    public bool Detach(MountHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!ReferenceEquals(handle.Table, this))
        {
            return false;
        }

        var index = _edges.FindIndex(e => ReferenceEquals(e, handle.Edge));
        if (index < 0)
        {
            return false;
        }

        _edges.RemoveAt(index);
        return true;
    }

    /// <summary>执行序快照：声明序边（按声明序→建边序）在前，追加序边（按建边序）在后。</summary>
    public IReadOnlyList<MountEdge> Snapshot()
    {
        var declared = _edges
            .Where(static e => e.OrderSource == EdgeOrderSource.Declared)
            .OrderBy(static e => e.Order)
            .ThenBy(static e => e.Sequence);
        var appended = _edges
            .Where(static e => e.OrderSource == EdgeOrderSource.Appended)
            .OrderBy(static e => e.Sequence);
        return declared.Concat(appended).ToArray();
    }

    /// <summary>全部边的登记序快照（审查导出用；不表达执行序）。</summary>
    public IReadOnlyList<MountEdge> All => _edges;
}

/// <summary>包装器的一条登记项。</summary>
public sealed class WrapEntry
{
    internal WrapEntry(
        WrapTarget target,
        WrapperPhase phase,
        HandlerAsset handler,
        DeclarationOrder order,
        long sequence,
        OrchestrationEntity? declaring = null)
    {
        Target = target;
        Phase = phase;
        Handler = handler;
        Order = order;
        Sequence = sequence;
        Declaring = declaring;
    }

    /// <summary>包装目标。</summary>
    public WrapTarget Target { get; }

    /// <summary>相位。</summary>
    public WrapperPhase Phase { get; }

    /// <summary>包装器资产。</summary>
    public HandlerAsset Handler { get; }

    /// <summary>声明序。</summary>
    public DeclarationOrder Order { get; }

    /// <summary>登记序（同声明序时的稳定兜底）。</summary>
    public long Sequence { get; }

    /// <summary>
    /// 声明该包装的实体（组件实例；null＝框架/测试直接登记的匿名包装）。
    /// <para>
    /// 执行时 owner／host **回填为该实体**（R4）：旧 <c>injects</c> 注入的 handler 看到的 <c>view.Host</c> 是
    /// "哪张卡注册了我"，而不是被执行成员的宿主。若按被执行成员绑定，引擎成员上的包装会看到 <c>Host = null</c>，
    /// 与旧行为不等价（S11 迁移的行为等价性依赖这一条）。
    /// </para>
    /// </summary>
    public OrchestrationEntity? Declaring { get; }
}

/// <summary>包装器撤销句柄（句柄即权限）。</summary>
public sealed class WrapHandle
{
    internal WrapHandle(WrapEntry entry)
    {
        Entry = entry;
    }

    internal WrapEntry Entry { get; }

    /// <summary>是否已撤销。</summary>
    public bool IsRevoked { get; private set; }

    internal void MarkRevoked() => IsRevoked = true;
}

/// <summary>
/// 包装注册面（S2a）：**成员包装器与判定器包装器共用**的同一注册面（R1i）。
/// <para>
/// 执行序语义（按目标聚合）：<c>before</c> 按声明序正序（**FIFO**，R1i 修订）／<c>after</c> 按声明序（FIFO）／
/// <c>replace</c> 取**最后声明者**。不引入优先级数字。
/// </para>
/// <para>
/// <c>before</c> 由 LIFO 改 FIFO 的理由（D3 裁决）：旧 <c>Initiate</c>／<c>Announce</c> 段内是
/// <c>priority</c> **升序**（减伤叠加的钳制点依赖它）；LIFO 会让 "−1" 与 "封顶 10" 的次序反转。
/// 装饰器嵌套语义（LIFO 的唯一好处）在 Orc 里没有真实用例。
/// </para>
/// </summary>
public sealed class WrapRegistry
{
    private readonly Dictionary<WrapTarget, List<WrapEntry>> _wraps = new();
    private long _sequence;

    /// <summary>登记一条包装。</summary>
    /// <exception cref="ArgumentNullException">declaration／handler 为 null。</exception>
    public WrapHandle Register(WrapDeclaration declaration, HandlerAsset handler, DeclarationOrder order = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return Register(declaration.Target, declaration.Phase, handler, order);
    }

    /// <summary>
    /// 按**归一化后的目标**登记一条包装（框架内部）：执行期以目标实体自身的地址
    /// （<c>engine#…</c>／<c>self#…</c>）比对包装键，因此 <c>host#…</c> 声明必须先归一化为
    /// 宿主实体的地址，否则该包装永远匹配不上（静默不生效——本方法存在的唯一理由）。
    /// </summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public WrapHandle Register(
        WrapTarget target,
        WrapperPhase phase,
        HandlerAsset handler,
        DeclarationOrder order = default,
        OrchestrationEntity? declaring = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var entry = new WrapEntry(target, phase, handler, order, _sequence++, declaring);
        if (!_wraps.TryGetValue(target, out var list))
        {
            list = new List<WrapEntry>();
            _wraps[target] = list;
        }

        list.Add(entry);
        return new WrapHandle(entry);
    }

    /// <summary>
    /// 登记一条**内联**判定器包装器（无 handler 资产）：供运行期注入（旧 <c>judicatorWrappers</c> 的落地路径）
    /// 与测试夹具使用。相位语义与资产声明的包装器完全相同（同一注册面，R1i）。
    /// </summary>
    /// <param name="judge">同步改写体（<c>(args, result) → result</c>）。</param>
    /// <param name="declaring">
    /// 声明实体（可选）：非 null 时执行会把它（或其宿主的 <see cref="OrchestrationEntity.Bound"/>）追加到载荷末位。
    /// </param>
    /// <exception cref="ArgumentNullException">judge 为 null。</exception>
    public WrapHandle RegisterJudge(
        WrapTarget target,
        WrapperPhase phase,
        Func<object[]?, object[]?, object[]?> judge,
        DeclarationOrder order = default,
        OrchestrationEntity? declaring = null)
    {
        ArgumentNullException.ThrowIfNull(judge);

        var asset = new HandlerAsset(
            $"inline-judge:{_sequence}",
            (_, _, _) => throw new InvalidOperationException(
                "内联判定器包装器只能按判定器包装器调用（同步签名），不能按成员 handler 调用。"),
            judge: judge);
        return Register(target, phase, asset, order, declaring);
    }

    /// <summary>撤销一条包装（幂等）。</summary>
    public bool Revoke(WrapHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsRevoked)
        {
            return false;
        }

        if (_wraps.TryGetValue(handle.Entry.Target, out var list) && list.Remove(handle.Entry))
        {
            handle.MarkRevoked();
            return true;
        }

        return false;
    }

    /// <summary>某目标上的全部包装项（按相位过滤；声明序→登记序）。</summary>
    public IReadOnlyList<WrapEntry> Entries(WrapTarget target, WrapperPhase? phase = null)
    {
        if (!_wraps.TryGetValue(target, out var list))
        {
            return Array.Empty<WrapEntry>();
        }

        return list
            .Where(e => phase is null || e.Phase == phase)
            .OrderBy(static e => e.Order)
            .ThenBy(static e => e.Sequence)
            .ToArray();
    }

    /// <summary><c>before</c> 包装器执行序（**FIFO**：声明序正序；R1i 修订）。</summary>
    public IReadOnlyList<WrapEntry> BeforeExecutionOrder(WrapTarget target)
        => Entries(target, WrapperPhase.Before);

    /// <summary><c>replace</c> 生效项（最后声明者；无＝null）。</summary>
    public WrapEntry? LastReplace(WrapTarget target)
    {
        var entries = Entries(target, WrapperPhase.Replace);
        return entries.Count == 0 ? null : entries[^1];
    }

    /// <summary>是否登记了任何包装（审查导出用）。</summary>
    public bool IsEmpty => _wraps.Values.All(static l => l.Count == 0);

    /// <summary>全部包装项（审查导出用；按声明序）。</summary>
    public IReadOnlyList<WrapEntry> All
        => _wraps.Values.SelectMany(static l => l)
            .OrderBy(static e => e.Order)
            .ThenBy(static e => e.Sequence)
            .ToArray();
}
