using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S11 验收（S4 修订）：包级 dry-run 预检——资产名全局唯一／包 GUID 冲突／卡包空资产告警；
/// 包级依赖已退役（依赖一律资产级），不再参与预检。纯只读（Errors/Warnings 分列，无 Error 放行）。
/// </summary>
public class PackagePrecheckTests
{
    private static readonly Guid SharedGuid = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");

    [Fact]
    public void 包级依赖已退役_不再参与预检()
    {
        var report = PackagePrecheck.Validate(Report(
            Pkg("p1", "1.0.0", dependsOn: ("missing", "1.0.0")),
            Pkg("p2", "1.0.0", guid: null, dependsOn: ("p1", "1.*.*"))));

        Assert.True(report.Ok);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void 资产名全局唯一_GUID冲突触发阻断()
    {
        var report = PackagePrecheck.Validate(Report(
            Pkg("a", "1.0.0", guid: SharedGuid, assets: ("json", "table", "t.json")),
            Pkg("b", "1.0.0", guid: SharedGuid, assets: ("json", "table", "t.json"))));

        Assert.False(report.Ok);
        Assert.Contains(report.Errors, e => e.Contains("包身份"));
        Assert.Contains(report.Errors, e => e.Contains("身份须唯一"));
    }

    [Fact]
    public void 同包多版本_资产名随版本不同_不误报()
    {
        var report = PackagePrecheck.Validate(Report(
            Pkg("a", "1.0.0", assets: ("json", "table", "t.json")),
            Pkg("a", "1.2.0", assets: ("json", "table", "t.json"))));

        Assert.True(report.Ok);
    }

    [Fact]
    public void 卡包空资产_告警不阻断()
    {
        var report = PackagePrecheck.Validate(Report(Pkg("pack", "1.0.0", kind: PackageKind.CardPack)));

        Assert.True(report.Ok);
        Assert.Single(report.Warnings);
        Assert.Contains("未声明任何资产", report.Warnings[0]);
    }

    [Fact]
    public void 快照预检_只校验资产名唯一_依赖不再参与()
    {
        var snapshot = AssetCatalogSnapshot.FromPackages(new[]
        {
            Package("p1", PackageKind.Plugin, ("1.0.0", null)),
            Package("p2", PackageKind.Plugin, ("1.0.0", ("p1", "2.0.0"))),
        });

        var report = PackagePrecheck.Validate(snapshot);

        Assert.True(report.Ok);
        Assert.Empty(report.Errors);
    }

    private static PackageManifest Pkg(
        string id,
        string version,
        Guid? guid = null,
        PackageKind kind = PackageKind.Plugin,
        (string Type, string Name, string Path)? assets = null,
        (string Id, string Version)? dependsOn = null)
        => new(
            kind,
            id,
            guid ?? Guid.NewGuid(),
            PackageVersion.Parse(version),
            dependsOn is { } dep ? new[] { new PackageDependency(dep.Id, dep.Version) } : Array.Empty<PackageDependency>(),
            assets is { } a ? new[] { new PackageAsset(a.Type, a.Name, a.Path) } : Array.Empty<PackageAsset>());

    private static PackageDiscoveryReport Report(params PackageManifest[] manifests)
        => new(manifests.Select(m => new DiscoveredPackage(m, "root/" + m.Id)).ToArray(), Array.Empty<string>());

    private static LoadedPackage Package(
        string id, PackageKind kind, (string Version, (string Id, string Version)? Dep) spec)
    {
        var manifest = Pkg(id, spec.Version, kind: kind, dependsOn: spec.Dep);
        return new LoadedPackage(manifest, "root/" + id, Array.Empty<LoadedAsset>(), Array.Empty<LoadedPluginScript>());
    }
}
