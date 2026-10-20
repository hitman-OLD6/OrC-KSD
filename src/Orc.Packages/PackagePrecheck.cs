using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 包级预检报告（dry-run；S11）：Errors／Warnings 分列 ＋ 无 Error 判定面（沿用分发预检器口径）。
/// </summary>
/// <param name="Errors">阻断项（&gt;0 不放行）。</param>
/// <param name="Warnings">告警项（不阻断）。</param>
public sealed record PackagePrecheckReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    /// <summary>无 Error ＝ 放行。</summary>
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// 包级分发预检器（S11 之①：**纯只读** dry-run 校验）：在装载/建局**之前**检出——
/// ①包身份唯一性（同 GUID 不得跨 id 共用）；②**资产全名全局唯一**（不唯一＝后者不予加载）。
/// 包级依赖已退役（S4/D31：依赖一律资产级），故不再做依赖可解析性校验。
/// 载荷外情形（建模期 fail-fast 之②／装载期单卡隔离之③）由既有链路承担；本预检只报事实、不改状态。
/// </summary>
public static class PackagePrecheck
{
    /// <summary>预检一份发现结果（目录扫描后、冷加载前）。</summary>
    /// <exception cref="ArgumentNullException">discovery 为 null。</exception>
    public static PackagePrecheckReport Validate(PackageDiscoveryReport discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var errors = new List<string>(discovery.Failures);
        var warnings = new List<string>();

        var resolution = PackageResolver.Resolve(new PackageDiscoveryReport(discovery.Packages, Array.Empty<string>()));
        errors.AddRange(resolution.Failures);
        errors.AddRange(DuplicateAssetFullNames(resolution.LoadOrder.Select(p => p.Manifest)));

        foreach (var package in resolution.LoadOrder)
        {
            if (package.Manifest.Kind == PackageKind.CardPack && package.Manifest.Assets.Count == 0)
            {
                warnings.Add($"{package.Manifest.Id}@{package.Manifest.Version}：卡包未声明任何资产。");
            }
        }

        return new PackagePrecheckReport(Dedupe(errors), Dedupe(warnings));
    }

    /// <summary>预检一个对局快照（建局装配前；校验资产全局唯一）。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static PackagePrecheckReport Validate(AssetCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new PackagePrecheckReport(
            DuplicateAssetFullNames(snapshot.Packages.Select(p => p.Manifest)).ToArray(), Array.Empty<string>());
    }

    private static IEnumerable<string> DuplicateAssetFullNames(IEnumerable<PackageManifest> manifests)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var guidOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            // 包 GUID 冲突（不同包 id 共用同一身份 ⇒ 资产名空间塌缩）＝明确错误
            if (guidOwner.TryGetValue(manifest.Guid8, out var guidId)
                && !string.Equals(guidId, manifest.Id, StringComparison.Ordinal))
            {
                yield return $"包 GUID '{manifest.Guid8}' 被 '{guidId}' 与 '{manifest.Id}' 共用（包身份须唯一）。";
            }
            else
            {
                guidOwner[manifest.Guid8] = manifest.Id;
            }

            foreach (var asset in manifest.Assets)
            {
                var full = manifest.AssetNameOf(asset).ToString();
                if (seen.TryGetValue(full, out var owner))
                {
                    yield return $"资产名 '{full}' 重复（包 '{owner}' 与 '{manifest.Id}@{manifest.Version}'——不唯一＝后者不予加载）。";
                    continue;
                }

                seen[full] = $"{manifest.Id}@{manifest.Version}";
            }
        }
    }

    private static IReadOnlyList<string> Dedupe(IEnumerable<string> items)
        => items.Distinct(StringComparer.Ordinal).ToArray();
}
