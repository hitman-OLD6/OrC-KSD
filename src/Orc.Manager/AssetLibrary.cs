using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Packages;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>资产库中的一条包注册记录（同 id 多版本并存；只增不减）。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="Version">包版本。</param>
/// <param name="Guid">包 GUID。</param>
/// <param name="Directory">包目录。</param>
/// <param name="Assets">该包 lock 声明的资产条数（两域之和）。</param>
public sealed record PackageEntry(
    string PackageId,
    string Version,
    string Guid,
    string Directory,
    int Assets);

/// <summary>资产注册结果（R2：管理器能给出一次"资产注册结果"，服务器实例据此加载自己的卡池）。</summary>
/// <param name="Registered">已注册的包。</param>
/// <param name="Rejected">注册失败的包（目录 ＋ 原因）。</param>
public sealed record AssetRegistrationResult(
    IReadOnlyList<PackageEntry> Registered,
    IReadOnlyList<string> Rejected)
{
    /// <summary>JSON 形态（CLI / 管理面共用）。</summary>
    public JsonObject ToJson() => new()
    {
        ["registered"] = new JsonArray(Registered.Select(entry => (JsonNode)new JsonObject
        {
            ["packageId"] = entry.PackageId,
            ["version"] = entry.Version,
            ["guid"] = entry.Guid,
            ["directory"] = entry.Directory,
            ["assets"] = entry.Assets,
        }).ToArray()),
        ["rejected"] = new JsonArray(Rejected.Select(reason => (JsonNode)JsonValue.Create(reason)!).ToArray()),
    };
}

/// <summary>一份 seat 的卡包声明（引用形态；并集判定的输入单位）。</summary>
/// <param name="Side">哪一方（<c>seat0</c>/<c>seat1</c>；用于缺口清单点名）。</param>
/// <param name="Packages">声明。</param>
public sealed record PackageDeclaration(string Side, IReadOnlyList<PackageRef> Packages);

/// <summary>本局加载计划（管理器 → 服务器；服务器只执行，不做资产判定）。</summary>
/// <param name="PackDirectories">解析后的包目录（含实例级沙盒包）。</param>
/// <param name="Packages">并集后的包引用（回显）。</param>
/// <param name="Assets">并集后的资产条数。</param>
public sealed record MatchLoadPlan(
    IReadOnlyList<string> PackDirectories,
    IReadOnlyList<PackageRef> Packages,
    int Assets)
{
    /// <summary>JSON 形态（<c>match.start</c> 的 <c>plan</c> 段）。</summary>
    public JsonObject ToJson() => new()
    {
        ["packDirectories"] = new JsonArray(PackDirectories
            .Select(path => (JsonNode)JsonValue.Create(path)!)
            .ToArray()),
        ["packages"] = new JsonArray(Packages.Select(package => (JsonNode)package.ToJson()).ToArray()),
        ["assets"] = Assets,
    };
}

/// <summary>资产判定结局：要么给计划，要么给拒绝（码 ＋ 清单）。</summary>
/// <param name="Ok">是否可开局。</param>
/// <param name="Code">拒绝码（<see cref="ManagerCodes.LockConflict"/> / <see cref="ManagerCodes.MissingAssets"/>）。</param>
/// <param name="Message">人读说明。</param>
/// <param name="Issues">结构化清单。</param>
/// <param name="Plan">通过时的加载计划。</param>
public sealed record PlanOutcome(
    bool Ok,
    string Code,
    string? Message,
    JsonArray Issues,
    MatchLoadPlan? Plan)
{
    /// <summary>成功。</summary>
    public static PlanOutcome Accepted(MatchLoadPlan plan) => new(true, ManagerCodes.Ok, "ok", new JsonArray(), plan);

    /// <summary>拒绝。</summary>
    public static PlanOutcome Rejected(string code, string message, JsonArray issues)
        => new(false, code, message, issues, null);
}

/// <summary>
/// 管理器资产库（R2）：进程级、只读、**只增不减**（同 id 多版本并存，进程存活期内不回收）。
/// <para>
/// 职责＝资产注册、依赖分析、资产同步分析。**不持有**任何 <c>Match</c>/<c>Player</c>/<c>Room</c> 活对象。
/// </para>
/// <para>
/// 并集判定是**唯一权威**：两份 seat 声明 ＋ 实例级沙盒包 → 在资产库定位 → <see cref="LockMerger"/> 合并
/// → <see cref="LoadListPlanner.Plan"/> 产计划。管理器只映射现有 issue，不新写冲突判定。
/// </para>
/// </summary>
public sealed class AssetLibrary
{
    private readonly object _sync = new();
    private readonly List<PackageEntry> _packages = new();
    private readonly Dictionary<string, AssetLock> _locks = new(StringComparer.Ordinal);
    private readonly AssetRegistry _registry = new();

