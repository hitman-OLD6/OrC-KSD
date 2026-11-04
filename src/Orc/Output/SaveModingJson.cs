using System.Text.Json;
using Orc.Core.Model;

namespace Orc.Output;

/// <summary>
/// 存档 moding 的 JSON 序列化（本会话 S2；与 <see cref="AuditChainJson"/> 同库、同一确定性纪律）：
/// 键序＝固定段序 + 表内按键名 ordinal 排序；值表面封闭（标量与一维标量数组），无浮点非确定性写法。
/// <para>
/// 本类型只负责「形态确定地写出」；实体引用 → 实体名的转换发生在**导出时刻**（full-copy 导出器，
/// S3）——DTO 里已经是名字字符串。遇到封闭集之外的值＝fail-fast（调用方违约，不静默降级）。
/// </para>
/// </summary>
public static class SaveModingJson
{
    /// <summary>把一个存档 moding 写进当前 JSON 位置（对象形态：extends／slots／privateFields／attachments）。</summary>
    /// <exception cref="ArgumentNullException">writer／moding 为 null。</exception>
    public static void WriteModing(Utf8JsonWriter writer, SaveModing moding)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(moding);

        writer.WriteStartObject();
        writer.WriteString("extends", moding.Extends);
        writer.WriteStartObject("slots");
        WriteFields(writer, moding.Slots);
        writer.WriteEndObject();
        writer.WriteStartObject("privateFields");
        WriteFields(writer, moding.PrivateFields);
        writer.WriteEndObject();
        writer.WriteStartArray("attachments");
        foreach (var attachment in moding.Attachments)
        {
            writer.WriteStartObject();
            writer.WriteString("templateId", attachment.TemplateId);
            writer.WriteStartObject("slots");
            WriteFields(writer, attachment.Slots);
            writer.WriteEndObject();
            writer.WriteStartObject("privateFields");
            WriteFields(writer, attachment.PrivateFields);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>写一张字段表（键按 ordinal 排序——DTO 不承诺声明序，确定性优先）。</summary>
    internal static void WriteFields(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> fields)
    {
        foreach (var pair in fields.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            WriteValue(writer, pair.Value);
        }
    }

    /// <summary>值表面（封闭集）：null／布尔／字符串／数字／一维标量数组。越界＝fail-fast。</summary>
    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
            case decimal:
                WriteNumber(writer, value);
                break;
            case float:
            case double:
                WriteNumber(writer, value);
                break;
            case Array array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    if (item is Array)
                    {
                        throw new JsonException("存档字段值不支持嵌套数组（封闭集只允许一维标量数组）。");
                    }

                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new JsonException(
                    $"存档字段值类型 {value.GetType().Name} 超出封闭集"
                    + "（只允许标量与一维标量数组；实体引用须在导出时转成实体名）。");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            default:
                throw new JsonException($"数字形态 {value.GetType().Name} 不受支持。");
        }
    }
}
