using Orc.Core;
using Orc.Game.Plugins;

namespace Orc.Packages;

/// <summary>已装载资产（清单声明 → 落盘定位）。</summary>
/// <param name="Asset">清单声明。</param>
/// <param name="FullName">派生的资产全名（继承包 GUID8 与包版本）。</param>
/// <param name="AbsolutePath">绝对路径。</param>
public sealed record LoadedAsset(PackageAsset Asset, AssetName FullName, string AbsolutePath);

/// <summary>已求值的插件注册脚本（csx 冷加载期编译一次；按声明的作用域/相分相调用）。</summary>
/// <param name="Asset">脚本资产。</param>
/// <param name="Register">注册动作（宿主按相以对应注册上下文调用）。</param>
public sealed record LoadedPluginScript(LoadedAsset Asset, Action<IPluginRegistrationContext> Register)
{
    /// <summary>作用域（取自资产声明；P11 甲）。</summary>
    public AssetScope Scope => Asset.Asset.Scope;

    /// <summary>对局级执行相（取自资产声明；P11 甲）。</summary>
    public AssetActivationPhase Phase => Asset.Asset.Phase;
}

/// <summary>已装载插件包（清单 ＋ 包根 ＋ 资产 ＋ 已求值脚本）。</summary>
/// <param name="Manifest">清单。</param>
/// <param name="RootDirectory">包根目录。</param>
/// <param name="Assets">已定位资产（声明序）。</param>
/// <param name="Scripts">已求值注册脚本（csx 资产序）。</param>
public sealed record LoadedPackage(
    PackageManifest Manifest,
    string RootDirectory,
    IReadOnlyList<LoadedAsset> Assets,
    IReadOnlyList<LoadedPluginScript> Scripts);

/// <summary>宿主装载报告：已装载包 ＋ 失败明细（单包失败＝隔离整包、不阻断其余）。</summary>
/// <param name="Packages">已装载包（加载序）。</param>
/// <param name="Failures">失败明细。</param>
public sealed record HostLoadReport(IReadOnlyList<LoadedPackage> Packages, IReadOnlyList<string> Failures);

/// <summary>
/// 资产目录（进程级只读面）：插件冷加载后的装载结果——**同包多版本可并存**（不同全名）。
/// </summary>
public sealed class AssetCatalog
{
    private readonly LoadedPackage[] _packages;

    internal AssetCatalog(IReadOnlyList<LoadedPackage> packages)
    {
        _packages = packages.ToArray();
    }

    /// <summary>已装载包（加载序）。</summary>
    public IReadOnlyList<LoadedPackage> Packages => _packages;

    /// <summary>按包 id 查全部版本（未找到＝空）。</summary>
    public IReadOnlyList<LoadedPackage> ById(string id)
        => _packages.Where(p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal)).ToArray();

    /// <summary>按包 id ＋ 包版本（内容指纹）查（未找到＝null）。</summary>
    public LoadedPackage? Find(string id, PackageVersion version)
        => _packages.FirstOrDefault(
            p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal) && p.Manifest.Version == version);

    /// <summary>全部资产（跨包；加载序）。</summary>
    public IReadOnlyList<LoadedAsset> AllAssets()
        => _packages.SelectMany(p => p.Assets).ToArray();
}

/// <summary>
/// 对局级资产目录**快照**（P6-a）：从目录冻结一份供单个对局使用——**对局内不可变**；
/// 并记录**版本锁定**（<c>id@version</c>）供审计/复现。快照彼此独立 ⇒ moding 不跨对局泄漏（Q3-a）。
/// </summary>
public sealed class AssetCatalogSnapshot
{
    private readonly LoadedPackage[] _packages;
    private readonly string[] _versionLocks;
    private readonly Orc.Game.Cards.RuntimeCardRegistration[] _runtimeCards;

