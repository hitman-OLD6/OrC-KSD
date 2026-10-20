using System.Globalization;
using System.Text;
using System.Text.Json;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 事件流 JSON 序列化器（S5）：把事件流导出为树形 JSON——
/// 节点＝{ id, entries, children }、条目＝{ kind, timestamp, level, source, message, keywords, data }。
/// 导出语义（树形＋本地条目）：节点的 entries 仅含该流自身写入的条目（<see cref="EventStream.LocalEntries"/>），
/// 后代条目只出现在其后代节点；同一逻辑条目在整棵树中恰出现一次（位于其写入流的节点下）。
/// 从任意流导出＝以该流为根的子树（不含祖先、含全部后代）；总流导出＝整棵树（同一入口、同一语义）。
/// 形式约定（本工具）：kind / level 为小写字符串；timestamp 为 ISO 8601（"O" round-trip，UTC）；
/// data 值经 <see cref="JsonValueWriter"/> 确定性降级（不可直接表示的值输出降级对象，不抛错）。
/// 传入 <see cref="IEventVisibility"/> 时按观察者脱敏（多人支持 S5）：隐藏实体输出 <c>{"$hidden":true}</c>。
/// 空流/空树＝合法输出（空数组，不报错）；复杂度近线性于条目总数；不设深度/规模上限（树递归）。
/// </summary>
public static class EventStreamJson
{
    /// <summary>导出以 stream 为根的子树为 JSON 字符串（不脱敏）。</summary>
    /// <exception cref="ArgumentNullException">stream 为 null。</exception>
    public static string Serialize(EventStream stream, bool indented = false)
        => Serialize(stream, indented, visibility: null, viewer: null);

    /// <summary>导出以 stream 为根的子树为 JSON 字符串（按观察者脱敏）。</summary>
    /// <exception cref="ArgumentNullException">stream 为 null。</exception>
    public static string Serialize(
        EventStream stream,
        bool indented,
        IEventVisibility? visibility,
        object? viewer)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            WriteNode(writer, stream, visibility, viewer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>写一个节点（递归子树；不脱敏）。</summary>
    internal static void WriteNode(Utf8JsonWriter writer, EventStream stream)
        => WriteNode(writer, stream, visibility: null, viewer: null);

    /// <summary>写一个节点（递归子树）：id / entries（本地条目，写入时序）/ children（挂载时序）。</summary>
    internal static void WriteNode(
        Utf8JsonWriter writer,
        EventStream stream,
        IEventVisibility? visibility,
        object? viewer)
    {
        writer.WriteStartObject();
        writer.WriteString("id", stream.Id);

        writer.WriteStartArray("entries");
        foreach (var entry in stream.LocalEntries)
        {
            WriteEntry(writer, entry, visibility, viewer);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("children");
        foreach (var child in stream.Children)
        {
            WriteNode(writer, child, visibility, viewer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>写一条条目（不脱敏）。</summary>
    internal static void WriteEntry(Utf8JsonWriter writer, LogEntry entry)
        => WriteEntry(writer, entry, visibility: null, viewer: null);

    /// <summary>写一条条目：kind / timestamp / level / source / message / keywords / data。</summary>
    internal static void WriteEntry(
        Utf8JsonWriter writer,
        LogEntry entry,
        IEventVisibility? visibility,
        object? viewer)
    {
        // 条目级判定：整条 data 中的实体按"是否对观察者隐藏"处理（如对手抽牌只留事件骨架）。
        var hideEntities = visibility?.IsEntryHidden(entry.Data, viewer) ?? false;

        writer.WriteStartObject();
        writer.WriteString("kind", KindName(entry.Kind));
        writer.WriteString("timestamp", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("level", LevelName(entry.Level));
        writer.WriteString("source", entry.Source);
        writer.WriteString("message", entry.Message);

        writer.WriteStartArray("keywords");
        foreach (var keyword in entry.Keywords)
        {
            writer.WriteStringValue(keyword);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("data");
        foreach (var pair in entry.Data)
        {
            writer.WritePropertyName(pair.Key);
            JsonValueWriter.Write(writer, pair.Value, visibility, viewer, hideEntities);
        }

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>kind 的 JSON 表示（小写字符串）。</summary>
    private static string KindName(LogEntryKind kind) => kind switch
    {
        LogEntryKind.Log => "log",
        LogEntryKind.Attach => "attach",
        LogEntryKind.Update => "update",
        _ => kind.ToString(),
    };

    /// <summary>level 的 JSON 表示（小写字符串）。</summary>
    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => level.ToString(),
    };
}
