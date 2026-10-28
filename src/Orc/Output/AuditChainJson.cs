using System.Text;
using System.Text.Json;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 审查链 JSON 序列化器（S-C4；真源）：把 <see cref="AuditChain"/> 导出为结构化 JSON——
/// 顶层 { schemaVersion, entities[], members[], edges[], wraps[], nodes[] }。
/// <para>
/// 批次 3（S10／A-4）schemaVersion 升 2：原 <c>hooks</c> 段（总线订阅读面）由四集合替代——
/// <c>entities</c>＝{ name, prefab, kind, hosts, host, oneShot, disposalSignal, origin, members[] }；
/// <c>members</c>＝{ entity, name, layer, visibility, signal, timing, declaredBy, edges }；
/// <c>edges</c>＝{ target, targetMember, declaration, declaring, declaringPrefab, declaringKind, member,
/// source, orderSource, position, layer, graphGap }（<c>source</c> 四类闭合、<c>orderSource</c> 边级）；
/// <c>wraps</c>＝{ target, phase, handler, order, sequence, declaring, source }。
/// </para>
/// <para>
/// 节点（触发器种类）＝{ id, stableKey, declaredKey, displayName, kind, serializable, hooks[], events[], instances[] }，
/// 与 schemaVersion 1 一致（能力不退化）。
/// 形式约定：id 为 16 位小写十六进制（稳定标识原始值）；kind／source／phase 为小写字符串；顺序＝确定性顺序。
/// </para>
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

        WriteGraph(writer, chain.Graph);
        WritePackages(writer, chain.Packages);
        WriteRandomSelectors(writer, chain.RandomSelectors);

        writer.WriteStartArray("nodes");
        foreach (var node in chain.Nodes)
        {
            WriteNode(writer, node);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// 包装载记录（S14 第 3 条）：<c>packages</c> 段——
    /// <c>{ id, required, status(loaded|skipped|failed), skipped, failures[] }</c>。
    /// 被跳过的可选包必须出现在这里（否则作者会以为包生效了）。
    /// </summary>
    private static void WritePackages(Utf8JsonWriter writer, IReadOnlyList<Orc.Core.PackLoadRecord> packages)
    {
        writer.WriteStartArray("packages");
        foreach (var package in packages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", package.PackId);
            writer.WriteBoolean("required", package.Required);
            writer.WriteString("status", package.Status switch
            {
                Orc.Core.PackLoadStatus.Loaded => "loaded",
                Orc.Core.PackLoadStatus.Skipped => "skipped",
                _ => "failed",
            });
            writer.WriteBoolean("skipped", package.Skipped);
            writer.WriteStartArray("failures");
            foreach (var failure in package.Failures)
            {
                writer.WriteStringValue(failure);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 随机选择器记录（R2c／R7）：<c>randomSelectors[] = { viewer, selector }</c>。
    /// 生产对局恒为空（<c>random</c> 被拒）；仅测试／沙盒／简单 AI 显式开启后才有条目。
    /// </summary>
    private static void WriteRandomSelectors(Utf8JsonWriter writer, IReadOnlyList<Orc.Core.RandomSelectionRecord> records)
    {
        writer.WriteStartArray("randomSelectors");
        foreach (var record in records)
        {
            writer.WriteStartObject();
            writer.WriteString("viewer", record.Viewer);
            writer.WriteString("selector", record.Selector);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>四个图集合（S10）：实体／成员／挂载边／包装。</summary>
    private static void WriteGraph(Utf8JsonWriter writer, Orc.Core.Model.AuditGraph graph)
    {
        writer.WriteStartArray("entities");
        foreach (var entity in graph.Entities)
        {
            writer.WriteStartObject();
            writer.WriteString("name", entity.Name);
            writer.WriteString("prefab", entity.PrefabId);
            writer.WriteString("kind", entity.Kind);
            writer.WriteString("hosts", entity.Hosts.ToString());
            writer.WriteString("host", entity.Host);
            writer.WriteBoolean("oneShot", entity.OneShot);
            writer.WriteString("disposalSignal", entity.DisposalSignal);
            writer.WriteString("origin", entity.Origin);
            writer.WriteStartArray("members");
            foreach (var member in entity.Members)
            {
                writer.WriteStringValue(member);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("members");
        foreach (var member in graph.Members)
        {
            writer.WriteStartObject();
            writer.WriteString("entity", member.Entity);
            writer.WriteString("name", member.Name);
            writer.WriteString("layer", member.Layer.ToString());
            writer.WriteString("visibility", member.Visibility.ToString());
            writer.WriteBoolean("signal", member.IsSignal);
            writer.WriteString("timing", member.Timing?.ToString());
            writer.WriteString("declaredBy", member.DeclaredBy);
            writer.WriteNumber("edges", member.EdgeCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("edges");
        foreach (var edge in graph.Edges)
        {
            writer.WriteStartObject();
            writer.WriteString("target", edge.Target);
            writer.WriteString("targetMember", edge.TargetMember);
            writer.WriteString("declaration", edge.Declaration);
            writer.WriteString("declaring", edge.DeclaringEntity);
            writer.WriteString("declaringPrefab", edge.DeclaringPrefab);
            writer.WriteString("declaringKind", edge.DeclaringKind);
            writer.WriteString("member", edge.Member);
            writer.WriteString("source", Source(edge.Source));
            writer.WriteString("orderSource", edge.OrderSource == Orc.Core.Model.EdgeOrderSource.Declared ? "declared" : "appended");
            writer.WriteNumber("position", edge.Position);
            writer.WriteString("layer", edge.MemberLayer.ToString());
            writer.WriteBoolean("graphGap", edge.GraphGap);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("wraps");
        foreach (var wrap in graph.Wraps)
        {
            writer.WriteStartObject();
            writer.WriteString("target", wrap.Target);
            writer.WriteString("phase", wrap.Phase.ToString().ToLowerInvariant());
            writer.WriteString("handler", wrap.Handler);
            writer.WriteString("order", wrap.Order);
            writer.WriteNumber("sequence", wrap.Sequence);
            writer.WriteString("declaring", wrap.DeclaringEntity);
            writer.WriteString("source", Source(wrap.Source));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>边来源的导出词（S10：四类闭合，不存在 unknown／空来源）。</summary>
    private static string Source(Orc.Core.Model.EdgeSource source) => source switch
    {
        Orc.Core.Model.EdgeSource.Explicit => "explicit",
        Orc.Core.Model.EdgeSource.Injected => "injected",
        Orc.Core.Model.EdgeSource.FrameworkDisposal => "framework:disposal",
        Orc.Core.Model.EdgeSource.FrameworkOneShot => "framework:one-shot",
        _ => source.ToString().ToLowerInvariant(),
    };

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
        writer.WriteString("kind", node.Kind);
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
            writer.WriteString("position", info.Position);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("downstream");
        foreach (var edge in node.Downstream)
        {
            writer.WriteStartObject();
            writer.WriteString("key", edge.DownstreamKey);
            if (resolved.TryGetValue(edge.DownstreamKey, out var id) && id is not null)
            {
                writer.WriteString("resolved", Hex(id.Value));
            }
            else
            {
                writer.WriteNull("resolved"); // 图缺口：未登记的目标
            }

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
