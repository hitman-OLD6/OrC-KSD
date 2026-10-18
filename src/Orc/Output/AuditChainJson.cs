using System.Text;
using System.Text.Json;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 审查链 JSON 序列化器（S-C4；真源）：把 <see cref="AuditChain"/> 导出为结构化 JSON——
/// 顶层 { schemaVersion, hooks[], nodes[] }；节点（触发器种类）＝
/// { id, stableKey, declaredKey, displayName, kind, serializable, hooks[], events[], instances[] }；
/// 事件＝{ name, seq, band, priority, wrapperCount / beforeWrapperCount / afterWrapperCount / replaceWrapperCount / seamCount / seamWrapperCount, downstream[] }，downstream 项＝{ key, resolved }（未登记＝null＝图缺口）。
/// 形式约定：id 为 16 位小写十六进制（稳定标识原始值）；kind 为小写字符串；顺序＝管理器产出的确定性顺序。
/// 空链＝合法输出（空数组）；不抛错（除写入器自身外部故障）。
/// </summary>
public static class AuditChainJson
{
    /// <summary>导出审查链为 JSON 字符串。</summary>
    /// <exception cref="ArgumentNullException">chain 为 null。</exception>
    public static string Serialize(AuditChain chain, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(chain);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            WriteChain(writer, chain);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteChain(Utf8JsonWriter writer, AuditChain chain)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", chain.SchemaVersion);

        writer.WriteStartArray("hooks");
        foreach (var hook in chain.Hooks)
        {
            writer.WriteStartObject();
            writer.WriteString("hook", hook.Hook);
            writer.WriteString("id", Hex(hook.Id.Value));
            writer.WriteStartArray("subscribers");
            foreach (var subscriber in hook.Subscribers)
            {
                writer.WriteStringValue(subscriber);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("nodes");
        foreach (var node in chain.Nodes)
        {
            WriteNode(writer, node);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, TriggerNode node)
    {
        var resolved = new Dictionary<string, ulong?>(StringComparer.Ordinal);
        foreach (var edge in node.Downstream)
        {
            resolved[edge.DownstreamKey] = edge.ResolvedTriggerId?.Value;
        }

        writer.WriteStartObject();
        writer.WriteString("id", Hex(node.Id.Value));
        writer.WriteString("stableKey", node.StableKey);
        writer.WriteBoolean("declaredKey", node.HasDeclaredStableKey);
        writer.WriteString("displayName", node.DisplayName);
        writer.WriteString("kind", node.Kind == TriggerKind.Active ? "active" : "passive");
        writer.WriteBoolean("serializable", node.Serializable);

        writer.WriteStartArray("hooks");
        foreach (var hook in node.HookNames)
        {
            writer.WriteStringValue(hook);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("events");
        foreach (var info in node.Events)
        {
            writer.WriteStartObject();
            writer.WriteString("name", info.Name);
            writer.WriteNumber("seq", info.Seq);
            writer.WriteNumber("band", info.BandValue);
            writer.WriteNumber("priority", info.Priority);
            writer.WriteNumber("wrapperCount", info.WrapperCount);
            writer.WriteNumber("beforeWrapperCount", info.BeforeWrapperCount);
            writer.WriteNumber("afterWrapperCount", info.AfterWrapperCount);
            writer.WriteNumber("replaceWrapperCount", info.ReplaceWrapperCount);
            writer.WriteNumber("seamCount", info.SeamCount);
            writer.WriteNumber("seamWrapperCount", info.SeamWrapperCount);

            writer.WriteStartArray("downstream");
            foreach (var key in info.Downstream)
            {
                writer.WriteStartObject();
                writer.WriteString("key", key);
                if (resolved.TryGetValue(key, out var id) && id is not null)
                {
                    writer.WriteString("resolved", Hex(id.Value));
                }
                else
                {
                    writer.WriteNull("resolved"); // 图缺口：未登记的下游键
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("instances");
        foreach (var instance in node.Instances)
        {
            writer.WriteStartObject();
            writer.WriteString("host", instance.Host);
            writer.WriteString("origin", instance.Origin);
            writer.WriteBoolean("mounted", instance.Mounted);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string Hex(ulong value) => value.ToString("x16");
}
