using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 加载计算期的问题码（**结构化报告**；只有 <see cref="LockConflict"/> 与 <see cref="Cycle"/> 是结构性失败）。
/// <para>
/// 资产级问题（依赖缺失／域越界／未注册…）只把**该资产**判非法并从列表剔除，**不整段失败**（R15/D23）。
/// </para>
/// </summary>
public enum AssetLoadIssueCode
{
    /// <summary>同域两份 lock 对同一资产锁定了不同版本（结构性：本次计算不产出列表）。</summary>
    LockConflict = 0,

    /// <summary>lock 引用的版本未在注册表注册（该资产不参与加载）。</summary>
    Unregistered = 1,

    /// <summary>注册域与 lock 域不符（跨域不参与本域加载；测试不污染正式）。</summary>
    DomainMismatch = 2,

    /// <summary>依赖资产不在本次加载集合（lock 无条目或未注册）。</summary>
    DependencyMissing = 3,

    /// <summary>依赖资产的锁定版本不满足声明的版本约束（lock 为准，仍装载；仅报告）。</summary>
    DependencyVersionMismatch = 4,

    /// <summary>正式资产依赖测试资产（R15：单向依赖被破坏 ⇒ 该资产判非法）。</summary>
    FormalDependsOnTest = 5,

    /// <summary>测试资产依赖的正式资产不在正式 lock（R15：该测试资产判非法，不整段失败）。</summary>
    TestDependsOnUnlockedFormal = 6,

    /// <summary>内含资产找不到宿主（宿主不在本次加载集合／无源文件）——不参与拓扑。</summary>
    OrphanEmbedded = 7,

    /// <summary>依赖资产因非法被排除（该资产一并判非法）。</summary>
    DependencyInvalid = 8,

    /// <summary>环依赖（结构性：<see cref="AssetLoadIssue.Path"/> 为环路径）。</summary>
    Cycle = 9,

    /// <summary>元数据声明的冲突双方同处本次加载集合（R16/D52：双向硬冲突 ⇒ 结构性失败）。</summary>
    DeclaredConflict = 10,

    /// <summary>
    /// lock 对同一资产 GUID 重复声明（自相矛盾，无法判断哪条为真）——结构性失败（F12/I16）。
    /// 保留 merge 稳定序的首条，其余每条各报一条。
    /// </summary>
    DuplicateLockEntry = 11,
}

/// <summary>加载计算期的一条问题（结构化；<see cref="Path"/> 仅环依赖使用）。</summary>
/// <param name="Code">问题码。</param>
/// <param name="Domain">所属域。</param>
/// <param name="AppId">相关资产的应用层 ID（无＝null）。</param>
/// <param name="Message">人读说明（环依赖含完整环路径）。</param>
/// <param name="Path">环路径（按依赖方向，首尾同一资产；非环＝空）。</param>
public sealed record AssetLoadIssue(
    AssetLoadIssueCode Code,
    AssetDomain Domain,
    string? AppId,
    string Message,
    IReadOnlyList<string> Path)
{
    /// <summary>是否为结构性失败（整次计算不产出可加载列表）。</summary>
    public bool IsFatal => Code is AssetLoadIssueCode.LockConflict
        or AssetLoadIssueCode.Cycle
        or AssetLoadIssueCode.DeclaredConflict
        or AssetLoadIssueCode.DuplicateLockEntry;

    /// <inheritdoc />
    public override string ToString() => $"[{AssetDomainTokens.Token(Domain)}] {Code}：{Message}";
}

/// <summary>
/// **加载列表计算器**（R7/D10/D17/D18）：lock 集合 → 注册面取版本 → 依赖检测 → Kahn 拓扑分层 →
/// 层内局部 ID 字典序。依赖检测、拓扑排序与加载顺序**在同一次计算中完成**（装载期不做依赖查找）。
/// <para>
/// 环依赖＝结构性失败（报错并输出**环路径**）；内含资产**不作为独立节点**（随宿主同层）；
/// 正式资产不得依赖测试资产（R15）；测试资产依赖的正式资产不在正式 lock ⇒ 该测试资产判非法、不整段失败。
/// </para>
/// </summary>
public static class LoadListPlanner
{
    /// <summary>计算加载计划（纯函数：不读写缓存、不装载）。</summary>
    /// <param name="registry">资产注册表（只读查询）。</param>
    /// <param name="locks">参与合并的 lock（正式／测试按域分别合并，跨域绝不互相合并）。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public static AssetLoadPlan Plan(AssetRegistry registry, IEnumerable<AssetLock> locks)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(locks);

