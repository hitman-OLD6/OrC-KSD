using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Packages.Loading;

namespace Orc.Packages;

/// <summary>元数据来源：sidecar 声明／无 sidecar 的合成元数据（R2「无元数据」路径）。</summary>
public enum AssetMetadataSource
{
    /// <summary>由 sidecar 声明。</summary>
    Sidecar = 0,

    /// <summary>源文件在而 sidecar 缺 ⇒ 合成默认元数据（按「无元数据」走三档判定；不阻断包）。</summary>
    Synthesized = 1,

    /// <summary>插件代码声明（D63：asm 插件无源文件，由 <c>IPluginAssetSource</c> 提供；清单文件是其落盘镜像）。</summary>
    PluginDeclared = 2,
}

/// <summary>
/// 一条**资产版本**的注册条目（R1/D10）：元数据 ＋ 已生成的 DTO（卡定义／效果预制体）。
/// <b>全仓只有注册面持有 DTO</b>——中间层只持 GUID 映射。
/// </summary>
/// <param name="Metadata">资产元数据（含派生的 GUID）。</param>
/// <param name="Domain">资产域（正式／测试）。</param>
/// <param name="PackageId">所属包 id（人类可读；应用层前缀来源）。</param>
/// <param name="AppId">应用层 ID（<c>pack/{包id}/{局部ID}</c>，不含种类）。</param>
/// <param name="MetadataSource">元数据来源。</param>
/// <param name="Card">卡定义 DTO（非卡资产＝null）。</param>
/// <param name="Prefab">效果预制体 DTO（内含的 trigger/handler 共享宿主预制体；未解析＝null）。</param>
/// <param name="SourcePath">源文件绝对路径（无源文件＝null）。</param>
public sealed record AssetRegistration(
    AssetMetadata Metadata,
    AssetDomain Domain,
    string PackageId,
    string AppId,
    AssetMetadataSource MetadataSource,
    CardDefinition? Card,
    EffectSnapshot? Prefab,
    string? SourcePath)
{
    /// <summary>资产 GUID（版本级身份）。</summary>
    public Guid AssetGuid => Metadata.Guid;

    /// <summary>
    /// 所属包的稳定身份（`manifest.guid`；S6 加性：报告需要资产名 `<c>{种类}：{局部ID}@{包GUID8}#{版本}</c>`）。
    /// 未设置（<see cref="Guid.Empty"/>）时资产名的 GUID8 段退化为 <c>00000000</c>。
    /// </summary>
    public Guid PackageGuid { get; init; }

    /// <summary>
    /// 注册期记录的**字段级问题清单**（R5/D40）：宽松解析的产出，随资产进注册面供加载期判三档
    /// （非法项＝该资产不生成卡 DTO，但仍注册身份——报告与卡位因此可见其字段问题）。
    /// 非卡资产与无问题卡＝空。
    /// </summary>
    public IReadOnlyList<CardFieldIssue> LoadIssues { get; init; } = Array.Empty<CardFieldIssue>();

    /// <summary>包 GUID 前 8 位（资产名用）。</summary>
    public string PackageGuid8 => PackageGuid == Guid.Empty
        ? "00000000"
        : PackageGuid.ToString("N")[..8];

    /// <summary>资产全名（R6：<c>{种类}：{局部ID}@{包GUID8}#{版本}</c>；版本按书写段数展示）。</summary>
    public string AssetName
        => $"{AssetKindTokens.Token(Kind)}{Orc.Core.AssetName.CanonicalSeparator}{LocalId}@{PackageGuid8}#{Version}";

    /// <summary>资产种类。</summary>
    public AssetKind Kind => Metadata.Kind;

    /// <summary>包内局部 ID。</summary>
    public string LocalId => Metadata.Id;

    /// <summary>资产版本。</summary>
    public AssetVersion Version => Metadata.Version;

    /// <summary>
    /// 是否为**内含资产**（R4/D17）：sidecar 的 <c>anchor</c> 指向宿主文件内的 stableKey／事件 id。
    /// 内含资产不作为独立拓扑节点（随宿主同层装载）。
    /// </summary>
    public bool IsEmbedded => Metadata.Anchor is not null;
}

/// <summary>
/// **资产注册表**（进程级；I2/D10）：键＝GUID，存「元数据 ＋ 已生成 DTO」，
/// 二级索引 <c>(包ID, 种类, 局部ID) → 有序版本列表</c>。
/// <para>
/// 职责**只有注册面**：<b>不做任何版本选择</b>（对外只给 <see cref="VersionsOf"/> 候选，
/// 定版由 lock／遮蔽规则在中间层之上完成）。同资产多版本并存注册（不同 GUID）。
/// </para>
/// </summary>
public sealed class AssetRegistry
{
    private readonly Dictionary<Guid, AssetRegistration> _byGuid = new();
    private readonly Dictionary<AssetKey, List<Guid>> _index = new();
    private readonly Dictionary<string, RuntimeCardRegistration> _runtimeCards = new(StringComparer.Ordinal);

