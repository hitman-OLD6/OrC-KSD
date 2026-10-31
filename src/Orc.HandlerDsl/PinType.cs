using Orc.Core.Model;

namespace Orc.HandlerDsl;

/// <summary>引脚类型的种类（封闭集，SPEC §6）。</summary>
public enum PinTypeKind
{
    /// <summary>实体引用（不设静态子类型，子类型由引脚声明的运行期接受集约束）。</summary>
    Entity,

    /// <summary>数字。</summary>
    Number,

    /// <summary>字符串（枚举＝string ＋ 引脚的允许取值集）。</summary>
    String,

    /// <summary>布尔。</summary>
    Boolean,

    /// <summary>集合；元素类型见 <see cref="PinType.Element"/>。</summary>
    List,
}

/// <summary>
/// 引脚类型（v1 封闭集）：<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c>／<c>list&lt;T&gt;</c>，
/// 其中 T ∈ 前四者。
/// <para>
/// **不提供**结构体、字典、可空类型、任意 CLR 类型、<c>list&lt;list&lt;…&gt;&gt;</c>
/// ——不是「暂不支持」，是**不可表达**（SPEC §6.2）。
/// </para>
/// <para>
/// <see cref="Subtypes"/> 只在 <see cref="PinTypeKind.Entity"/> 上有意义：
/// 空集表示「无约束／未知」——引脚侧读作「接受任意子类型」，表达式侧读作「静态不可确定」。
/// 因此兼容判定只在**两侧都非空且交集为空**时拒绝（SPEC §7.6）。
/// </para>
/// </summary>
public sealed class PinType : IEquatable<PinType>
{
    /// <summary>规范文本名（封闭集全集，供错误消息与目录校验使用）。</summary>
    public static IReadOnlyList<string> CanonicalNames { get; } = new[]
    {
        "entity", "number", "string", "boolean",
        "list<entity>", "list<number>", "list<string>", "list<boolean>",
    };

    /// <summary>实体子类型名的封闭集（v1）。</summary>
    public static IReadOnlyList<string> EntitySubtypeNames { get; } = new[] { "unit", "hq", "card" };

    private static readonly string[] NoSubtypes = Array.Empty<string>();

    /// <summary>实体类型（子类型未知／不限）。</summary>
    public static PinType Entity { get; } = new(PinTypeKind.Entity, null, NoSubtypes);

    /// <summary>数字类型。</summary>
    public static PinType Number { get; } = new(PinTypeKind.Number, null, NoSubtypes);

    /// <summary>字符串类型。</summary>
    public static PinType String { get; } = new(PinTypeKind.String, null, NoSubtypes);

    /// <summary>布尔类型。</summary>
    public static PinType Boolean { get; } = new(PinTypeKind.Boolean, null, NoSubtypes);

    private PinType(PinTypeKind kind, PinType? element, IReadOnlyList<string> subtypes)
    {
        Kind = kind;
        Element = element;
        Subtypes = subtypes;
    }

    /// <summary>种类。</summary>
    public PinTypeKind Kind { get; }

    /// <summary>集合的元素类型（仅 <see cref="PinTypeKind.List"/> 非 null）。</summary>
    public PinType? Element { get; }

    /// <summary>实体子类型集（仅 entity；空＝无约束／未知）。</summary>
    public IReadOnlyList<string> Subtypes { get; }

    /// <summary>是否为集合。</summary>
    public bool IsList => Kind == PinTypeKind.List;

    /// <summary>构造集合类型；元素不得是集合（封闭集不含 <c>list&lt;list&lt;…&gt;&gt;</c>）。</summary>
    /// <exception cref="ArgumentNullException">element 为 null。</exception>
    /// <exception cref="ArgumentException">element 本身是集合。</exception>
    public static PinType List(PinType element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.IsList)
        {
            throw new ArgumentException("引脚类型封闭集不含 list<list<…>>（SPEC §6.2）。", nameof(element));
        }