        var materialized = locks as IReadOnlyList<AssetLock> ?? locks.ToArray();
        var issues = new List<AssetLoadIssue>();

        var formalMerge = LockMerger.Merge(AssetDomain.Formal, materialized);
        var testMerge = LockMerger.Merge(AssetDomain.Test, materialized);

        var formal = Build(registry, formalMerge, testMerge, formalList: null, issues);
        var test = Build(registry, testMerge, formalMerge, formal, issues);

        return new AssetLoadPlan(formal, test, issues);
    }

    /// <summary>构建单个域的层状列表。</summary>
    /// <param name="registry">注册表。</param>
    /// <param name="merge">本域 lock 合并结果。</param>
    /// <param name="otherMerge">另一域的 lock 合并结果（仅用于跨域依赖判定）。</param>
    /// <param name="formalList">已算好的正式列表（测试域用它判定「正式依赖已满足」；正式域传 null）。</param>
    /// <param name="issues">问题收集器。</param>
    private static LoadList Build(
        AssetRegistry registry,
        LockMergeResult merge,
        LockMergeResult otherMerge,
        LoadList? formalList,
        List<AssetLoadIssue> issues)
    {
        var domain = merge.Domain;

        foreach (var conflict in merge.Conflicts)
        {
            issues.Add(new AssetLoadIssue(
                AssetLoadIssueCode.LockConflict, domain, conflict.AppId, conflict.ToString(), []));
        }

        var (nodes, embedded, hostOfEmbedded) = Resolve(registry, merge, domain, issues);
        var nodesByKey = IndexByKey(nodes);

        var hostByEmbeddedKey = new Dictionary<AssetKey, AssetRegistration>();
        foreach (var asset in embedded)
        {
            if (hostOfEmbedded.TryGetValue(asset.AssetGuid, out var host))
            {
                hostByEmbeddedKey.TryAdd(new AssetKey(asset.Kind, asset.AppId), host);
            }
        }

        // 另一域的全部资产键（正式域用它识别「正式资产依赖测试资产」）。
        var other = Resolve(registry, otherMerge, otherMerge.Domain, []);
        var otherKeys = IndexByKey(other.Nodes.Concat(other.Embedded)).Keys.ToHashSet();

        var ordered = nodes
            .OrderBy(asset => asset.LocalId, StringComparer.Ordinal)
            .ThenBy(asset => asset.Kind)
            .ThenBy(asset => asset.AppId, StringComparer.Ordinal)
            .ToArray();

        // ── 依赖检测（一次计算内完成）：资产级非法 ⇒ 剔除并报告，不整段失败（R15/D23） ──
        var excluded = new HashSet<Guid>();
        var edges = new Dictionary<Guid, List<AssetRegistration>>();

        foreach (var node in ordered)
        {
            var resolved = new List<AssetRegistration>();
            (AssetLoadIssueCode Code, string Message)? failure = null;

            foreach (var reference in node.Metadata.DependsOn)
            {
                var appId = NormalizeReference(node.PackageId, reference.AppId);
                var key = new AssetKey(reference.Kind, appId);

                if (nodesByKey.TryGetValue(key, out var target))
                {
                    resolved.Add(target);
                    CheckConstraint(node, reference, target, domain, issues);
                    continue;
                }

                if (hostByEmbeddedKey.TryGetValue(key, out var host))
                {
                    resolved.Add(host); // 依赖内含资产 ⇒ 依赖其宿主（内含资产随宿主同层）
                    CheckConstraint(node, reference, host, domain, issues);
                    continue;
                }

                if (domain == AssetDomain.Test && TryFind(formalList, key, out var formalAsset))
                {
                    // 测试资产依赖的正式资产已在正式列表：先装正式、再装测试 ⇒ 无需跨域边。
                    CheckConstraint(node, reference, formalAsset!, domain, issues);
                    continue;
                }

                failure = JudgeUnmet(node, key, reference, domain, otherKeys, registry);
                break;
            }

            if (failure is null)
            {
                edges[node.AssetGuid] = resolved.DistinctBy(asset => asset.AssetGuid).ToList();
                continue;
            }

            excluded.Add(node.AssetGuid);
            issues.Add(new AssetLoadIssue(failure.Value.Code, domain, node.AppId, failure.Value.Message, []));
        }

        // 依赖传播：依赖资产被判非法 ⇒ 依赖者一并非法（不整段失败）。
        var cascaded = true;
        while (cascaded)
        {
            cascaded = false;
            foreach (var node in ordered)
            {
                if (excluded.Contains(node.AssetGuid) || !edges.TryGetValue(node.AssetGuid, out var dependencies))
                {
                    continue;
                }

                var invalid = dependencies.FirstOrDefault(dependency => excluded.Contains(dependency.AssetGuid));
                if (invalid is null)
                {
                    continue;
                }

                excluded.Add(node.AssetGuid);
                edges.Remove(node.AssetGuid);
                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.DependencyInvalid,
                    domain,
                    node.AppId,
                    $"依赖 {invalid.AppId}#{invalid.Version} 被判非法（不生成）——该资产一并判非法。",
                    []));
                cascaded = true;
                break;
            }
        }

        // ── 声明冲突（R16/D52）：**域内双向硬冲突** ⇒ 结构性失败（本次计算不产出列表） ──
        // 声明语义＝「我不能与它共存」，单向声明即成立（不要求对方也声明）；
        // 判定集合＝**依赖检测与传播之后仍在列表中的资产**（F5：本会被剔除的资产不参与冲突判定；
        // 内含资产不参与、测试不污染正式）。
        var active = ordered.Where(node => !excluded.Contains(node.AssetGuid)).ToArray();
        var conflictIndex = IndexByKey(active);
        foreach (var node in active)
        {
            foreach (var declared in node.Metadata.ConflictsWith)
            {
                var conflictAppId = NormalizeReference(node.PackageId, declared.AppId);
                if (!conflictIndex.TryGetValue(new AssetKey(declared.Kind, conflictAppId), out var target)
                    || target.AssetGuid == node.AssetGuid
                    || !declared.IsSatisfiedBy(target.Version))
                {
                    continue;
                }

                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.DeclaredConflict,
                    domain,
                    node.AppId,
                    $"资产 {node.AssetName} 声明与 {target.AssetName} 冲突"
                    + "（双向硬冲突：两者同处本次加载集合）——本次计算不产出列表。",
                    []));
            }
        }

        return Layer(domain, ordered, edges, excluded, hostOfEmbedded, embedded, issues);
    }

    /// <summary>lock → 注册面取版本；内含资产按「同包同源文件」归并到宿主。</summary>
    private static (List<AssetRegistration> Nodes, List<AssetRegistration> Embedded, Dictionary<Guid, AssetRegistration> HostOf)
        Resolve(
            AssetRegistry registry,
            LockMergeResult merge,
            AssetDomain domain,
            List<AssetLoadIssue> issues)
    {
        var nodes = new List<AssetRegistration>();
        var embedded = new List<AssetRegistration>();
        var seenGuids = new Dictionary<Guid, AssetLockEntry>();

        foreach (var entry in merge.Entries)
        {
            if (seenGuids.TryGetValue(entry.AssetGuid, out var kept))
            {
                // F12/I16：同一 GUID 多条＝lock 自相矛盾 ⇒ 结构化问题（不抛、不静默去重）。
                // 保留 merge 稳定序（kind → appId → version）的首条，每个被丢弃的额外条目各报一条。
                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.DuplicateLockEntry,
                    domain,
                    entry.AppId,
                    $"lock 对同一资产 GUID '{entry.AssetGuid}' 重复声明："
                    + $"保留 {kept.AppId}#{kept.Version}，丢弃 {entry.AppId}#{entry.Version}"
                    + "——结构性非法（本次计算不产出列表）。",
                    []));
                continue;
            }

            seenGuids.Add(entry.AssetGuid, entry);

            var registration = registry.Find(entry.AssetGuid);
            if (registration is null)
            {
                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.Unregistered,
                    domain,
                    entry.AppId,
                    $"lock 引用的资产 {entry.AssetGuid}（{entry.AppId}#{entry.Version}）未在注册表注册——不参与加载。",
                    []));
                continue;
            }

            if (registration.Domain != domain)
            {
                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.DomainMismatch,
                    domain,
                    entry.AppId,
                    $"资产 {entry.AppId}#{entry.Version} 以 {AssetDomainTokens.Token(registration.Domain)} 域注册，"
                    + $"与 lock 的 {AssetDomainTokens.Token(domain)} 域不符——跨域不参与本域加载。",
                    []));
                continue;
            }

            (registration.IsEmbedded ? embedded : nodes).Add(registration);
        }

        // 宿主＝同包同源文件中 anchor 为 null 的注册（多个候选取应用层 ID 最小者，保证确定性）。
        var hostsByFile = new Dictionary<(string PackageId, string Source), AssetRegistration>();
        foreach (var node in nodes)
        {
            if (node.SourcePath is not { } source)
            {
                continue;
            }

            var key = (node.PackageId, source);
            if (!hostsByFile.TryGetValue(key, out var existing)
                || string.CompareOrdinal(node.AppId, existing.AppId) < 0)
            {
                hostsByFile[key] = node;
            }
        }

        var hostOf = new Dictionary<Guid, AssetRegistration>();
        foreach (var asset in embedded)
        {
            if (asset.SourcePath is not { } source || !hostsByFile.TryGetValue((asset.PackageId, source), out var host))
            {
                issues.Add(new AssetLoadIssue(
                    AssetLoadIssueCode.OrphanEmbedded,
                    domain,
                    asset.AppId,
                    $"内含资产 {asset.AppId}#{asset.Version} 的宿主不在本次加载集合（随宿主同层装载，故不参与拓扑）。",
                    []));
                continue;
            }

            hostOf[asset.AssetGuid] = host;
        }

        return (nodes, embedded, hostOf);
    }

    /// <summary>依赖不可满足的域规则（R15：正式不得依赖测试；测试的正式依赖必须已入正式 lock）。</summary>
    private static (AssetLoadIssueCode Code, string Message) JudgeUnmet(
        AssetRegistration owner,
        AssetKey key,
        AssetReference reference,
        AssetDomain domain,
        IReadOnlySet<AssetKey> otherKeys,
        AssetRegistry registry)
    {
        var target = $"{AssetKindTokens.Token(reference.Kind)}：{key.AppId}（约束 {reference.VersionConstraint}）";

        if (domain == AssetDomain.Formal)
        {
            return otherKeys.Contains(key)
                ? (AssetLoadIssueCode.FormalDependsOnTest,
                    $"正式资产 {owner.AppId} 依赖测试资产 {target}——正式资产不得依赖测试资产（R15）。")
                : (AssetLoadIssueCode.DependencyMissing,
                    $"正式资产 {owner.AppId} 的依赖 {target} 不在本次加载集合（未入 lock 或未注册）。");
        }

        return ExistsInRegistry(registry, key)
            ? (AssetLoadIssueCode.TestDependsOnUnlockedFormal,
                $"测试资产 {owner.AppId} 依赖的正式资产 {target} 未入正式 lock（该测试资产判非法，不整段失败）。")
            : (AssetLoadIssueCode.DependencyMissing,
                $"测试资产 {owner.AppId} 的依赖 {target} 不在加载集合（测试与正式 lock 均无条目）。");
    }

    private static void CheckConstraint(
        AssetRegistration owner,
        AssetReference reference,
        AssetRegistration target,
        AssetDomain domain,
        List<AssetLoadIssue> issues)
    {
        if (reference.IsSatisfiedBy(target.Version))
        {
            return;
        }

        issues.Add(new AssetLoadIssue(
            AssetLoadIssueCode.DependencyVersionMismatch,
            domain,
            owner.AppId,
            $"{owner.AppId} 对 {target.AppId} 的约束 {reference.VersionConstraint} 与锁定的版本 {target.Version} 不符"
            + "（lock 为准，仍装载）。",
            []));
    }

    /// <summary>Kahn 分层（第 N 层只依赖 ≤N-1 层）；环依赖报错并输出环路径。</summary>
    private static LoadList Layer(
        AssetDomain domain,
        IReadOnlyList<AssetRegistration> ordered,
        Dictionary<Guid, List<AssetRegistration>> edges,
        HashSet<Guid> excluded,
        Dictionary<Guid, AssetRegistration> hostOfEmbedded,
        IReadOnlyList<AssetRegistration> embedded,
        List<AssetLoadIssue> issues)
    {
        var active = ordered.Where(node => !excluded.Contains(node.AssetGuid)).ToArray();
        var byGuid = active.ToDictionary(node => node.AssetGuid);

        var indegree = active.ToDictionary(node => node.AssetGuid, _ => 0);
        var dependents = active.ToDictionary(node => node.AssetGuid, _ => new List<Guid>());

        foreach (var node in active)
        {
            if (!edges.TryGetValue(node.AssetGuid, out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.Where(dependency => byGuid.ContainsKey(dependency.AssetGuid)))
            {
                dependents[dependency.AssetGuid].Add(node.AssetGuid);
                indegree[node.AssetGuid]++;
            }
        }

        var level = active.ToDictionary(node => node.AssetGuid, _ => 0);
        var ready = active.Where(node => indegree[node.AssetGuid] == 0).Select(node => node.AssetGuid).ToList();
        var processed = new List<AssetRegistration>(active.Length);

        while (ready.Count > 0)
        {
            ready.Sort((left, right) => CompareOrder(byGuid[left], byGuid[right]));
            var current = ready[0];
            ready.RemoveAt(0);
            processed.Add(byGuid[current]);

            foreach (var dependent in dependents[current])
            {
                level[dependent] = Math.Max(level[dependent], level[current] + 1);
                if (--indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (processed.Count < active.Length)
        {
            ReportCycles(domain, active, edges, processed, byGuid, issues);
        }

        var layerCount = processed.Count == 0 ? 0 : processed.Max(node => level[node.AssetGuid]) + 1;
        var layers = new List<LoadListLayer>(layerCount);
        for (var index = 0; index < layerCount; index++)
        {
            var items = processed
                .Where(node => level[node.AssetGuid] == index)
                .Select(node => new LoadListItem(node, EmbeddedOf(node, hostOfEmbedded, embedded)))
                .OrderBy(item => item.LocalId, StringComparer.Ordinal)
                .ThenBy(item => item.Kind)
                .ThenBy(item => item.AppId, StringComparer.Ordinal)
                .ToArray();
            layers.Add(new LoadListLayer(index, items));
        }

        return new LoadList(domain, layers);
    }

    private static void ReportCycles(
        AssetDomain domain,
        IReadOnlyList<AssetRegistration> active,
        Dictionary<Guid, List<AssetRegistration>> edges,
        IReadOnlyList<AssetRegistration> processed,
        IReadOnlyDictionary<Guid, AssetRegistration> byGuid,
        List<AssetLoadIssue> issues)
    {
        var settled = processed.Select(node => node.AssetGuid).ToHashSet();
        var pending = active
            .Where(node => !settled.Contains(node.AssetGuid))
            .Select(node => node.AssetGuid)
            .ToHashSet();

        while (pending.Count > 0)
        {
            var cycle = FindCycle(active, edges, pending);
            if (cycle is null)
            {
                break; // 剩余节点只依赖环内节点、自身不成环——随环一起不参与分层。
            }

            var path = cycle.Select(guid => byGuid[guid].AppId).ToArray();
            issues.Add(new AssetLoadIssue(
                AssetLoadIssueCode.Cycle,
                domain,
                path[0],
                $"环依赖：{string.Join(" → ", path)}（禁止环形依赖）。",
                path));

            foreach (var guid in cycle)
            {
                pending.Remove(guid);
            }
        }
    }

    /// <summary>在剩余节点上按依赖方向找一条环，返回环路径（末位＝首位）。</summary>
    private static List<Guid>? FindCycle(
        IReadOnlyList<AssetRegistration> candidates,
        Dictionary<Guid, List<AssetRegistration>> edges,
        HashSet<Guid> pending)
    {
        var path = new List<Guid>();
        var onPath = new HashSet<Guid>();
        var done = new HashSet<Guid>();
        List<Guid>? cycle = null;

        bool Walk(Guid current)
        {
            path.Add(current);
            onPath.Add(current);

            if (edges.TryGetValue(current, out var dependencies))
            {
                foreach (var dependency in dependencies)
                {
                    if (!pending.Contains(dependency.AssetGuid))
                    {
                        continue;
                    }

                    if (onPath.Contains(dependency.AssetGuid))
                    {
                        var start = path.IndexOf(dependency.AssetGuid);
                        cycle = [.. path.Skip(start), dependency.AssetGuid];
                        return true;
                    }

                    if (!done.Contains(dependency.AssetGuid) && Walk(dependency.AssetGuid))
                    {
                        return true;
                    }
                }
            }

            onPath.Remove(current);
            path.RemoveAt(path.Count - 1);
            done.Add(current);
            return false;
        }

        foreach (var node in candidates)
        {
            if (!pending.Contains(node.AssetGuid) || done.Contains(node.AssetGuid))
            {
                continue;
            }

            path.Clear();
            onPath.Clear();
            if (Walk(node.AssetGuid))
            {
                return cycle;
            }
        }

        return null;
    }

    private static IReadOnlyList<AssetRegistration> EmbeddedOf(
        AssetRegistration host,
        Dictionary<Guid, AssetRegistration> hostOfEmbedded,
        IReadOnlyList<AssetRegistration> embedded)
        => embedded
            .Where(asset => hostOfEmbedded.TryGetValue(asset.AssetGuid, out var owner) && owner.AssetGuid == host.AssetGuid)
            .OrderBy(asset => asset.Kind)
            .ThenBy(asset => asset.AppId, StringComparer.Ordinal)
            .ToArray();

    private static bool TryFind(LoadList? list, AssetKey key, out AssetRegistration? asset)
    {
        asset = null;
        if (list is null)
        {
            return false;
        }

        foreach (var item in list.Items)
        {
            if (item.Asset.Kind == key.Kind && string.Equals(item.Asset.AppId, key.AppId, StringComparison.Ordinal))
            {
                asset = item.Asset;
                return true;
            }

            foreach (var embedded in item.Embedded)
            {
                if (embedded.Kind == key.Kind && string.Equals(embedded.AppId, key.AppId, StringComparison.Ordinal))
                {
                    asset = embedded;
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<AssetKey, AssetRegistration> IndexByKey(IEnumerable<AssetRegistration> assets)
    {
        var index = new Dictionary<AssetKey, AssetRegistration>();
        foreach (var asset in assets)
        {
            index.TryAdd(new AssetKey(asset.Kind, asset.AppId), asset);
        }

        return index;
    }

    private static bool ExistsInRegistry(AssetRegistry registry, AssetKey key)
        => AssetAppId.TrySplit(key.AppId, out var packageId, out var localId)
            && registry.VersionsOf(packageId, key.Kind, localId).Count > 0;

    /// <summary>依赖引用归一：同包裸局部 ID 补前缀（D47 三档书写），跨包必须已写全名。</summary>
    private static string NormalizeReference(string packageId, string reference)
        => AssetAppId.IsAppId(reference) ? reference : AssetAppId.Compose(packageId, reference);

    private static int CompareOrder(AssetRegistration left, AssetRegistration right)
    {
        var compare = string.CompareOrdinal(left.LocalId, right.LocalId);
        if (compare != 0)
        {
            return compare;
        }

        compare = left.Kind.CompareTo(right.Kind);
        return compare != 0 ? compare : string.CompareOrdinal(left.AppId, right.AppId);
    }

    private readonly record struct AssetKey(AssetKind Kind, string AppId);
}
