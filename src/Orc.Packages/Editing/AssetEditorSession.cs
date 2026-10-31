using System.Text;
using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Packages.Decks;
using Orc.Packages.Fs;

namespace Orc.Packages.Editing;

/// <summary>资产键（种类 ＋ 包内局部 ID）——「资产」的跨版本身份（R1）。</summary>
internal readonly record struct EditorAssetKey(AssetKind Kind, string LocalId);

/// <summary>会话内的一条资产声明（元数据 ＋ 载体定位）。路径一律相对包根（<c>/</c> 分隔）。</summary>
internal sealed record EditorAssetEntry(
    AssetMetadata Metadata,
    string SidecarRelative,
    string SidecarAbsolute,
    string SourceRelative,
    string SourceAbsolute)
{
    /// <summary>资产键。</summary>
    public EditorAssetKey Key => new(Metadata.Kind, Metadata.Id);

    /// <summary>应用层 ID。</summary>
    public string AppId(string packageId) => AssetAppId.Compose(packageId, Metadata.Id);
}

/// <summary>
/// **一次编辑会话**（R8/R10；临时卡包生命周期）：只操作 <c>&lt;包目录&gt;.tmp</c>，
/// 正式包在转正前始终只读。
/// <para>
/// 不变量：
/// <list type="bullet">
///   <item>临时包与正式包是**同级兄弟**，其父目录在会话开始前经 <see cref="IEditableRootPolicy"/> 判定；</item>
///   <item>临时包已存在＝会话开始即失败（**不清理、不复用**他人残留）；</item>
///   <item>测试 lock 每次结构变更后**自动重算**；正式 lock 只在转正时写；</item>
///   <item>所有写入路径都经 realpath 归一 ＋ 父目录授权，且必须落在临时包内。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class AssetEditorSession
{
    /// <summary>临时包目录后缀（同级兄弟）。</summary>
    public const string TemporarySuffix = ".tmp";

    private const string ManifestFileName = "manifest.json";
    private const string VersionsDirectory = "versions";

    /// <summary>moding 草案后缀（S11／D11：<c>modings/&lt;localId&gt;.moding.json</c>）。</summary>
    private const string ModingFileSuffix = ".moding.json";

    /// <summary>
    /// handler DSL 文档后缀（D11：<c>handler-dsl/&lt;name&gt;.hdsl.json</c>）。
    /// <para>
    /// 它**不是资产**：无 id／版本／lock，提交即写盘（不进 <c>_assets</c>、不 bump 版本、不重算 lock）。
    /// 也正因如此，它必须落在既有 7 类资产扫描模式之外，且**不得**加 <c>.meta.json</c> sidecar。
    /// </para>
    /// </summary>
    private const string DslFileSuffix = ".hdsl.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IEditableRootPolicy _policy;
    private readonly NormalizedPath _temporaryRoot;
    private readonly Dictionary<EditorAssetKey, EditorAssetEntry> _assets = new();
    private readonly Dictionary<EditorAssetKey, EditorAssetEntry> _initialAssets = new();
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletedSourceRelatives = new(StringComparer.Ordinal);
    private readonly Dictionary<EditorAssetKey, IReadOnlyList<string>> _tags = new();
    private readonly List<string> _warnings = new();

    /// <summary>
    /// 提交期判定用的卡池（D36）：会话开始时的 registry 快照 ＋ **本会话已提交卡的内存增量**。
    /// 与注册期同一语义——不做全根扫描（一次提交读上千文件、99% 无关），也绝不自己扫盘（D38）。
    /// </summary>
    private readonly Dictionary<string, CardDefinition> _pool;

    /// <summary>卡组要求张数（D11/D15：与注册期同一口径，故由选项注入而非写死）。</summary>
    private readonly int _requiredDeckSize;

    private AssetEditorSession(
        string packageId,
        Guid packageGuid,
        string packageRoot,
        string temporaryRoot,
        bool isNewPackage,
        PackageManifest manifest,
        IReadOnlyList<string> dependsOnPackages,
        IEditableRootPolicy policy,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredDeckSize)
    {
        PackageId = packageId;
        PackageGuid = packageGuid;
        PackageRoot = packageRoot;
        TemporaryRoot = temporaryRoot;
        _temporaryRoot = new NormalizedPath(temporaryRoot);
        IsNewPackage = isNewPackage;
        Manifest = manifest;
        DependsOnPackages = dependsOnPackages;
        _policy = policy;
        _pool = new Dictionary<string, CardDefinition>(pool, StringComparer.Ordinal);
        _requiredDeckSize = requiredDeckSize;
    }

    /// <summary>包 id。</summary>
    public string PackageId { get; }

    /// <summary>包稳定身份（GUID 派生输入）。</summary>
    public Guid PackageGuid { get; }

    /// <summary>正式包根（新建包＝最终目录，转正前不存在）。</summary>
    public string PackageRoot { get; }

    /// <summary>临时包目录。</summary>
    public string TemporaryRoot { get; }

    /// <summary>是否新建包。</summary>
    public bool IsNewPackage { get; }

    /// <summary>当前清单（版本＝内容指纹，编辑期带 <c>-tmp</c>）。</summary>
    public PackageManifest Manifest { get; private set; }

    /// <summary>期望依赖的卡包 ID（新建模式输入）。</summary>
    public IReadOnlyList<string> DependsOnPackages { get; }

    /// <summary>打开会话（已存在临时包＝<see cref="AssetEditorErrorCode.TemporaryPackageExists"/>）。</summary>
    /// <param name="request">会话入参。</param>
    /// <param name="policy">可编辑根策略（必填；构造期固定）。</param>
    /// <param name="knownPackages">已知可编辑包（用于依赖存在性告警）。</param>
    /// <exception cref="AssetEditorException">入参非法／路径未授权／临时包已存在。</exception>
    public static AssetEditorSession Open(
        EditorSessionRequest request,
        IEditableRootPolicy policy,
        IReadOnlyCollection<EditablePackageInfo> knownPackages,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredDeckSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(pool);

        return request.IsCreateNew
            ? CreateNew(request, policy, knownPackages, pool, requiredDeckSize)
            : OpenExisting(request, policy, pool, requiredDeckSize);
    }

    /// <summary>
    /// 从注册面快照出卡池（D36）：只取**已通过卡牌判定**的卡（<c>Card</c> 非空）；
    /// 同 appId 多版本取最高版。判定集合＝会话内可见的全部包（不走 lock——D8：编辑器提交期看注册集合）。
    /// </summary>
    public static IReadOnlyDictionary<string, CardDefinition> SnapshotPool(AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var pool = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        foreach (var group in registry.All
                     .Where(asset => asset.Kind == AssetKind.Card && asset.Card is not null)
                     .GroupBy(asset => asset.AppId, StringComparer.Ordinal))
        {
            pool[group.Key] = group.OrderByDescending(asset => asset.Version).First().Card!;
        }

        return pool;
    }

    /// <summary>会话快照。</summary>
    public EditorSessionInfo Describe()
        => new(PackageId, PackageRoot, TemporaryRoot, IsNewPackage, PackageGuid,
            DependsOnPackages, _assets.Count, _warnings.ToArray(), _requiredDeckSize);

    /// <summary>当前测试 lock（每次结构变更后已重算）。</summary>
    public AssetLock BuildTestLock()
        => AssetLock.Create(
            PackageId,
            AssetDomain.Test,
            _assets.Values.Select(entry => LockEntry(entry, entry.Metadata.Version)));

    /// <summary>枚举资产（进程内 lambda 过滤；null＝全部）。</summary>
    public IReadOnlyList<EditorAssetInfo> ListAssets(Func<EditorAssetInfo, bool>? filter)
    {
        var all = OrderedAssets().Select(ToInfo).ToArray();
        return filter is null ? all : all.Where(filter).ToArray();
    }

    /// <summary>取一条资产（未声明＝<see cref="AssetEditorErrorCode.AssetNotFound"/>）。</summary>
    public EditorAssetInfo GetAsset(AssetKind kind, string localId) => ToInfo(RequireEntry(kind, localId));

    /// <summary>
    /// 提交（**双通道**）：<paramref name="content"/> 为 null ＝ 文件指针通道（前端已直写，后端只读文件）；
    /// 非 null ＝ 内容通道（工具／agent 直传，后端写盘）。
    /// </summary>
    /// <exception cref="AssetEditorException">路径未授权／内容不可解析／资产归属冲突。</exception>
    public EditorCommitResult Commit(string relativePath, string? content)
    {
        var absolute = ResolveWritable(relativePath);
        var existed = File.Exists(absolute);
        var previous = content is not null && existed ? File.ReadAllBytes(absolute) : null;
        if (content is not null)
        {
            WriteText(absolute, content);
        }

        if (!File.Exists(absolute))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.AssetNotFound, $"提交目标不存在：'{relativePath}'（临时包：{TemporaryRoot}）。");
        }

        var committed = absolute.EndsWith(".card.json", StringComparison.OrdinalIgnoreCase)
            ? CommitCard(absolute)
            : absolute.EndsWith(".prefab.json", StringComparison.OrdinalIgnoreCase)
                ? CommitPrefab(absolute)
                : absolute.EndsWith(".deck.json", StringComparison.OrdinalIgnoreCase)
                    ? CommitDeck(absolute)
                    : absolute.EndsWith(ModingFileSuffix, StringComparison.OrdinalIgnoreCase)
                        ? CommitModing(absolute)
                        : absolute.EndsWith(DslFileSuffix, StringComparison.OrdinalIgnoreCase)
                            // DSL 文档**不是资产**（无 id／版本／lock，D11）：写入即完成，不碰资产声明与 lock。
                            ? new EditorCommitResult(Asset: null, VersionBumped: false)
                            : throw new AssetEditorException(
                                AssetEditorErrorCode.InvalidContent,
                                $"不支持的提交类型：'{relativePath}'（只接受 *.card.json／*.prefab.json／"
                                + $"*.deck.json／*{ModingFileSuffix}／*{DslFileSuffix}）。");

        // 「拒绝写入」要真的一点都不写（R7/S11）：内容通道已经把文件落盘了，这里把源文件恢复原样。
        // 文件指针通道（content＝null）不动——那份文件是前端自己写的，撤销它不是我们的职责。
        if (committed.Violations is not null && content is not null)
        {
            if (previous is not null)
            {
                File.WriteAllBytes(absolute, previous);
            }
            else
            {
                File.Delete(absolute);
            }
        }

        // lock／版本对 DSL **完全无感**（有意，D11）：`.hdsl.json` 不是资产、不进 lock、不 bump 任何版本——
        // 这不是漏了依赖追踪，而是「DSL 由草案承担可见性，自己不是资产」。因此这条通道提前返回、不重算 lock。
        if (absolute.EndsWith(DslFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return committed;
        }

        RecomputeLocks();
        return committed;
    }

    /// <summary>
    /// 提交 **moding 草案**（S11／D11）：草案是合法资产（kind＝<see cref="AssetKind.Moding"/>，只进测试 lock），
    /// 只存**增量**——重载／新增的成员、挂载与包装声明；展开后的成员表不落盘（D13）。
    /// </summary>
    /// <exception cref="AssetEditorException">不可解析／缺 id／缺 extends／同 id 跨文件声明。</exception>
    private EditorCommitResult CommitModing(string absolutePath)
    {
        var sourceRelative = RelativeToTemporary(absolutePath);
        var sidecarAbsolute = absolutePath + AssetSidecarJson.SidecarExtension;
        var sidecarRelative = sourceRelative + AssetSidecarJson.SidecarExtension;
        var sidecar = File.Exists(sidecarAbsolute) ? ReadSidecar(sidecarAbsolute) : null;

        ComponentAsset draft;
        try
        {
            draft = AssetSchema.Read(File.ReadAllText(absolutePath), sourceRelative);
        }
        catch (PrefabLoadException ex)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"moding 草案不可解析：'{sourceRelative}'：{ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"moding 草案缺少 asset.id：'{sourceRelative}'。");
        }

        // 草案依赖父效果实体（R1）：没有 extends 就不是草案，而是组件资产（那种文件用 *.component.json 提交）。
        if (draft.Extends is null)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"moding 草案 '{draft.Id}' 缺少 asset.extends（父效果 id）：草案必须声明它依赖哪份父资产（R1）。");
        }

        var declaredDrafts = sidecar?.Assets.Where(asset => asset.Kind == AssetKind.Moding).ToArray()
                            ?? Array.Empty<AssetMetadata>();
        if (declaredDrafts.Length >= 1
            && !declaredDrafts.Any(asset => string.Equals(asset.Id, draft.Id, StringComparison.Ordinal)))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"草案 id '{draft.Id}' 不在 sidecar 声明的 moding 集合"
                + $"（{string.Join("、", declaredDrafts.Select(asset => asset.Id))}）：'{sourceRelative}'。");
        }

        var key = new EditorAssetKey(AssetKind.Moding, draft.Id);
        _assets.TryGetValue(key, out var existing);
        if (existing is not null && !string.Equals(existing.SidecarRelative, sidecarRelative, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.AssetExists,
                $"资产 moding：{draft.Id} 已由 '{existing.SidecarRelative}' 声明（同 ID 不允许跨文件）。");
        }

        var metadata = existing?.Metadata
                       ?? new AssetMetadata(
                           Guid.Empty, AssetKind.Moding, draft.Id, EditorVersionRules.Initial,
                           null, null, Array.Empty<AssetReference>(), Array.Empty<AssetReference>());
        var previousVersion = metadata.Version;
        var version = existing is not null ? AutoNext(sidecarRelative, previousVersion) : metadata.Version;
        var bumped = version != previousVersion;
        metadata = metadata with
        {
            Version = version,
            Guid = DeriveGuid(AssetKind.Moding, draft.Id, version),
            // 草案不是内含资产、也不参与卡位装载（与 deck 同一口径）。
            Anchor = null,
            Loader = null,
            ServiceKind = null,
        };

        var assets = (sidecar?.Assets ?? Array.Empty<AssetMetadata>())
            .Where(asset => asset.Kind != AssetKind.Moding || !string.Equals(asset.Id, draft.Id, StringComparison.Ordinal))
            .Append(metadata)
            .ToArray();
        WriteSidecar(sidecarAbsolute, sourceRelative, assets);

        var entry = new EditorAssetEntry(metadata, sidecarRelative, sidecarAbsolute, sourceRelative, absolutePath);
        _assets[key] = entry;
        _tags.Remove(key);
        return new EditorCommitResult(ToInfo(entry), bumped);
    }

    /// <summary>新建资产（版本从 <c>1.0.0</c> 起；卡牌源文件缺失时补一份可解析骨架）。</summary>
    /// <exception cref="AssetEditorException">重复声明／源文件缺失（非卡资产）／内含资产缺 anchor。</exception>
    public EditorAssetInfo CreateAsset(
        AssetKind kind, string localId, string? sourcePath = null, string? anchor = null, string? loader = null)
    {
        ValidateLocalId(localId);
        var key = new EditorAssetKey(kind, localId);
        if (_assets.ContainsKey(key))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.AssetExists, $"资产 {AssetKindTokens.Token(kind)}：{localId} 已声明（同种类同 ID 只能有一个来源）。");
        }

        if (kind is AssetKind.Trigger or AssetKind.Handler && string.IsNullOrWhiteSpace(anchor))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"内含资产（{AssetKindTokens.Token(kind)}）必须指定 anchor 与宿主源文件。");
        }

        if (kind == AssetKind.Deck && (!string.IsNullOrWhiteSpace(anchor) || !string.IsNullOrWhiteSpace(loader)))
        {
            // D62：在入口就拒绝，不写进 sidecar 再等注册期报 FieldNotAllowed。
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                "卡组不接受 anchor／loader 参数（卡组不是内含资产，也不参与卡位装载）。");
        }

        var sourceRelative = string.IsNullOrWhiteSpace(sourcePath)
            ? DefaultSourceFor(kind, localId)
            : NormalizeRelative(sourcePath);

        if (string.IsNullOrWhiteSpace(sourceRelative))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"内含资产（{AssetKindTokens.Token(kind)}）必须指定宿主源文件路径。");
        }

        var sourceAbsolute = TemporaryPath(sourceRelative);
        EnsureWritable(sourceAbsolute, sourceRelative);

        if (!File.Exists(sourceAbsolute))
        {
            if (kind == AssetKind.Deck)
            {
                // D51：卡组**不补骨架**。任何骨架都必然非法（空 cards 张数不足、主国未定归属无从判），
                // 补一个「新建成功但永远提交不了」的陷阱不如直接把下一步写给作者。
                throw new AssetEditorException(
                    AssetEditorErrorCode.InvalidContent,
                    $"卡组源文件不存在：请先写入 `{sourceRelative}`（含 id／name／deckConfig／cards）再声明。");
            }

            if (kind != AssetKind.Card)
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.InvalidContent,
                    $"源文件不存在：'{sourceRelative}'（请先写入源文件，再声明资产）。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(sourceAbsolute)!);
            WriteText(sourceAbsolute, CardStub(localId));
        }

        VerifyDeclaredSourceId(kind, localId, sourceRelative, sourceAbsolute);

        var sidecarAbsolute = sourceAbsolute + AssetSidecarJson.SidecarExtension;
        var sidecarRelative = sourceRelative + AssetSidecarJson.SidecarExtension;
        var metadata = new AssetMetadata(
            DeriveGuid(kind, localId, EditorVersionRules.Initial),
            kind,
            localId,
            EditorVersionRules.Initial,
            anchor,
            string.IsNullOrWhiteSpace(loader) ? null : loader,
            Array.Empty<AssetReference>(),
            Array.Empty<AssetReference>());

        var existing = File.Exists(sidecarAbsolute) ? ReadSidecar(sidecarAbsolute) : null;
        var assets = existing?.Assets.ToList() ?? new List<AssetMetadata>();
        assets.Add(metadata);
        WriteSidecar(sidecarAbsolute, sourceRelative, assets);

        _assets[key] = new EditorAssetEntry(metadata, sidecarRelative, sidecarAbsolute, sourceRelative, sourceAbsolute);
        _pinned.Add(sidecarRelative); // 新建即 1.0.0：整组首次提交不再自动 +1（R12）
        _tags.Remove(key);

        RecomputeLocks();
        return ToInfo(_assets[key]);
    }

    /// <summary>
    /// 删除资产：当前版本移出声明（正式 lock 随之不含），历史版本仍留在 <c>versions/</c>。
    /// <para>
    /// F6：删除宿主（<c>anchor = null</c>）时**级联删除**同 sidecar 的全部内含资产——
    /// 内含资产没有独立源文件，宿主被删后其 anchor 无处解析；删除内含资产本身不连带宿主。
    /// </para>
    /// </summary>
    /// <exception cref="AssetEditorException">资产未声明。</exception>
    public void DeleteAsset(AssetKind kind, string localId)
    {
        var entry = RequireEntry(kind, localId);
        var victims = entry.Metadata.Anchor is null
            ? GroupOf(entry.SidecarRelative)
            : [entry];

        var removedKeys = victims.Select(victim => victim.Key).ToHashSet();
        var sidecar = ReadSidecar(entry.SidecarAbsolute);
        var remaining = sidecar.Assets
            .Where(asset => !removedKeys.Contains(new EditorAssetKey(asset.Kind, asset.Id)))
            .ToArray();

        if (remaining.Length == 0)
        {
            File.Delete(entry.SidecarAbsolute);
            if (File.Exists(entry.SourceAbsolute))
            {
                File.Delete(entry.SourceAbsolute);
            }

            _deletedSourceRelatives.Add(entry.SourceRelative);
        }
        else
        {
            WriteSidecar(entry.SidecarAbsolute, entry.SourceRelative, remaining);
        }

        foreach (var victim in victims)
        {
            _assets.Remove(victim.Key);
            _tags.Remove(victim.Key);
        }

        if (!_assets.Values.Any(item => string.Equals(item.SidecarRelative, entry.SidecarRelative, StringComparison.Ordinal)))
        {
            _pinned.Remove(entry.SidecarRelative); // 组已删空：pin 一并清除；组未删空则保留
        }

        if (kind == AssetKind.Card)
        {
            // D39：删卡必须同时移出卡池——漏掉这一步，「已删的卡」仍能通过卡组判定。
            UpdatePool(AssetAppId.Compose(PackageId, localId), definition: null);
        }

        RecomputeLocks();
    }

    /// <summary>
    /// 设定版本（**组粒度**，F2）：目标条目所在 sidecar 的全部条目（宿主 ＋ 内含）统一到目标版本。
    /// <paramref name="version"/> 为 null ＝ 默认自动递增（组内基线一致时追加测试段，否则尾段 +1）；
    /// 手写须严格大于**组内最大**版本（降级／补 0 后相等＝拒绝）。
    /// </summary>
    /// <exception cref="AssetEditorException">格式非法或未严格递增。</exception>
    public EditorAssetInfo SetVersion(AssetKind kind, string localId, string? version)
    {
        var entry = RequireEntry(kind, localId);
        var group = GroupOf(entry.SidecarRelative);
        var (max, maxKey) = MaxVersionOf(group);
        var next = string.IsNullOrWhiteSpace(version)
            ? AutoNext(entry.SidecarRelative, max)
            : EditorVersionRules.ParseIncreasing(
                version, max, $"{AssetKindTokens.Token(maxKey.Kind)}：{maxKey.LocalId}");

        ApplyVersion(entry, next);
        _pinned.Add(entry.SidecarRelative); // 手写/显式设定：整组下一次提交不再自动 +1
        RecomputeLocks();
        return ToInfo(_assets[entry.Key]);
    }

    /// <summary>设定资产依赖（应用层 ID 或裸局部 ID；裸 ID 归一到本包前缀）。测试 lock 随之重算。</summary>
    /// <exception cref="AssetEditorException">依赖声明非法（空白／约束不可解析／依赖自身）。</exception>
    public EditorAssetInfo SetDependencies(AssetKind kind, string localId, IEnumerable<AssetReference> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var entry = RequireEntry(kind, localId);
        var normalized = new List<AssetReference>();

        foreach (var reference in dependencies)
        {
            if (string.IsNullOrWhiteSpace(reference.AppId))
            {
                throw new AssetEditorException(AssetEditorErrorCode.DependencyInvalid, "依赖的应用层 ID 为空。");
            }

            if (!AssetVersionConstraint.TryParse(reference.VersionConstraint, out _))
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.DependencyInvalid,
                    $"依赖 '{reference.AppId}' 的版本约束 '{reference.VersionConstraint}' 非法（期望 1.2.3／1.*.*／1.^.*）。");
            }

            var appId = AssetAppId.IsAppId(reference.AppId)
                ? reference.AppId.Trim()
                : AssetAppId.Compose(PackageId, reference.AppId.Trim());

            if (reference.Kind == kind && string.Equals(appId, entry.AppId(PackageId), StringComparison.Ordinal))
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.DependencyInvalid, $"资产 {AssetKindTokens.Token(kind)}：{localId} 不得依赖自身。");
            }

            normalized.Add(reference with { AppId = appId });
        }

        var metadata = entry.Metadata with { DependsOn = normalized };
        var sidecar = ReadSidecar(entry.SidecarAbsolute);
        var assets = sidecar.Assets
            .Select(asset => asset.Kind == metadata.Kind && string.Equals(asset.Id, metadata.Id, StringComparison.Ordinal) ? metadata : asset)
            .ToArray();
        WriteSidecar(entry.SidecarAbsolute, entry.SourceRelative, assets);

        _assets[entry.Key] = entry with { Metadata = metadata };
        RecomputeLocks();
        return ToInfo(_assets[entry.Key]);
    }

    /// <summary>`lock.preview`：转正后的正式 lock（预测）＋ 当前测试 lock（实况）。</summary>
    public EditorLockPreview PreviewLocks()
    {
        var promoted = ComputePromotedVersions();
        var formal = AssetLock.Create(
            PackageId,
            AssetDomain.Formal,
            OrderedAssets().Select(entry => LockEntry(entry, promoted[entry.Key])));
        return new EditorLockPreview(formal, BuildTestLock());
    }

    /// <summary>
    /// **转正**（R10）：去测试段并递增 → 写两 lock → 当前版本源文件＋sidecar 归档到 <c>versions/&lt;版本&gt;/</c>
    /// → 去掉 <c>tmp</c>（包版本）并落地到正式包目录 → 删除临时目录。
    /// </summary>
    /// <exception cref="AssetEditorException">落地失败（目标已存在等）。</exception>
    public EditorPromotionResult Promote()
    {
        var promoted = ComputePromotedVersions();

        foreach (var group in _assets.Values.GroupBy(entry => entry.SidecarRelative, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            if (entries.All(entry => promoted[entry.Key] == entry.Metadata.Version))
            {
                continue; // F12-④：版本未变化 ⇒ 不重排、不重序列化（转正后字节不变）
            }

            var updated = entries
                .Select(entry => entry.Metadata with
                {
                    Version = promoted[entry.Key],
                    Guid = DeriveGuid(entry.Metadata.Kind, entry.Metadata.Id, promoted[entry.Key]),
                })
                .ToArray();

            WriteSidecar(entries[0].SidecarAbsolute, entries[0].SourceRelative, updated);
            for (var i = 0; i < entries.Length; i++)
            {
                _assets[entries[i].Key] = entries[i] with { Metadata = updated[i] };
            }
        }

        var sidecars = CurrentSidecars();
        Manifest = Manifest with
        {
            Version = PackageVersion.ComputeContent(PackageId, sidecars),
            Metadata = SidecarRelatives(),
        };
        WriteManifest();

        var formal = AssetLock.Create(
            PackageId,
            AssetDomain.Formal,
            OrderedAssets().Select(entry => LockEntry(entry, entry.Metadata.Version)));
        var test = AssetLock.Create(PackageId, AssetDomain.Test, Array.Empty<AssetLockEntry>());
        AssetLockJson.WriteFile(AssetLockJson.PathFor(TemporaryRoot, AssetDomain.Formal), formal);
        AssetLockJson.WriteFile(AssetLockJson.PathFor(TemporaryRoot, AssetDomain.Test), test);

        IReadOnlyList<string> archived;
        if (IsNewPackage)
        {
            archived = Array.Empty<string>();
            if (Directory.Exists(PackageRoot) || File.Exists(PackageRoot))
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.TemporaryPackageExists, $"目标包目录已存在：'{PackageRoot}'（拒绝覆盖）。");
            }

            Directory.Move(TemporaryRoot, PackageRoot);
        }
        else
        {
            var plan = BuildPromotionPlan(promoted);
            ApplyPromotionPlan(plan);
            DeleteTemporary();
            archived = plan.ArchivedVersions;
        }

        return new EditorPromotionResult(
            PackageId,
            PackageRoot,
            IsNewPackage,
            OrderedAssets().Select(ToInfo).ToArray(),
            formal,
            test,
            archived);
    }

    /// <summary>放弃：删除整个临时目录（含测试 lock），正式包不受影响。</summary>
    public void Discard() => DeleteTemporary();

    // ── 会话打开 ─────────────────────────────────────────────────────────────

    private static AssetEditorSession OpenExisting(
        EditorSessionRequest request,
        IEditableRootPolicy policy,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredDeckSize)
    {
        var packageRoot = RequireAbsolute(request.PackageRoot, "包目录");
        if (!Directory.Exists(packageRoot.Value))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PackageNotFound, $"包目录不存在：'{packageRoot.Value}'。");
        }

        var manifestPath = Path.Combine(packageRoot.Value, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PackageNotFound, $"包目录缺少 manifest.json：'{packageRoot.Value}'。");
        }

        var manifest = ReadManifest(manifestPath);
        if (manifest.Kind != PackageKind.CardPack)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage, $"编辑器只接受 kind=cardpack 的包（实际 '{manifest.Kind}'）。");
        }

        var temporaryRoot = new NormalizedPath(packageRoot.Value + TemporarySuffix);
        RequireApproved(policy, temporaryRoot);
        RequireAbsentTemporary(temporaryRoot);

        CopyPackage(packageRoot.Value, temporaryRoot.Value);

        try
        {
            var session = new AssetEditorSession(
                manifest.Id,
                manifest.Guid,
                packageRoot.Value,
                temporaryRoot.Value,
                isNewPackage: false,
                manifest,
                Array.Empty<string>(),
                policy,
                pool,
                requiredDeckSize);

            session.Refresh();
            session.CaptureBaselines();
            session.RecomputeLocks();
            return session;
        }
        catch
        {
            TryDeleteDirectory(temporaryRoot.Value); // 打开失败不留残留（否则下次 begin 被自己挡住）
            throw;
        }
    }

    private static AssetEditorSession CreateNew(
        EditorSessionRequest request,
        IEditableRootPolicy policy,
        IReadOnlyCollection<EditablePackageInfo> knownPackages,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredDeckSize)
    {
        var packageId = request.PackageId?.Trim() ?? string.Empty;
        if (packageId.Length == 0 || packageId.Contains('/') || packageId.Contains('\\')
            || packageId.EndsWith(TemporarySuffix, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage, $"包 id '{packageId}' 非法（不得为空、不得含路径分隔符或 .tmp 后缀）。");
        }

        if (request.DependsOnPackages.Count == 0)
        {
            // R8 硬要求：新建卡包必须给定期望依赖的卡包 ID。
            throw new AssetEditorException(
                AssetEditorErrorCode.DependenciesRequired, "新建卡包必须指定至少一个期望依赖的卡包 ID（R8）。");
        }

        var parent = RequireAbsolute(request.ParentDirectory, "父目录");
        if (!Directory.Exists(parent.Value))
        {
            throw new AssetEditorException(AssetEditorErrorCode.PackageNotFound, $"父目录不存在：'{parent.Value}'。");
        }

        var packageRoot = new NormalizedPath(Path.Combine(parent.Value, packageId));
        if (Directory.Exists(packageRoot.Value) || File.Exists(packageRoot.Value))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.TemporaryPackageExists, $"目标包目录已存在：'{packageRoot.Value}'（新建必须用新 id）。");
        }

        var temporaryRoot = new NormalizedPath(packageRoot.Value + TemporarySuffix);
        RequireApproved(policy, temporaryRoot);
        RequireAbsentTemporary(temporaryRoot);

        Directory.CreateDirectory(Path.Combine(temporaryRoot.Value, "cards"));

        var manifest = new PackageManifest(
            PackageKind.CardPack,
            packageId,
            AssetMigrationTool.DerivePackageGuid(packageId),
            PackageVersion.ComputeContent(packageId, Array.Empty<(string, AssetSidecar)>()).AsTemporary(),
            Array.Empty<PackageDependency>(),
            Array.Empty<PackageAsset>(),
            request.Name,
            Array.Empty<string>(),
            PackageManifest.CurrentSchemaVersion,
            request.DependsOnPackages
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        WriteManifest(temporaryRoot.Value, manifest);

        try
        {
            var session = new AssetEditorSession(
                packageId,
                manifest.Guid,
                packageRoot.Value,
                temporaryRoot.Value,
                isNewPackage: true,
                manifest,
                request.DependsOnPackages,
                policy,
                pool,
                requiredDeckSize);

            foreach (var dependency in request.DependsOnPackages)
            {
                if (!knownPackages.Any(package => string.Equals(package.PackageId, dependency, StringComparison.Ordinal)))
                {
                    session._warnings.Add($"期望依赖的卡包 '{dependency}' 未在可编辑包列表中找到（不阻断，转正前请确认）。");
                }
            }

            session.Refresh();
            session.CaptureBaselines();
            session.RecomputeLocks();
            return session;
        }
        catch
        {
            TryDeleteDirectory(temporaryRoot.Value);
            throw;
        }
    }

    // ── 提交 ────────────────────────────────────────────────────────────────

    private EditorCommitResult CommitCard(string absolutePath)
    {
        var sourceRelative = RelativeToTemporary(absolutePath);
        var sidecarAbsolute = absolutePath + AssetSidecarJson.SidecarExtension;
        var sidecarRelative = sourceRelative + AssetSidecarJson.SidecarExtension;
        var sidecar = File.Exists(sidecarAbsolute) ? ReadSidecar(sidecarAbsolute) : null;

        var load = CardDataLoader.LoadFileLenient(absolutePath);
        if (!load.IsLoaded)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"卡数据体不可解析：'{sourceRelative}'：{load.Failure}");
        }

        var declaredCards = sidecar?.Assets.Where(asset => asset.Kind == AssetKind.Card).ToArray()
                            ?? Array.Empty<AssetMetadata>();
        var localId = load.Id;
        if (string.IsNullOrWhiteSpace(localId) && declaredCards.Length == 1)
        {
            localId = declaredCards[0].Id;
        }

        if (string.IsNullOrWhiteSpace(localId))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"卡数据体缺少 id（且 sidecar 无法唯一补位）：'{sourceRelative}'。");
        }

        if (declaredCards.Length >= 1 && !declaredCards.Any(asset => string.Equals(asset.Id, localId, StringComparison.Ordinal)))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"源文件 id '{localId}' 不在 sidecar 声明的卡集合"
                + $"（{string.Join("、", declaredCards.Select(asset => asset.Id))}）：'{sourceRelative}'。");
        }

        var key = new EditorAssetKey(AssetKind.Card, localId);
        _assets.TryGetValue(key, out var existing);
        if (existing is not null && !string.Equals(existing.SidecarRelative, sidecarRelative, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.AssetExists,
                $"资产 card：{localId} 已由 '{existing.SidecarRelative}' 声明（同 ID 不允许跨文件）。");
        }

        var metadata = existing?.Metadata
                       ?? new AssetMetadata(
                           Guid.Empty, AssetKind.Card, localId, EditorVersionRules.Initial,
                           null, null, Array.Empty<AssetReference>(), Array.Empty<AssetReference>());
        var previousVersion = metadata.Version;
        var version = existing is not null ? AutoNext(sidecarRelative, previousVersion) : metadata.Version;
        var bumped = version != previousVersion;
        metadata = metadata with { Version = version, Guid = DeriveGuid(AssetKind.Card, localId, version) };

        var assets = (sidecar?.Assets ?? Array.Empty<AssetMetadata>())
            .Where(asset => asset.Kind != AssetKind.Card || !string.Equals(asset.Id, localId, StringComparison.Ordinal))
            .Append(metadata)
            .ToArray();
        WriteSidecar(sidecarAbsolute, sourceRelative, assets);

        var entry = new EditorAssetEntry(metadata, sidecarRelative, sidecarAbsolute, sourceRelative, absolutePath);
        _assets[key] = entry;
        _tags.Remove(key);
        UpdatePool(AssetAppId.Compose(PackageId, localId), load.Definition);
        return new EditorCommitResult(ToInfo(entry), bumped);
    }

    /// <summary>
    /// 卡池增量（D39 的三条变更）：新卡／改卡写回，**非法卡移除**（否则已删的卡仍能通过卡组判定）。
    /// </summary>
    private void UpdatePool(string appId, CardDefinition? definition)
    {
        if (definition is null)
        {
            _pool.Remove(appId);
            return;
        }

        _pool[appId] = definition;
    }

    /// <summary>
    /// 提交卡组（R7/D43/D54）：解析 → 用会话卡池按**恒 strict** 判定 → 合法才写 sidecar（含自动补声明）
    /// 并派生 <c>dependsOn</c>；非法＝不写、不递增版本，返回全部违规。
    /// </summary>
    private EditorCommitResult CommitDeck(string absolutePath)
    {
        var sourceRelative = RelativeToTemporary(absolutePath);
        var sidecarAbsolute = absolutePath + AssetSidecarJson.SidecarExtension;
        var sidecarRelative = sourceRelative + AssetSidecarJson.SidecarExtension;
        var sidecar = File.Exists(sidecarAbsolute) ? ReadSidecar(sidecarAbsolute) : null;

        var text = File.ReadAllText(absolutePath);
        var parsed = DeckDataJson.Parse(text);
        if (!parsed.IsParsed)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"卡组数据体不可解析：'{sourceRelative}'：{parsed.Failure}");
        }

        var declaredDecks = sidecar?.Assets.Where(asset => asset.Kind == AssetKind.Deck).ToArray()
                            ?? Array.Empty<AssetMetadata>();
        var localId = parsed.Body?.Id;
        if (string.IsNullOrWhiteSpace(localId) && declaredDecks.Length == 1)
        {
            localId = declaredDecks[0].Id;
        }

        if (string.IsNullOrWhiteSpace(localId))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"卡组数据体缺少 id（且 sidecar 无法唯一补位）：'{sourceRelative}'。");
        }

        if (declaredDecks.Length >= 1 && !declaredDecks.Any(asset => string.Equals(asset.Id, localId, StringComparison.Ordinal)))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"卡组数据体 id '{localId}' 不在 sidecar 声明集合"
                + $"（{string.Join("、", declaredDecks.Select(asset => asset.Id))}）：'{sourceRelative}'。");
        }

        var key = new EditorAssetKey(AssetKind.Deck, localId);
        _assets.TryGetValue(key, out var existing);
        if (existing is not null && !string.Equals(existing.SidecarRelative, sidecarRelative, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.AssetExists,
                $"资产 deck：{localId} 已由 '{existing.SidecarRelative}' 声明（同 ID 不允许跨文件）。");
        }

        // 资产通路恒 strict（D10）：档位只服务手写卡列表通路。
        var compiled = DeckAssetCompiler.Compile(text, localId, PackageId, _pool, _requiredDeckSize);
        if (compiled.Failure is { } failure)
        {
            throw new AssetEditorException(AssetEditorErrorCode.InvalidContent, failure);
        }

        if (compiled.Violations.Count > 0)
        {
            // 拒绝写入：不写 sidecar、不递增版本；违规原样交回调用方分类展示。
            return new EditorCommitResult(Asset: null, VersionBumped: false)
            {
                Violations = compiled.Violations,
            };
        }

        var metadata = existing?.Metadata
                       ?? new AssetMetadata(
                           Guid.Empty, AssetKind.Deck, localId, EditorVersionRules.Initial,
                           null, null, Array.Empty<AssetReference>(), Array.Empty<AssetReference>());
        var previousVersion = metadata.Version;
        var version = existing is not null ? AutoNext(sidecarRelative, previousVersion) : metadata.Version;
        var bumped = version != previousVersion;

        metadata = metadata with
        {
            Version = version,
            Guid = DeriveGuid(AssetKind.Deck, localId, version),
            // 卡组恒不是内含资产、也不参与卡位装载（D59/D63）：声明面把这三个字段钉成 null。
            Anchor = null,
            Loader = null,
            ServiceKind = null,
            // 依赖由 cards 派生（R4/D31）：作者不手写；约束恒 *.*.*（版本无关）。
            DependsOn = DeckRegistrar.DeriveDependsOn(compiled.Definition),
        };

        var assets = (sidecar?.Assets ?? Array.Empty<AssetMetadata>())
            .Where(asset => asset.Kind != AssetKind.Deck || !string.Equals(asset.Id, localId, StringComparison.Ordinal))
            .Append(metadata)
            .ToArray();
        WriteSidecar(sidecarAbsolute, sourceRelative, assets);

        var entry = new EditorAssetEntry(metadata, sidecarRelative, sidecarAbsolute, sourceRelative, absolutePath);
        _assets[key] = entry;
        _tags.Remove(key);
        return new EditorCommitResult(ToInfo(entry), bumped);
    }

    private EditorCommitResult CommitPrefab(string absolutePath)
    {
        var sourceRelative = RelativeToTemporary(absolutePath);
        var text = File.ReadAllText(absolutePath);
        EffectSnapshot snapshot;
        try
        {
            snapshot = PrefabJson.Deserialize(text);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"效果预制体不可解析：'{sourceRelative}'：{ex.Message}", ex);
        }

        var localId = snapshot.Root.Id;
        if (string.IsNullOrWhiteSpace(localId))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"效果预制体缺少根 id：'{sourceRelative}'。");
        }

        var sidecarAbsolute = absolutePath + AssetSidecarJson.SidecarExtension;
        var sidecarRelative = sourceRelative + AssetSidecarJson.SidecarExtension;
        var sidecar = File.Exists(sidecarAbsolute) ? ReadSidecar(sidecarAbsolute) : null;
        var host = sidecar?.Assets.FirstOrDefault(asset => asset.Anchor is null);

        if (sidecar is not null && sidecar.Assets.Count > 0 && host is null)
        {
            // F7：sidecar 只有内含条目 ⇒ 结构化拒绝（原实现会在 committed 保持 null 时 NRE）。
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"sidecar 缺宿主条目（只有内含资产）：'{sidecarRelative}'"
                + "——请补一条 anchor 为空的宿主声明后再提交。");
        }

        if (host is not null && !string.Equals(host.Id, localId, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"预制体根 id '{localId}' 与 sidecar 声明 '{host.Id}' 不一致：'{sourceRelative}'。");
        }

        var key = new EditorAssetKey(AssetKind.Effect, localId);
        _assets.TryGetValue(key, out var existing);

        // F2：版本基准是**整组**（宿主 ＋ 全部内含）的最大版本，避免内含资产被留在旧版。
        var previousVersion = GroupMaxVersion(sidecarRelative, sidecar?.Assets);
        var hostVersion = existing is not null ? AutoNext(sidecarRelative, previousVersion) : previousVersion;
        var bumped = hostVersion != previousVersion;

        var assets = (sidecar?.Assets ?? Array.Empty<AssetMetadata>())
            .Select(asset => asset with
            {
                // 内含资产（anchor != null）版本继承宿主文件版本。
                Version = hostVersion,
                Guid = DeriveGuid(asset.Kind, asset.Id, hostVersion),
            })
            .ToList();

        if (assets.Count == 0)
        {
            assets.Add(new AssetMetadata(
                DeriveGuid(AssetKind.Effect, localId, hostVersion),
                AssetKind.Effect,
                localId,
                hostVersion,
                null,
                null,
                Array.Empty<AssetReference>(),
                Array.Empty<AssetReference>()));
        }

        WriteSidecar(sidecarAbsolute, sourceRelative, assets);

        EditorAssetEntry? committed = null;
        foreach (var asset in assets)
        {
            var entry = new EditorAssetEntry(
                asset,
                sidecarRelative,
                sidecarAbsolute,
                sourceRelative,
                absolutePath);
            _assets[entry.Key] = entry;
            _tags.Remove(entry.Key);
            if (entry.Key == key)
            {
                committed = entry;
            }
        }

        return new EditorCommitResult(ToInfo(committed!), bumped);
    }

    // ── 内部：版本与写入 ─────────────────────────────────────────────────────

    /// <summary>
    /// 默认自动递增（**组粒度**，F2）：**首次进入测试域**＝追加测试段（<c>1.0.0</c> → <c>1.0.0.1</c>，D53）；
    /// 已在测试域内／无正式版基线（本会话新建）／组内历史分裂＝尾段 +1。
    /// 显式设定或新建过（pinned，按 sidecar 存）＝本次不递增，且**一次提交只消费一次**。
    /// </summary>
    /// <param name="sidecarRelative">sidecar 相对路径（pin 与组的键）。</param>
    /// <param name="current">组内当前最大版本。</param>
    private AssetVersion AutoNext(string sidecarRelative, AssetVersion current)
    {
        if (_pinned.Remove(sidecarRelative))
        {
            return current;
        }

        var group = GroupOf(sidecarRelative);
        var consistent = group.Count > 0 && group.All(entry => entry.Metadata.Version == current);
        var atBaseline = consistent
            && group.All(entry => _initialAssets.TryGetValue(entry.Key, out var initial)
                                  && entry.Metadata.Version == initial.Metadata.Version);

        return atBaseline ? EditorVersionRules.TestVersionOf(current) : EditorVersionRules.Next(current);
    }

    /// <summary>同 sidecar 的全部条目（宿主 ＋ 内含），按 <c>(Kind, Id)</c> 稳定排序。</summary>
    private IReadOnlyList<EditorAssetEntry> GroupOf(string sidecarRelative)
        => _assets.Values
            .Where(entry => string.Equals(entry.SidecarRelative, sidecarRelative, StringComparison.Ordinal))
            .OrderBy(entry => entry.Metadata.Kind)
            .ThenBy(entry => entry.Metadata.Id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>组内最大版本及其来源条目（并列时取 <c>(Kind, Id)</c> 最小者，保证消息稳定）。</summary>
    private static (AssetVersion Version, EditorAssetKey Key) MaxVersionOf(IReadOnlyList<EditorAssetEntry> group)
    {
        var owner = group[0];
        foreach (var entry in group)
        {
            if (entry.Metadata.Version > owner.Metadata.Version)
            {
                owner = entry;
            }
        }

        return (owner.Metadata.Version, owner.Key);
    }

    /// <summary>组内最大版本（可并入 sidecar 磁盘声明，覆盖「前端直写尚未提交」的条目）。</summary>
    private AssetVersion GroupMaxVersion(string sidecarRelative, IEnumerable<AssetMetadata>? declared)
    {
        var versions = GroupOf(sidecarRelative)
            .Select(entry => entry.Metadata.Version)
            .Concat(declared?.Select(asset => asset.Version) ?? [])
            .ToArray();

        return versions.Length == 0
            ? EditorVersionRules.Initial
            : versions.Aggregate((left, right) => left > right ? left : right);
    }

    /// <summary>把**整组**（同 sidecar 的全部条目）统一改写为目标版本，GUID 各自重算并整份写回 sidecar。</summary>
    private void ApplyVersion(EditorAssetEntry entry, AssetVersion version)
    {
        var group = GroupOf(entry.SidecarRelative);
        var updated = group
            .Select(item => item.Metadata with
            {
                Version = version,
                Guid = DeriveGuid(item.Metadata.Kind, item.Metadata.Id, version),
            })
            .ToArray();

        WriteSidecar(entry.SidecarAbsolute, entry.SourceRelative, updated);
        for (var index = 0; index < group.Count; index++)
        {
            _assets[group[index].Key] = group[index] with { Metadata = updated[index] };
        }
    }

    private Dictionary<EditorAssetKey, AssetVersion> ComputePromotedVersions()
    {
        var result = new Dictionary<EditorAssetKey, AssetVersion>();
        foreach (var group in _assets.Values.GroupBy(entry => entry.SidecarRelative, StringComparer.Ordinal))
        {
            var entries = group
                .OrderBy(entry => entry.Metadata.Kind)
                .ThenBy(entry => entry.Metadata.Id, StringComparer.Ordinal)
                .ToArray();

            var (max, _) = MaxVersionOf(entries);
            if (entries.Any(entry => entry.Metadata.Version != max))
            {
                // 历史分裂（F2 前遗留的数据）：无论是否改动，整组自愈到 PromoteVersion(组内最大)。
                var unified = PromoteVersion(entries.First(entry => entry.Metadata.Version == max));
                foreach (var entry in entries)
                {
                    result[entry.Key] = unified;
                }

                continue;
            }

            var host = entries.FirstOrDefault(entry => entry.Metadata.Anchor is null);
            var hostVersion = host is null ? (AssetVersion?)null : PromoteVersion(host);

            foreach (var entry in entries)
            {
                result[entry.Key] = host is not null && hostVersion!.Value != host.Metadata.Version
                    ? hostVersion!.Value
                    : PromoteVersion(entry);
            }
        }

        return result;
    }

    private AssetVersion PromoteVersion(EditorAssetEntry entry)
        => EditorVersionRules.Promote(
            entry.Metadata.Version,
            _initialAssets.TryGetValue(entry.Key, out var initial) ? initial.Metadata.Version : null);

    private string ResolveWritable(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new AssetEditorException(AssetEditorErrorCode.PathDenied, "提交路径为空。");
        }

        NormalizedPath target;
        try
        {
            target = Path.IsPathRooted(relativePath)
                ? PathNormalizer.Normalize(relativePath)
                : PathNormalizer.Combine(_temporaryRoot, relativePath);
        }
        catch (PathViolationException ex)
        {
            throw new AssetEditorException(AssetEditorErrorCode.PathDenied, ex.Message, ex);
        }

        EnsureWritable(target.Value, relativePath);
        return target.Value;
    }

    private void EnsureWritable(string absolutePath, string display)
    {
        NormalizedPath target;
        try
        {
            target = PathNormalizer.Normalize(absolutePath);
        }
        catch (PathViolationException ex)
        {
            throw new AssetEditorException(AssetEditorErrorCode.PathDenied, ex.Message, ex);
        }

        if (!target.IsUnderOrEqual(_temporaryRoot))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PathDenied,
                $"写入目标越出临时包：'{display}'（临时包：'{TemporaryRoot}'）——正式包在转正前只读。");
        }

        if (!_policy.TryApprove(target.Value, out var reason))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PathDenied, $"写入目标未获授权：'{target.Value}'——{reason}");
        }

        // F3/R3：versions/** 是只读历史区（由转正归档维护）——写入侧与枚举侧复用同一判定。
        var relative = RelativeToTemporary(target.Value);
        if (IsUnderVersions(relative))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PathDenied,
                $"写入目标落在历史版本目录：'{relative}'——'{VersionsDirectory}/' 为只读禁区"
                + "（编辑器只写当前正式版；历史副本由转正归档维护）。");
        }
    }

    private void RecomputeLocks()
    {
        Manifest = Manifest with
        {
            Version = PackageVersion.ComputeContent(PackageId, CurrentSidecars()).AsTemporary(),
            Metadata = SidecarRelatives(), // 资产清单：只含「有资产声明」的 sidecar（与指纹集合允许不同）
        };
        WriteManifest();

        AssetLockJson.WriteFile(
            AssetLockJson.PathFor(TemporaryRoot, AssetDomain.Test),
            BuildTestLock());
    }

    private IReadOnlyList<string> SidecarRelatives()
        => _assets.Values
            .Select(entry => entry.SidecarRelative)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private void Refresh()
    {
        _assets.Clear();
        _tags.Clear();

        foreach (var sidecarPath in EnumerateSidecars())
        {
            var sidecarRelative = RelativeToTemporary(sidecarPath);
            var sidecar = ReadSidecar(sidecarPath);
            if (!string.Equals(sidecar.PackageId, PackageId, StringComparison.Ordinal))
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.InvalidPackage,
                    $"sidecar 的 packageId '{sidecar.PackageId}' 与包 id '{PackageId}' 不符：'{sidecarRelative}'。");
            }

            var sourceRelative = sidecar.Source.Replace('\\', '/');
            var sourceAbsolute = ResolvePackageRelative(sourceRelative, sidecarRelative);

            foreach (var asset in sidecar.Assets)
            {
                var key = new EditorAssetKey(asset.Kind, asset.Id);
                if (_assets.ContainsKey(key))
                {
                    throw new AssetEditorException(
                        AssetEditorErrorCode.InvalidPackage,
                        $"资产 {AssetKindTokens.Token(asset.Kind)}：{asset.Id} 在多个 sidecar 中重复声明。");
                }

                _assets[key] = new EditorAssetEntry(
                    asset,
                    sidecarRelative,
                    sidecarPath,
                    sourceRelative,
                    sourceAbsolute);
            }
        }
    }

    private void CaptureBaselines()
    {
        _initialAssets.Clear();
        foreach (var (key, entry) in _assets)
        {
            _initialAssets[key] = entry;
        }
    }

    private IEnumerable<string> EnumerateSidecars()
    {
        if (!Directory.Exists(TemporaryRoot))
        {
            yield break;
        }

        // F1/I18：枚举面只有一处（PackageScanner），且显式排除 versions/** 与插件清单镜像。
        foreach (var relative in PackageScanner.EnumerateCurrentSidecars(TemporaryRoot))
        {
            yield return TemporaryPath(relative);
        }
    }

    /// <summary>
    /// 内容指纹的输入：**从磁盘枚举**当前版 sidecar 再逐个读入（不从 <c>_assets</c> 反推）——
    /// 使「无任何资产声明的空 sidecar」同样参与指纹（F1）。
    /// </summary>
    private IReadOnlyList<(string RelativePath, AssetSidecar Sidecar)> CurrentSidecars()
        => PackageScanner.EnumerateCurrentSidecars(TemporaryRoot)
            .Select(relative => (relative, ReadSidecar(TemporaryPath(relative))))
            .ToArray();

    private static bool IsUnderVersions(string relative)
        => relative.StartsWith(VersionsDirectory + "/", StringComparison.OrdinalIgnoreCase);

    private void WriteManifest()
        => WriteManifest(TemporaryRoot, Manifest);

    private static PackageManifest ReadManifest(string manifestPath)
    {
        try
        {
            return PackageManifest.Parse(File.ReadAllText(manifestPath), out _);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage, $"manifest 不可解析：'{manifestPath}'：{ex.Message}", ex);
        }
    }

    private static void WriteManifest(string packageRoot, PackageManifest manifest)
        => File.WriteAllText(Path.Combine(packageRoot, ManifestFileName), manifest.ToJson(), Utf8NoBom);

    private void WriteSidecar(string sidecarAbsolute, string sourceRelative, IEnumerable<AssetMetadata> assets)
        => AssetSidecarJson.WriteFile(
            sidecarAbsolute,
            new AssetSidecar(
                AssetSidecarJson.CurrentSchemaVersion,
                PackageId,
                sourceRelative.Replace('\\', '/'),
                assets.ToArray()));

    private AssetSidecar ReadSidecar(string sidecarAbsolute)
    {
        try
        {
            return AssetSidecarJson.ReadFile(sidecarAbsolute);
        }
        catch (FormatException ex)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage,
                $"sidecar 不可解析：'{RelativeToTemporary(sidecarAbsolute)}'：{ex.Message}", ex);
        }
    }

    private static void WriteText(string absolutePath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content, Utf8NoBom);
    }

    // ── 内部：路径 ──────────────────────────────────────────────────────────

    private string TemporaryPath(string relative)
        => Path.Combine(TemporaryRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private string FormalPath(string relative)
        => Path.Combine(PackageRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private string RelativeToTemporary(string absolutePath)
        => Path.GetRelativePath(TemporaryRoot, absolutePath).Replace('\\', '/');

    /// <summary>sidecar 声明的相对路径 → 包内绝对路径（**必须**落在临时包内；防 meta 伪造越界）。</summary>
    private string ResolvePackageRelative(string relativePath, string sidecarRelative)
    {
        NormalizedPath target;
        try
        {
            target = PathNormalizer.Normalize(TemporaryPath(relativePath));
        }
        catch (PathViolationException ex)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage,
                $"sidecar 的 source '{relativePath}' 非法：'{sidecarRelative}'——{ex.Reason}", ex);
        }

        if (!target.IsUnderOrEqual(_temporaryRoot))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidPackage,
                $"sidecar 的 source '{relativePath}' 越出包目录：'{sidecarRelative}'。");
        }

        return target.Value;
    }

    /// <summary>会话内相对路径（包根基准）：拒绝绝对／UNC／<c>..</c>（不静默改写）。</summary>
    private static string NormalizeRelative(string relativePath)
    {
        var trimmed = relativePath.Trim();
        if (trimmed.Length == 0
            || Path.IsPathRooted(trimmed)
            || PathNormalizer.IsUncPath(trimmed)
            || PathNormalizer.ContainsParentSegment(trimmed))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"源文件路径 '{relativePath}' 非法（须为包内相对路径，不得含 '..'）。");
        }

        return trimmed.Replace('\\', '/');
    }

    private static NormalizedPath RequireAbsolute(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AssetEditorException(AssetEditorErrorCode.PackageNotFound, $"{what}为空。");
        }

        try
        {
            return PathNormalizer.Normalize(path);
        }
        catch (PathViolationException ex)
        {
            throw new AssetEditorException(AssetEditorErrorCode.PathDenied, ex.Message, ex);
        }
    }

    private static void RequireApproved(IEditableRootPolicy policy, NormalizedPath path)
    {
        if (!policy.TryApprove(path.Value, out var reason))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.PathDenied, $"临时包目录未获授权：'{path.Value}'——{reason}");
        }
    }

    private static void RequireAbsentTemporary(NormalizedPath temporaryRoot)
    {
        if (Directory.Exists(temporaryRoot.Value) || File.Exists(temporaryRoot.Value))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.TemporaryPackageExists,
                $"临时包目录已存在：'{temporaryRoot.Value}'（不清理、不复用既有残留——请先自行确认并移除）。");
        }
    }

    // ── 内部：落地 ──────────────────────────────────────────────────────────

    /// <summary>一条落地写项：<paramref name="OriginalBytes"/>＝目标已存在时的原始字节（回滚快照）。</summary>
    private sealed record PromotionWrite(
        string RelativePath,
        PromotionWriteKind Kind,
        byte[]? Bytes,
        byte[]? OriginalBytes);

    /// <summary>写项种类（前置校验：<see cref="PromotionWriteKind.Create"/> ⇔ 目标不存在；其余 ⇔ 目标存在）。</summary>
    private enum PromotionWriteKind
    {
        Create,
        Overwrite,
        Delete,
    }

    /// <summary>
    /// 一次转正的**完整写计划**（F10）：历史归档 ＋ 墓碑删除 ＋ 临时包→正式包复制一次算齐；
    /// 计划完全生成后才开始写盘（禁止「先复制再补记录」）。
    /// </summary>
    private sealed class PromotionWritePlan
    {
        /// <summary>按应用顺序排列的写项（归档 → 墓碑 → 复制）。</summary>
        public List<PromotionWrite> Writes { get; } = [];

        /// <summary>本次归档的历史版本目录（<c>versions/&lt;v&gt;</c>；同内容跳过写也算）。</summary>
        public List<string> ArchivedVersions { get; } = [];
    }

    private PromotionWritePlan BuildPromotionPlan(IReadOnlyDictionary<EditorAssetKey, AssetVersion> promoted)
    {
        var plan = new PromotionWritePlan();
        AppendArchiveWrites(promoted, plan);

        foreach (var relative in _deletedSourceRelatives.OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var targetRelative in new[] { relative, relative + AssetSidecarJson.SidecarExtension })
            {
                var target = FormalPath(targetRelative);
                if (File.Exists(target))
                {
                    plan.Writes.Add(new PromotionWrite(
                        targetRelative, PromotionWriteKind.Delete, null, File.ReadAllBytes(target)));
                }
            }
        }

        foreach (var file in Directory
                     .EnumerateFiles(TemporaryRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = RelativeToTemporary(file);
            var target = FormalPath(relative);
            var content = File.ReadAllBytes(file);
            plan.Writes.Add(File.Exists(target)
                ? new PromotionWrite(relative, PromotionWriteKind.Overwrite, content, File.ReadAllBytes(target))
                : new PromotionWrite(relative, PromotionWriteKind.Create, content, null));
        }

        return plan;
    }

    /// <summary>
    /// 归档被改动（或删除）资产的**旧正式版**：源文件＋sidecar → <c>versions/&lt;版本&gt;/</c>。
    /// <para>
    /// 幂等：目标已存在且**逐字节相同** ⇒ 不生成写项（但仍计入 <see cref="PromotionWritePlan.ArchivedVersions"/>）；
    /// 内容不同 ⇒ 立即抛 <see cref="AssetEditorErrorCode.PromoteFailed"/>（此时尚未写入任何文件）。
    /// </para>
    /// </summary>
    private void AppendArchiveWrites(
        IReadOnlyDictionary<EditorAssetKey, AssetVersion> promoted,
        PromotionWritePlan plan)
    {
        var groups = new Dictionary<(string Source, AssetVersion Version), List<AssetMetadata>>();

        foreach (var (key, initial) in _initialAssets)
        {
            if (initial.Metadata.Kind == AssetKind.Deck)
            {
                // D4/R11：卡组不保留历史版本——旧内容直接由新版本取代（转正时覆写正式文件即可）。
                continue;
            }

            var baseline = initial.Metadata.Version;
            var changed = !promoted.TryGetValue(key, out var next) || next != baseline;
            if (!changed)
            {
                continue;
            }

            var oldSource = FormalPath(initial.SourceRelative);
            if (!File.Exists(oldSource))
            {
                _warnings.Add($"归档跳过：旧版本源文件缺失 '{initial.SourceRelative}'。");
                continue;
            }

            var groupKey = (initial.SourceRelative, baseline);
            if (!groups.TryGetValue(groupKey, out var list))
            {
                list = new List<AssetMetadata>();
                groups[groupKey] = list;
            }

            list.Add(initial.Metadata);
        }

        var writes = new List<(string Relative, byte[] Content)>();
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ((source, version), metadata) in groups)
        {
            var versionText = version.ToString();
            var targetRelative = $"{VersionsDirectory}/{versionText}/{source}";
            writes.Add((targetRelative, File.ReadAllBytes(FormalPath(source))));

            var archivedSidecar = new AssetSidecar(
                AssetSidecarJson.CurrentSchemaVersion,
                PackageId,
                targetRelative,
                metadata.OrderBy(asset => asset.Kind).ThenBy(asset => asset.Id, StringComparer.Ordinal).ToArray());
            writes.Add((
                targetRelative + AssetSidecarJson.SidecarExtension,
                Utf8NoBom.GetBytes(AssetSidecarJson.ToJson(archivedSidecar))));

            versions.Add($"{VersionsDirectory}/{versionText}");
        }

        foreach (var (relative, content) in writes.OrderBy(write => write.Relative, StringComparer.Ordinal))
        {
            var target = FormalPath(relative);
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                {
                    continue; // 同内容＝跳过写（历史字节不被改动）
                }

                throw new AssetEditorException(
                    AssetEditorErrorCode.PromoteFailed,
                    $"版本归档冲突：'{relative}' 已存在且内容不同——拒绝覆盖历史"
                    + "（转正中止，正式包未写入任何文件；临时包已保留）。");
            }

            plan.Writes.Add(new PromotionWrite(relative, PromotionWriteKind.Create, content, null));
        }

        plan.ArchivedVersions.AddRange(versions);
    }

    /// <summary>
    /// 按计划顺序写盘；任一 IO 失败 ⇒ **逆序回滚**已执行项（含失败项本身），再抛
    /// <see cref="AssetEditorErrorCode.PromoteFailed"/>（临时包保留，不清理）。
    /// </summary>
    private void ApplyPromotionPlan(PromotionWritePlan plan)
    {
        var applied = new List<PromotionWrite>();
        var createdDirectories = new List<string>();
        PromotionWrite? failing = null;

        try
        {
            foreach (var write in plan.Writes)
            {
                failing = write;
                var target = FormalPath(write.RelativePath);
                switch (write.Kind)
                {
                    case PromotionWriteKind.Create:
                        if (File.Exists(target))
                        {
                            throw new IOException($"目标已存在（Create 须目标不存在）：'{write.RelativePath}'。");
                        }

                        EnsureDirectory(Path.GetDirectoryName(target)!, createdDirectories);
                        File.WriteAllBytes(target, write.Bytes!);
                        break;

                    case PromotionWriteKind.Overwrite:
                        if (!File.Exists(target))
                        {
                            throw new IOException($"目标不存在（Overwrite 须目标存在）：'{write.RelativePath}'。");
                        }

                        File.WriteAllBytes(target, write.Bytes!);
                        break;

                    case PromotionWriteKind.Delete:
                        if (!File.Exists(target))
                        {
                            throw new IOException($"目标不存在（Delete 须目标存在）：'{write.RelativePath}'。");
                        }

                        File.Delete(target);
                        break;
                }

                applied.Add(write);
                failing = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = new List<string>();
            var candidates = new List<PromotionWrite>(applied);
            if (failing is not null)
            {
                candidates.Add(failing); // 失败项可能已部分写入：一并回滚
            }

            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                var write = candidates[index];
                try
                {
                    var target = FormalPath(write.RelativePath);
                    switch (write.Kind)
                    {
                        case PromotionWriteKind.Create:
                            DeleteFileIfExists(target);
                            break;

                        case PromotionWriteKind.Overwrite:
                        case PromotionWriteKind.Delete:
                            File.WriteAllBytes(target, write.OriginalBytes!);
                            break;
                    }
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    rollback.Add(
                        $"{write.Kind} '{write.RelativePath}'：{rollbackException.GetType().Name}：{rollbackException.Message}");
                }
            }

            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.Delete(directory, recursive: false); // 只删本次新建的空目录
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    // 忽略：非空或已被他人使用时不强删。
                }
            }

            var failedItem = failing;
            if (failedItem is null)
            {
                throw; // 不可达：异常只可能发生在某一写项执行期间
            }

            var rollbackState = rollback.Count == 0 ? "回滚完整" : $"回滚失败 {rollback.Count} 项";
            var details = rollback.Count == 0 ? "" : "；回滚明细：" + string.Join("；", rollback);
            throw new AssetEditorException(
                AssetEditorErrorCode.PromoteFailed,
                $"转正失败：{failedItem.Kind} '{failedItem.RelativePath}'"
                + $"（{ex.GetType().Name}：{ex.Message}）——{rollbackState}{details}；"
                + $"临时包已保留：'{TemporaryRoot}'。",
                ex);
        }
    }

    private static void EnsureDirectory(string path, List<string> created)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            EnsureDirectory(parent, created);
        }

        Directory.CreateDirectory(path);
        created.Add(path);
    }

    private void DeleteTemporary() => TryDeleteDirectory(TemporaryRoot);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不掩盖原始异常：交由下次 begin 的「临时包已存在」指名。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CopyPackage(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var file in Directory
                     .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (relative.StartsWith(VersionsDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 历史版本留在正式包（D55：编辑只针对当前正式版）
            }

            if (string.Equals(relative, AssetLockJson.FileNameFor(AssetDomain.Test), StringComparison.Ordinal))
            {
                continue; // 测试 lock 每次会话重算，不继承旧残留
            }

            var target = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── 内部：投影 ──────────────────────────────────────────────────────────

    private EditorAssetEntry RequireEntry(AssetKind kind, string localId)
    {
        if (string.IsNullOrWhiteSpace(localId))
        {
            throw new AssetEditorException(AssetEditorErrorCode.AssetNotFound, "资产局部 ID 为空。");
        }

        return _assets.TryGetValue(new EditorAssetKey(kind, localId.Trim()), out var entry)
            ? entry
            : throw new AssetEditorException(
                AssetEditorErrorCode.AssetNotFound, $"资产 {AssetKindTokens.Token(kind)}：{localId} 未在当前会话声明。");
    }

    private IEnumerable<EditorAssetEntry> OrderedAssets()
        => _assets.Values
            .OrderBy(entry => entry.Metadata.Kind)
            .ThenBy(entry => entry.Metadata.Id, StringComparer.Ordinal);

    private EditorAssetInfo ToInfo(EditorAssetEntry entry)
        => new(
            entry.Metadata.Kind,
            PackageId,
            entry.Metadata.Id,
            entry.AppId(PackageId),
            entry.Metadata.Version,
            entry.Metadata.Guid,
            entry.SourceRelative,
            entry.SidecarRelative,
            !_initialAssets.ContainsKey(entry.Key),
            TagsOf(entry));

    private IReadOnlyList<string> TagsOf(EditorAssetEntry entry)
    {
        if (entry.Metadata.Kind != AssetKind.Card)
        {
            return Array.Empty<string>();
        }

        if (_tags.TryGetValue(entry.Key, out var cached))
        {
            return cached;
        }

        IReadOnlyList<string> tags;
        try
        {
            tags = CardDataLoader.LoadFileLenient(entry.SourceAbsolute).Definition?.Tags ?? Array.Empty<string>();
        }
        catch (IOException)
        {
            tags = Array.Empty<string>();
        }

        _tags[entry.Key] = tags;
        return tags;
    }

    private AssetLockEntry LockEntry(EditorAssetEntry entry, AssetVersion version)
        => new(
            DeriveGuid(entry.Metadata.Kind, entry.Metadata.Id, version),
            entry.Metadata.Kind,
            entry.AppId(PackageId),
            version);

    private Guid DeriveGuid(AssetKind kind, string localId, AssetVersion version)
        => AssetIdentity.DeriveAssetGuid(PackageGuid, kind, localId, version);

    private static void ValidateLocalId(string localId)
    {
        if (string.IsNullOrWhiteSpace(localId)
            || localId.Contains('/')
            || localId.Contains('\\')
            || !string.Equals(localId, localId.Trim(), StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent, $"资产局部 ID '{localId}' 非法（不得为空／含路径分隔符／首尾空白）。");
        }
    }

    /// <summary>
    /// F7：新建资产时按 kind 比对**源文件声明的 id** 与待声明 id（只在能读出非空 id 时判定；
    /// 解析失败／缺 id 保持既有语义，不在此处升级为拒绝）。
    /// </summary>
    private static void VerifyDeclaredSourceId(AssetKind kind, string localId, string sourceRelative, string sourceAbsolute)
    {
        string? declaredId;
        try
        {
            if (kind == AssetKind.Card && sourceAbsolute.EndsWith(".card.json", StringComparison.OrdinalIgnoreCase))
            {
                declaredId = CardDataLoader.LoadFileLenient(sourceAbsolute).Id;
            }
            else if (kind == AssetKind.Effect && sourceAbsolute.EndsWith(".prefab.json", StringComparison.OrdinalIgnoreCase))
            {
                declaredId = PrefabJson.Deserialize(File.ReadAllText(sourceAbsolute)).Root.Id;
            }
            else if (kind == AssetKind.Deck && sourceAbsolute.EndsWith(".deck.json", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = DeckDataJson.ParseFile(sourceAbsolute);
                if (!parsed.IsParsed)
                {
                    // D52：create 只要求「存在且可解析」——不可解析就是拒绝，不写一份指向坏文件的声明。
                    throw new AssetEditorException(
                        AssetEditorErrorCode.InvalidContent,
                        $"卡组数据体不可解析：'{sourceRelative}'：{parsed.Failure}");
                }

                declaredId = parsed.Body?.Id;
            }
            else if (kind == AssetKind.Moding && sourceAbsolute.EndsWith(ModingFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                declaredId = AssetSchema.Read(File.ReadAllText(sourceAbsolute), sourceRelative).Id;
            }
            else
            {
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException
            or System.Text.Json.JsonException or PrefabLoadException)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(declaredId) && !string.Equals(declaredId, localId, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"源文件声明的 id '{declaredId}' 与待声明 id '{localId}' 不一致：'{sourceRelative}'。");
        }
    }

    private static string DefaultSourceFor(AssetKind kind, string localId) => kind switch
    {
        AssetKind.Card => $"cards/{localId}.card.json",
        AssetKind.Effect => $"effects/{localId}.prefab.json",
        // R11/D53：卡组固定落 decks/ 顶层。
        AssetKind.Deck => $"decks/{localId}.deck.json",
        // S11／D11：moding 草案固定落 modings/ 顶层（DSL 文档不属于 kind→路径映射：它不是资产）。
        AssetKind.Moding => $"modings/{localId}.moding.json",
        _ => string.Empty,
    };

    /// <summary>新建卡的骨架（可解析、四类必填组件齐全；内容由前端后续提交替换）。</summary>
    private static string CardStub(string localId)
        => $$"""
             {
               "schemaVersion": 1,
               "id": "{{localId}}",
               "name": "{{localId}}",
               "components": [
                 {
                   "component": "typeCategory",
                   "type": [
                     "infantry"
                   ]
                 },
                 {
                   "component": "factionCost",
                   "faction": "Germany",
                   "kredits": 0
                 },
                 {
                   "component": "battleStats",
                   "operationCost": 1,
                   "attack": 1,
                   "defense": 1
                 },
                 {
                   "component": "tagData",
                   "rarity": "Standard"
                 }
               ]
             }
             """;
}
