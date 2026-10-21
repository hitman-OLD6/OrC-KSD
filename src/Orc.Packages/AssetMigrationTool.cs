using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards.Data;

namespace Orc.Packages;

/// <summary>迁移动作（S10a）：<see cref="Create"/>＝目标文件不存在；<see cref="Skip"/>＝已存在且不重写；
/// <see cref="Overwrite"/>＝仅 <c>--force</c>（或内容已过期、必须升级的 manifest／lock）才重写。</summary>
public enum MigrationAction
{
    /// <summary>新建。</summary>
    Create = 0,

    /// <summary>跳过（已存在，内容不动）。</summary>
    Skip = 1,

    /// <summary>重写既有文件。</summary>
    Overwrite = 2,
}

/// <summary>迁移计划内声明的资产（R6 的 sidecar 条目摘要；S10b 可直接断言）。</summary>
/// <param name="Guid">资产 GUID（由包 GUID ＋ 种类 ＋ 局部 ID ＋ 版本派生）。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="Id">包内局部 ID。</param>
/// <param name="Version">资产版本（迁移初始＝1.0.0）。</param>
public sealed record MigrationAsset(Guid Guid, AssetKind Kind, string Id, AssetVersion Version);

/// <summary>一条源文件 → sidecar 的迁移条目。</summary>
/// <param name="SourceFile">源文件相对包根路径（<c>/</c> 分隔）。</param>
/// <param name="SidecarFile">sidecar 相对包根路径（<c>&lt;源文件名&gt;.meta.json</c>，同目录）。</param>
/// <param name="Action">动作。</param>
/// <param name="Assets">该 sidecar 声明（或将要声明）的资产。</param>
/// <param name="SidecarExists">sidecar 是否已存在（区分「已迁移」与「不可迁移」两种 Skip）。</param>
/// <param name="Content">要写入的规范文本（Skip＝null）。</param>
public sealed record MigrationEntry(
    string SourceFile,
    string SidecarFile,
    MigrationAction Action,
    IReadOnlyList<MigrationAsset> Assets,
    bool SidecarExists = false,
    string? Content = null);

/// <summary>结构化障碍（不猜、不写）：语义歧义或源文件不可解析时如实记录。</summary>
/// <param name="File">相关文件相对包根路径（包级＝<c>manifest.json</c> 或空串）。</param>
/// <param name="Reason">原因（人读）。</param>
public sealed record MigrationObstacle(string File, string Reason);

/// <summary>
/// 单文件动作（manifest／两份 lock 与 sidecar 共用）：<paramref name="Content"/> 为待写入的**规范文本**
/// （<see cref="MigrationAction.Skip"/> 时为 null——dry-run 期间同样不落盘，仅携带计划）。
/// </summary>
/// <param name="RelativePath">相对包根路径（<c>/</c> 分隔）。</param>
/// <param name="Action">动作。</param>
/// <param name="Content">规范文本（Skip＝null）。</param>
public sealed record MigrationFileAction(string RelativePath, MigrationAction Action, string? Content)
{
    /// <summary>是否需要写盘。</summary>
    public bool Pending => Action != MigrationAction.Skip && Content is not null;
}

