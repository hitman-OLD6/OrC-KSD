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

/// <summary>卡属性／玩家资源读取变体（<c>{ "read": { "of": "self", "path": "attack" } }</c>）。</summary>
public sealed class ReadExpression : DslExpression
{
    /// <summary>构造。</summary>
    public ReadExpression(string path, string @of, string property)
        : base(path)
    {
        Of = @of;
        Property = property;
    }

    /// <summary>读取对象（封闭集：<c>self</c>／<c>viewer</c>）。</summary>
    public string Of { get; }

    /// <summary>读取项（取值集由目录的 <c>readables</c> 声明）。</summary>
    public string Property { get; }
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