    internal AssetCatalogSnapshot(
        IReadOnlyList<LoadedPackage> packages,
        IReadOnlyList<Orc.Game.Cards.RuntimeCardRegistration>? runtimeCards = null)
    {
        _runtimeCards = runtimeCards?.ToArray() ?? [];
        // 装配优先级（R8）：插件先、卡包后（各自保持目录加载序）。
        _packages = packages
            .Where(p => p.Manifest.Kind == PackageKind.Plugin)
            .Concat(packages.Where(p => p.Manifest.Kind == PackageKind.CardPack))
            .ToArray();
        _versionLocks = _packages
            .Select(p => $"{p.Manifest.Id}@{p.Manifest.Version}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>本局冻结的包集（装配序：插件先、卡包后）。</summary>
    public IReadOnlyList<LoadedPackage> Packages => _packages;

    /// <summary>本局冻结的**引擎级运行期卡数据**（S4；创建快照时点冻结——之后注册不影响本局）。</summary>
    public IReadOnlyList<Orc.Game.Cards.RuntimeCardRegistration> RuntimeCards => _runtimeCards;

    /// <summary>由已装载包构造快照（宿主/测试路径；内部按装配序分区：插件先、卡包后）。</summary>
    /// <exception cref="ArgumentNullException">packages 为 null。</exception>
    public static AssetCatalogSnapshot FromPackages(IReadOnlyList<LoadedPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return new AssetCatalogSnapshot(packages);
    }

    /// <summary>本局插件（装配序）。</summary>
    public IReadOnlyList<LoadedPackage> Plugins
        => _packages.Where(p => p.Manifest.Kind == PackageKind.Plugin).ToArray();

    /// <summary>本局卡包（装配序）。</summary>
    public IReadOnlyList<LoadedPackage> CardPacks
        => _packages.Where(p => p.Manifest.Kind == PackageKind.CardPack).ToArray();

    /// <summary>按包 id 查全部版本（未找到＝空）。</summary>
    public IReadOnlyList<LoadedPackage> ById(string id)
        => _packages.Where(p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal)).ToArray();

    /// <summary>按包 id ＋ 包版本（内容指纹）查（未找到＝null）。</summary>
    public LoadedPackage? Find(string id, PackageVersion version)
        => _packages.FirstOrDefault(
            p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal) && p.Manifest.Version == version);

    /// <summary>本局版本锁定记录（<c>id@version</c>；稳定序）。</summary>
    public IReadOnlyList<string> VersionLocks => _versionLocks;

    /// <summary>全部资产（跨包；加载序）。</summary>
    public IReadOnlyList<LoadedAsset> AllAssets()
        => _packages.SelectMany(p => p.Assets).ToArray();
}

/// <summary>
/// 引擎宿主（进程级；S7）：**插件冷加载**（发现 → 解析排序 → 装载定位）＋ 资产目录；
/// 对局经 <see cref="CreateSnapshot"/> 从目录冻结快照（每局独立）。
/// 冷加载语义：宿主启动时一次装载、运行期不重载（卸载/重载留后续批次）。
/// </summary>
public sealed class EngineHost
{
    private readonly IPluginScriptEvaluator _evaluator;
    private readonly List<string> _failures;
    private readonly List<string> _warnings;
    private readonly PackageLifecycleNotifier _lifecycle;
    private bool _cardPacksLocked;

    private EngineHost(
        AssetCatalog catalog,
        IReadOnlyList<string> failures,
        IReadOnlyList<string> warnings,
        IPluginScriptEvaluator evaluator,
        PackageLifecycleNotifier lifecycle)
    {
        Catalog = catalog;
        _failures = new List<string>(failures);
        _warnings = new List<string>(warnings);
        _evaluator = evaluator;
        _lifecycle = lifecycle;
        CardData = new RuntimeCardCatalog(Assets);
    }

    /// <summary>
    /// 卡包资产是否被锁（对局进行中；S3·D3/P3b）：锁定期 **卸载/重载** 一律明确失败——
    /// 热加载新包仍允许（只对新建对局生效）。宿主在开局时 <see cref="LockCardPacks"/>、终局后 <see cref="UnlockCardPacks"/>。
    /// </summary>
    public bool CardPacksLocked => _cardPacksLocked;

    /// <summary>锁定卡包资产（对局开始）。幂等。</summary>
    public void LockCardPacks() => _cardPacksLocked = true;

    /// <summary>解锁卡包资产（对局结束）。幂等。</summary>
    public void UnlockCardPacks() => _cardPacksLocked = false;

    /// <summary>资产目录（进程级；多版本并存）。</summary>
    public AssetCatalog Catalog { get; private set; }

    /// <summary>装载失败明细（发现/解析/装载/脚本求值/进程级装配/热加载 合并；隔离整包）。</summary>
    public IReadOnlyList<string> LoadFailures => _failures;

    /// <summary>迁移告警（如已退役的包级 <c>dependsOn</c>；不阻断装载）。</summary>
    public IReadOnlyList<string> LoadWarnings => _warnings;

    /// <summary>全局（进程级）装配结果（冷加载期；P11 甲）。</summary>
    public GlobalActivationResult GlobalActivation { get; private init; } =
        new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>约定目录冷加载（<c>&lt;base&gt;/plugins</c> 与 <c>&lt;base&gt;/packs</c>）。</summary>
    /// <exception cref="ArgumentNullException">baseDirectory 为 null。</exception>
    public static EngineHost Start(string baseDirectory, IPluginScriptEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        return FromDiscovery(PackageDiscovery.Discover(baseDirectory), evaluator);
    }

