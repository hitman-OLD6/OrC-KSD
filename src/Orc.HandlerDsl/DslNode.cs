namespace Orc.HandlerDsl;

/// <summary>
/// DSL 树节点（SPEC §3）。**节点无标识**：没有 id、没有 <c>ref</c>、没有跳转边；
/// 一切定位用 <see cref="Path"/>（节点路径，如 <c>actions[2].then[0]</c>）。
/// <para>
/// 树里不存在「同一节点」——每个位置就是一个独立实例；数组顺序即执行顺序。
/// </para>
/// </summary>
public abstract class DslNode
{
    private readonly string _op;

    /// <summary>构造节点。</summary>
    protected DslNode(string op, string path)
    {
        _op = op;
        Path = path;
    }

    /// <summary>节点名（内建 op 或目录节点名）。</summary>
    public virtual string Op => _op;

    /// <summary>节点路径（唯一标识手段）。</summary>
    public string Path { get; }

    /// <summary>子节点（**按执行顺序**；供遍历／折叠标记计算使用）。</summary>
    public virtual IReadOnlyList<DslNode> Children => Array.Empty<DslNode>();
}

/// <summary>子数组容器基类：通用容器的序列体／循环体统一叫 <c>actions</c>（SPEC §3）。</summary>
public abstract class ActionContainerNode : DslNode
{
    /// <summary>构造。</summary>
    protected ActionContainerNode(string op, string path, IReadOnlyList<DslNode> actions)
        : base(op, path) => Actions = actions;

    /// <summary>序列体／循环体（顺序即执行顺序；可为空）。</summary>
    public IReadOnlyList<DslNode> Actions { get; }

    /// <inheritdoc />
    public override IReadOnlyList<DslNode> Children => Actions;
}

/// <summary><c>{"op":"sequence","actions":[…]}</c></summary>
public sealed class SequenceNode : ActionContainerNode
{
    /// <summary>构造。</summary>
    public SequenceNode(string path, IReadOnlyList<DslNode> actions)
        : base("sequence", path, actions)
    {
    }

    /// <inheritdoc />
    public override string Op => "sequence";
}

/// <summary><c>{"op":"branch","test":…,"then":[…],"else":[…]?}</c>——静态判定（纯数据流）。</summary>
public sealed class BranchNode : DslNode
{
    /// <summary>构造。</summary>
    public BranchNode(string path, DslExpression test, IReadOnlyList<DslNode> then, IReadOnlyList<DslNode>? @else)
        : base("branch", path)
    {
        Test = test;
        Then = then;
        Else = @else;
    }

    /// <summary>判定表达式（必须推导为 boolean）。</summary>
    public DslExpression Test { get; }

    /// <summary>真分支（必填且非空）。</summary>
    public IReadOnlyList<DslNode> Then { get; }

    /// <summary>假分支；<c>null</c> ＝没有 else（**必须省略字段**，<c>"else": []</c> 被拒）。</summary>
    public IReadOnlyList<DslNode>? Else { get; }

    /// <inheritdoc />
    public override string Op => "branch";

    /// <inheritdoc />
    public override IReadOnlyList<DslNode> Children =>
        Else is null ? Then : Then.Concat(Else).ToArray();
}

/// <summary><c>{"op":"loop","times":…,"actions":[…]}</c>——固定次数循环。</summary>
public sealed class LoopNode : ActionContainerNode
{
    /// <summary>构造。</summary>
    public LoopNode(string path, DslExpression times, IReadOnlyList<DslNode> actions)
        : base("loop", path, actions) => Times = times;

    /// <summary>次数（任意 <c>number</c> 表达式；只在循环开始时求值一次）。</summary>
    public DslExpression Times { get; }

    /// <inheritdoc />
    public override string Op => "loop";
}

/// <summary><c>{"op":"while","test":…,"limit":…,"actions":[…]}</c>——条件循环（带上限）。</summary>
public sealed class WhileNode : ActionContainerNode
{
    /// <summary>构造。</summary>
    public WhileNode(string path, DslExpression test, DslExpression limit, IReadOnlyList<DslNode> actions)
        : base("while", path, actions)
    {
        Test = test;
        Limit = limit;
    }

    /// <summary>条件（**每轮重估**）。</summary>
    public DslExpression Test { get; }

    /// <summary>上限（只在循环开始时求值一次）。</summary>
    public DslExpression Limit { get; }

    /// <inheritdoc />
    public override string Op => "while";
}

