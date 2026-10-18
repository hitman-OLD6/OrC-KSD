using System.Text;
using Orc.Core;

namespace Orc.Output;

/// <summary>
/// 审查链文本视图（S-C4；由 JSON 真源同源派生）：把 <see cref="AuditChain"/> 渲染为人类可读的缩进文本，
/// 满足"生成所有效果的文本视图"的口径。行结构（确定性、LF 换行）：
/// 首行 = 审查链 + schema 版本；随后 hooks 段（hook 名 [标识] ← 订阅者）与 nodes 段
/// （每种触发器种类一行头部 + 事件行 + 下游边行 + 实例行）。空链＝仅首行与计数行。
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

        sb.Append("hooks(").Append(chain.Hooks.Count).Append("):").Append('\n');
        foreach (var hook in chain.Hooks)
        {
            sb.Append("  ").Append(hook.Hook).Append(" [").Append(Hex(hook.Id.Value)).Append("] ← ");
            sb.Append(hook.Subscribers.Count == 0 ? "(无订阅者)" : string.Join(", ", hook.Subscribers));
            sb.Append('\n');
        }

        sb.Append("nodes(").Append(chain.Nodes.Count).Append("):").Append('\n');
        foreach (var node in chain.Nodes)
        {
            WriteNode(sb, node);
        }

        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, TriggerNode node)
    {
        sb.Append("  ").Append(node.Kind == TriggerKind.Active ? "[主动] " : "[被动] ")
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
                .Append(" band=").Append(info.BandValue)
                .Append(" prio=").Append(info.Priority);
            if (info.WrapperCount > 0)
            {
                sb.Append(" wrappers=").Append(info.WrapperCount)
                    .Append(" (b:").Append(info.BeforeWrapperCount)
                    .Append("/a:").Append(info.AfterWrapperCount)
                    .Append("/r:").Append(info.ReplaceWrapperCount).Append(')');
            }

            if (info.SeamCount > 0)
            {
                sb.Append(" seams=").Append(info.SeamCount).Append("(s:").Append(info.SeamWrapperCount).Append(')');
            }

            sb.Append('\n');

            foreach (var key in info.Downstream)
            {
                var resolved = node.Downstream.FirstOrDefault(e => string.Equals(e.DownstreamKey, key, StringComparison.Ordinal));
                sb.Append("        → ").Append(key);
                sb.Append(resolved?.ResolvedTriggerId is { IsUnset: false } id
                    ? " (resolved " + Hex(id.Value) + ")"
                    : " (未登记·图缺口)");
                sb.Append('\n');
            }
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