    /// <summary>由发现报告冷加载（可测路径）。csx 注册脚本在此**编译一次**（冷加载期）。</summary>
    /// <exception cref="ArgumentNullException">discovery 为 null。</exception>
    /// <exception cref="InvalidOperationException">插件装载序声明违规（未覆盖／有环／未知包 ID）。</exception>
    /// <param name="extraPackages">
    /// 额外**代码入口包**（S7·P10c；如随引擎/前端发布的 `.ui` 内置插件——其 csx 入口绑定到本进程程序集，
    /// 不经磁盘发现）。在磁盘包之后并入装载序。
    /// </param>
    /// <param name="pluginLoadOrder">
    /// 插件装载序的**显式声明**（S4；内置包注册清单）。非 null 时执行三项启动断言并按声明排序；
    /// 任一断言不满足＝启动失败并指名。
    /// </param>
    public static EngineHost FromDiscovery(
        PackageDiscoveryReport discovery,
        IPluginScriptEvaluator? evaluator = null,
        IReadOnlyList<LoadedPackage>? extraPackages = null,
        IReadOnlyList<PackageLoadOrderEntry>? pluginLoadOrder = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        evaluator ??= new Script.CSharpPluginScriptEvaluator();

        var resolution = PackageResolver.Resolve(discovery);
        var failures = new List<string>(resolution.Failures);
        var loaded = new List<LoadedPackage>();

        foreach (var resolved in resolution.LoadOrder)
        {
            try
            {
                loaded.Add(Materialize(resolved, evaluator));
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{resolved.Manifest.Id}@{resolved.Manifest.Version}：{ex.Message}");
            }
        }

        if (extraPackages is not null)
        {
            loaded.AddRange(extraPackages);
        }

        if (pluginLoadOrder is not null)
        {
            loaded = OrderPlugins(loaded, pluginLoadOrder);
        }

        var lifecycle = new PackageLifecycleNotifier();
        var global = ActivateGlobalPhase(loaded, lifecycle);
        failures.AddRange(global.Failures);

        // S7/S6：全局相装配完成（观察者已注册）后，为**冷加载期已就位的卡包**补一次 Loaded 通知——
        // 使随包词条等由观察者驱动的装配生效（否则冷加载的卡包词条永不被注册）。
        foreach (var package in loaded.Where(p => p.Manifest.Kind == PackageKind.CardPack))
        {
            failures.AddRange(lifecycle.Notify(PackageLifecycleChange.Loaded, package));
        }

        return new EngineHost(new AssetCatalog(loaded), failures, discovery.WarningList, evaluator, lifecycle)
        {
            GlobalActivation = global,
        };
    }