    /// <summary>
    /// **卡牌加载器声明面**（S6；进程级信任边界的挂载点）：sidecar 的 <c>loader</c> 只能引用此处已注册的名字。
    /// 与对局级 <c>TriggerRegistry(layer=LowLevel)</c>（仅记账）**不是同一层**，不得混用。
    /// </summary>
    public CardLoaderRegistry CardLoaders { get; } = new();

    /// <summary>已注册资产版本（注册序）。</summary>
    public IReadOnlyList<AssetRegistration> All => _byGuid.Values.ToArray();

    /// <summary>已注册资产版本数。</summary>
    public int Count => _byGuid.Count;

    /// <summary>注册一条资产版本（同 GUID 已存在＝拒绝；只追加不覆盖）。</summary>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一 GUID 重复注册（含跨包 GUID 冲突——结构性）。</exception>
    public void Register(AssetRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!TryRegister(registration, out var failure))
        {
            throw new InvalidOperationException(failure);
        }
    }

    /// <summary>尝试注册（同 GUID 已存在＝false ＋ 原因；不抛错）。</summary>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    public bool TryRegister(AssetRegistration registration, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (_byGuid.TryGetValue(registration.AssetGuid, out var existing))
        {
            failure = $"资产 GUID '{registration.AssetGuid}' 已注册"
                + $"（既有 {existing.PackageId}/{existing.LocalId}#{existing.Version}，"
                + $"新 {registration.PackageId}/{registration.LocalId}#{registration.Version}）——GUID 冲突。";
            return false;
        }

        _byGuid.Add(registration.AssetGuid, registration);
        var key = new AssetKey(registration.PackageId, registration.Kind, registration.LocalId);
        if (!_index.TryGetValue(key, out var versions))
        {
            versions = new List<Guid>();
            _index.Add(key, versions);
        }

        versions.Add(registration.AssetGuid);
        failure = null;
        return true;
    }

    /// <summary>按 GUID 取（未注册＝null）。</summary>
    public AssetRegistration? Find(Guid assetGuid) => _byGuid.GetValueOrDefault(assetGuid);

    /// <summary>
    /// 某资产（包 ID ＋ 种类 ＋ 局部 ID）的**全部已注册版本**（版本升序）。
    /// 只给候选——本类型不择版（D10/I2）。
    /// </summary>
    /// <exception cref="ArgumentNullException">packageId/localId 为 null。</exception>
    public IReadOnlyList<AssetRegistration> VersionsOf(string packageId, AssetKind kind, string localId)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(localId);

        if (!_index.TryGetValue(new AssetKey(packageId, kind, localId), out var guids))
        {
            return Array.Empty<AssetRegistration>();
        }

        return guids
            .Select(guid => _byGuid[guid])
            .OrderBy(registration => registration.Version)
            .ToArray();
    }

    /// <summary>某包的全部已注册资产（注册序）。</summary>
    /// <exception cref="ArgumentNullException">packageId 为 null。</exception>
    public IReadOnlyList<AssetRegistration> ByPackage(string packageId)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        return _byGuid.Values
            .Where(registration => string.Equals(registration.PackageId, packageId, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>某应用层 ID 的全部已注册版本（跨包同名亦各自计入；注册序）。</summary>
    /// <exception cref="ArgumentNullException">appId 为 null。</exception>
    public IReadOnlyList<AssetRegistration> ByAppId(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return _byGuid.Values
            .Where(registration => string.Equals(registration.AppId, appId, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>某域的全部已注册资产（注册序）。</summary>
    public IReadOnlyList<AssetRegistration> ByDomain(AssetDomain domain)
        => _byGuid.Values.Where(registration => registration.Domain == domain).ToArray();

    // ── 引擎级运行期卡数据（S4·R5/D7；由 RuntimeCardCatalog 转调，本类型是唯一字典持有者） ──

    /// <summary>引擎级运行期卡（注册序）。</summary>
    public IReadOnlyList<RuntimeCardRegistration> RuntimeCards => _runtimeCards.Values.ToArray();

    /// <summary>是否已含该运行期卡 id。</summary>
    /// <exception cref="ArgumentException">id 为 null/空白。</exception>
    public bool ContainsRuntimeCard(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _runtimeCards.ContainsKey(id);
    }

    /// <summary>注册运行期卡（只追加不覆盖）。</summary>
    /// <exception cref="ArgumentNullException">entry 为 null。</exception>
    /// <exception cref="InvalidOperationException">同 id 已注册。</exception>
    public void RegisterRuntimeCard(RuntimeCardRegistration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        if (!_runtimeCards.TryAdd(entry.Id, entry))
        {
            throw new InvalidOperationException($"卡 id '{entry.Id}' 已在引擎级卡数据目录注册（只追加不覆盖）。");
        }
    }

    private readonly record struct AssetKey(string PackageId, AssetKind Kind, string LocalId);
}