    /// <summary>已注册的包（只增不减）。</summary>
    public IReadOnlyList<PackageEntry> Packages
    {
        get
        {
            lock (_sync)
            {
                return _packages.ToArray();
            }
        }
    }

    /// <summary>
    /// 注册卡包目录（幂等：同目录重复注册＝跳过）。包级结构性非法＝该包被拒绝并给出原因，不抛。
    /// </summary>
    public AssetRegistrationResult Register(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var rejected = new List<string>();

        lock (_sync)
        {
            foreach (var raw in directories)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var directory = Path.GetFullPath(raw);
                if (_packages.Any(entry => string.Equals(entry.Directory, directory, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var manifestPath = Path.Combine(directory, "manifest.json");
                    if (!File.Exists(manifestPath))
                    {
                        rejected.Add($"{directory}：缺 manifest.json（不是卡包）");
                        continue;
                    }

                    var manifest = PackageManifest.Parse(File.ReadAllText(manifestPath), out _);
                    PackageRegistrar.Register(directory, manifest, _registry);

                    var assets = 0;
                    foreach (var domain in new[] { AssetDomain.Formal, AssetDomain.Test })
                    {
                        if (AssetLockJson.ReadPackage(directory, domain) is { } @lock)
                        {
                            assets += @lock.Assets.Count;
                            // 同 id 多版本并存：key 用 "id#version"（并集判定按声明定位）。
                            _locks[$"{manifest.Id}#{manifest.Version}"] = @lock;
                        }
                    }

                    _packages.Add(new PackageEntry(
                        manifest.Id,
                        manifest.Version.ToString(),
                        manifest.Guid.ToString("N"),
                        directory,
                        assets));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
                    or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
                {
                    rejected.Add($"{directory}：{ex.Message}");
                }
            }

            return new AssetRegistrationResult(_packages.ToArray(), rejected);
        }
    }

    /// <summary>
    /// 依赖分析（R2）：哪些包/资产可用、哪些不可用及原因（结构性失败 ⇒ 全部不可用）。
    /// </summary>
    public JsonObject AnalyzeDependencies()
    {
        lock (_sync)
        {
            var locks = _locks.Values.ToArray();
            if (locks.Length == 0)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["message"] = "资产库为空（尚未注册任何卡包）。",
                    ["packages"] = new JsonArray(),
                    ["issues"] = new JsonArray(),
                };
            }

            var plan = LoadListPlanner.Plan(_registry, locks);

            var issues = new JsonArray();
            foreach (var issue in plan.Issues)
            {
                issues.Add(new JsonObject
                {
                    ["code"] = issue.Code.ToString(),
                    ["fatal"] = issue.IsFatal,
                    ["domain"] = issue.Domain.ToString(),
                    ["appId"] = issue.AppId,
                    ["message"] = issue.Message,
                });
            }

            return new JsonObject
            {
                ["ok"] = plan.IsValid,
                ["packages"] = new JsonArray(_packages.Select(entry => (JsonNode)new JsonObject
                {
                    ["packageId"] = entry.PackageId,
                    ["version"] = entry.Version,
                    ["assets"] = entry.Assets,
                }).ToArray()),
                ["formalAssets"] = plan.Formal.Count,
                ["testAssets"] = plan.Test.Count,
                ["issues"] = issues,
            };
        }
    }

    /// <summary>
    /// 资产同步分析（R2）：两份 lock 声明的差异清单——缺哪些资产、哪些版本不一致，并点名"哪一方缺什么"。
    /// </summary>
    public JsonObject DiffLocks(PackageDeclaration left, PackageDeclaration right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        lock (_sync)
        {
            var missing = new JsonArray();
            var mismatched = new JsonArray();

            var leftResolved = Resolve(left, missing);
            var rightResolved = Resolve(right, missing);

            // 同一包在两方声明中版本不同＝冲突（拒绝开局）。
            foreach (var (packageId, leftVersion) in leftResolved)
            {
                if (rightResolved.TryGetValue(packageId, out var rightVersion)
                    && !string.Equals(leftVersion, rightVersion, StringComparison.Ordinal))
                {
                    mismatched.Add(new JsonObject
                    {
                        ["packageId"] = packageId,
                        ["left"] = leftVersion,
                        ["right"] = rightVersion,
                    });
                }
            }

            var leftLocks = LocksOf(leftResolved.Keys);
            var rightLocks = LocksOf(rightResolved.Keys);

            var assetDiff = new JsonArray();
            AppendAssetDiff(assetDiff, left.Side, right.Side, leftLocks, rightLocks);
            AppendAssetDiff(assetDiff, right.Side, left.Side, rightLocks, leftLocks);

            return new JsonObject
            {
                ["ok"] = missing.Count == 0 && mismatched.Count == 0 && assetDiff.Count == 0,
                ["missing"] = missing,
                ["versionMismatch"] = mismatched,
                ["assetDiff"] = assetDiff,
            };
        }
    }

