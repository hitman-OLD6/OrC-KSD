using System.Text;
using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 服务子类 token（D62）：<see cref="AssetKind.Service"/> 的 <c>serviceKind</c> 取值。
/// <para>
/// 新增服务种类**只加常量**（必要时加白名单项），不改 <see cref="AssetKind"/> 枚举——
/// 这是 D62 选「单一 kind ＋ 子类字符串」的目的：官方持续扩展引擎服务时不牵连排序与序列化。
/// </para>
/// </summary>
public static class ServiceKindTokens
{
    /// <summary>判定器。</summary>
    public const string Judicator = "judicator";

    /// <summary>资产服务。</summary>
    public const string AssetService = "assetService";

    /// <summary>词条。</summary>
    public const string Keyword = "keyword";

    /// <summary>卡数据组件。</summary>
    public const string CardComponent = "cardComponent";

    /// <summary>行动契约实现。</summary>
    public const string Action = "action";

    /// <summary>投影提供者。</summary>
    public const string Projection = "projection";

    /// <summary>生命周期观察者。</summary>
    public const string Observer = "observer";

    /// <summary>是否为已定义的服务子类（大小写敏感：token 是受控常量）。</summary>
    /// <exception cref="ArgumentNullException">token 为 null。</exception>
    public static bool IsDefined(string? token)
        => token is Judicator or AssetService or Keyword or CardComponent or Action or Projection or Observer;
}

/// <summary>
/// 插件资产声明（D63）：插件代码声明的**一条服务资产**。
/// <para>
/// 代码声明是权威、<c>assets.meta.json</c> 是其落盘镜像（供审查与 lock 生成）；启动时以代码声明注册，
/// 再按 lock 校验（Q32：旧 lock 引用旧版本 ⇒ 校验失败并提示重锁）。
/// </para>
/// </summary>
/// <param name="ServiceKind">服务子类（见 <see cref="ServiceKindTokens"/>）。</param>
/// <param name="Name">服务名（判定器名／词条 id／组件名…；受控常量集合为唯一来源）。</param>
/// <param name="Version">资产版本（官方更新插件时递增）。</param>
/// <param name="Dependencies">资产级依赖（被依赖方也必须是本体系内的服务资产）。</param>
public sealed record PluginAssetDescriptor(
    string ServiceKind,
    string Name,
    AssetVersion Version,
    IReadOnlyList<AssetReference>? Dependencies = null)
{
    /// <summary>局部 ID＝<c>{serviceKind}.{name}</c>（应用层 ID 的唯一段；不含 <c>/</c>）。</summary>
    public string LocalId => $"{ServiceKind}.{Name}";
}

/// <summary>
/// 插件资产声明面（D63）：asm 插件实现本接口，由宿主在冷加载期登记其服务资产。
/// </summary>
public interface IPluginAssetSource
{
    /// <summary>声明方包 id（须与 manifest 一致）。</summary>
    string PackageId { get; }

    /// <summary>声明全部服务资产（稳定序；重复名由注册面拒绝）。</summary>
    IReadOnlyList<PluginAssetDescriptor> Describe();
}

/// <summary>插件资产注册结果。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="Registrations">已注册的资产版本条目（注册面持有 DTO 的那一份）。</param>
public sealed record PluginAssetRegistrationResult(
    string PackageId,
    IReadOnlyList<AssetRegistration> Registrations);

/// <summary>
/// 插件服务资产注册（D63）：把代码声明转成 <see cref="AssetKind.Service"/> 资产版本并登记进注册面。
/// <para>
/// 只走注册面，**不进卡组装载链**（服务资产不实例化、不参与卡组拓扑）。
/// </para>
/// </summary>
public static class PluginAssetRegistrar
{
    /// <summary>插件资产清单文件名（包级；替代 asm 插件无法挂载的 per-source sidecar）。</summary>
    public const string ManifestFileName = "assets.meta.json";

    /// <summary>由声明面注册本包全部服务资产。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidOperationException">包类型不是插件，或声明包 id 与 manifest 不符。</exception>
    /// <exception cref="FormatException">服务子类未定义、名称为空或资产版本非法。</exception>
    public static PluginAssetRegistrationResult Register(
        PackageManifest manifest,
        IPluginAssetSource source,
        AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);

        if (manifest.Kind != PackageKind.Plugin)
        {
            throw new InvalidOperationException(
                $"插件资产注册只接受 plugin 包（实际 {manifest.Kind}/{manifest.Id}）。");
        }

        if (!string.Equals(source.PackageId, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"插件资产声明的包 id '{source.PackageId}' 与 manifest '{manifest.Id}' 不符。");
        }