        return new PinType(PinTypeKind.List, element, NoSubtypes);
    }

    /// <summary>返回带实体子类型约束／标注的副本（仅 entity 合法）。</summary>
    /// <exception cref="ArgumentException">非 entity，或子类型名不在封闭集内。</exception>
    public PinType WithSubtypes(params string[] subtypes)
    {
        ArgumentNullException.ThrowIfNull(subtypes);
        if (Kind != PinTypeKind.Entity)
        {
            throw new ArgumentException("子类型只对 entity 有意义（SPEC §6.4）。", nameof(subtypes));
        }

        foreach (var subtype in subtypes)
        {
            if (!EntitySubtypeNames.Contains(subtype, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"实体子类型 '{subtype}' 超出封闭集：只允许 {string.Join("／", EntitySubtypeNames)}。",
                    nameof(subtypes));
            }
        }

        return new PinType(PinTypeKind.Entity, null, subtypes.Length == 0 ? NoSubtypes : subtypes.ToArray());
    }

    /// <summary>把集合类型的元素替换为带子类型的实体（<c>list&lt;entity&gt;</c> 的静态子类型标注）。</summary>
    public PinType WithElementSubtypes(params string[] subtypes)
    {
        if (!IsList || Element!.Kind != PinTypeKind.Entity)
        {
            throw new ArgumentException("只对 list<entity> 有意义。", nameof(subtypes));
        }

        return List(Element.WithSubtypes(subtypes));
    }

    /// <summary>解析规范文本名（大小写不敏感、忽略首尾空白）；超出封闭集＝失败。</summary>
    public static bool TryParse(string? text, out PinType type)
    {
        type = Number;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "entity": type = Entity; return true;
            case "number": type = Number; return true;
            case "string": type = String; return true;
            case "boolean": type = Boolean; return true;
            case "list<entity>": type = List(Entity); return true;
            case "list<number>": type = List(Number); return true;
            case "list<string>": type = List(String); return true;
            case "list<boolean>": type = List(Boolean); return true;
            default: return false;
        }
    }

    /// <summary>解析规范文本名；超出封闭集＝拒绝（消息列出封闭集全文）。</summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    /// <exception cref="FormatException">类型名不在封闭集内。</exception>
    public static PinType Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var type))
        {
            throw new FormatException(
                $"引脚类型 '{text}' 超出封闭集：只允许 {string.Join("／", CanonicalNames)}"
                + "（不提供结构体、字典、可空类型、任意 CLR 类型与 list<list<…>>，SPEC §6）。");
        }

        return type;
    }

    /// <summary>
    /// 视图字段类型 → 引脚类型（结构性等价：<c>entity[]</c> ↔ <c>list&lt;entity&gt;</c>，SPEC §6.6）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的视图字段类型。</exception>
    public static PinType FromViewFieldType(ViewFieldType type) => type switch
    {
        ViewFieldType.Entity => Entity,
        ViewFieldType.Number => Number,
        ViewFieldType.String => String,
        ViewFieldType.Boolean => Boolean,
        ViewFieldType.EntityArray => List(Entity),
        ViewFieldType.NumberArray => List(Number),
        ViewFieldType.StringArray => List(String),
        ViewFieldType.BooleanArray => List(Boolean),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未定义的视图字段类型。"),
    };

    /// <summary>规范文本名（子类型不参与，子类型走目录的独立字段）。</summary>
    public string Format()
    {
        var name = Kind switch
        {
            PinTypeKind.Entity => "entity",
            PinTypeKind.Number => "number",
            PinTypeKind.String => "string",
            PinTypeKind.Boolean => "boolean",
            PinTypeKind.List => $"list<{Element!.Format()}>",
            _ => throw new InvalidOperationException("未定义的引脚类型种类。"),
        };

        return Subtypes.Count == 0
            ? name
            : $"{name}{{{string.Join(",", Subtypes)}}}";
    }

    /// <summary>
    /// 赋值兼容（SPEC §7.6）：基类型**必须完全相同**（列表比到元素）；
    /// entity 两侧子类型集都非空且无交集＝不兼容；任一侧为空＝放行。
    /// </summary>
    /// <param name="pin">目标引脚类型。</param>
    /// <param name="mismatch">不兼容原因（兼容时 null）。</param>
    public bool IsAssignableTo(PinType pin, out string? mismatch)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (Kind != pin.Kind)
        {
            mismatch = $"期望 {pin.Format()}，实际 {Format()}";
            return false;
        }

        if (Kind == PinTypeKind.List)
        {
            if (!Element!.IsAssignableTo(pin.Element!, out var elementMismatch))
            {
                mismatch = $"期望 {pin.Format()}，实际 {Format()}（元素类型不符：{elementMismatch}）";
                return false;
            }
        }

        if (Kind == PinTypeKind.Entity
            && Subtypes.Count > 0
            && pin.Subtypes.Count > 0
            && !Subtypes.Intersect(pin.Subtypes, StringComparer.Ordinal).Any())
        {
            mismatch = $"实体子类型不匹配：期望 {pin.Format()}，实际 {Format()}";
            return false;
        }

        mismatch = null;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(PinType? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        if (Kind == PinTypeKind.List && !Element!.Equals(other.Element))
        {
            return false;
        }

        return Subtypes.OrderBy(s => s, StringComparer.Ordinal)
            .SequenceEqual(other.Subtypes.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PinType);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        if (Kind == PinTypeKind.List)
        {
            hash.Add(Element);
        }

        foreach (var subtype in Subtypes.OrderBy(s => s, StringComparer.Ordinal))
        {
            hash.Add(subtype, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Format();
}
