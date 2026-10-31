namespace Orc.HandlerDsl;

/// <summary>
/// 视图字段声明的一条（挂载点目标成员的视图声明，SPEC §11）：
/// 名字 ＋ 引脚类型 ＋ **可写性**（只有 <see cref="Writable"/> 允许 <c>write</c> 写）。
/// </summary>
/// <param name="Name">字段名（DSL 里的 <c>{"view":"…"}</c> 或 <c>{"op":"write","field":"…"}</c> 按它判）。</param>
/// <param name="Type">字段类型（已按 SPEC §6.6 归一为引脚类型）。</param>
/// <param name="Writable">是否可写（<c>write</c> 的唯一依据，R17）。</param>
public sealed record DslViewField(string Name, PinType Type, bool Writable = false);

/// <summary>
/// 挂载点目标成员的**视图声明快照**（校验与生成的输入）。
/// <para>
/// 为什么不是 <c>ViewSpec</c>：契约层不认识 <c>Orc.Core.Model</c> 的视图类型；装载器（S8）把
/// <c>PrefabType.FindMember(name).View</c> 投影成本类型再传进来，投影是纯数据搬运。
/// </para>
/// </summary>
public sealed class DslView
{
    private readonly Dictionary<string, DslViewField> _fields;

    /// <summary>构造。</summary>
    /// <exception cref="ArgumentNullException">fields 为 null 或含 null 元素。</exception>
    /// <exception cref="ArgumentException">字段名重复。</exception>
    public DslView(IEnumerable<DslViewField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        _fields = new Dictionary<string, DslViewField>(StringComparer.Ordinal);
        var list = new List<DslViewField>();
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!_fields.TryAdd(field.Name, field))
            {
                throw new ArgumentException($"视图字段 '{field.Name}' 重复声明。", nameof(fields));
            }

            list.Add(field);
        }

        Fields = list;
        Types = list.ToDictionary(static f => f.Name, static f => f.Type, StringComparer.Ordinal);
    }

    /// <summary>空视图（无字段：任何 <c>view</c>／<c>write</c> 都会被拒）。</summary>
    public static DslView Empty { get; } = new(Array.Empty<DslViewField>());

    /// <summary>字段清单（声明序）。</summary>
    public IReadOnlyList<DslViewField> Fields { get; }

    /// <summary>字段名 → 引脚类型（类型推导用）。</summary>
    public IReadOnlyDictionary<string, PinType> Types { get; }

    /// <summary>按名取字段。</summary>
    public bool TryGetField(string name, out DslViewField field) =>
        _fields.TryGetValue(name, out field!);
}
