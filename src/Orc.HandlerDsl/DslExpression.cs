namespace Orc.HandlerDsl;

/// <summary>字面量的 JSON 类型（决定推导出的引脚类型，SPEC §4.2）。</summary>
public enum LiteralKind
{
    /// <summary>数字。</summary>
    Number,

    /// <summary>字符串。</summary>
    String,

    /// <summary>布尔。</summary>
    Boolean,
}

/// <summary>
/// 引脚取值表达式（SPEC §4 的**五种变体之一**）。每个表达式有确定类型，
/// 类型只由「节点 kind ＋ 入参类型」决定、**与上下文（目标引脚）无关**（SPEC §7.1）。
/// <para>
/// <see cref="Path"/> 是该表达式在文档中的位置（如 <c>actions[0].amount.args[1]</c>），
/// 一切错误定位用它。
/// </para>
/// </summary>
public abstract class DslExpression
{
    /// <summary>构造表达式。</summary>
    protected DslExpression(string path) => Path = path;

    /// <summary>表达式位置路径。</summary>
    public string Path { get; }
}

/// <summary>字面量变体（<c>{ "lit": … }</c>）。</summary>
public sealed class LiteralExpression : DslExpression
{
    private LiteralExpression(string path, LiteralKind kind, double number, string? text, bool boolean, string raw)
        : base(path)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Boolean = boolean;
        RawText = raw;
    }

    /// <summary>字面量种类。</summary>
    public LiteralKind Kind { get; }

    /// <summary>数字值（仅 <see cref="LiteralKind.Number"/>）。</summary>
    public double Number { get; }

    /// <summary>字符串值（仅 <see cref="LiteralKind.String"/>）。</summary>
    public string? Text { get; }

    /// <summary>布尔值（仅 <see cref="LiteralKind.Boolean"/>）。</summary>
    public bool Boolean { get; }

    /// <summary>JSON 原文（生成格式冻结时按原文与 <c>InvariantCulture</c> 规则落字面量，SPEC §13 F4）。</summary>
    public string RawText { get; }

    /// <summary>构造数字字面量。</summary>
    public static LiteralExpression NumberLiteral(string path, double value, string raw) =>
        new(path, LiteralKind.Number, value, null, false, raw);

    /// <summary>构造字符串字面量。</summary>
    public static LiteralExpression StringLiteral(string path, string value) =>
        new(path, LiteralKind.String, 0, value, false, value);

    /// <summary>构造布尔字面量。</summary>
    public static LiteralExpression BooleanLiteral(string path, bool value) =>
        new(path, LiteralKind.Boolean, 0, null, value, value ? "true" : "false");
}

/// <summary>绑定引用变体（<c>{ "var": "x" }</c>）。</summary>
public sealed class VarExpression : DslExpression
{
    /// <summary>构造。</summary>
    public VarExpression(string path, string name)
        : base(path) => Name = name;

    /// <summary>绑定名。</summary>
    public string Name { get; }
}

/// <summary>视图字段读取变体（<c>{ "view": "Defense" }</c>）。</summary>
public sealed class ViewExpression : DslExpression
{
    /// <summary>构造。</summary>
    public ViewExpression(string path, string field)
        : base(path) => Field = field;

    /// <summary>视图字段名。</summary>
    public string Field { get; }
}

/// <summary>
/// 属性读取变体（<c>{ "read": { "of": …, "path": "attack" } }</c>）。
/// <para>
/// <see cref="Of"/> 有两种形态，**恰有一个非空**：
/// </para>
/// <list type="bullet">
/// <item><see cref="ReservedTarget"/>：保留字面量 <c>self</c>／<c>viewer</c>（简写形态）；</item>
/// <item><see cref="Target"/>：任意**实体表达式**（如 <c>{"var":"t"}</c>）——
/// 用来读「被遍历的那个实体」自身的属性，这是 v1 早期写不出「若该单位防御 ≤ 2」的根因。</item>
/// </list>
/// <para>
/// 两种形态在生成码里归一：读某个实体自身的属性 ≡ <c>ReadValue(那个实体, "self", path)</c>，
/// 所以运行期<b>零改动</b>（<c>ReadValue</c> 的首参本来就是「读谁」）。
/// </para>
/// </summary>
public sealed class ReadExpression : DslExpression
{
    /// <summary>构造。</summary>
    /// <param name="path">节点路径。</param>
    /// <param name="reservedTarget">保留字面量目标（<c>self</c>／<c>viewer</c>）；用表达式形态时传 null。</param>
    /// <param name="target">实体表达式目标；用保留字面量时传 null。</param>
    /// <param name="property">读取项（取值集由目录的 <c>readables</c> 按实体种类声明）。</param>
    public ReadExpression(string path, string? reservedTarget, DslExpression? target, string property)
        : base(path)
    {
        ReservedTarget = reservedTarget;
        Target = target;
        Property = property;
    }

    /// <summary>保留字面量目标（封闭集：<c>self</c>／<c>viewer</c>）；表达式形态下为 null。</summary>
    public string? ReservedTarget { get; }

    /// <summary>实体表达式目标；保留字面量形态下为 null。</summary>
    public DslExpression? Target { get; }

    /// <summary>读取项（取值集由目录的 <c>readables</c> 声明）。</summary>
    public string Property { get; }

    /// <summary>是否为「保留字面量」形态（否则是实体表达式形态）。</summary>
    public bool IsReserved => ReservedTarget is not null;
}

/// <summary>算子应用变体（<c>{ "op": "sub", "args": [ … ] }</c>，可嵌套）。</summary>
public sealed class OpExpression : DslExpression
{
    /// <summary>构造。</summary>
    public OpExpression(string path, string op, IReadOnlyList<DslExpression> args)
        : base(path)
    {
        Op = op;
        Args = args;
    }

    /// <summary>算子名（封闭集，SPEC §5）。</summary>
    public string Op { get; }

    /// <summary>入参（非空）。</summary>
    public IReadOnlyList<DslExpression> Args { get; }
}
