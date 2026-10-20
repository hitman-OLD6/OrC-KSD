using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orc.Output;

/// <summary>
/// 事件流段 JSON 序列化器：把一次「动作作用域」产出的段导出为
/// <c>{ sequence, timestamp, entries[], children[] }</c>——
/// 其中 <c>entries</c> 为该段聚合的**因果条目**（含冒泡；与段对象一致），
/// <c>children</c> 为本段新增的顶层子树（逐节点沿用 <see cref="EventStreamJson"/> 的写法，同一事实只有一个表示）。
/// <para>形式约定与 <see cref="EventStreamJson"/> 一致：kind/level 小写字符串、timestamp 为 ISO 8601（"O"，UTC）、
/// data 值经 <see cref="JsonValueWriter"/> 确定性降级。空段＝合法输出（空数组）。</para>
/// <para>传入 <see cref="IEventVisibility"/> 时按观察者脱敏（多人支持 S5）：不可见实体输出 <c>{"$hidden":true}</c>，
/// 不写名字、不写引用键。</para>
/// </summary>
public static class EventSegmentJson
{
    /// <summary>导出一段为 JSON 字符串（不脱敏）。</summary>
    /// <exception cref="ArgumentNullException">segment 为 null。</exception>
    public static string Serialize(EventSegment segment, bool indented = false)
        => Serialize(segment, indented, visibility: null, viewer: null);

    /// <summary>导出一段为 JSON 字符串（按观察者脱敏）。</summary>
    /// <exception cref="ArgumentNullException">segment 为 null。</exception>
    public static string Serialize(
        EventSegment segment,
        bool indented,
        IEventVisibility? visibility,
        object? viewer)
    {
        ArgumentNullException.ThrowIfNull(segment);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            WriteSegment(writer, segment, visibility, viewer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>导出多段为 JSON 数组字符串（顺序＝传入顺序；不脱敏）。</summary>
    /// <exception cref="ArgumentNullException">segments 为 null。</exception>
    public static string SerializeAll(IEnumerable<EventSegment> segments, bool indented = false)
        => SerializeAll(segments, indented, visibility: null, viewer: null);

    /// <summary>导出多段为 JSON 数组字符串（按观察者脱敏）。</summary>
    /// <exception cref="ArgumentNullException">segments 为 null。</exception>
    public static string SerializeAll(
        IEnumerable<EventSegment> segments,
        bool indented,
        IEventVisibility? visibility,
        object? viewer)
    {
        ArgumentNullException.ThrowIfNull(segments);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartArray();
            foreach (var segment in segments)
            {
                if (segment is null)
                {
                    continue;
                }

                WriteSegment(writer, segment, visibility, viewer);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>写一个段（不脱敏）。</summary>
    internal static void WriteSegment(Utf8JsonWriter writer, EventSegment segment)
        => WriteSegment(writer, segment, visibility: null, viewer: null);

    /// <summary>写一个段：sequence / timestamp / entries / children。</summary>
    internal static void WriteSegment(
        Utf8JsonWriter writer,
        EventSegment segment,
        IEventVisibility? visibility,
        object? viewer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", segment.Sequence);
        writer.WriteString("timestamp", segment.Timestamp.ToString("O", CultureInfo.InvariantCulture));

        writer.WriteStartArray("entries");
        foreach (var entry in segment.Entries)
        {
            EventStreamJson.WriteEntry(writer, entry, visibility, viewer);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("children");
        foreach (var child in segment.Children)
        {
            EventStreamJson.WriteNode(writer, child, visibility, viewer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