    /// <summary>
    /// 并集判定（R2/S4）：两份声明 ＋ 沙盒包 → 定位 → 合并 lock → 产计划。
    /// <para>
    /// 致命 issue → <c>lock-conflict</c>（指名双方与版本）；非致命 <c>Missing</c>/<c>DependencyMissing</c>
    /// → <c>missing-assets</c>（点名哪一方缺哪些 <c>{kind,id,version}</c>）。
    /// </para>
    /// </summary>
    public PlanOutcome Plan(
        PackageDeclaration left,
        PackageDeclaration right,
        string? sandboxPackDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        lock (_sync)
        {
            var issues = new JsonArray();
            var missing = new JsonArray();

            var leftResolved = Resolve(left, missing, issues);
            var rightResolved = Resolve(right, missing, issues);

            var conflictPackages = new JsonArray();
            foreach (var (packageId, leftVersion) in leftResolved)
            {
                if (rightResolved.TryGetValue(packageId, out var rightVersion)
                    && !string.Equals(leftVersion, rightVersion, StringComparison.Ordinal))
                {
                    conflictPackages.Add(new JsonObject
                    {
                        ["packageId"] = packageId,
                        ["left"] = leftVersion,
                        ["right"] = rightVersion,
                        ["leftSide"] = left.Side,
                        ["rightSide"] = right.Side,
                    });
                }
            }

            if (conflictPackages.Count > 0)
            {
                issues.Add(new JsonObject
                {
                    ["code"] = ManagerCodes.LockConflict,
                    ["message"] = "同一包在双方声明中版本不同（并集冲突）。",
                    ["conflicts"] = conflictPackages,
                });

                return PlanOutcome.Rejected(
                    ManagerCodes.LockConflict,
                    "双方卡包声明存在版本冲突（同 id 版本不同＝冲突）。",
                    issues);
            }

            var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in leftResolved)
            {
                selected[pair.Key] = pair.Value;
            }

            foreach (var pair in rightResolved)
            {
                selected.TryAdd(pair.Key, pair.Value);
            }

            var locks = LocksOf(selected.Keys);

            // 沙盒包：**实例级固定包**，强制加入每局并集（不由玩家声明）。
            var directories = new List<string>(DirectoriesOf(selected.Keys));
            if (sandboxPackDirectory is { Length: > 0 } sandbox && Directory.Exists(sandbox))
            {
                try
                {
                    var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(sandbox, "manifest.json")), out _);
                    foreach (var domain in new[] { AssetDomain.Formal, AssetDomain.Test })
                    {
                        if (AssetLockJson.ReadPackage(sandbox, domain) is { } @lock)
                        {
                            locks.Add(@lock);
                            directories.Add(sandbox);
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
                    or InvalidOperationException or ArgumentException)
                {
                    // 沙盒包非法＝不加入并集（开局侧还有"剔除重试一次"的降级）。
                    issues.Add(new JsonObject
                    {
                        ["code"] = "sandbox-invalid",
                        ["message"] = $"沙盒包不可用（已从并集剔除）：{ex.Message}",
                        ["fatal"] = false,
                    });
                }
            }

            if (missing.Count > 0)
            {
                issues.Add(new JsonObject
                {
                    ["code"] = ManagerCodes.MissingAssets,
                    ["message"] = "并集所需的资产在资产库中缺失。",
                    ["missing"] = missing,
                });
            }

            var plan = LoadListPlanner.Plan(_registry, locks);
            foreach (var issue in plan.Issues)
            {
                if (issue.IsFatal)
                {
                    issues.Add(new JsonObject
                    {
                        ["code"] = ManagerCodes.LockConflict,
                        ["message"] = issue.ToString(),
                        ["fatal"] = true,
                    });
                }
                else if (issue.Code is AssetLoadIssueCode.DependencyMissing or AssetLoadIssueCode.Unregistered
                    or AssetLoadIssueCode.DependencyInvalid or AssetLoadIssueCode.DependencyVersionMismatch)
                {
                    issues.Add(new JsonObject
                    {
                        ["code"] = ManagerCodes.MissingAssets,
                        ["message"] = issue.ToString(),
                        ["appId"] = issue.AppId,
                        ["fatal"] = false,
                    });
                }
            }

            var fatal = issues.Any(node => node?["code"]?.GetValue<string>() == ManagerCodes.LockConflict);
            if (fatal)
            {
                return PlanOutcome.Rejected(
                    ManagerCodes.LockConflict,
                    "并集存在结构性 lock 冲突（同 id 版本不同或环依赖）。",
                    issues);
            }

            if (issues.Any(node => node?["code"]?.GetValue<string>() == ManagerCodes.MissingAssets))
            {
                return PlanOutcome.Rejected(
                    ManagerCodes.MissingAssets,
                    "并集存在资产缺口（拒绝开局）。",
                    issues);
            }

            var packages = selected.Select(pair => new PackageRef(pair.Key, pair.Value)).ToArray();
            return PlanOutcome.Accepted(new MatchLoadPlan(directories, packages, plan.Count));
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>把一份声明解析为 packageId → version；缺失的引用进 <paramref name="missing"/>。</summary>
    private Dictionary<string, string> Resolve(
        PackageDeclaration declaration,
        JsonArray missing,
        JsonArray? issues = null)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reference in declaration.Packages)
        {
            var versions = _packages
                .Where(entry => string.Equals(entry.PackageId, reference.PackageId, StringComparison.Ordinal))
                .ToArray();

            if (versions.Length == 0)
            {
                missing.Add(new JsonObject
                {
                    ["side"] = declaration.Side,
                    ["packageId"] = reference.PackageId,
                    ["version"] = reference.Version,
                    ["reason"] = "资产库中没有该包",
                });
                continue;
            }

            var match = reference.Version is { Length: > 0 } wanted
                ? versions.FirstOrDefault(entry => string.Equals(entry.Version, wanted, StringComparison.Ordinal))
                : versions[^1];

            if (match is null)
            {
                missing.Add(new JsonObject
                {
                    ["side"] = declaration.Side,
                    ["packageId"] = reference.PackageId,
                    ["version"] = reference.Version,
                    ["reason"] = "资产库中没有该版本",
                    ["available"] = new JsonArray(versions
                        .Select(entry => (JsonNode)JsonValue.Create(entry.Version)!)
                        .ToArray()),
                });
                continue;
            }

            resolved[reference.PackageId] = match.Version;
            _ = issues;
        }

