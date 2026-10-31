namespace Orc.HandlerDsl;

/// <summary>
/// 目录里的一个引脚（元模型，SPEC §12）。<see cref="PinType.Subtypes"/> 承载实体引脚的运行期接受子类型集，
/// <see cref="EnumValues"/> 承载 <c>string</c> 引脚的允许取值集（枚举＝string ＋ 允许取值集）。
/// </summary>
public sealed class DslPin
{
    /// <summary>构造。</summary>
    public DslPin(
        string name,
        PinType type,
        bool required = true,
        IReadOnlyList<string>? enumValues = null,
        string? enumType = null)
    {
        Name = name;
        Type = type;
        Required = required;
        EnumValues = enumValues ?? Array.Empty<string>();
        EnumType = enumType;
    }

    /// <summary>引脚名（契约可见；改名＝删除引脚＝破坏性变更）。</summary>
    public string Name { get; }

    /// <summary>引脚类型（封闭集）。</summary>
    public PinType Type { get; }

    /// <summary>是否必填（无默认值且在所有重载里都出现＝必填）。</summary>
    public bool Required { get; }

    /// <summary>允许取值集（仅 string 引脚；空＝不限）。</summary>
    public IReadOnlyList<string> EnumValues { get; }

    /// <summary>
    /// 枚举引脚的 CLR 类型全名（仅 <c>string</c> 引脚、且来源是 C# 枚举参数时有值）。
    /// <para>
    /// 生成码需要它才能落出 <c>Orc.Game.Effects.EffectDuration.TurnEnd</c> 这样的形态
    /// （SPEC §6.3：枚举用 <c>string</c> ＋ 允许取值集表达，值本身仍是编译期字面量）。
    /// </para>
    /// </summary>
    public string? EnumType { get; }

    /// <summary>该引脚是否为枚举引脚（<see cref="EnumType"/> 非空）。</summary>
    public bool IsEnum => !string.IsNullOrEmpty(EnumType);

    /// <summary>
    /// 结果拆解里的**枚举输出**标记（<c>[HandlerDslResult("status:string:enum")]</c>）：<see cref="EnumType"/> 取此保留值，
    /// 生成码按 <c>.ToString()</c> 把结果对象的枚举属性落成 <c>string</c>（闭合到封闭集）。
    /// </summary>
    public const string EnumOutputMarker = "enum";

    /// <summary>是否为「枚举输出」（值域由结果对象的枚举类型给出，落码时 <c>.ToString()</c>）。</summary>
    public bool IsEnumOutput => string.Equals(EnumType, EnumOutputMarker, StringComparison.Ordinal);
}

/// <summary>
/// 目录里的一个节点（一个可调用原语）：输入／输出引脚 ＋ 折叠标记 ＋ 扫描溯源。
/// <para>
/// <see cref="Outputs"/> 的数量决定 <c>bind</c> 的合法形态：单输出→字符串；多输出→对象。
/// </para>
/// </summary>
public sealed class DslNodeSignature
{
    /// <summary>构造。</summary>
    public DslNodeSignature(
        string name,
        IReadOnlyList<DslPin> inputs,
        IReadOnlyList<DslPin> outputs,
        bool hasAwait = false,
        bool hasSideEffect = false,
        string? face = null,
        string? method = null,
        string? implicitHost = null,
        bool isStatic = false,
        string? faceType = null,
        string? cancellationTokenParameter = null)
    {
        Name = name;
        Inputs = inputs;
        Outputs = outputs;
        HasAwait = hasAwait;
        HasSideEffect = hasSideEffect;
        Face = face;
        Method = method;
        ImplicitHost = implicitHost;
        IsStatic = isStatic;
        FaceType = faceType;
        CancellationTokenParameter = cancellationTokenParameter;
    }

    /// <summary>节点名（契约可见标识；<c>node-unknown</c> 按它判）。</summary>
    public string Name { get; }

    /// <summary>输入引脚。</summary>
    public IReadOnlyList<DslPin> Inputs { get; }

    /// <summary>输出引脚（空＝无返回值）。</summary>
    public IReadOnlyList<DslPin> Outputs { get; }

    /// <summary>
    /// **单节点**折叠标记：本节点自身会挂起（方法返回 <c>Task</c>）。
    /// <para>
    /// **不含子树**——目录是节点模板，同一节点在不同位置可带不同子树，「某个节点的子树」在这一层没有意义。
    /// 递归聚合由前端在具体树上做（SPEC §12.3）。
    /// </para>
    /// </summary>
    public bool HasAwait { get; }

