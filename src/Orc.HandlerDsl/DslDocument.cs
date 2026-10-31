namespace Orc.HandlerDsl;

/// <summary>
/// 一份 handler DSL 文档（SPEC §2）：<c>schemaVersion</c>（硬）＋ 可选 <c>catalogRevision</c>（软，诊断用）
/// ＋ 节点树 <see cref="Actions"/>。
/// <para>
/// 文档**不**携带资产 id、成员名、挂载地址、触发时机、相位（R13）：归属由预制体／草案的成员声明持有。
/// </para>
/// </summary>
public sealed class DslDocument
{
    /// <summary>构造。</summary>
    public DslDocument(int schemaVersion, int? catalogRevision, IReadOnlyList<DslNode> actions)
    {
        SchemaVersion = schemaVersion;
        CatalogRevision = catalogRevision;
        Actions = actions;
    }

    /// <summary>契约版本（硬轴）。</summary>
    public int SchemaVersion { get; }

    /// <summary>文档针对的目录内容版本（软轴；只作诊断，永不作为拒绝理由）。</summary>
    public int? CatalogRevision { get; }

    /// <summary>节点树根（顺序即执行顺序；可为空）。</summary>
    public IReadOnlyList<DslNode> Actions { get; }
}
