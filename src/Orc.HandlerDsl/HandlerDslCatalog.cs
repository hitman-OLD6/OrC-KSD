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
        string? cancellationTokenParameter = null,
        DslNodeStage stage = DslNodeStage.Alpha,
        string? problem = null)
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
        Stage = stage;
        Problem = problem;
    }

    /// <summary>节点名（契约可见标识；<c>node-unknown</c> 按它判）。</summary>
    public string Name { get; }

    /// <summary>
    /// 能力分区（R36）：<see cref="DslNodeStage.Alpha"/> ＝这版真能跑；
    /// <see cref="DslNodeStage.Preview"/> ＝目录里有、但**现在跑不起来**（原因见 <see cref="Problem"/>）。
    /// <para>
    /// **不裁剪目录**：preview 节点照样出现在清单里，只影响展示顺序与提示——
    /// 藏起来会让作者以为这版没有这个能力。判据是显式标注，不是自动探针（见 <c>HandlerDslPreviewAttribute</c>）。
    /// </para>
    /// </summary>
    public DslNodeStage Stage { get; }

    /// <summary>人读原因（<see cref="DslNodeStage.Alpha"/> 恒为 null）。</summary>
    public string? Problem { get; }

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

    /// <summary>
    /// 可读项：**实体种类 → （<c>read.path</c> 的取值 → 类型）**。
    /// <para>
    /// 按**实体种类**分组，不是按 <c>read.of</c> 的 <c>self</c>／<c>viewer</c> 分组——
    /// 因为 <c>of</c> 可以是一个实体表达式（如 <c>{"var":"t"}</c>），此时「owner 视角」不成立，
    /// 能读什么只取决于那个实体是哪一类（单位／总部／玩家）。
    /// </para>
    /// <para>
    /// 这张表把运行期 <c>EffectRuntime</c> 早已存在的分区（<c>self</c> 读 <c>kredits</c> 会抛
    /// <see cref="NotSupportedException"/>）上移到元数据，使校验期能拒掉无意义的组合。
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PinType>> Readables { get; }

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
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, PinType>>? readables = null,
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
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PinType>> Readables { get; }

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
/// 可读项的**实体种类**（<c>read.of</c> 最终定位到的实体属于哪一类）。
/// <para>
/// 分组的依据是「能读什么只取决于实体是哪一类」，与调用者视角无关——这是把
/// <c>read.of</c> 从 <c>self</c>／<c>viewer</c> 放开成实体表达式之后唯一自洽的维度。
/// </para>
/// </summary>
public static class DslReadableKinds
{
    /// <summary>单位卡。</summary>
    public const string Unit = "unit";

    /// <summary>总部。</summary>
    public const string Hq = "hq";

    /// <summary>玩家。</summary>
    public const string Player = "player";

    /// <summary>全部种类（顺序稳定）。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Unit, Hq, Player };

    /// <summary>「卡」的并集（单位 ＋ 总部）——<c>read.of=self</c> 的宿主可能是两者之一。</summary>
    public static IReadOnlyList<string> Cards { get; } = new[] { Unit, Hq };
}

/// <summary>
/// v1 的可读项初版集合（SPEC §12.8）。真实取值集由目录声明；这里只是「无目录时」的兜底与文档锚点。
/// </summary>
public static class DslReadables
{
    /// <summary><c>read.of</c> 的保留字面量：读宿主卡自身。</summary>
    public const string Self = "self";

    /// <summary><c>read.of</c> 的保留字面量：读宿主归属的玩家。</summary>
    public const string Viewer = "viewer";

    /// <summary>保留字面量全集（<c>of</c> 写成字符串时只允许这两个取值）。</summary>
    public static IReadOnlyList<string> ReservedTargets { get; } = new[] { Self, Viewer };

    /// <summary>
    /// 实体种类 → （可读项 → 类型）。
    /// <para>
    /// 与运行期 <c>KeywordEffectActionFace.ReadValue</c> 的分支**逐条对应**：
    /// <c>of=self</c> 只认单位/总部的项，<c>of=viewer</c> 只认玩家的项，越界在运行期抛
    /// <see cref="NotSupportedException"/>。表在这里＝把那条事实搬到校验期。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, PinType>> V1 { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, PinType>>(StringComparer.Ordinal)
        {
            [DslReadableKinds.Unit] = new Dictionary<string, PinType>(StringComparer.Ordinal)
            {
                ["attack"] = PinType.Number,
                ["defense"] = PinType.Number,
                ["opCost"] = PinType.Number,
                ["deployCost"] = PinType.Number,
                ["name"] = PinType.String,
                ["zone"] = PinType.String,
            },
            [DslReadableKinds.Hq] = new Dictionary<string, PinType>(StringComparer.Ordinal)
            {
                ["hqHealth"] = PinType.Number,
                ["name"] = PinType.String,
            },
            [DslReadableKinds.Player] = new Dictionary<string, PinType>(StringComparer.Ordinal)
            {
                ["kredits"] = PinType.Number,
                ["maxKredits"] = PinType.Number,
                ["hqHealth"] = PinType.Number,
            },
        };