        return new PluginAssetRegistrationResult(
            manifest.Id,
            Register(manifest, source.Describe(), registry));
    }

    /// <summary>由声明清单注册（宿主/工具复用点）。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="FormatException">声明非法。</exception>
    public static IReadOnlyList<AssetRegistration> Register(
        PackageManifest manifest,
        IReadOnlyList<PluginAssetDescriptor> descriptors,
        AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(registry);

        var registrations = new List<AssetRegistration>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!ServiceKindTokens.IsDefined(descriptor.ServiceKind))
            {
                throw new FormatException(
                    $"服务资产子类 '{descriptor.ServiceKind}' 未定义（期望 {nameof(ServiceKindTokens)} 中的常量之一）。");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Name))
            {
                throw new FormatException($"服务资产（{descriptor.ServiceKind}）的名为空。");
            }

            var localId = descriptor.LocalId;
            var metadata = new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(manifest.Guid, AssetKind.Service, localId, descriptor.Version),
                AssetKind.Service,
                localId,
                descriptor.Version,
                Anchor: null,
                Loader: null,
                DependsOn: descriptor.Dependencies ?? [],
                ConflictsWith: [],
                ServiceKind: descriptor.ServiceKind);

            var registration = new AssetRegistration(
                metadata,
                AssetDomain.Formal,
                manifest.Id,
                AssetAppId.Compose(manifest.Id, localId),
                AssetMetadataSource.PluginDeclared,
                Card: null,
                Prefab: null,
                SourcePath: null)
            {
                PackageGuid = manifest.Guid,
            };

            registry.Register(registration);
            registrations.Add(registration);
        }

        return registrations;
    }

    /// <summary>构造指向某服务资产的依赖引用（跨包必须显式给 packageId）。</summary>
    /// <exception cref="ArgumentException">参数为空或含非法分隔。</exception>
    public static AssetReference ServiceReference(
        string packageId,
        string serviceKind,
        string name,
        string versionConstraint = "1.*.*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AssetReference(
            AssetKind.Service,
            AssetAppId.Compose(packageId, $"{serviceKind}.{name}"),
            versionConstraint);
    }
}

/// <summary>插件 lock 校验问题。</summary>
/// <param name="Code">问题码（见 <see cref="PluginAssetLockVerifier"/> 常量）。</param>
/// <param name="Message">人读说明。</param>
public sealed record PluginAssetLockIssue(string Code, string Message);

/// <summary>插件 lock 校验结果。</summary>
/// <param name="Ok">是否完全一致（无问题）。</param>
/// <param name="Issues">问题明细。</param>
public sealed record PluginAssetLockVerification(bool Ok, IReadOnlyList<PluginAssetLockIssue> Issues);

/// <summary>
/// 插件 lock 校验（Q32/D64）：lock 表达"插件正式版"；asm 插件**不保留历史版本**，
/// 官方更新 ⇒ 新资产版本、新 GUID ⇒ 旧 lock 引用旧版本 ⇒ 校验失败并**提示重锁**。
/// </summary>
public static class PluginAssetLockVerifier
{
    /// <summary>缺少正式 lock。</summary>
    public const string MissingLock = "missing-lock";

    /// <summary>lock 引用的资产版本在注册面不存在（旧版本；内容不可恢复）⇒ 重锁。</summary>
    public const string StaleLock = "stale-lock";

    /// <summary>注册面存在但未纳入正式 lock（不会加载）⇒ 重锁。</summary>
    public const string Unlisted = "unlisted";

    /// <summary>lock 对同一资产 GUID 重复声明（lock 自相矛盾）⇒ 修改 lock 文本或重锁。</summary>
    public const string DuplicateEntry = "duplicate-entry";