/// <summary><c>{"op":"iterate","over":…,"as":"x","actions":[…]}</c>——遍历（唯一的循环来源）。</summary>
public sealed class IterateNode : ActionContainerNode
{
    /// <summary>构造。</summary>
    public IterateNode(string path, DslExpression over, string @as, IReadOnlyList<DslNode> actions)
        : base("iterate", path, actions)
    {
        Over = over;
        As = @as;
    }

    /// <summary>集合表达式（必须是 <c>list&lt;T&gt;</c>）。</summary>
    public DslExpression Over { get; }

    /// <summary>元素绑定名（循环体内可见、循环外不可见）。</summary>
    public string As { get; }

    /// <inheritdoc />
    public override string Op => "iterate";
}

/// <summary><c>choice</c> 的一个选项（<c>{"label":…,"actions":[…]}</c>）。</summary>
public sealed class ChoiceOption
{
    /// <summary>构造。</summary>
    public ChoiceOption(string label, IReadOnlyList<DslNode> actions, string path)
    {
        Label = label;
        Actions = actions;
        Path = path;
    }

    /// <summary>选项文案。</summary>
    public string Label { get; }

    /// <summary>选中后执行的动作（与其它选项同一作用域层级、互斥不叠加）。</summary>
    public IReadOnlyList<DslNode> Actions { get; }

    /// <summary>选项路径（<c>actions[0].options[1]</c>）。</summary>
    public string Path { get; }
}

/// <summary><c>{"op":"choice","prompt":…,"options":[…]}</c>——玩家交互（与 <c>ask</c> 同受 <c>ask-in-loop</c> 约束）。</summary>
public sealed class ChoiceNode : DslNode
{
    /// <summary>构造。</summary>
    public ChoiceNode(string path, string prompt, IReadOnlyList<ChoiceOption> options)
        : base("choice", path)
    {
        Prompt = prompt;
        Options = options;
    }

    /// <summary>提示文案。</summary>
    public string Prompt { get; }

    /// <summary>选项（非空）。</summary>
    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <inheritdoc />
    public override string Op => "choice";

    /// <inheritdoc />
    public override IReadOnlyList<DslNode> Children => Options.SelectMany(o => o.Actions).ToArray();
}

/// <summary><c>{"op":"let","name":…,"value":…}</c>——跨多处引用同一中间值的唯一正路（SPEC §8.6）。</summary>
public sealed class LetNode : DslNode
{
    /// <summary>构造。</summary>
    public LetNode(string path, string name, DslExpression value)
        : base("let", path)
    {
        Name = name;
        Value = value;
    }

    /// <summary>绑定名。</summary>
    public string Name { get; }

    /// <summary>求值一次的表达式。</summary>
    public DslExpression Value { get; }

    /// <inheritdoc />
    public override string Op => "let";
}

/// <summary><c>{"op":"write","field":…,"value":…}</c>——写当前执行帧的视图字段（R17）。</summary>
public sealed class WriteNode : DslNode
{
    /// <summary>构造。</summary>
    public WriteNode(string path, string field, DslExpression value)
        : base("write", path)
    {
        Field = field;
        Value = value;
    }

    /// <summary>视图字段名（必须是编译期字面量）。</summary>
    public string Field { get; }

    /// <summary>写入值。</summary>
    public DslExpression Value { get; }

    /// <inheritdoc />
    public override string Op => "write";
}

/// <summary><c>ask</c> 的无交互通道降级策略（<b>只管「无通道」这一种情况</b>，SPEC §10.2）。</summary>
public enum AskFallback
{
    /// <summary>缺省：返回空集。</summary>
    Empty,

    /// <summary>取候选第一，并在诊断面留痕（必须由作者显式选择）。</summary>
    First,
}

/// <summary><c>{"op":"ask",…}</c>——发起玩家交互，结果恒为 <c>list&lt;entity&gt;</c>（R23）。</summary>
public sealed class AskNode : DslNode
{
    /// <summary>构造。</summary>
    public AskNode(
        string path,
        string kind,
        string prompt,
        string? side,
        string? zone,
        DslExpression? filter,
        DslBind? bind,
        AskFallback fallback)
        : base("ask", path)
    {
        Kind = kind;
        Prompt = prompt;
        Side = side;
        Zone = zone;
        Filter = filter;
        Bind = bind;
        Fallback = fallback;
    }

    /// <summary>候选实体子类型（<c>unit</c>／<c>hq</c>／<c>card</c>）；结果静态子类型＝它。</summary>
    public string Kind { get; }

    /// <summary>提示文案。</summary>
    public string Prompt { get; }

    /// <summary>可选：阵营限定。</summary>
    public string? Side { get; }

    /// <summary>可选：区域限定。</summary>
    public string? Zone { get; }

