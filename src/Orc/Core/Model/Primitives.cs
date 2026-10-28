namespace Orc.Core.Model;

/// <summary>
/// 实体类别（R6b／R3c）：引擎／卡牌／组件／handler 四类实体。
/// 说明：类别只表达"这个实体是什么"，**不表达主动性**——旧模型的「主动／被动」二分已在 R1 中被抛弃，
/// 因此本枚举与任何"是否参与挂载"的判定无关（挂载能力由成员可见性位标志表达，见 <see cref="MemberVisibility"/>）。
/// </summary>
public enum EntityKind
{
    /// <summary>引擎实体（对局级单例，承载全局流程成员，寻址根为 <c>engine</c>）。</summary>
    Engine,

    /// <summary>卡牌实体（可组合实体：可携带组件实体）。</summary>
    Card,

    /// <summary>组件实体（效果＝组件实体的成员 handler 集；自托管装配与注销）。</summary>
    Component,

    /// <summary>handler 实体（全局懒注册的可执行节点；不是每实例对象）。</summary>
    Handler,
}

/// <summary>
/// 成员可见性（R5：位标志、正交组合、默认全关）。
/// <para>
/// 三种能力互不干扰，可任意组合：
/// <list type="bullet">
/// <item><see cref="Callable"/>：可被（跨实体的）调用方直接调用；</item>
/// <item><see cref="Hostable"/>：可作宿主——允许外来挂载边挂到本成员上；</item>
/// <item><see cref="Mountable"/>：可被挂载——允许本成员作为一条边挂到别的成员上。</item>
/// </list>
/// 默认 <see cref="None"/>（私有）；继承时子预制体只能收紧、不能放宽（R5）。
/// 可见性只约束**跨实体**挂载：实体自己的初始化装配不受约束（<c>self#</c> 自挂载恒成立，R5 补充）。
/// </para>
/// </summary>
[Flags]
public enum MemberVisibility
{
    /// <summary>全关（私有，默认）。</summary>
    None = 0,

    /// <summary>可被调用。</summary>
    Callable = 1,

    /// <summary>可作宿主（可挂载他人的 handler／成员）。</summary>
    Hostable = 2,

    /// <summary>可被挂载（可挂到他人的成员上）。</summary>
    Mountable = 4,

    /// <summary>三种能力全开。</summary>
    All = Callable | Hostable | Mountable,
}

/// <summary>
/// 挂载边的来源（S10 边来源枚举的模型侧定义，R7／R1f／R3k）：
/// 任何一条边都必须能归入四类之一，**不存在"无来源"**——框架合成边同样必须留痕。
/// </summary>
public enum EdgeSource
{
    /// <summary>作者在预制体里显式声明。</summary>
    Explicit,

    /// <summary>元效果端注入（旧 <c>injects</c> 的外来挂载项迁移而来）。</summary>
    Injected,

    /// <summary>框架合成的注销边（<c>framework:disposal</c>，R1f）。</summary>
    FrameworkDisposal,

    /// <summary>框架合成的一次性注销边（<c>framework:one-shot</c>，R3k）。</summary>
    FrameworkOneShot,
}

/// <summary>
/// 边的位次来源（R1g／R1h；**边级属性**，不是信号或实体的属性）：
/// <see cref="Declared"/>＝按静态声明序定位（支持有序插入／回填原位）；
/// <see cref="Appended"/>＝运行期挂边，追加到末尾（建边序）。
/// </summary>
public enum EdgeOrderSource
{
    /// <summary>声明序（宿主预制体的组件声明序 + 组件内挂载声明序）。</summary>
    Declared,

    /// <summary>追加序（运行期挂边；同一成员上恒排在被声明边之后）。</summary>
    Appended,
}

/// <summary>
/// 成员寻址的作用域（R1d：<c>作用域#成员名</c> 的第一个 <c>#</c> 之前是作用域）。
/// </summary>
public enum AddressScope
{
    /// <summary>引擎实体（对局级单例，寻址根固定为 <c>engine</c>）。</summary>
    Engine,

    /// <summary>宿主实体（组件所附着的卡牌／HQ；按当前宿主实例解析）。</summary>
    Host,

    /// <summary>组件自身。</summary>
    Self,
}

/// <summary>
/// 宿主种类（R3h）：卡牌实体的四种类型预制体覆盖的宿主类型，以及组件 <c>requires-host</c> 的取值域。
/// </summary>
[Flags]
public enum HostKind
{
    /// <summary>非宿主类型（该预制体不描述宿主）。</summary>
    None = 0,

    /// <summary>单位卡。</summary>
    Unit = 1,

    /// <summary>指令卡。</summary>
    Order = 2,

    /// <summary>反制卡。</summary>
    Counter = 4,

    /// <summary>HQ。</summary>
    Hq = 8,

