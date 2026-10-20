using System.Text;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards.Data;
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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IEditableRootPolicy _policy;
    private readonly NormalizedPath _temporaryRoot;
    private readonly Dictionary<EditorAssetKey, EditorAssetEntry> _assets = new();
    private readonly Dictionary<EditorAssetKey, EditorAssetEntry> _initialAssets = new();
    private readonly HashSet<EditorAssetKey> _pinned = new();
    private readonly HashSet<string> _deletedSourceRelatives = new(StringComparer.Ordinal);
    private readonly Dictionary<EditorAssetKey, IReadOnlyList<string>> _tags = new();
    private readonly List<string> _warnings = new();

    private AssetEditorSession(
        string packageId,
        Guid packageGuid,
        string packageRoot,
        string temporaryRoot,
        bool isNewPackage,
        PackageManifest manifest,
        IReadOnlyList<string> dependsOnPackages,
        IEditableRootPolicy policy)
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
        IReadOnlyCollection<EditablePackageInfo> knownPackages)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);

        return request.IsCreateNew
            ? CreateNew(request, policy, knownPackages)
            : OpenExisting(request, policy);
    }

    /// <summary>会话快照。</summary>
    public EditorSessionInfo Describe()
        => new(PackageId, PackageRoot, TemporaryRoot, IsNewPackage, PackageGuid,
            DependsOnPackages, _assets.Count, _warnings.ToArray());

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
                : throw new AssetEditorException(
                    AssetEditorErrorCode.InvalidContent,
                    $"不支持的提交类型：'{relativePath}'（只接受 *.card.json 与 *.prefab.json）。");

        RecomputeLocks();
        return committed;
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
            if (kind != AssetKind.Card)
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.InvalidContent,
                    $"源文件不存在：'{sourceRelative}'（请先写入源文件，再声明资产）。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(sourceAbsolute)!);
            WriteText(sourceAbsolute, CardStub(localId));
        }

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
        _pinned.Add(key); // 新建即 1.0.0：首次提交不再自动 +1（R12）
        _tags.Remove(key);

        RecomputeLocks();
        return ToInfo(_assets[key]);
    }

    /// <summary>删除资产：当前版本移出声明（正式 lock 随之不含），历史版本仍留在 <c>versions/</c>。</summary>
    /// <exception cref="AssetEditorException">资产未声明。</exception>
    public void DeleteAsset(AssetKind kind, string localId)
    {
        var entry = RequireEntry(kind, localId);
        var sidecar = ReadSidecar(entry.SidecarAbsolute);
        var remaining = sidecar.Assets
            .Where(asset => asset.Kind != kind || !string.Equals(asset.Id, localId, StringComparison.Ordinal))
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

        _assets.Remove(entry.Key);
        _pinned.Remove(entry.Key);
        _tags.Remove(entry.Key);

        RecomputeLocks();
    }

    /// <summary>
    /// 设定版本：<paramref name="version"/> 为 null ＝ 默认自动递增（尾段 +1）；
    /// 手写须严格大于旧版（降级／补 0 后相等＝拒绝）。
    /// </summary>
    /// <exception cref="AssetEditorException">格式非法或未严格递增。</exception>
    public EditorAssetInfo SetVersion(AssetKind kind, string localId, string? version)
    {
        var entry = RequireEntry(kind, localId);
        var next = string.IsNullOrWhiteSpace(version)
            ? AutoNext(entry.Key, entry.Metadata.Version)
            : EditorVersionRules.ParseIncreasing(version, entry.Metadata.Version);

        ApplyVersion(entry, next);
        _pinned.Add(entry.Key); // 手写/显式设定：下一次提交不再自动 +1
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

        var sidecars = SidecarRelatives();
        Manifest = Manifest with
        {
            Version = PackageVersion.Compute(PackageId, sidecars),
            Metadata = sidecars,
        };
        WriteManifest();

        var formal = AssetLock.Create(
            PackageId,
            AssetDomain.Formal,
            OrderedAssets().Select(entry => LockEntry(entry, entry.Metadata.Version)));
        var test = AssetLock.Create(PackageId, AssetDomain.Test, Array.Empty<AssetLockEntry>());
        AssetLockJson.WriteFile(AssetLockJson.PathFor(TemporaryRoot, AssetDomain.Formal), formal);
        AssetLockJson.WriteFile(AssetLockJson.PathFor(TemporaryRoot, AssetDomain.Test), test);

        var archived = IsNewPackage ? Array.Empty<string>() : ArchiveHistory(promoted);
        if (IsNewPackage)
        {
            if (Directory.Exists(PackageRoot) || File.Exists(PackageRoot))
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.TemporaryPackageExists, $"目标包目录已存在：'{PackageRoot}'（拒绝覆盖）。");
            }

            Directory.Move(TemporaryRoot, PackageRoot);
        }
        else
        {
            ApplyToFormalPackage();
            DeleteTemporary();
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

    private static AssetEditorSession OpenExisting(EditorSessionRequest request, IEditableRootPolicy policy)
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
                policy);

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
        IReadOnlyCollection<EditablePackageInfo> knownPackages)
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
            PackageVersion.Compute(packageId, Array.Empty<string>()).AsTemporary(),
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
                policy);

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

        if (declaredCards.Length == 1 && !string.Equals(declaredCards[0].Id, localId, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"sidecar 与源文件 id 不一致（sidecar '{declaredCards[0].Id}'，源文件 '{localId}'）：'{sourceRelative}'。");
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
        var version = existing is not null ? AutoNext(key, metadata.Version) : metadata.Version;
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

        if (host is not null && !string.Equals(host.Id, localId, StringComparison.Ordinal))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidContent,
                $"预制体根 id '{localId}' 与 sidecar 声明 '{host.Id}' 不一致：'{sourceRelative}'。");
        }

        var key = new EditorAssetKey(AssetKind.Effect, localId);
        _assets.TryGetValue(key, out var existing);
        var previousVersion = existing?.Metadata.Version ?? host?.Version ?? EditorVersionRules.Initial;
        var hostVersion = existing is not null ? AutoNext(key, previousVersion) : previousVersion;
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
    /// 默认自动递增：**首次进入测试域**＝追加测试段（<c>1.0.0</c> → <c>1.0.0.1</c>，D53）；
    /// 已在测试域内／无正式版基线（本会话新建）＝尾段 +1。显式设定过版本（pinned）＝本次不递增。
    /// </summary>
    private AssetVersion AutoNext(EditorAssetKey key, AssetVersion current)
    {
        if (_pinned.Remove(key))
        {
            return current;
        }

        return _initialAssets.TryGetValue(key, out var initial) && current == initial.Metadata.Version
            ? EditorVersionRules.TestVersionOf(current)
            : EditorVersionRules.Next(current);
    }

    private void ApplyVersion(EditorAssetEntry entry, AssetVersion version)
    {
        var metadata = entry.Metadata with { Version = version, Guid = DeriveGuid(entry.Metadata.Kind, entry.Metadata.Id, version) };
        var sidecar = ReadSidecar(entry.SidecarAbsolute);
        var assets = sidecar.Assets
            .Select(asset => asset.Kind == metadata.Kind && string.Equals(asset.Id, metadata.Id, StringComparison.Ordinal) ? metadata : asset)
            .ToArray();
        WriteSidecar(entry.SidecarAbsolute, entry.SourceRelative, assets);
        _assets[entry.Key] = entry with { Metadata = metadata };
    }

    private Dictionary<EditorAssetKey, AssetVersion> ComputePromotedVersions()
    {
        var result = new Dictionary<EditorAssetKey, AssetVersion>();
        foreach (var group in _assets.Values.GroupBy(entry => entry.SidecarRelative, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
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
    }

    private void RecomputeLocks()
    {
        var sidecars = SidecarRelatives();
        Manifest = Manifest with
        {
            Version = PackageVersion.Compute(PackageId, sidecars).AsTemporary(),
            Metadata = sidecars,
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

        var files = Directory
            .EnumerateFiles(TemporaryRoot, "*" + AssetSidecarJson.SidecarExtension, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = RelativeToTemporary(file);
            if (IsUnderVersions(relative))
            {
                continue; // 历史版本目录不参与编辑（D55：只扫当前正式版）
            }

            yield return file;
        }
    }

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

    /// <summary>把临时包文件覆盖到正式包（先删墓碑，再逐文件复制）。</summary>
    private void ApplyToFormalPackage()
    {
        foreach (var relative in _deletedSourceRelatives)
        {
            DeleteFileIfExists(FormalPath(relative));
            DeleteFileIfExists(FormalPath(relative) + AssetSidecarJson.SidecarExtension);
        }

        foreach (var file in Directory
                     .EnumerateFiles(TemporaryRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var target = FormalPath(RelativeToTemporary(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// 归档被改动（或删除）资产的**旧正式版**：源文件＋sidecar 复制到 <c>versions/&lt;版本&gt;/</c>。
    /// <para>
    /// 归档 sidecar 只含本次归档的条目（避免同文件未变更资产的 GUID 重复注册），
    /// 且 <c>source</c> 写**相对包根**的完整路径（含 <c>versions/…</c> 前缀）——注册面按包根校验
    /// <c>source</c> 与文件名一致，故历史副本必须自带完整相对路径。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> ArchiveHistory(IReadOnlyDictionary<EditorAssetKey, AssetVersion> promoted)
    {
        var groups = new Dictionary<(string Source, AssetVersion Version), List<AssetMetadata>>();

        foreach (var (key, initial) in _initialAssets)
        {
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

        var versions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ((source, version), metadata) in groups)
        {
            var versionText = version.ToString();
            var targetRelative = $"{VersionsDirectory}/{versionText}/{source}";
            var targetAbsolute = FormalPath(targetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);
            File.Copy(FormalPath(source), targetAbsolute, overwrite: true);

            AssetSidecarJson.WriteFile(
                targetAbsolute + AssetSidecarJson.SidecarExtension,
                new AssetSidecar(
                    AssetSidecarJson.CurrentSchemaVersion,
                    PackageId,
                    targetRelative,
                    metadata.OrderBy(asset => asset.Kind).ThenBy(asset => asset.Id, StringComparer.Ordinal).ToArray()));

            versions.Add($"{VersionsDirectory}/{versionText}");
        }

        return versions.ToArray();
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

    private static string DefaultSourceFor(AssetKind kind, string localId) => kind switch
    {
        AssetKind.Card => $"cards/{localId}.card.json",
        AssetKind.Effect => $"effects/{localId}.prefab.json",
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