        return resolved;
    }

    private List<AssetLock> LocksOf(IEnumerable<string> packageIds)
    {
        var locks = new List<AssetLock>();
        foreach (var packageId in packageIds)
        {
            var versions = _packages
                .Where(entry => string.Equals(entry.PackageId, packageId, StringComparison.Ordinal))
                .Select(entry => entry.Version)
                .ToArray();

            foreach (var version in versions.Length == 0 ? new[] { string.Empty } : versions)
            {
                if (_locks.TryGetValue($"{packageId}#{version}", out var @lock))
                {
                    locks.Add(@lock);
                }
            }
        }

        return locks;
    }

    private IEnumerable<string> DirectoriesOf(IEnumerable<string> packageIds)
    {
        var ids = packageIds.ToHashSet(StringComparer.Ordinal);
        return _packages
            .Where(entry => ids.Contains(entry.PackageId))
            .Select(entry => entry.Directory)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendAssetDiff(
        JsonArray target,
        string side,
        string otherSide,
        IReadOnlyList<AssetLock> mine,
        IReadOnlyList<AssetLock> theirs)
    {
        var other = theirs
            .SelectMany(@lock => @lock.Assets)
            .Select(entry => (entry.Kind, entry.AppId))
            .ToHashSet();

        foreach (var entry in mine.SelectMany(@lock => @lock.Assets))
        {
            if (!other.Contains((entry.Kind, entry.AppId)))
            {
                target.Add(new JsonObject
                {
                    ["side"] = side,
                    ["missingIn"] = otherSide,
                    ["kind"] = entry.Kind.ToString(),
                    ["appId"] = entry.AppId,
                    ["version"] = entry.Version.ToString(),
                });
            }
        }
    }
}