    /// <summary>可选：候选过滤（必须推导为 boolean）。</summary>
    public DslExpression? Filter { get; }

    /// <summary>可选：绑定名（单输出，字符串形态）。</summary>
    public DslBind? Bind { get; }

    /// <summary>无交互通道时的降级策略。</summary>
    public AskFallback Fallback { get; }

    /// <inheritdoc />
    public override string Op => "ask";
}

/// <summary>脚本节点的声明引脚（自带 schema，不来自目录扫描）。</summary>
public sealed class DslScriptPin
{
    /// <summary>构造。</summary>
    public DslScriptPin(string name, PinType type, string path)
    {
        Name = name;
        Type = type;
        Path = path;
    }

    /// <summary>引脚名（与外层交互的唯一名）。</summary>
    public string Name { get; }

    /// <summary>引脚类型（封闭集）。</summary>
    public PinType Type { get; }

    /// <summary>引脚路径（<c>actions[0].inputs[0]</c>）。</summary>
    public string Path { get; }
}

/// <summary><c>{"op":"script",…}</c>——逃生舱：正文是 csx 片段，只经声明引脚与图交互（R15）。</summary>
public sealed class ScriptNode : DslNode
{
    /// <summary>构造。</summary>
    public ScriptNode(
        string path,
        IReadOnlyList<DslScriptPin> inputs,
        IReadOnlyList<DslScriptPin> outputs,
        string body)
        : base("script", path)
    {
        Inputs = inputs;
        Outputs = outputs;
        Body = body;
    }

    /// <summary>输入引脚。</summary>
    public IReadOnlyList<DslScriptPin> Inputs { get; }

    /// <summary>输出引脚。</summary>
    public IReadOnlyList<DslScriptPin> Outputs { get; }

    /// <summary>csx 片段正文（生成时原样内联，不参与 DSL 的控制流限制）。</summary>
    public string Body { get; }

    /// <inheritdoc />
    public override string Op => "script";
}

/// <summary>
/// 动作节点的 <c>bind</c>（**只有两种形态，不许可等价写法**，SPEC §8.9）：
/// 单输出→字符串；多输出→对象（键集必须恰好等于目录声明的输出名集合）。
/// </summary>
public sealed class DslBind
{
    private DslBind(string? single, IReadOnlyList<KeyValuePair<string, string>> multiple, string path)
    {
        Single = single;
        Multiple = multiple;
        Path = path;
    }

    /// <summary>单输出绑定名（多输出形态时 null）。</summary>
    public string? Single { get; }

    /// <summary>多输出映射（按文档顺序；单输出形态时为空）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Multiple { get; }

    /// <summary>是否为单输出形态。</summary>
    public bool IsSingle => Single is not null;

    /// <summary>bind 位置路径（<c>actions[0].bind</c>）。</summary>
    public string Path { get; }

    /// <summary>构造单输出形态。</summary>
    public static DslBind SingleBind(string name, string path) =>
        new(name, Array.Empty<KeyValuePair<string, string>>(), path);

    /// <summary>构造多输出形态。</summary>
    public static DslBind MultipleBind(IReadOnlyList<KeyValuePair<string, string>> map, string path) =>
        new(null, map, path);
}

/// <summary>动作节点的入参（按文档顺序，保证遍历确定性）。</summary>
public sealed class DslArgument
{
    /// <summary>构造。</summary>
    public DslArgument(string name, DslExpression value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>引脚名（＝目录声明的输入引脚名）。</summary>
    public string Name { get; }

    /// <summary>取值表达式。</summary>
    public DslExpression Value { get; }
}

/// <summary>
/// 动作节点（目录生成）：字段集＝目录声明的输入引脚名 ＋ 可选 <c>bind</c>；表外字段＝<c>pin-unknown</c>。
/// </summary>
public sealed class ActionNode : DslNode
{
    /// <summary>构造。</summary>
    public ActionNode(string path, string nodeName, IReadOnlyList<DslArgument> arguments, DslBind? bind)
        : base(nodeName, path)
    {
        NodeName = nodeName;
        Arguments = arguments;
        Bind = bind;
    }

    /// <summary>节点名（契约可见标识）。</summary>
    public string NodeName { get; }

    /// <summary>入参（按文档顺序）。</summary>
    public IReadOnlyList<DslArgument> Arguments { get; }

    /// <summary>返回值绑定（<c>null</c> ＝忽略返回值，不产生未使用变量）。</summary>
    public DslBind? Bind { get; }

    /// <inheritdoc />
    public override string Op => NodeName;

    /// <summary>取一个入参（未连接＝null）。</summary>
    public DslArgument? Argument(string name) =>
        Arguments.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}