    /// <summary>**单节点**折叠标记：本节点自身有副作用（子树聚合由前端做，见 <see cref="HasAwait"/>）。</summary>
    public bool HasSideEffect { get; }

    /// <summary>来源门面类型名（扫描溯源）。</summary>
    public string? Face { get; }

    /// <summary>来源方法名（可与节点名不同——改名由标注解耦）。</summary>
    public string? Method { get; }

    /// <summary>
    /// 隐式宿主参数名（<c>host</c>／<c>viewer</c>／<c>self</c> 白名单，SPEC §12.1）：不出引脚，
    /// 生成码固定用 <c>orcSelf</c>。
    /// </summary>
    public string? ImplicitHost { get; }

    /// <summary>来源方法是否静态（静态门面如 <c>KeywordEffectActionFace</c>：生成码直接 <c>类型.方法</c>）。</summary>
    public bool IsStatic { get; }

    /// <summary>来源门面的 CLR 类型全名（生成码定位调用目标用；与 <see cref="Face"/> 的短名互补）。</summary>
    public string? FaceType { get; }

    /// <summary>
    /// 来源方法的 <see cref="CancellationToken"/> 形参名（无此形参＝null）。
    /// <para>
    /// 该形参**不出引脚**（作者不该连线），但生成码必须把它传下去：否则动作调用恒用
    /// <c>default(CancellationToken)</c>，取消与超时永远传导不到动作内部。
    /// 用形参名而非布尔量：形参位置不固定（<c>DamageAsync</c> 在第 4 位、<c>DrawAsync</c> 在第 3 位），
    /// 生成码按**命名实参**填写，位置无关。
    /// </para>
    /// </summary>
    public string? CancellationTokenParameter { get; }

    /// <summary>按名取输入引脚（无此引脚＝null）。</summary>
    public DslPin? Input(string name) =>
        Inputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>按名取输出引脚（无此引脚＝null）。</summary>
    public DslPin? Output(string name) =>
        Outputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// 节点目录的只读查询面（**契约侧**形态）：继承内核的 <see cref="Orc.Core.IHandlerCatalog"/>（只认 <see cref="Orc.Core.IHandlerCatalog.Revision"/>），
/// 在这里补上节点／引脚／可读项的类型化查询。
/// <para>
/// 目录是 <c>Compile</c> 的**调用参数**而非注入物（D4）：<c>catalogRevision</c> 是目录的属性，不是编译器的属性。
/// </para>
/// </summary>
public interface IHandlerDslCatalog : Orc.Core.IHandlerCatalog
{
    /// <summary>全部节点（顺序稳定，供枚举与一致性比对）。</summary>
    IReadOnlyList<DslNodeSignature> Nodes { get; }

    /// <summary>可读项（<c>read.path</c> 的封闭取值集 → 类型）。</summary>
    IReadOnlyDictionary<string, PinType> Readables { get; }

    /// <summary>按名查节点。</summary>
    bool TryGetNode(string name, out DslNodeSignature node);

    /// <summary>本次扫描的门面类型名清单（含标注展开结果的来源；上下文原语不在其中）。</summary>
    IReadOnlyList<string> ScannedFaces { get; }
}

/// <summary>
/// 目录的不可变实现（测试快照、运行期扫描结果都用它）。
/// </summary>
public sealed class DslCatalog : IHandlerDslCatalog
{
    private readonly Dictionary<string, DslNodeSignature> _nodes;

    /// <summary>构造。</summary>
    /// <param name="revision">目录内容版本。</param>
    /// <param name="nodes">节点清单。</param>
    /// <param name="readables">可读项；null＝v1 初版集合（<see cref="DslReadables.V1"/>）。</param>
    /// <exception cref="ArgumentException">节点重名，或节点名与内建 op 冲突（扫描期断言失败）。</exception>
    public DslCatalog(
        int revision,
        IEnumerable<DslNodeSignature> nodes,
        IReadOnlyDictionary<string, PinType>? readables = null,
        IReadOnlyList<string>? scannedFaces = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        Revision = revision;
        Readables = readables ?? DslReadables.V1;
        ScannedFaces = scannedFaces ?? Array.Empty<string>();

        _nodes = new Dictionary<string, DslNodeSignature>(StringComparer.Ordinal);
        var ordered = new List<DslNodeSignature>();
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (HandlerDslContract.IsBuiltinOp(node.Name))
            {
                throw new ArgumentException(
                    $"目录节点名 '{node.Name}' 与内建 op 冲突：内建 10 个 op 与扫描名必须不相交（SPEC §3）。",
                    nameof(nodes));
            }

            if (!_nodes.TryAdd(node.Name, node))
            {
                throw new ArgumentException($"目录重名：'{node.Name}'。", nameof(nodes));
            }

            ordered.Add(node);
        }

