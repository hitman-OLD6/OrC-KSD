using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S6 验收：版本约束解析/匹配；版本选择（满足约束取最高版）；依赖拓扑排序；失败隔离（缺失／无满足版本／循环）。
/// </summary>
public class PackageResolverTests
{
    // ---------- 版本约束 ----------

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.*.*", true)]
    [InlineData("1.2.*", true)]
    [InlineData("1.^.*", true)]
    [InlineData("1.^2.*", true)]
    [InlineData("1.2.^1", true)]
    [InlineData("1.2", false)]
    [InlineData("*.1.1", false)]
    [InlineData("1.x.3", false)]
    [InlineData("1.^x.3", false)]
    public void 版本约束解析(string text, bool expected)
        => Assert.Equal(expected, AssetVersionConstraint.TryParse(text, out _));

    [Fact]
    public void 数字下界_该段须不小于下界()
    {
        var constraint = AssetVersionConstraint.Parse("1.^2.*");

        Assert.False(constraint.IsSatisfiedBy(new AssetVersion(1, 1, 9)));
        Assert.True(constraint.IsSatisfiedBy(new AssetVersion(1, 2, 0)));
        Assert.True(constraint.IsSatisfiedBy(new AssetVersion(1, 7, 3)));
        Assert.False(constraint.IsSatisfiedBy(new AssetVersion(2, 2, 0)));

        // `^` 省略数字＝下界 0（任意）——与既有语义兼容
        var anyBound = AssetVersionConstraint.Parse("1.^.*");
        Assert.True(anyBound.IsSatisfiedBy(new AssetVersion(1, 0, 0)));
        Assert.Equal("1.^.*", anyBound.ToString());
        Assert.Equal("1.^2.*", constraint.ToString());
    }

    [Fact]
    public void 版本约束匹配_主版锁定与通配()
    {
        var exact = AssetVersionConstraint.Parse("1.2.3");
        Assert.True(exact.IsSatisfiedBy(new AssetVersion(1, 2, 3)));
        Assert.False(exact.IsSatisfiedBy(new AssetVersion(1, 2, 4)));
        Assert.False(exact.IsSatisfiedBy(new AssetVersion(2, 2, 3)));

        var wildcard = AssetVersionConstraint.Parse("1.2.*");
        Assert.True(wildcard.IsSatisfiedBy(new AssetVersion(1, 2, 9)));
        Assert.False(wildcard.IsSatisfiedBy(new AssetVersion(1, 3, 0)));

        var atLeast = AssetVersionConstraint.Parse("1.^.*");
        Assert.True(atLeast.IsSatisfiedBy(new AssetVersion(1, 0, 0)));
        Assert.True(atLeast.IsSatisfiedBy(new AssetVersion(1, 9, 9)));
        Assert.False(atLeast.IsSatisfiedBy(new AssetVersion(2, 0, 0)));
    }

    [Fact]
    public void 版本约束_往返一致()
    {
        foreach (var text in new[] { "1.2.3", "1.*.*", "1.2.*", "1.^.*" })
        {
            Assert.Equal(text, AssetVersionConstraint.Parse(text).ToString());
        }
    }

    // ---------- 解析（S4：包版本去解析化） ----------

    [Fact]
    public void 同ID包多版本并存_全部参与加载_不择版()
    {
        var report = PackageResolver.Resolve(Report(
            Pkg("a", "aaa111"),
            Pkg("a", "bbb222"),
            Pkg("a", "ccc333")));

        Assert.Empty(report.Failures);
        Assert.Equal(3, report.LoadOrder.Count);
        Assert.All(report.LoadOrder, p => Assert.Equal("a", p.Manifest.Id));
    }

    [Fact]
    public void 包版本不参与择版_指纹不同不影响加载集()
    {
        // 包版本＝内容指纹：既不是"最高版"也不参与依赖；三个不同指纹的包原样全部加载。
        var report = PackageResolver.Resolve(Report(
            Pkg("pack", "1.0.0"),
            Pkg("pack", "9.9.9-tmp")));

        Assert.Empty(report.Failures);
        Assert.Equal(2, report.LoadOrder.Count);
        Assert.Contains(report.LoadOrder, p => p.Manifest.Version.IsTemporary);
    }

    [Fact]
    public void 包级dependsOn已退役_不再影响解析()
    {
        var report = PackageResolver.Resolve(Report(
            Pkg("b", "b1", deps: [new PackageDependency("missing", "1.0.0")]),
            Pkg("a", "a1")));

        // 依赖一律资产级：包级 dependsOn 既不做存在性校验、也不建拓扑边。
        Assert.Empty(report.Failures);
        Assert.Equal(new[] { "b", "a" }, report.LoadOrder.Select(p => p.Manifest.Id));
    }

    [Fact]
    public void 包身份被不同id共用_隔离后者()
    {
        var shared = Guid.NewGuid();
        var report = PackageResolver.Resolve(new PackageDiscoveryReport(
            new[] { Pkg("a", "v1", guid: shared), Pkg("b", "v1", guid: shared) },
            Array.Empty<string>()));

        Assert.Single(report.LoadOrder);
        Assert.Equal("a", report.LoadOrder[0].Manifest.Id);
        var failure = Assert.Single(report.Failures);
        Assert.Contains("包身份", failure);
    }

    [Fact]
    public void 发现阶段失败_并入报告()
    {
        var report = PackageResolver.Resolve(new PackageDiscoveryReport(
            new[] { Pkg("a", "1.0.0") }, new[] { "x/manifest.json：缺少清单文件" }));

        Assert.Single(report.LoadOrder);
        Assert.Single(report.Failures);
    }

    private static DiscoveredPackage Pkg(
        string id, string version, PackageDependency[]? deps = null, Guid? guid = null)
        => new(
            new PackageManifest(
                PackageKind.Plugin, id, guid ?? Guid.NewGuid(), PackageVersion.Parse(version),
                deps ?? Array.Empty<PackageDependency>(), Array.Empty<PackageAsset>()),
            "root/" + id + "/" + version);

    private static PackageDiscoveryReport Report(params DiscoveredPackage[] packages)
        => new(packages, Array.Empty<string>());
}
