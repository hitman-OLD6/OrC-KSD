namespace Orc.Packages;

/// <summary>已解析的包（可加载）：清单 ＋ 包根目录。</summary>
/// <param name="Manifest">清单。</param>
/// <param name="RootDirectory">包根目录。</param>
public sealed record ResolvedPackage(PackageManifest Manifest, string RootDirectory);

/// <summary>包解析报告：加载顺序（稳定目录序）＋ 失败明细（隔离整包、不阻断其余）。</summary>
/// <param name="LoadOrder">加载顺序（插件装载序由 <see cref="PluginLoadOrder"/> 的显式声明决定）。</param>
/// <param name="Failures">失败明细（<c>包：原因</c>）。</param>
public sealed record PackageResolutionReport(
    IReadOnlyList<ResolvedPackage> LoadOrder,
    IReadOnlyList<string> Failures);

/// <summary>
/// 包解析（S4）：**只做身份面**——同 ID 包可并存（D31），包版本不再参与任何择版
/// （<c>SelectHighest</c> 已退役）；包级依赖已退役，依赖一律资产级（由资产加载器在 lock 合并后判定）。
/// 失败隔离：包身份（GUID）被不同 id 共用＝结构性冲突，隔离后者。
/// </summary>
public static class PackageResolver
{
    /// <summary>解析（输入＝发现报告；输出＝加载顺序＋失败明细）。</summary>
    /// <exception cref="ArgumentNullException">discovery 为 null。</exception>
    public static PackageResolutionReport Resolve(PackageDiscoveryReport discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var failures = new List<string>(discovery.Failures);
        var owner = new Dictionary<Guid, string>();
        var order = new List<ResolvedPackage>(discovery.Packages.Count);

        foreach (var package in discovery.Packages)
        {
            var manifest = package.Manifest;
            if (owner.TryGetValue(manifest.Guid, out var existing)
                && !string.Equals(existing, manifest.Id, StringComparison.Ordinal))
            {
                failures.Add($"{manifest.Id}@{manifest.Version}：包身份 {manifest.Guid} 已被 '{existing}' 使用（身份须唯一）");
                continue;
            }

            owner[manifest.Guid] = manifest.Id;
            order.Add(new ResolvedPackage(manifest, package.RootDirectory));
        }

        return new PackageResolutionReport(order, failures);
    }
}