    /// <summary>
    /// 给定种类集合，求可读项的并集（后者覆盖前者的同名项——同名项的标量类型必然一致）。
    /// </summary>
    public static IReadOnlyDictionary<string, PinType> Union(IEnumerable<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var merged = new Dictionary<string, PinType>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            if (!V1.TryGetValue(kind, out var items))
            {
                continue;
            }

            foreach (var pair in items)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    /// <summary>
    /// 把 <c>read.of</c> 的目标折算成「可能的实体种类集合」。
    /// <para>
    /// 三条来源，语义都只是「这个目标可能是哪几类实体」：<c>viewer</c>＝玩家；
    /// <c>self</c>＝宿主卡（单位或总部，静态未知）；实体表达式＝由它的静态子类型决定，
    /// 子类型未知时取卡的两类。
    /// </para>
    /// </summary>
    /// <param name="reservedTarget"><c>self</c>／<c>viewer</c>；用表达式形态时传 null。</param>
    /// <param name="expressionTargetType">实体表达式推导出的类型（可含子类型集）；保留字面量形态传 null。</param>
    public static IReadOnlyList<string> KindsOf(string? reservedTarget, PinType? expressionTargetType)
    {
        if (string.Equals(reservedTarget, Viewer, StringComparison.Ordinal))
        {
            return new[] { DslReadableKinds.Player };
        }

        if (string.Equals(reservedTarget, Self, StringComparison.Ordinal))
        {
            return DslReadableKinds.Cards;
        }

        // 实体表达式：子类型里属于「卡」的部分；一个都没定 ⇒ 卡的两类都算可能。
        if (expressionTargetType is { Kind: PinTypeKind.Entity } && expressionTargetType.Subtypes.Count > 0)
        {
            var kinds = expressionTargetType.Subtypes
                .Where(static s => s is DslReadableKinds.Unit or DslReadableKinds.Hq)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (kinds.Length > 0)
            {
                return kinds;
            }
        }

        return DslReadableKinds.Cards;
    }

    /// <summary>
    /// 在给定种类集合下解析读取项。
    /// </summary>
    /// <param name="readables">目录声明的可读项表。</param>
    /// <param name="kinds">目标的可能实体种类（见 <see cref="KindsOf"/>）。</param>
    /// <param name="path">读取项名。</param>
    /// <param name="type">命中时的类型（未命中＝null）。</param>
    /// <param name="available">该种类集合下可用的项（**并集**，用于诊断文案）。</param>
    public static bool TryResolve(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, PinType>> readables,
        IReadOnlyList<string> kinds,
        string path,
        out PinType? type,
        out IReadOnlyList<string> available)
    {
        ArgumentNullException.ThrowIfNull(readables);
        ArgumentNullException.ThrowIfNull(kinds);

        var found = new HashSet<string>(StringComparer.Ordinal);
        type = null;

        foreach (var kind in kinds)
        {
            if (!readables.TryGetValue(kind, out var items))
            {
                continue;
            }

            foreach (var pair in items)
            {
                found.Add(pair.Key);
            }

            if (type is null && items.TryGetValue(path, out var itemType))
            {
                type = itemType;
            }
        }

        available = found.Order(StringComparer.Ordinal).ToArray();
        return type is not null;
    }

    /// <summary>诊断文案里的种类说明（<c>self</c>／<c>viewer</c>／实体表达式）。</summary>
    public static string DescribeKinds(IReadOnlyList<string> kinds) =>
        string.Join("／", kinds ?? Array.Empty<string>());
}

/// <summary>
/// 节点的**能力分区**（R36）。判据是能否真的跑起来，不是「目录里有没有」。
/// </summary>
public enum DslNodeStage
{
    /// <summary>这版真能跑：编译通过，且运行期不会因「能力未接入」而失败。</summary>
    Alpha = 0,

    /// <summary>目录里有、但跑不起来：需要由显式标注给出原因，并由一条行为测试背书。</summary>
    Preview = 1,
}