    /// <summary>
    /// 按显式装载序排列插件（卡包保持原序并排在插件之后），并执行三项启动断言。
    /// </summary>
    private static List<LoadedPackage> OrderPlugins(
        List<LoadedPackage> loaded, IReadOnlyList<PackageLoadOrderEntry> pluginLoadOrder)
    {
        var plugins = loaded.Where(p => p.Manifest.Kind == PackageKind.Plugin).ToArray();
        var discoveredIds = plugins.Select(p => p.Manifest.Id).ToArray();
        var order = PluginLoadOrder.Sort(pluginLoadOrder, discoveredIds, out var violations);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "插件装载序非法（启动失败）：" + string.Join("；", violations));
        }

        var ordered = order
            .SelectMany(id => plugins.Where(p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal)))
            .ToList();
        ordered.AddRange(loaded.Where(p => p.Manifest.Kind != PackageKind.Plugin));
        return ordered;
    }

    /// <summary>
    /// **全局（进程级）装配相**（P11 甲·<see cref="AssetScope.Process"/>）：冷加载期把各包的进程级脚本
    /// 经 <see cref="GlobalRegistrationContext"/> 装配到静态注册面（词条／数据组件）。
    /// 失败＝隔离该包（不阻断其余）——与本相"注册"语义一致。
    /// </summary>
    private static GlobalActivationResult ActivateGlobalPhase(
        IReadOnlyList<LoadedPackage> packages, IPackageLifecycleServices? lifecycle)
    {
        var services = new GlobalRegistrationServices();
        var activated = new List<string>();
        var failures = new List<string>();

        foreach (var package in packages)
        {
            var scripts = package.Scripts.Where(s => s.Scope == AssetScope.Process).ToArray();
            if (scripts.Length == 0)
            {
                continue;
            }

            var label = $"{package.Manifest.Id}@{package.Manifest.Version}";
            var context = new GlobalRegistrationContext(package, services, lifecycle);
            try
            {
                foreach (var script in scripts)
                {
                    script.Register(context);
                }

                activated.Add(label);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{label}：{ex.Message}");
            }
        }

        return new GlobalActivationResult(activated, failures);
    }

    /// <summary>
    /// **引擎级运行期卡数据目录**（S4·R5/D7·第一级）：注册进此处的卡随 <see cref="CreateSnapshot"/> 冻结进
    /// **之后新建**的对局；只追加不覆盖。底层字典由 <see cref="Assets"/> 承载。
    /// </summary>
    public RuntimeCardCatalog CardData { get; }

    /// <summary>
    /// **资产注册表**（进程级；S3/I2）：资产版本的唯一登记面（键＝GUID；只做注册、不择版）。
    /// </summary>
    public AssetRegistry Assets { get; } = new();

    /// <summary>冻结本局资产目录快照（对局内不可变；每局独立）。含引擎级运行期卡数据。</summary>
    public AssetCatalogSnapshot CreateSnapshot() => new(Catalog.Packages, CardData.Snapshot());

    /// <summary>
    /// **热加载**一个卡包（S9）：仅 <see cref="PackageKind.CardPack"/> 可热加载（插件只能冷加载——拒绝并记录）。
    /// 依赖须能被**现有目录**满足（同 id 多版本可并存）。成功＝并入目录 ⇒ **只对新建对局生效**
    /// （已冻结的快照不受影响）；失败＝隔离该包、不阻断其余。
    /// </summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    public bool TryLoadCardPack(string packageRoot, out string? failure)
    {
        if (!TryLoadCardPackCore(packageRoot, out failure, out var loaded))
        {
            return false;
        }

        NotifyLifecycle(PackageLifecycleChange.Loaded, loaded!);
        return true;
    }

    private bool TryLoadCardPackCore(string packageRoot, out string? failure, out LoadedPackage? loaded)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        loaded = null;

        var manifestPath = Path.Combine(packageRoot, PackageDiscovery.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            failure = "缺少清单文件";
            return false;
        }

        PackageManifest manifest;
        try
        {
            manifest = PackageManifest.Parse(File.ReadAllText(manifestPath), out var warnings);
            _warnings.AddRange(warnings);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            failure = ex.Message;
            return false;
        }

        if (manifest.Kind != PackageKind.CardPack)
        {
            failure = "仅卡包支持热加载（插件只能冷加载）";
            return false;
        }

        // 包级依赖已退役（S4/D31：依赖一律资产级）——热加载不再做包版本择版校验。
        try
        {
            loaded = Materialize(new ResolvedPackage(manifest, packageRoot), _evaluator);
            Catalog = new AssetCatalog(Catalog.Packages.Concat(new[] { loaded }).ToArray());
            failure = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>回调包生命周期观察者（失败并入装载失败明细；不阻断主流程）。</summary>
    private void NotifyLifecycle(PackageLifecycleChange change, LoadedPackage package)
        => _failures.AddRange(_lifecycle.Notify(change, package));

    /// <summary>
    /// **卸载**一个卡包（S 批次 5）：仅卡包可卸载（插件只能冷加载＝拒绝）。已冻结快照不受影响。
    /// </summary>
    /// <exception cref="ArgumentNullException">id 为 null。</exception>
    public bool TryUnloadCardPack(string id, PackageVersion version, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_cardPacksLocked)
        {
            failure = "对局进行中：禁止卸载卡包（请先结束对局）";
            return false;
        }

        var target = Catalog.Find(id, version);
        if (target is null)
        {
            failure = $"卡包 '{id}@{version}' 未加载";
            return false;
        }

        if (target.Manifest.Kind != PackageKind.CardPack)
        {
            failure = "插件不可卸载（只能冷加载）";
            return false;
        }

        Catalog = new AssetCatalog(Catalog.Packages.Where(p => !ReferenceEquals(p, target)).ToArray());
        failure = null;
        NotifyLifecycle(PackageLifecycleChange.Unloaded, target);
        return true;
    }

    /// <summary>**卸载**某 id 的全部版本（返回实际卸载数；插件＝不卸载、计入 0）。</summary>
    /// <exception cref="ArgumentNullException">id 为 null。</exception>
    public int UnloadCardPacks(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_cardPacksLocked)
        {
            return 0; // 对局进行中：批量卸载一律不执行（与 TryUnloadCardPack 同口径）
        }

        var removable = Catalog.Packages
            .Where(p => string.Equals(p.Manifest.Id, id, StringComparison.Ordinal))
            .Where(p => p.Manifest.Kind == PackageKind.CardPack)
            .ToArray();
        if (removable.Length == 0)
        {
            return 0;
        }

        Catalog = new AssetCatalog(Catalog.Packages.Except(removable).ToArray());
        foreach (var package in removable)
        {
            NotifyLifecycle(PackageLifecycleChange.Unloaded, package);
        }

        return removable.Length;
    }

    /// <summary>
    /// **重载**一个卡包（批次 5）＝卸载同 <c>id@version</c> 后按路径重新装载（单包失败＝隔离、原状保留）。
    /// 仅卡包可重载（插件＝拒绝）。
    /// </summary>
    /// <exception cref="ArgumentNullException">packageRoot 为 null。</exception>
    public bool TryReloadCardPack(string packageRoot, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);

        if (_cardPacksLocked)
        {
            failure = "对局进行中：禁止重载卡包（请先结束对局）";
            return false;
        }

        var manifestPath = Path.Combine(packageRoot, PackageDiscovery.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            failure = "缺少清单文件";
            return false;
        }

        PackageManifest manifest;
        try
        {
            manifest = PackageManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            failure = ex.Message;
            return false;
        }

        // 原状保留：先暂存，装载失败＝回滚（不留半改状态）。
        var backup = Catalog.Packages;
        if (manifest.Kind == PackageKind.CardPack)
        {
            Catalog = new AssetCatalog(
                backup.Where(p => !(string.Equals(p.Manifest.Id, manifest.Id, StringComparison.Ordinal)
                                    && p.Manifest.Version == manifest.Version)).ToArray());
        }

        if (TryLoadCardPackCore(packageRoot, out failure, out var reloaded))
        {
            NotifyLifecycle(PackageLifecycleChange.Reloaded, reloaded!);
            return true;
        }

        Catalog = new AssetCatalog(backup); // 回滚
        return false;
    }

    /// <summary>
    /// **回滚**目录到某快照所代表的资产集（批次 5）：把目录重建为该快照的包集（版本随之回退）。
    /// 用于"变更失败即回退"或显式降级；已冻结快照永不受影响。
    /// </summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    /// <returns>回滚后目录中的包数。</returns>
    public int RestoreFrom(AssetCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Catalog = new AssetCatalog(snapshot.Packages);
        return Catalog.Packages.Count;
    }

    /// <summary>热加载一个目录下的全部卡包（单包失败＝隔离记录；返回失败明细）。</summary>
    /// <exception cref="ArgumentNullException">packsDirectory 为 null。</exception>
    public IReadOnlyList<string> LoadCardPacks(string packsDirectory)
    {
        ArgumentNullException.ThrowIfNull(packsDirectory);

        var failures = new List<string>();
        if (!Directory.Exists(packsDirectory))
        {
            return failures;
        }

        foreach (var directory in PackageScanner.EnumeratePackageDirectories(packsDirectory))
        {
            if (!TryLoadCardPack(directory, out var failure))
            {
                failures.Add($"{directory}：{failure}");
                _failures.Add($"{directory}：{failure}");
            }
        }

        return failures;
    }

    private static LoadedPackage Materialize(ResolvedPackage resolved, IPluginScriptEvaluator evaluator)
    {
        var assets = new List<LoadedAsset>(resolved.Manifest.Assets.Count);
        foreach (var asset in resolved.Manifest.Assets)
        {
            var absolute = Path.Combine(resolved.RootDirectory, asset.Path);
            if (!File.Exists(absolute))
            {
                throw new InvalidOperationException($"资产 '{asset.Name}' 的资源缺失（{asset.Path}）");
            }

            assets.Add(new LoadedAsset(asset, resolved.Manifest.AssetNameOf(asset), absolute));
        }

        var scripts = new List<LoadedPluginScript>();
        foreach (var loadedAsset in assets)
        {
            if (loadedAsset.FullName.Kind != AssetLoadKind.Csx)
            {
                continue;
            }

            var source = File.ReadAllText(loadedAsset.AbsolutePath);
            var result = evaluator.Evaluate(new PluginScriptRequest(
                source, EntryName: "Register", CacheScope: resolved.Manifest.CacheScope));

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"脚本 '{loadedAsset.Asset.Name}' 求值失败（{result.ErrorCategory}）：{result.Error}");
            }

            scripts.Add(new LoadedPluginScript(loadedAsset, result.Register!));
        }

        return new LoadedPackage(resolved.Manifest, resolved.RootDirectory, assets, scripts);
    }
}