/// <summary>
/// 一个包的**迁移计划**（S10a/I9）：纯函数产出、可被测试与 S10b 直接断言；<see cref="Apply"/> 才落盘。
/// 内容＝源文件→sidecar 条目 ＋ manifest 动作 ＋ 两份 lock 动作 ＋ 障碍明细。
/// </summary>
/// <param name="PackageRoot">包绝对路径。</param>
/// <param name="PackageId">包 id（既有 manifest 或按目录名派生）。</param>
/// <param name="Manifest">manifest 动作。</param>
/// <param name="FormalLock">正式 lock 动作。</param>
/// <param name="TestLock">测试 lock 动作。</param>
/// <param name="Entries">sidecar 条目（按源文件序稳定）。</param>
/// <param name="Obstacles">障碍明细（非空＝该计划不完整，需人工介入）。</param>
public sealed record MigrationPlan(
    string PackageRoot,
    string PackageId,
    MigrationFileAction Manifest,
    MigrationFileAction FormalLock,
    MigrationFileAction TestLock,
    IReadOnlyList<MigrationEntry> Entries,
    IReadOnlyList<MigrationObstacle> Obstacles)
{
    /// <summary>是否有待写文件（dry-run 判据：有变更＝需要 <c>--apply</c>）。</summary>
    public bool HasPendingWrites
        => Manifest.Pending
           || FormalLock.Pending
           || TestLock.Pending
           || Entries.Any(entry => entry.Action != MigrationAction.Skip && entry.Content is not null);

    /// <summary>是否全部跳过（幂等判据）。</summary>
    public bool IsFullySkipped => !HasPendingWrites;

    /// <summary>按动作统计条目数（含 manifest 与两份 lock）。</summary>
    public int CountOf(MigrationAction action)
    {
        var count = Entries.Count(entry => entry.Action == action);
        if (Manifest.Action == action)
        {
            count++;
        }

        if (FormalLock.Action == action)
        {
            count++;
        }

        if (TestLock.Action == action)
        {
            count++;
        }

        return count;
    }

    /// <summary>全部待写文件（相对路径 ＋ 规范文本；Skip 不计入）。</summary>
    public IReadOnlyList<(string RelativePath, string Content)> PendingWrites()
    {
        var writes = new List<(string, string)>();
        foreach (var entry in Entries)
        {
            if (entry.Action != MigrationAction.Skip && entry.Content is { } content)
            {
                writes.Add((entry.SidecarFile, content));
            }
        }

        foreach (var action in new[] { Manifest, FormalLock, TestLock })
        {
            if (action.Pending)
            {
                writes.Add((action.RelativePath, action.Content!));
            }
        }

        return writes;
    }
}

/// <summary>一次 <see cref="AssetMigrationTool.Apply"/> 的结果。</summary>
/// <param name="Plan">被执行的计划。</param>
/// <param name="WrittenFiles">实际写出的绝对路径（稳定序）。</param>
public sealed record MigrationResult(MigrationPlan Plan, IReadOnlyList<string> WrittenFiles);

/// <summary>
/// **存量迁移工具**（S10a/I9）：把无元数据的卡包迁到「sidecar ＋ manifest v2 ＋ 两 lock」。
/// <para>
/// 形态＝库内**纯函数**（<see cref="Plan"/> 只计算、不写盘；<see cref="Apply"/> 才写盘），CLI 只做参数解析与渲染
/// （<c>tools/Orc.Tools.Migrate</c>）。默认 dry-run、<c>--apply</c> 才落盘、<c>--force</c> 才重写既有 sidecar。
/// </para>
/// <para>
/// 幂等：已存在 sidecar ⇒ <see cref="MigrationAction.Skip"/>（不重算版本，故无版本漂移）；manifest／lock 内容
/// 与规范文本一致即 Skip。重复执行恒为「全 Skip」。
/// </para>
/// <para>不猜：源文件不可解析、sidecar 不可解析、同一 (kind, appId) 多版本 ⇒ 记录
/// <see cref="MigrationObstacle"/> 且**不生成**该文件的 sidecar（交人工处理）。</para>
/// </summary>
public static class AssetMigrationTool
{
    /// <summary>迁移产出的初始资产版本（R12/D28：新建资产展示 <c>1.0.0</c>）。</summary>
    public static readonly AssetVersion InitialVersion = new(1, 0, 0);

    /// <summary>包目录名前缀（<c>card-pack-demo</c> ⇒ 包 id <c>demo</c>）。</summary>
    public const string PackageDirectoryPrefix = "card-pack-";

    /// <summary>历史版本目录名（<c>versions/</c> 不生成 sidecar：R3 由目录约定自动扫描）。</summary>
    public const string VersionsDirectoryName = PackageScanner.VersionsDirectoryName;

