using System.Globalization;

namespace Orc.Core.Model;

/// <summary>
/// 视图字段类型——**封闭内建集**（R1a 方案 C 的定稿）：
/// 实体引用／数字／字符串／布尔，以及以上四种的数组。
/// <para>
/// **不支持嵌套结构**：本枚举没有"复合／对象／字典"成员，因此嵌套结构在模型层**不可表达**（显式不支持，不是静默降级）。
/// 需要复杂计算或复合结构时退回 csx（在脚本内部自行组织，不进入视图声明）。
/// </para>
/// </summary>
public enum ViewFieldType
{
    /// <summary>实体引用（取到的就是真实实体对象，转换语义不变）。</summary>
    Entity,

    /// <summary>数字（整型／浮点／decimal 均归此类）。</summary>
    Number,

    /// <summary>字符串。</summary>
    String,

    /// <summary>布尔。</summary>
    Boolean,

    /// <summary>实体引用数组。</summary>
    EntityArray,

    /// <summary>数字数组。</summary>
    NumberArray,

    /// <summary>字符串数组。</summary>
    StringArray,

    /// <summary>布尔数组。</summary>
    BooleanArray,
}

/// <summary>
/// 视图字段类型名的文本解析（预制体文本 → 封闭类型集）。
/// 只接受八个固定词（<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c> 及其 <c>[]</c> 形态，大小写不敏感）。
/// 任何嵌套／泛型／复合写法（含 <c>&lt;...&gt;</c>、<c>{...}</c>、二维数组、逗号）一律拒绝——
/// 这条拒绝是 R1a「字段类型超出封闭集＝预制体装载期拒绝」的落地。
/// </summary>
public static class ViewFieldTypes
{
    private static readonly Dictionary<string, ViewFieldType> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["entity"] = ViewFieldType.Entity,
        ["number"] = ViewFieldType.Number,
        ["string"] = ViewFieldType.String,
        ["boolean"] = ViewFieldType.Boolean,
        ["entity[]"] = ViewFieldType.EntityArray,
        ["number[]"] = ViewFieldType.NumberArray,
        ["string[]"] = ViewFieldType.StringArray,
        ["boolean[]"] = ViewFieldType.BooleanArray,
    };

    /// <summary>解析类型名；超出封闭集＝拒绝。</summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    /// <exception cref="ArgumentException">类型名不在封闭集内（消息指出非法值与封闭集全文）。</exception>
    public static ViewFieldType Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        if (Names.TryGetValue(trimmed, out var type))
        {
            return type;
        }

        throw new ArgumentException(
            $"视图字段类型 '{text}' 超出封闭内建集：只允许 entity／number／string／boolean 及其数组形态（entity[] 等）；"
            + "嵌套结构与自定义类型不可表达（R1a）。",
            nameof(text));
    }

    /// <summary>尝试解析（不抛异常）。</summary>
    public static bool TryParse(string? text, out ViewFieldType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Names.TryGetValue(text.Trim(), out type);
    }

    /// <summary>取规范类型名（供审查导出）。</summary>
    public static string Format(ViewFieldType type) => type switch
    {
        ViewFieldType.Entity => "entity",
        ViewFieldType.Number => "number",
        ViewFieldType.String => "string",
        ViewFieldType.Boolean => "boolean",
        ViewFieldType.EntityArray => "entity[]",
        ViewFieldType.NumberArray => "number[]",
        ViewFieldType.StringArray => "string[]",
        ViewFieldType.BooleanArray => "boolean[]",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未定义的视图字段类型。"),
    };

    /// <summary>
    /// 运行期取值校验：值是否符合声明的字段类型（封闭集）。
    /// null 恒不合规（必填/可选的缺失判定在投影层，不在本方法）。
    /// </summary>
    public static bool Accepts(ViewFieldType type, object? value)
    {
        if (value is null)
        {
            return false;
        }

        return type switch
        {
            ViewFieldType.Entity => true, // 实体引用：任何非 null 对象（转换语义由作者在 csx 里做，框架不猜）
            ViewFieldType.Number => IsNumber(value),
            ViewFieldType.String => value is string,
            ViewFieldType.Boolean => value is bool,
            ViewFieldType.EntityArray => value is Array array && !IsNestedArray(array),
            ViewFieldType.NumberArray => value is Array numbers && !IsNestedArray(numbers) && AllElements(numbers, static v => IsNumber(v!)),
            ViewFieldType.StringArray => value is Array strings && !IsNestedArray(strings) && AllElements(strings, static v => v is string),
            ViewFieldType.BooleanArray => value is Array flags && !IsNestedArray(flags) && AllElements(flags, static v => v is bool),
            _ => false,
        };
    }

    /// <summary>数字判定（整型／浮点／decimal）。</summary>
    public static bool IsNumber(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>取字段类型的默认值（可选字段缺失时的投影结果）。</summary>
    public static object? DefaultValue(ViewFieldType type) => type switch
    {
        ViewFieldType.Entity => null,
        ViewFieldType.Number => 0,
        ViewFieldType.String => string.Empty,
        ViewFieldType.Boolean => false,
        ViewFieldType.EntityArray => Array.Empty<object?>(),
        ViewFieldType.NumberArray => Array.Empty<decimal>(),
        ViewFieldType.StringArray => Array.Empty<string>(),
        ViewFieldType.BooleanArray => Array.Empty<bool>(),
        _ => null,
    };

    private static bool IsNestedArray(Array array) => array.Rank != 1 || array.GetType().GetElementType()?.IsArray == true;

    private static bool AllElements(Array array, Func<object?, bool> predicate)
    {
        foreach (var item in array)
        {
            if (item is null || !predicate(item))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 单个视图字段声明（R1a）：字段名 + 封闭类型 + 读写 + 必填。
/// <para>
/// 名字空间严格限定为**数据面**（仅视图声明字段）：私有字段不在其中，唯一通道是 <c>owner.&lt;Name&gt;</c>（R3e／W-14）。
/// </para>
/// </summary>
public sealed class ViewFieldDefinition
{
    /// <summary>创建字段声明。</summary>
    /// <param name="name">字段名（必须是合法 C# 标识符，供 <c>view.&lt;Name&gt;</c> 按名访问）。</param>
    /// <param name="type">封闭内建类型。</param>
    /// <param name="writable">是否可写（默认只读；写只读字段＝拒绝）。</param>
    /// <param name="required">是否必填（默认必填；必填缺失＝首次执行 fail-fast，见 A-1）。</param>
    /// <exception cref="ArgumentException">字段名不是合法标识符。</exception>
    public ViewFieldDefinition(string name, ViewFieldType type, bool writable = false, bool required = true)
    {
        if (!IsIdentifier(name))
        {
            throw new ArgumentException(
                $"视图字段名 '{name}' 不是合法标识符（视图按名投影，字段名须可写作 view.<Name>）。", nameof(name));
        }

        Name = name;
        Type = type;
        Writable = writable;
        Required = required;
    }

    /// <summary>字段名（＝数据面键名）。</summary>
    public string Name { get; }

    /// <summary>封闭内建类型。</summary>
    public ViewFieldType Type { get; }

    /// <summary>是否可写（写回宿主数据面，R1c）。</summary>
    public bool Writable { get; }

    /// <summary>是否必填。</summary>
    public bool Required { get; }

    /// <summary>标识符判定（首字符字母或下划线；其余字母／数字／下划线）。</summary>
    public static bool IsIdentifier(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!char.IsLetter(text[0]) && text[0] != '_')
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]) && text[i] != '_')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 视图声明（R1a／R1c）：一份**字段清单**，由 ctxViewer 按名从数据面投影。
/// 不再有"C# 视图类"这一形态——形状完全由本清单表达，运行期代表是唯一的承载视图 <see cref="ViewCarrier"/>。
/// </summary>
public sealed class ViewSpec
{
    /// <summary>空视图（无字段；仅用于不读任何执行数据的成员）。</summary>
    public static readonly ViewSpec Empty = new(Array.Empty<ViewFieldDefinition>());

    private readonly Dictionary<string, ViewFieldDefinition> _fields;

    /// <summary>创建视图声明。</summary>
    /// <exception cref="ArgumentNullException">fields 含 null 元素。</exception>
    /// <exception cref="ArgumentException">字段名重复。</exception>
    public ViewSpec(IEnumerable<ViewFieldDefinition> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var list = new List<ViewFieldDefinition>();
        _fields = new Dictionary<string, ViewFieldDefinition>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field, nameof(fields));
            if (!_fields.TryAdd(field.Name, field))
            {
                throw new ArgumentException($"视图字段名 '{field.Name}' 重复声明（视图内字段名须唯一）。", nameof(fields));
            }

            list.Add(field);
        }

        Fields = list;
    }

    /// <summary>字段清单（声明序）。</summary>
    public IReadOnlyList<ViewFieldDefinition> Fields { get; }

    /// <summary>字段名集合（声明序）。</summary>
    public IReadOnlyList<string> Names => Fields.Select(static f => f.Name).ToArray();

    /// <summary>是否声明了该字段。</summary>
    public bool Contains(string name) => _fields.ContainsKey(name);

    /// <summary>取字段声明；未声明＝null。</summary>
    public ViewFieldDefinition? Find(string name) => _fields.TryGetValue(name, out var field) ? field : null;

    /// <summary>字段名是否可写（未声明＝false）。</summary>
    public bool IsWritable(string name) => Find(name)?.Writable == true;

    /// <summary>字段名是否必填（未声明＝false）。</summary>
    public bool IsRequired(string name) => Find(name)?.Required == true;

    /// <summary>字段名集合比较：本视图的**全部**字段名是否都落在 <paramref name="allowed"/> 内（R1c 装载期校验用）。</summary>
    public IReadOnlyList<string> MissingFrom(ISet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return Fields.Where(f => !allowed.Contains(f.Name)).Select(static f => f.Name).ToArray();
    }

    /// <summary>便捷构造：按（名, 类型）列表创建（全部只读必填）。</summary>
    public static ViewSpec Of(params (string Name, ViewFieldType Type)[] fields)
        => new(fields.Select(static f => new ViewFieldDefinition(f.Name, f.Type)));

    /// <summary>便捷构造：按（名, 类型, 可写）列表创建（全部必填）。</summary>
    public static ViewSpec Of(params (string Name, ViewFieldType Type, bool Writable)[] fields)
        => new(fields.Select(static f => new ViewFieldDefinition(f.Name, f.Type, f.Writable)));

    /// <summary>
    /// 投影：按名从数据面取值。必填缺失＝<see cref="KeyNotFoundException"/>（A-1：数据面值缺失在首次执行 fail-fast）。
    /// 可选缺失＝类型默认值。值类型不符＝<see cref="InvalidCastException"/>（装载期已校验声明，运行期只剩数据面脏值）。
    /// </summary>
    internal object? Project(ViewFieldDefinition field, IDictionary<string, object?> plane)
    {
        if (!plane.TryGetValue(field.Name, out var value) || value is null)
        {
            if (field.Required)
            {
                throw new KeyNotFoundException(
                    $"视图必填字段 '{field.Name}' 在数据面缺失（键不存在或值为 null）；声明类型 {ViewFieldTypes.Format(field.Type)}。");
            }

            return ViewFieldTypes.DefaultValue(field.Type);
        }

        if (!ViewFieldTypes.Accepts(field.Type, value))
        {
            throw new InvalidCastException(
                $"数据面键 '{field.Name}' 的值类型 {value.GetType().Name} 不符合声明的视图类型 {ViewFieldTypes.Format(field.Type)}。");
        }

        return value;
    }

    /// <summary>数值归一（供 <see cref="ViewCarrier.Get{T}"/> 的数字读取）。</summary>
    internal static T ConvertNumber<T>(object value) where T : struct
        => (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
}
