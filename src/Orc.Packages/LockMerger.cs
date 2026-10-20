using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// lock 冲突（D7/R15）：同一资产（种类 ＋ 应用层 ID）在两份**同域** lock 中锁定了不同版本。
/// 报告须指名资产与双方版本（及所属包）。
/// </summary>
/// <param name="Kind">资产种类。</param>
/// <param name="AppId">应用层 ID。</param>
/// <param name="FirstPackageId">先出现冲突的包 id。</param>
/// <param name="FirstVersion">该包锁定的版本。</param>
/// <param name="SecondPackageId">后出现冲突的包 id。</param>
/// <param name="SecondVersion">该包锁定的版本。</param>
public sealed record LockConflict(
    AssetKind Kind,
    string AppId,
    string FirstPackageId,
    AssetVersion FirstVersion,
    string SecondPackageId,
    AssetVersion SecondVersion)
{
    /// <inheritdoc />
    public override string ToString()
        => $"资产 {AssetKindTokens.Token(Kind)}：{AppId} 在 lock 间版本冲突"
            + $"（{FirstPackageId} 锁 {FirstVersion}，{SecondPackageId} 锁 {SecondVersion}）。";
}

/// <summary>
/// 单域合并结果：本次要加载的资产版本集合（未入 lock 的版本不参与）＋ 冲突明细。
/// </summary>
/// <param name="Domain">合并域。</param>
/// <param name="Entries">合并后的锁定条目（稳定序）。</param>
/// <param name="Conflicts">冲突明细（空＝可加载）。</param>
public sealed record LockMergeResult(
    AssetDomain Domain,
    IReadOnlyList<AssetLockEntry> Entries,
    IReadOnlyList<LockConflict> Conflicts)
{
    /// <summary>无冲突。</summary>
    public bool Ok => Conflicts.Count == 0;
}

/// <summary>
/// lock 合并（D7/D15/R15）：**按域分别合并**——正式 lock 只与正式 lock 合并、测试 lock 只与测试 lock 合并；
/// 同一资产出现不同版本＝结构化冲突（指名双方），否则合并结果即本次加载集合。
/// </summary>
public static class LockMerger
{
    /// <summary>合并某域的 lock（其它域的 lock 一律忽略——跨域绝不互相合并）。</summary>
    /// <param name="domain">目标域。</param>
    /// <param name="locks">参与合并的 lock。</param>
    /// <exception cref="ArgumentNullException">locks 为 null。</exception>
    public static LockMergeResult Merge(AssetDomain domain, IEnumerable<AssetLock> locks)
    {
        ArgumentNullException.ThrowIfNull(locks);

        var entries = new List<AssetLockEntry>();
        var conflicts = new List<LockConflict>();
        var seen = new Dictionary<(AssetKind Kind, string AppId), (AssetLockEntry Entry, string PackageId)>();

        foreach (var @lock in locks)
        {
            ArgumentNullException.ThrowIfNull(@lock);
            if (@lock.Domain != domain)
            {
                continue; // 跨域绝不互相合并
            }

            foreach (var entry in @lock.Assets)
            {
                var key = (entry.Kind, entry.AppId);
                if (!seen.TryGetValue(key, out var existing))
                {
                    seen.Add(key, (entry, @lock.PackageId));
                    entries.Add(entry);
                    continue;
                }

                if (existing.Entry.AssetGuid == entry.AssetGuid)
                {
                    continue; // 同一版本重复出现＝无冲突（去重）
                }

                conflicts.Add(new LockConflict(
                    entry.Kind,
                    entry.AppId,
                    existing.PackageId,
                    existing.Entry.Version,
                    @lock.PackageId,
                    entry.Version));
            }
        }

        var ordered = entries
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.AppId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Version)
            .ToArray();

        return new LockMergeResult(domain, ordered, conflicts);
    }

    /// <summary>合并全部 lock：分别产出正式域与测试域两个结果（各域独立判定冲突）。</summary>
    /// <exception cref="ArgumentNullException">locks 为 null。</exception>
    public static IReadOnlyList<LockMergeResult> MergeAll(IEnumerable<AssetLock> locks)
    {
        ArgumentNullException.ThrowIfNull(locks);

        var materialized = locks.ToArray();
        return new[]
        {
            Merge(AssetDomain.Formal, materialized),
            Merge(AssetDomain.Test, materialized),
        };
    }
}
