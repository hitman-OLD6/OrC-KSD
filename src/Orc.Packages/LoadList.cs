using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 加载列表中的一条**拓扑节点**（D17/R4）：宿主资产 ＋ 随宿主**同层**装载的内含资产。
/// <para>
/// 内含资产（sidecar 中 <c>anchor</c> 指向 stableKey／事件 id 的触发器／handler）**不是独立拓扑节点**——
/// 它们挂在宿主条目上，随宿主所在层一起装载（否则「效果依赖其触发器」与「触发器依赖其宿主效果」必然成环）。
/// </para>
/// </summary>
/// <param name="Asset">宿主资产（anchor 为 null 的注册条目）。</param>
/// <param name="Embedded">随宿主同层装载的内含资产（稳定序：种类 → 应用层 ID）。</param>
public sealed record LoadListItem(AssetRegistration Asset, IReadOnlyList<AssetRegistration> Embedded)
{
    /// <summary>宿主资产 GUID。</summary>
    public Guid AssetGuid => Asset.AssetGuid;

    /// <summary>宿主包内局部 ID（层内排序键）。</summary>
    public string LocalId => Asset.LocalId;

    /// <summary>宿主资产种类。</summary>
    public AssetKind Kind => Asset.Kind;

    /// <summary>宿主应用层 ID。</summary>
    public string AppId => Asset.AppId;

    /// <summary>资产域。</summary>
    public AssetDomain Domain => Asset.Domain;

    /// <summary>宿主资产版本。</summary>
    public AssetVersion Version => Asset.Version;

    /// <summary>
    /// 层比对签名（D13/D21）：宿主 GUID ＋ 内含资产 GUID（**一层内已按局部 ID 字典序固定**，故按序比对即可）。
    /// </summary>
    public string Signature => Embedded.Count == 0
        ? Asset.AssetGuid.ToString("N")
        : $"({Asset.AssetGuid:N};{string.Join(",", Embedded.Select(asset => asset.AssetGuid.ToString("N")))})";

    /// <inheritdoc />
    public override string ToString()
        => Embedded.Count == 0
            ? $"{AssetKindTokens.Token(Kind)}：{AppId}#{Version}"
            : $"{AssetKindTokens.Token(Kind)}：{AppId}#{Version}（内含 {Embedded.Count}）";
}

/// <summary>
/// 加载列表的一层（D10/R7）：**第 <paramref name="Index"/> 层只依赖 ≤ <c>Index-1</c> 层**；
/// 层内按局部 ID 字典序（D16/D21——仅供确定性展示与比对，**不承载语义顺序**）。
/// </summary>
/// <param name="Index">层号（第 0 层无依赖）。</param>
/// <param name="Items">该层条目（局部 ID 字典序）。</param>
public sealed record LoadListLayer(int Index, IReadOnlyList<LoadListItem> Items)
{
    /// <summary>该层条目数（不含内含资产）。</summary>
    public int Count => Items.Count;

    /// <summary>层签名（层比对用）。</summary>
    public string Signature => string.Join("|", Items.Select(item => item.Signature));
}

/// <summary>
/// **加载列表**（R7/D10）：二维层状列表——第 0 层无依赖、第 N 层只依赖 ≤N-1 层；
/// 层内按局部 ID 字典序。依赖检测、拓扑排序、加载顺序**已在构造时一次算完**
/// （<see cref="LoadListPlanner"/>），运行期不做任何依赖查找。
/// <para>
/// 域是列表的第一等属性：正式域与测试域各自一张列表，**先正式后测试**装载（R15）。
/// </para>
/// </summary>
public sealed class LoadList
{
    private readonly LoadListLayer[] _layers;
    private readonly LoadListItem[] _items;

    internal LoadList(AssetDomain domain, IEnumerable<LoadListLayer> layers)
    {
        Domain = domain;
        _layers = layers.ToArray();
        _items = _layers.SelectMany(layer => layer.Items).ToArray();
    }

    /// <summary>空列表（某域本次无资产）。</summary>
    public static LoadList Empty(AssetDomain domain) => new(domain, Array.Empty<LoadListLayer>());

    /// <summary>资产域。</summary>
    public AssetDomain Domain { get; }

    /// <summary>层（第 0 层起连续）。</summary>
    public IReadOnlyList<LoadListLayer> Layers => _layers;

    /// <summary>全部条目（层序展开；层内保持局部 ID 字典序）。</summary>
    public IReadOnlyList<LoadListItem> Items => _items;

    /// <summary>层数。</summary>
    public int LayerCount => _layers.Length;

    /// <summary>拓扑节点数（**不含**内含资产）。</summary>
    public int Count => _items.Length;

    /// <summary>内含资产总数（随宿主同层装载）。</summary>
    public int EmbeddedCount => _items.Sum(item => item.Embedded.Count);

    /// <summary>按 GUID 查条目（宿主或内含资产——内含资产返回其宿主条目；未找到＝null）。</summary>
    public LoadListItem? Find(Guid assetGuid)
        => _items.FirstOrDefault(item =>
            item.AssetGuid == assetGuid || item.Embedded.Any(asset => asset.AssetGuid == assetGuid));

    /// <summary>从第 <paramref name="layer"/> 层起的全部条目（含该层；越界＝空）。</summary>
    public IReadOnlyList<LoadListItem> FromLayer(int layer)
        => layer <= 0
            ? _items
            : layer >= LayerCount ? Array.Empty<LoadListItem>() : _layers.Skip(layer).SelectMany(l => l.Items).ToArray();

    /// <summary>
    /// 与旧列表按层比对（D13）：返回**首个发生变更的层号**；完全一致＝-1。
    /// <para>旧列表为 null（首次装载）⇒ 非空列表返回 0、空列表返回 -1。</para>
    /// </summary>
    /// <param name="previous">旧列表（null＝无缓存）。</param>
    public int FirstChangedLayer(LoadList? previous)
    {
        if (previous is null)
        {
            return LayerCount == 0 ? -1 : 0;
        }

        var max = Math.Max(LayerCount, previous.LayerCount);
        for (var i = 0; i < max; i++)
        {
            var current = i < LayerCount ? _layers[i].Signature : null;
            var old = i < previous.LayerCount ? previous._layers[i].Signature : null;
            if (!string.Equals(current, old, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{AssetDomainTokens.Token(Domain)}：{LayerCount} 层／{Count} 节点";
}

/// <summary>
/// 一次**加载计算**的产出（R7/D10）：正式域与测试域各一张层状列表 ＋ 计算期问题报告。
/// <para>问题一律**结构化**报告；只有 <see cref="AssetLoadIssue.IsFatal"/> 为真的问题才会使本次计算失败。</para>
/// </summary>
/// <param name="Formal">正式加载列表。</param>
/// <param name="Test">测试加载列表。</param>
/// <param name="Issues">计算期问题（资产级：不整段失败；结构性：见 <see cref="IsValid"/>）。</param>
public sealed record AssetLoadPlan(
    LoadList Formal,
    LoadList Test,
    IReadOnlyList<AssetLoadIssue> Issues)
{
    /// <summary>取某域的列表。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的域。</exception>
    public LoadList For(AssetDomain domain) => domain switch
    {
        AssetDomain.Formal => Formal,
        AssetDomain.Test => Test,
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "未定义的资产域。"),
    };

    /// <summary>两域节点总数。</summary>
    public int Count => Formal.Count + Test.Count;

    /// <summary>是否可加载（无结构性失败：lock 冲突／环依赖）。</summary>
    public bool IsValid => !Issues.Any(issue => issue.IsFatal);
}