    /// <summary>全部宿主类型（<c>any</c>）。</summary>
    Any = Unit | Order | Counter | Hq,
}

/// <summary>
/// 信号的时机相位（S3 第 4c 条／R1g）：只用于判定"挂载是否可能生效"，**不是执行序依据**。
/// <para>
/// <see cref="Load"/>＝加载期信号（如 <c>engine#card.load</c>）：<c>active-when: placed</c> 的组件在该信号触发时尚未装配，
/// 挂上去永不生效——装载期拒绝。<see cref="Placed"/>＝入场期信号；<see cref="Any"/>＝与装配批次无关的时机。
/// </para>
/// <para>
/// 与 <see cref="Orc.Core.Model.MemberDefinition.SignalTiming"/> 的关系：非信号成员该值为 null；
/// 本枚举**不引入任何优先级或次序语义**（次序只由声明序／追加序推出，R1g）。
/// </para>
/// </summary>
public enum SignalTiming
{
    /// <summary>与装配批次无关（大多数信号）。</summary>
    Any,

    /// <summary>加载期信号：只有 <c>active-when: loaded</c> 的组件才可能赶上。</summary>
    Load,

    /// <summary>入场期信号：<c>active-when: placed</c> 的组件正常参与。</summary>
    Placed,
}

/// <summary>
/// 组件装配时点（R3i／S8：预制体字段 <c>active-when</c>）：缺省 <see cref="Placed"/>，
/// 且该缺省必须在预制体声明层**显式落值**（不靠 C# 默认参数隐式决定，S8 第 13 条）。
/// <para>本批（S3）只用到它做"无意义挂载＝装载期拒绝"的判定（S3 第 4c 条）；装配时点本身属 S8。</para>
/// </summary>
public enum ActiveWhen
{
    /// <summary>宿主加载后即装配（"手牌中即生效"类效果）。</summary>
    Loaded,

    /// <summary>宿主入场后才装配（缺省）。</summary>
    Placed,
}

/// <summary>
/// 静态声明序（R1g 的唯一次序依据）：组件声明序 → 成员／挂载声明序 → 边声明序。
/// <para>
/// **装配批次不影响顺序**：<c>loaded</c>／<c>placed</c> 两批混排时仍按本结构比较，
/// 因此边表必须支持"按声明序有序插入"，而不是只支持追加。
/// </para>
/// <para>
/// 框架合成边（注销兜底／一次性）的位次＝该组件的"最后一条边"（R1f）：
/// 由 <see cref="FrameworkLast"/> 构造——同组件段内恒排在作者边之后。
/// </para>
/// </summary>
public readonly struct DeclarationOrder : IComparable<DeclarationOrder>, IEquatable<DeclarationOrder>
{
    /// <summary>创建声明序。</summary>
    public DeclarationOrder(int component, int member, int edge)
    {
        Component = component;
        Member = member;
        Edge = edge;
    }

    /// <summary>宿主预制体里组件的声明序（引擎／卡牌自身成员＝0，组件从 1 起）。</summary>
    public int Component { get; }

    /// <summary>组件内成员（挂载声明）的声明序。</summary>
    public int Member { get; }

    /// <summary>同一成员内边／包装器的声明序。</summary>
    public int Edge { get; }

    /// <summary>框架合成边（注销兜底／一次性）的位次＝该组件的最后一个挂载项（R1f）。</summary>
    /// <param name="component">组件声明序。</param>
    public static DeclarationOrder FrameworkLast(int component) => new(component, int.MaxValue, int.MaxValue);

    /// <summary>按（组件，成员，边）字典序比较。</summary>
    public int CompareTo(DeclarationOrder other)
    {
        var c = Component.CompareTo(other.Component);
        if (c != 0)
        {
            return c;
        }

        c = Member.CompareTo(other.Member);
        return c != 0 ? c : Edge.CompareTo(other.Edge);
    }

    /// <inheritdoc />
    public bool Equals(DeclarationOrder other)
        => Component == other.Component && Member == other.Member && Edge == other.Edge;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeclarationOrder other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Component, Member, Edge);

    /// <summary>字典序比较运算符。</summary>
    public static bool operator <(DeclarationOrder left, DeclarationOrder right) => left.CompareTo(right) < 0;

    /// <summary>字典序比较运算符。</summary>
    public static bool operator >(DeclarationOrder left, DeclarationOrder right) => left.CompareTo(right) > 0;

    /// <summary>字典序比较运算符。</summary>
    public static bool operator <=(DeclarationOrder left, DeclarationOrder right) => left.CompareTo(right) <= 0;

    /// <summary>字典序比较运算符。</summary>
    public static bool operator >=(DeclarationOrder left, DeclarationOrder right) => left.CompareTo(right) >= 0;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(DeclarationOrder left, DeclarationOrder right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(DeclarationOrder left, DeclarationOrder right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"{Component}/{Member}/{Edge}";
}
