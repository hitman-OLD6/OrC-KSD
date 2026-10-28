using System.Globalization;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 值降级写入器（S5 内部共用）：把任意值写入 <see cref="Utf8JsonWriter"/>——事件流 data 与快照组件属性共用同一「确定性处理」策略：
/// 标量（字符串/字符/布尔/数值）→ 原生表示；时间/Guid/Uri/枚举 → 字符串表示；string 键字典 → 对象（递归）；其它可枚举 → 数组（递归）；
/// 实体（<see cref="Entity"/>）与引用（<see cref="IRefInfo"/>）→ {"$ref": 引用键, "name": 名, "alive": 存活性}；
/// 被判定为不可见 → {"$hidden": true}（**不写名字、不写引用键**）；
/// 效果（<see cref="Effect"/>）→ {"$effect": 名}；其余未知对象 → {"$type": 类型全名}（不递归成员——防环）。
/// 递归深度上限 <see cref="MaxDepth"/>：超出 → {"$truncated": true}（防自引用环导致的无限递归）。
/// 不抛异常（除写入器自身的外部故障）：任意值均产出确定表示，供消费方解析。
/// </summary>
public static class JsonValueWriter
{
    /// <summary>字典/数组递归深度上限（防御性；超限输出截断标记，不报错）。</summary>
    internal const int MaxDepth = 32;

    /// <summary>写入一个任意值（顶层入口；不脱敏）。</summary>
    public static void Write(Utf8JsonWriter writer, object? value)
        => Write(writer, value, 0, visibility: null, viewer: null, hideEntities: false);

    /// <summary>写入一个任意值（带可见性判定；<paramref name="hideEntities"/> 为真＝条目级整体隐藏）。</summary>
    public static void Write(
        Utf8JsonWriter writer,
        object? value,
        IEventVisibility? visibility,
        object? viewer,
        bool hideEntities)
        => Write(writer, value, 0, visibility, viewer, hideEntities);

    private static void Write(
        Utf8JsonWriter writer,
        object? value,
        int depth,
        IEventVisibility? visibility,
        object? viewer,
        bool hideEntities)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case short sh:
                writer.WriteNumberValue(sh);
                return;
            case byte by:
                writer.WriteNumberValue(by);
                return;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                return;
            case uint ui:
                writer.WriteNumberValue(ui);
                return;
            case ulong ul:
                writer.WriteNumberValue(ul);
                return;
            case ushort us:
                writer.WriteNumberValue(us);
                return;
            case float f:
                WriteFloating(writer, f);
                return;
            case double d:
                WriteFloating(writer, d);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case char c:
                writer.WriteStringValue(c.ToString());
                return;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                return;
            case Guid g:
                writer.WriteStringValue(g.ToString("N"));
                return;
            case Uri u:
                writer.WriteStringValue(u.ToString());
                return;
            case Enum e:
                writer.WriteStringValue(e.ToString());
                return;
        }

        if (depth >= MaxDepth)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("$truncated", true);
            writer.WriteEndObject();
            return;
        }

        switch (value)
        {
            case Entity entity:
                WriteEntity(writer, entity, visibility, viewer, hideEntities);
                return;
            case IRefInfo reference:
                WriteReference(writer, reference, visibility, viewer, hideEntities);
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteDictionary(writer, readOnlyDictionary, depth, visibility, viewer, hideEntities);
                return;
            case IDictionary<string, object?> dictionary:
                WriteDictionary(writer, dictionary, depth, visibility, viewer, hideEntities);
                return;
            case System.Collections.IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    Write(writer, item, depth + 1, visibility, viewer, hideEntities);
                }

                writer.WriteEndArray();
                return;
        }

        var type = value.GetType();
        writer.WriteStartObject();
        writer.WriteString("$type", type.FullName ?? type.Name);
        writer.WriteEndObject();
    }

    /// <summary>写实体：可见＝{"$ref": GUID, "name": 名, "alive": 存活}；不可见＝{"$hidden": true}。</summary>
    private static void WriteEntity(
        Utf8JsonWriter writer,
        Entity entity,
        IEventVisibility? visibility,
        object? viewer,
        bool hideEntities)
    {
        if (hideEntities || visibility?.IsEntityVisible(entity, viewer) == false)
        {
            WriteHidden(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("$ref", entity.Id.ToString("N"));
        writer.WriteString("name", entity.Name);
        writer.WriteBoolean("$alive", entity.Life.IsAlive);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 写引用：可见＝{"$ref": 目标 GUID（无 GUID 时退化为名）, "name": 名, "alive": 存活}；
    /// 不可见＝{"$hidden": true}。
    /// </summary>
    private static void WriteReference(
        Utf8JsonWriter writer,
        IRefInfo reference,
        IEventVisibility? visibility,
        object? viewer,
        bool hideEntities)
    {
        if (hideEntities || visibility?.IsEntityVisible(reference, viewer) == false)
        {
            WriteHidden(writer);
            return;
        }

        var key = reference is Ref<Entity> { IsAlive: true } entityReference
            ? entityReference.Value.Id.ToString("N")
            : reference.Name;

        writer.WriteStartObject();
        writer.WriteString("$ref", key);
        writer.WriteString("name", reference.Name);
        writer.WriteBoolean("$alive", reference.IsAlive);
        writer.WriteEndObject();
    }

    private static void WriteHidden(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("$hidden", true);
        writer.WriteEndObject();
    }

    /// <summary>写 string 键字典为 JSON 对象（键原样、值递归）。</summary>
    private static void WriteDictionary(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>> entries,
        int depth,
        IEventVisibility? visibility,
        object? viewer,
        bool hideEntities)
    {
        writer.WriteStartObject();
        foreach (var pair in entries)
        {
            writer.WritePropertyName(pair.Key);
            Write(writer, pair.Value, depth + 1, visibility, viewer, hideEntities);
        }

        writer.WriteEndObject();
    }

    /// <summary>写浮点：非有限值（NaN/∞）JSON 无原生表示，降级为字符串（确定性处理，不抛）。</summary>
    private static void WriteFloating(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>写浮点：非有限值（NaN/∞）JSON 无原生表示，降级为字符串（确定性处理，不抛）。</summary>
    private static void WriteFloating(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNumberValue(value);
        }
    }
}