        // 上下文原语（stop／interrupt）不是动作面方法，但 op 取值域必须认得它们（SPEC §9.2）：
        // 由目录在构造时**合成**，不进扫描输入集、不进契约包。
        foreach (var contextNode in HandlerDslContextNodes.All)
        {
            if (_nodes.ContainsKey(contextNode.Name))
            {
                throw new ArgumentException(
                    $"目录节点名 '{contextNode.Name}' 与上下文原语冲突（SPEC §9.2 保留名）。", nameof(nodes));
            }

            _nodes.Add(contextNode.Name, contextNode);
            ordered.Add(contextNode);
        }

        Nodes = ordered;
    }

    /// <inheritdoc />
    public int Revision { get; }

    /// <inheritdoc />
    public IReadOnlyList<DslNodeSignature> Nodes { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PinType> Readables { get; }

    /// <summary>本次扫描的门面类型名清单（回答「这个节点来自哪个门面」；上下文原语不在其中）。</summary>
    public IReadOnlyList<string> ScannedFaces { get; }

    /// <inheritdoc />
    public bool TryGetNode(string name, out DslNodeSignature node)
    {
        if (name is not null && _nodes.TryGetValue(name, out var found))
        {
            node = found;
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>空目录（revision 0；只含上下文原语 <c>stop</c>／<c>interrupt</c>）。</summary>
    public static DslCatalog Empty { get; } = new(0, Array.Empty<DslNodeSignature>());
}

/// <summary>
/// **上下文原语**（SPEC §9.2）：<c>stop</c>／<c>interrupt</c>。
/// <para>
/// 它们不是动作面方法（没有对应的 <c>Orc.Game</c> 方法可扫描），却必须是合法的 <c>op</c>——
/// 故由 <see cref="DslCatalog"/> 在构造时合成：0 引脚、无输出（**不接受 <c>bind</c>**）、有副作用、不挂起。
/// 生成码固定为 <c>ctx.Stop()</c>／<c>ctx.Interrupt()</c>（不经 <c>EffectRuntime</c>）。
/// </para>
/// <para>
/// 保留名：任何门面方法派生出同名节点＝扫描期失败（不允许用动作面方法顶替上下文原语）。
/// </para>
/// </summary>
public static class HandlerDslContextNodes
{
    /// <summary>停本成员剩余边（＝<c>ctx.Stop()</c>）。</summary>
    public const string Stop = "stop";

    /// <summary>链级中断（＝<c>ctx.Interrupt</c>）。</summary>
    public const string Interrupt = "interrupt";

    /// <summary>两个上下文原语节点（顺序稳定：stop、interrupt）。</summary>
    public static IReadOnlyList<DslNodeSignature> All { get; } = new[]
    {
        new DslNodeSignature(Stop, Array.Empty<DslPin>(), Array.Empty<DslPin>(), hasAwait: false, hasSideEffect: true),
        new DslNodeSignature(Interrupt, Array.Empty<DslPin>(), Array.Empty<DslPin>(), hasAwait: false, hasSideEffect: true),
    };

    /// <summary>是否为上下文原语名。</summary>
    public static bool IsContextOp(string? name) =>
        string.Equals(name, Stop, StringComparison.Ordinal) || string.Equals(name, Interrupt, StringComparison.Ordinal);
}

/// <summary>
/// v1 的可读项初版集合（SPEC §12.8）。真实取值集由目录声明；这里只是「无目录时」的兜底与文档锚点。
/// </summary>
public static class DslReadables
{
    /// <summary>可读项 → 类型（只含标量；实体属性不算「可读项」，走视图声明）。</summary>
    public static IReadOnlyDictionary<string, PinType> V1 { get; } =
        new Dictionary<string, PinType>(StringComparer.Ordinal)
        {
            ["attack"] = PinType.Number,
            ["defense"] = PinType.Number,
            ["opCost"] = PinType.Number,
            ["deployCost"] = PinType.Number,
            ["hqHealth"] = PinType.Number,
            ["kredits"] = PinType.Number,
            ["maxKredits"] = PinType.Number,
            ["name"] = PinType.String,
            ["zone"] = PinType.String,
        };
}
