using System.Reflection;
using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Migration;

/// <summary>
/// 旧视图（C# 类，<c>[ContextView]</c>）→ 新视图声明（字段清单）的一次性桥（S11）。
/// <para>
/// 迁移器**读**旧视图类只为得到一次性的字段清单：字段名、封闭内建类型、读写、必填。
/// 产物里不出现 <c>viewType</c>，新 schema 的视图完全由字段清单表达（R1a）——因此本类型只在
/// 迁移期使用，批次 5 旧类型退役时随迁移器一起离场。
/// </para>
/// <para>
/// 类型映射（封闭内建集）：<c>string</c>／<c>bool</c>／数字按字面；数组按其元素类型；
/// 其余引用类型（<c>Card?</c>／<c>Ref&lt;Entity&gt;?</c>／<c>IReadOnlyList&lt;Selector&gt;?</c>）一律映射为
/// <see cref="ViewFieldType.Entity"/>——封闭集里"实体引用"接受任何非 null 对象，转换语义由 csx 自行做（R1a）。
/// 这是无损的行为映射：旧路径从不校验字段类型，新路径也不因此多拒绝任何值。
/// </para>
/// <para><c>Host</c>／<c>Owner</c> 不进入字段清单：它们是承载视图的内建成员，由框架回填、不来自数据面（R4）。</para>
/// </summary>
public static class LegacyViewCatalog
{
    private static readonly Dictionary<string, ViewSpec> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>承载视图的内建成员名（不进入数据面字段清单）。</summary>
    public static IReadOnlyList<string> BuiltInViewMembers { get; } = new[] { "Host", "Owner" };

    /// <summary>解析旧视图类型全名（可带 <c>, Assembly</c> 后缀）为字段清单。</summary>
    /// <exception cref="ArgumentNullException">viewTypeName 为 null。</exception>
    /// <exception cref="InvalidOperationException">视图类型无法解析（迁移器不猜、不静默降级）。</exception>
    public static ViewSpec Resolve(string viewTypeName)
    {
        ArgumentNullException.ThrowIfNull(viewTypeName);

        var name = viewTypeName.Split(',')[0].Trim();
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var type = FindType(name)
                ?? throw new InvalidOperationException(
                    $"视图类型 '{viewTypeName}' 无法解析（迁移器需要读到旧视图的字段清单，不能猜）。");

            var spec = Build(type);
            Cache[name] = spec;
            return spec;
        }
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, throwOnError: false);
        if (direct is not null)
        {
            return direct;
        }

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            var found = assembly.GetType(fullName, throwOnError: false);
            if (found is not null)
            {
                return found;
            }
        }

        // 短名兜底（`EffectSnapshot.viewType` 只存 `Type.Name`，如 `CardEventView`——旧 prefab JSON 里存的是全名，
        // 但内存快照序列化回来是短名）。判据：只接受带 [ContextView] 的类型，且**简单名必须唯一**。
        // 多义＝拒绝（不猜）——这与本类"迁移器不猜、不静默降级"的口径一致。
        if (fullName.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var matches = new List<Type>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in SafeTypes(assembly))
            {
                if (string.Equals(type.Name, fullName, StringComparison.Ordinal)
                    && type.IsDefined(typeof(ContextViewAttribute), inherit: true))
                {
                    matches.Add(type);
                }
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new InvalidOperationException(
                $"视图短名 '{fullName}' 有 {matches.Count} 个候选（{string.Join("、", matches.Select(static t => t.FullName))}）——"
                + "迁移器不猜：请用全名声明 viewType。"),
        };
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
    }

    private static ViewSpec Build(Type viewType)
    {
        var fields = new List<ViewFieldDefinition>();
        var properties = viewType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.GetIndexParameters().Length == 0)
            .OrderBy(static p => p.MetadataToken)
            .ToArray();

        foreach (var property in properties)
        {
            if (BuiltInViewMembers.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var read = property.IsDefined(typeof(ReadAttribute), inherit: true);
            var mutate = property.IsDefined(typeof(MutateAttribute), inherit: true);
            var optional = property.IsDefined(typeof(OptionalAttribute), inherit: true);
            if (!read && !mutate)
            {
                continue;
            }

            fields.Add(new ViewFieldDefinition(
                property.Name,
                MapType(property.PropertyType),
                writable: mutate,
                required: !optional));
        }

        return new ViewSpec(fields);
    }

    /// <summary>CLR 类型 → 封闭内建类型（见类型注释的映射规则）。</summary>
    /// <exception cref="ArgumentNullException">type 为 null。</exception>
    public static ViewFieldType MapType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return ViewFieldType.String;
        }

        if (underlying == typeof(bool))
        {
            return ViewFieldType.Boolean;
        }

        if (IsNumeric(underlying))
        {
            return ViewFieldType.Number;
        }

        if (underlying.IsArray && underlying.GetArrayRank() == 1)
        {
            var element = underlying.GetElementType()!;
            return MapType(element) switch
            {
                ViewFieldType.Number => ViewFieldType.NumberArray,
                ViewFieldType.String => ViewFieldType.StringArray,
                ViewFieldType.Boolean => ViewFieldType.BooleanArray,
                _ => ViewFieldType.EntityArray,
            };
        }

        return ViewFieldType.Entity;
    }

    private static bool IsNumeric(Type type)
        => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
           || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
           || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}