    private const string ManifestFileName = PackageDiscovery.ManifestFileName;
    private const string CardSuffix = ".card.json";
    private const string PrefabSuffix = ".prefab.json";
    private const string PendingPackageSuffix = ".tmp";

    /// <summary>枚举一个资产根下的全部包目录（含 <c>manifest.json</c>，或含卡/效果源文件的目录）。</summary>
    /// <exception cref="ArgumentNullException">assetsRoot 为 null。</exception>
    public static IReadOnlyList<string> EnumeratePackageRoots(string assetsRoot)
    {
        ArgumentNullException.ThrowIfNull(assetsRoot);
        if (!Directory.Exists(assetsRoot))
        {
            return Array.Empty<string>();
        }

        var root = Path.GetFullPath(assetsRoot);
        var packages = new List<string>();
        if (IsPackageDirectory(root))
        {
            packages.Add(root);
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).EndsWith(PendingPackageSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 他人残留的临时包（<包目录>.tmp）不参与迁移（S8 约束：不清理、不复用）
            }

            if (IsPackageDirectory(directory))
            {
                packages.Add(Path.GetFullPath(directory));
            }
        }

        return packages;
    }

    /// <summary>
    /// 计算一个包的迁移计划（**不写盘**）。
    /// </summary>
    /// <param name="packageRoot">包根目录。</param>
    /// <param name="force">是否允许重写既有 sidecar（默认 false）。</param>
    /// <exception cref="ArgumentException">packageRoot 为 null/空白。</exception>
    /// <exception cref="DirectoryNotFoundException">包目录不存在。</exception>
    public static MigrationPlan Plan(string packageRoot, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"包目录不存在：{root}");
        }

        var obstacles = new List<MigrationObstacle>();
        var manifestPath = Path.Combine(root, ManifestFileName);
        var existingManifestText = File.Exists(manifestPath)
            ? Normalize(File.ReadAllText(manifestPath, Encoding.UTF8))
            : null;

        string packageId;
        Guid packageGuid;
        string? packageName = null;

        if (existingManifestText is not null)
        {
            PackageManifest parsed;
            try
            {
                parsed = PackageManifest.Parse(existingManifestText);
            }
            catch (FormatException ex)
            {
                obstacles.Add(new MigrationObstacle(ManifestFileName, $"manifest 不可解析：{ex.Message}"));
                return Blocked(root, DerivePackageId(root), obstacles);
            }

            if (parsed.Kind != PackageKind.CardPack)
            {
                obstacles.Add(new MigrationObstacle(
                    ManifestFileName, $"kind={parsed.Kind} 的包不属于卡包迁移范围（只迁移 cardpack）。"));
                return Blocked(root, parsed.Id, obstacles);
            }

            packageId = parsed.Id;
            packageGuid = parsed.Guid;
            packageName = parsed.Name;
        }
        else
        {
            // 无 manifest（demo／diy 等）：包 id 由目录名派生、包 GUID 由包 id 确定性派生
            // （不含时间戳／随机源 ⇒ 重复运行不漂移；不猜业务含义，只按目录名）。
            packageId = DerivePackageId(root);
            packageGuid = DerivePackageGuid(packageId);
        }

        var entries = new List<MigrationEntry>();
        foreach (var (absolute, relative) in EnumerateSourceFiles(root))
        {
            var sidecarRelative = relative + AssetSidecarJson.SidecarExtension;
            var sidecarAbsolute = absolute + AssetSidecarJson.SidecarExtension;
            var sidecarExists = File.Exists(sidecarAbsolute);

            if (sidecarExists && !force)
            {
                // 幂等：既有 sidecar 一律 Skip（不重算版本 ⇒ 无版本漂移）。
                entries.Add(new MigrationEntry(
                    relative,
                    sidecarRelative,
                    MigrationAction.Skip,
                    ReadExistingAssets(sidecarAbsolute, relative, obstacles),
                    SidecarExists: true));
                continue;
            }

            var generated = GenerateAssets(absolute, relative, packageGuid, obstacles);
            if (generated is null)
            {
                // 不猜：源文件不可解析 ⇒ 不生成 sidecar，仅记录障碍（既有 sidecar 保持不动）。
                entries.Add(new MigrationEntry(
                    relative,
                    sidecarRelative,
                    MigrationAction.Skip,
                    sidecarExists ? ReadExistingAssets(sidecarAbsolute, relative, obstacles) : Array.Empty<MigrationAsset>(),
                    SidecarExists: sidecarExists));
                continue;
            }

            entries.Add(new MigrationEntry(
                relative,
                sidecarRelative,
                sidecarExists ? MigrationAction.Overwrite : MigrationAction.Create,
                generated,
                SidecarExists: sidecarExists,
                Content: BuildSidecarContent(packageId, relative, generated)));
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.SourceFile, right.SourceFile));

        var metadata = entries
            .Where(entry => entry.SidecarExists || entry.Action != MigrationAction.Skip)
            .Select(entry => entry.SidecarFile)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var manifestAction = BuildFileAction(
            ManifestFileName,
            existingManifestText,
            new PackageManifest(
                PackageKind.CardPack,
                packageId,
                packageGuid,
                PackageVersion.ComputeContent(packageId, ContentFingerprintInput(root, entries)),
                Array.Empty<PackageDependency>(),
                Array.Empty<PackageAsset>(),
                packageName,
                metadata,
                PackageManifest.CurrentSchemaVersion).ToJson());

        var locks = BuildLocks(root, packageId, entries, obstacles);

        return new MigrationPlan(root, packageId, manifestAction, locks.Formal, locks.Test, entries, obstacles);
    }

    /// <summary>
    /// 执行计划（**写盘**）：只写 <see cref="MigrationAction.Create"/>/<see cref="MigrationAction.Overwrite"/> 的文件。
    /// </summary>
    /// <exception cref="ArgumentNullException">plan 为 null。</exception>
    public static MigrationResult Apply(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var written = new List<string>();
        foreach (var (relativePath, content) in plan.PendingWrites())
        {
            var absolute = Path.Combine(plan.PackageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(absolute, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            written.Add(absolute);
        }

        written.Sort(StringComparer.Ordinal);
        return new MigrationResult(plan, written);
    }

    /// <summary>包 id ⇒ 确定性包 GUID（<c>SHA-256("orc.package.guid\n{id}")</c> 前 16 字节）。</summary>
    /// <exception cref="ArgumentException">packageId 为 null/空白。</exception>
    public static Guid DerivePackageGuid(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("orc.package.guid\n" + packageId.Trim()));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>包目录名 ⇒ 包 id（去 <c>card-pack-</c> 前缀；小写化以保持稳定）。</summary>
    /// <exception cref="ArgumentException">目录名为空。</exception>
    public static string DerivePackageId(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var name = Path.GetFileName(Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[PackageDirectoryPrefix.Length..];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.ToLowerInvariant();
    }

    // ─────────────────────────────────────────────────────────── 内部

    private static string BuildSidecarContent(
        string packageId,
        string sourceRelative,
        IReadOnlyList<MigrationAsset> assets)
        => AssetSidecarJson.ToJson(new AssetSidecar(
            AssetSidecarJson.CurrentSchemaVersion,
            packageId,
            sourceRelative,
            assets
                .Select(asset => new AssetMetadata(
                    asset.Guid,
                    asset.Kind,
                    asset.Id,
                    asset.Version,
                    Anchor: null,
                    Loader: null,
                    DependsOn: [],
                    ConflictsWith: []))
                .ToArray()));

    private static (MigrationFileAction Formal, MigrationFileAction Test) BuildLocks(
        string root,
        string packageId,
        IReadOnlyList<MigrationEntry> entries,
        List<MigrationObstacle> obstacles)
    {
        var declared = entries.SelectMany(entry => entry.Assets).ToArray();

        foreach (var duplicate in entries
                     .SelectMany(entry => entry.Assets.Select(asset => (entry.SourceFile, Asset: asset)))
                     .GroupBy(pair => pair.Asset.Guid)
                     .Where(group => group.Select(pair => pair.SourceFile).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            obstacles.Add(new MigrationObstacle(
                "",
                $"同一资产 GUID {duplicate.Key} 被多个源文件声明"
                + $"（{string.Join("、", duplicate.Select(pair => pair.SourceFile).Distinct())}）"
                + "——包级结构性非法，需人工消除重复 id。"));
        }

        foreach (var conflict in declared
                     .GroupBy(asset => (asset.Kind, asset.Id))
                     .Where(group => group.Select(asset => asset.Version).Distinct().Count() > 1))
        {
            var versions = string.Join(" / ", conflict.Select(asset => asset.Version.ToString()).Distinct());
            obstacles.Add(new MigrationObstacle(
                "",
                $"同一资产 {AssetKindTokens.Token(conflict.Key.Kind)}:{conflict.Key.Id} 在包内声明了多个版本"
                + $"（{versions}）——lock 无法唯一表达，需人工定版。"));
        }

        var formal = BuildLockAction(root, packageId, declared, AssetDomain.Formal);
        var test = BuildLockAction(root, packageId, declared, AssetDomain.Test);
        return (formal, test);
    }

    private static MigrationFileAction BuildLockAction(
        string root,
        string packageId,
        IReadOnlyList<MigrationAsset> declared,
        AssetDomain domain)
    {
        var relative = AssetLockJson.FileNameFor(domain);
        var path = Path.Combine(root, relative);
        var existing = File.Exists(path) ? Normalize(File.ReadAllText(path, Encoding.UTF8)) : null;

        var lockModel = AssetLock.Create(
            packageId,
            domain,
            declared.Select(asset => new AssetLockEntry(asset.Guid, asset.Kind, AssetAppId.Compose(packageId, asset.Id), asset.Version)));
        return BuildFileAction(relative, existing, AssetLockJson.ToJson(lockModel));
    }

    /// <summary>
    /// 迁移期内容指纹输入（双路径，S20）：磁盘现有 sidecar ∪ 计划中 <see cref="MigrationAction.Create"/>／
    /// <see cref="MigrationAction.Overwrite"/> 的规范内容；同一 <c>sidecarRelative</c> 以**计划内容优先**
    /// （Create 项的 sidecar 尚未落盘）；<see cref="MigrationAction.Skip"/> 项一律读现有文件。
    /// </summary>
    private static IReadOnlyList<(string RelativePath, AssetSidecar Sidecar)> ContentFingerprintInput(
        string root,
        IReadOnlyList<MigrationEntry> entries)
    {
        var sidecars = new Dictionary<string, AssetSidecar>(StringComparer.Ordinal);

        foreach (var relative in PackageScanner.EnumerateCurrentSidecars(root))
        {
            try
            {
                sidecars[relative] = AssetSidecarJson.ReadFile(
                    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (FormatException)
            {
                // 不可解析的既有 sidecar 已作为障碍记录（ReadExistingAssets）；此处不进指纹输入。
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Action == MigrationAction.Skip || entry.Content is not { } content)
            {
                continue;
            }

            sidecars[entry.SidecarFile.Replace('\\', '/')] = AssetSidecarJson.Parse(content);
        }

        return sidecars
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }

    private static MigrationFileAction BuildFileAction(string relativePath, string? existingText, string canonicalText)
    {
        var normalized = Normalize(canonicalText);
        if (existingText is null)
        {
            return new MigrationFileAction(relativePath, MigrationAction.Create, normalized);
        }

        return string.Equals(existingText, normalized, StringComparison.Ordinal)
            ? new MigrationFileAction(relativePath, MigrationAction.Skip, null)
            : new MigrationFileAction(relativePath, MigrationAction.Overwrite, normalized);
    }

    private static MigrationPlan Blocked(string root, string packageId, IReadOnlyList<MigrationObstacle> obstacles)
        => new(
            root,
            packageId,
            new MigrationFileAction(ManifestFileName, MigrationAction.Skip, null),
            new MigrationFileAction(AssetLockJson.FormalFileName, MigrationAction.Skip, null),
            new MigrationFileAction(AssetLockJson.TestFileName, MigrationAction.Skip, null),
            Array.Empty<MigrationEntry>(),
            obstacles);

    private static IReadOnlyList<MigrationAsset> ReadExistingAssets(
        string sidecarAbsolute,
        string sourceRelative,
        List<MigrationObstacle> obstacles)
    {
        try
        {
            var sidecar = AssetSidecarJson.ReadFile(sidecarAbsolute);
            return sidecar.Assets
                .Select(asset => new MigrationAsset(asset.Guid, asset.Kind, asset.Id, asset.Version))
                .ToArray();
        }
        catch (FormatException ex)
        {
            obstacles.Add(new MigrationObstacle(
                sourceRelative + AssetSidecarJson.SidecarExtension,
                $"既有 sidecar 不可解析（未重写；如需重建请加 --force 并人工核对）：{ex.Message}"));
            return Array.Empty<MigrationAsset>();
        }
    }

    /// <summary>由源文件生成资产声明；不可解析／无声明＝null（并记录障碍）。</summary>
    private static IReadOnlyList<MigrationAsset>? GenerateAssets(
        string absolute,
        string relative,
        Guid packageGuid,
        List<MigrationObstacle> obstacles)
    {
        if (relative.EndsWith(CardSuffix, StringComparison.OrdinalIgnoreCase))
        {
            // 只取数据体的**权威 id**（R2：sidecar 的 id 须与源文件一致）。
            // 不用 CardDataLoader：它要求内置组件已注册（装配期状态），而元数据迁移不得依赖宿主装配面。
            var read = CardDataJson.Read(File.ReadAllText(absolute, Encoding.UTF8));
            if (read.Body is null)
            {
                obstacles.Add(new MigrationObstacle(relative, $"卡数据体不可解析：{read.Error}"));
                return null;
            }

            return
            [
                new MigrationAsset(
                    AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, read.Body.Id, InitialVersion),
                    AssetKind.Card,
                    read.Body.Id,
                    InitialVersion),
            ];
        }

        if (!relative.EndsWith(PrefabSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var snapshot = PrefabJson.Deserialize(File.ReadAllText(absolute, Encoding.UTF8));
            var id = snapshot.Root.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                obstacles.Add(new MigrationObstacle(relative, "预制体缺少 root.id，无法确定资产局部 ID。"));
                return null;
            }

            return
            [
                new MigrationAsset(
                    AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Effect, id, InitialVersion),
                    AssetKind.Effect,
                    id,
                    InitialVersion),
            ];
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException or IOException)
        {
            obstacles.Add(new MigrationObstacle(relative, $"预制体不可解析：{ex.Message}"));
            return null;
        }
    }

    private static IReadOnlyList<(string Absolute, string Relative)> EnumerateSourceFiles(string root)
    {
        var files = new List<(string, string)>();
        foreach (var absolute in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(absolute);
            if (!name.EndsWith(CardSuffix, StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(PrefabSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Relative(Path.GetRelativePath(root, absolute));
            if (IsExcluded(relative))
            {
                continue;
            }

            files.Add((absolute, relative));
        }

        files.Sort((left, right) => string.CompareOrdinal(left.Item2, right.Item2));
        return files;
    }

    private static bool IsExcluded(string relative)
    {
        var segments = relative.Split('/');
        if (segments.Length > 1 && string.Equals(segments[0], VersionsDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return true; // versions/ 不生成（R3：历史版本由目录约定自动扫描，不在迁移期补 sidecar）
        }

        return segments.Any(segment => segment.EndsWith(PendingPackageSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPackageDirectory(string directory)
    {
        if (File.Exists(Path.Combine(directory, ManifestFileName)))
        {
            return true;
        }

        return Directory.EnumerateDirectories(directory)
            .Any(child => Path.GetFileName(child).StartsWith(PackageScanner.CardsDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Path.GetFileName(child), PackageScanner.EffectsDirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string path) => path.Replace('\\', '/');

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
