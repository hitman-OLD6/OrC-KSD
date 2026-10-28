using System.Text;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 审查链文本视图（S-C4；由 JSON 真源同源派生）：把 <see cref="AuditChain"/> 渲染为人类可读的缩进文本。
/// 行结构（确定性、LF 换行）：
/// 首行 = 审查链 + schema 版本；随后实体／成员／挂载边／包装四段（S10 的实体—成员—挂载边图）
/// 与 nodes 段（每种触发器种类一行头部 + 事件行 + 下游边行 + 实例行）。空链＝仅首行与计数行。
/// </summary>
public static class AuditChainText
{
    /// <summary>渲染审查链为文本视图。</summary>
    /// <exception cref="ArgumentNullException">chain 为 null。</exception>
    public static string Serialize(AuditChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var sb = new StringBuilder();
        sb.Append("审查链 schemaVersion=").Append(chain.SchemaVersion).Append('\n');

        WriteGraph(sb, chain.Graph);
        WritePackages(sb, chain.Packages);
        WriteRandomSelectors(sb, chain.RandomSelectors);

        sb.Append("nodes(").Append(chain.Nodes.Count).Append("):").Append('\n');
        foreach (var node in chain.Nodes)
        {
            WriteNode(sb, node);
        }

        return sb.ToString();
    }

    /// <summary>包装载记录（S14 第 3 条）：被跳过的可选包与失败的必需包逐条一行（含首个失败原因）。</summary>
    private static void WritePackages(StringBuilder sb, IReadOnlyList<PackLoadRecord> packages)
    {
        sb.Append("packages(").Append(packages.Count).Append("):").Append('\n');
        foreach (var package in packages)
        {
            sb.Append("  ").Append(package.PackId)
                .Append(package.Required ? " required" : " optional")
                .Append(" status=").Append(package.Status switch
                {
                    PackLoadStatus.Loaded => "loaded",
                    PackLoadStatus.Skipped => "skipped",
                    _ => "failed",
                });

            if (package.Failures.Count > 0)
            {
                sb.Append(" failures=").Append(package.Failures.Count)
                    .Append(" first=").Append(package.Failures[0]);
            }

            sb.Append('\n');
        }
    }

    /// <summary>随机选择器记录（R2c／R7）：一行一条；生产对局恒为空。</summary>
    private static void WriteRandomSelectors(StringBuilder sb, IReadOnlyList<RandomSelectionRecord> records)
    {
        sb.Append("randomSelectors(").Append(records.Count).Append("):").Append('\n');
        foreach (var record in records)
        {
            sb.Append("  ").Append(record.Viewer).Append(' ').Append(record.Selector).Append('\n');
        }
    }

    /// <summary>实体—成员—挂载边图（S10）：四段文本。</summary>
    private static void WriteGraph(StringBuilder sb, Orc.Core.Model.AuditGraph graph)
    {
        sb.Append("entities(").Append(graph.Entities.Count).Append("):").Append('\n');
        foreach (var entity in graph.Entities)
        {
            sb.Append("  ").Append(entity.Name).Append(" : ").Append(entity.PrefabId)
                .Append(" kind=").Append(entity.Kind)
                .Append(" origin=").Append(entity.Origin);
            if (entity.Host is not null)
            {
                sb.Append(" host=").Append(entity.Host);
            }

            if (entity.OneShot)
            {
                sb.Append(" oneShot=true");
            }

            if (entity.DisposalSignal is not null)
            {
                sb.Append(" disposal=").Append(entity.DisposalSignal);
            }

            sb.Append(" members=").Append(string.Join(", ", entity.Members)).Append('\n');
        }

        sb.Append("edges(").Append(graph.Edges.Count).Append("):").Append('\n');
        foreach (var edge in graph.Edges)
        {
            sb.Append("  ").Append(edge.Target).Append(" ← ").Append(edge.DeclaringEntity)
                .Append('.').Append(edge.Member)
                .Append(" source=").Append(edge.Source.ToString().ToLowerInvariant())
                .Append(" order=").Append(edge.OrderSource == Orc.Core.Model.EdgeOrderSource.Declared
                    ? "declared"
                    : "appended")
                .Append('#').Append(edge.Position)
                .Append(" layer=").Append(edge.MemberLayer);
            if (edge.GraphGap)
            {
                sb.Append(" (未解析·图缺口)");
            }

            sb.Append('\n');
        }

        sb.Append("wraps(").Append(graph.Wraps.Count).Append("):").Append('\n');
        foreach (var wrap in graph.Wraps)
        {
            sb.Append("  ").Append(wrap.Target).Append(" phase=").Append(wrap.Phase.ToString().ToLowerInvariant())
                .Append(" ← ").Append(wrap.Handler)
                .Append(" order=").Append(wrap.Order)
                .Append(" source=").Append(wrap.Source.ToString().ToLowerInvariant())
                .Append('\n');
        }
    }

    private static void WriteNode(StringBuilder sb, TriggerNode node)
    {
        sb.Append("  ").Append(node.Kind == "active" ? "[主动] " : "[被动] ")
            .Append(node.DisplayName)
            .Append(" key=").Append(node.StableKey)
            .Append(node.HasDeclaredStableKey ? string.Empty : "(弱身份)")
            .Append(" id=").Append(Hex(node.Id.Value))
            .Append(" serializable=").Append(node.Serializable ? "true" : "false")
            .Append(" 实例=").Append(node.Instances.Count)
            .Append('\n');

        if (node.HookNames.Count > 0)
        {
            sb.Append("      hooks: ").Append(string.Join(", ", node.HookNames)).Append('\n');
        }

        foreach (var info in node.Events)
        {
            sb.Append("      #").Append(info.Seq).Append(' ').Append(info.Name)
                .Append(" pos=").Append(info.Position)
                .Append('\n');
        }

        foreach (var edge in node.Downstream)
        {
            sb.Append("      → ").Append(edge.DownstreamKey);
            sb.Append(edge.ResolvedTriggerId is { IsUnset: false } id
                ? " (resolved " + Hex(id.Value) + ")"
                : " (未登记·图缺口)");
            sb.Append('\n');
        }

        foreach (var instance in node.Instances)
        {
            sb.Append("      - ").Append(instance.Host)
                .Append(" origin=").Append(instance.Origin)
                .Append(" mounted=").Append(instance.Mounted ? "true" : "false")
                .Append('\n');
        }
    }

    private static string Hex(ulong value) => value.ToString("x16");
}