    /// <summary>校验包根下的 <c>lock.formal.json</c> 与本次注册结果是否一致。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public static PluginAssetLockVerification Verify(
        PackageManifest manifest,
        IReadOnlyList<AssetRegistration> registrations,
        string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);

        var issues = new List<PluginAssetLockIssue>();
        var @lock = AssetLockJson.ReadPackage(packageRoot, AssetDomain.Formal);
        if (@lock is null)
        {
            issues.Add(new PluginAssetLockIssue(
                MissingLock,
                $"插件 '{manifest.Id}' 缺少 {AssetLockJson.FormalFileName}——请运行插件资产重锁。"));
            return new PluginAssetLockVerification(false, issues);
        }

        // 容错索引：lock 自相矛盾（同 GUID 多条）只报问题，不抛异常（F12/I16）。
        var registered = new Dictionary<Guid, AssetRegistration>();
        foreach (var registration in registrations)
        {
            registered.TryAdd(registration.AssetGuid, registration);
        }

        var locked = new Dictionary<Guid, AssetLockEntry>();
        foreach (var entry in @lock.Assets)
        {
            if (locked.TryGetValue(entry.AssetGuid, out var kept))
            {
                issues.Add(new PluginAssetLockIssue(
                    DuplicateEntry,
                    $"lock 对同一资产 GUID '{entry.AssetGuid}' 重复声明："
                    + $"{kept.AppId}#{kept.Version} 与 {entry.AppId}#{entry.Version}"
                    + "——请修改 lock 文本或重锁。"));
                continue;
            }

            locked.Add(entry.AssetGuid, entry);
        }

        foreach (var entry in locked.Values.OrderBy(item => item.AppId, StringComparer.Ordinal))
        {
            if (!registered.TryGetValue(entry.AssetGuid, out var registration))
            {
                issues.Add(new PluginAssetLockIssue(
                    StaleLock,
                    $"lock 引用的资产版本已不存在：{AssetKindTokens.Token(entry.Kind)}：{entry.AppId}#{entry.Version}"
                    + $"——程序集插件不保留历史版本，请重锁（旧 lock 不会被静默降级）。"));
                continue;
            }

            // 四元组比对（guid 已由索引命中）＝「引用仍指向注册面同一身份」，否则视作篡改／过期。
            if (registration.Kind != entry.Kind
                || !string.Equals(registration.AppId, entry.AppId, StringComparison.Ordinal)
                || registration.Version != entry.Version)
            {
                issues.Add(new PluginAssetLockIssue(
                    StaleLock,
                    $"lock 条目与注册面不一致：{AssetKindTokens.Token(entry.Kind)}：{entry.AppId}#{entry.Version}"
                    + $"（注册面 {AssetKindTokens.Token(registration.Kind)}：{registration.AppId}#{registration.Version}）"
                    + "——lock 被篡改或已过期，请重锁。"));
            }
        }

        foreach (var registration in registrations)
        {
            if (!locked.ContainsKey(registration.AssetGuid))
            {
                issues.Add(new PluginAssetLockIssue(
                    Unlisted,
                    $"资产 {registration.AssetName} 未纳入正式 lock（当前不会加载）——请重锁。"));
            }
        }

        return new PluginAssetLockVerification(issues.Count == 0, issues);
    }
}

/// <summary>
/// 插件资产清单与 lock 的写出（D63/D64）：<c>assets.meta.json</c> 是代码声明的落盘镜像；
/// 两 lock 由同一注册结果生成（正式域＝全部服务资产；测试域＝空）。
/// </summary>
public static class PluginAssetManifestWriter
{
    /// <summary>写清单与两 lock（目录须已存在）。</summary>
    /// <param name="packageRoot">包根目录。</param>
    /// <param name="manifest">包清单（须为插件）。</param>
    /// <param name="registrations">注册结果（代码声明的产物）。</param>
    /// <param name="assemblyName">程序集名（写入清单的 <c>source</c> 字段——asm 插件的"源"）。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="ArgumentException">路径或程序集名为空。</exception>
    /// <exception cref="InvalidOperationException">包类型不是插件。</exception>
    public static void Write(
        string packageRoot,
        PackageManifest manifest,
        IReadOnlyList<AssetRegistration> registrations,
        string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        if (manifest.Kind != PackageKind.Plugin)
        {
            throw new InvalidOperationException(
                $"插件资产清单只适用于 plugin 包（实际 {manifest.Kind}/{manifest.Id}）。");
        }

        var assets = registrations
            .Select(registration => registration.Metadata)
            .OrderBy(metadata => metadata.Kind)
            .ThenBy(metadata => metadata.Id, StringComparer.Ordinal)
            .ThenBy(metadata => metadata.Version)
            .ToArray();

        var sidecar = new AssetSidecar(
            AssetSidecarJson.CurrentSchemaVersion,
            manifest.Id,
            assemblyName,
            assets);

        AssetSidecarJson.WriteFile(
            Path.Combine(packageRoot, PluginAssetRegistrar.ManifestFileName),
            sidecar);

        var entries = registrations
            .Select(registration => new AssetLockEntry(
                registration.AssetGuid,
                registration.Kind,
                registration.AppId,
                registration.Version))
            .ToArray();

        AssetLockJson.WriteFile(
            AssetLockJson.PathFor(packageRoot, AssetDomain.Formal),
            AssetLock.Create(manifest.Id, AssetDomain.Formal, entries));
        AssetLockJson.WriteFile(
            AssetLockJson.PathFor(packageRoot, AssetDomain.Test),
            AssetLock.Create(manifest.Id, AssetDomain.Test, []));
    }
}
