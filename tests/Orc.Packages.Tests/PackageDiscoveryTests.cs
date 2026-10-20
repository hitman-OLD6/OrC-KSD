using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S5 验收：包发现——约定目录扫描（plugins/packs 分置）／显式清单；单包失败＝隔离不阻断其余。
/// </summary>
public sealed class PackageDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-pkg-" + Guid.NewGuid().ToString("N"));

    public PackageDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void 目录扫描_发现插件与卡包()
    {
        WritePackage(Path.Combine(_root, "plugins", "p1"), PackageKind.Plugin, "p1");
        WritePackage(Path.Combine(_root, "packs", "c1"), PackageKind.CardPack, "c1");

        var report = PackageDiscovery.Discover(_root);

        Assert.Empty(report.Failures);
        Assert.Equal(2, report.Packages.Count);
        Assert.Contains(report.Packages, p => p.Manifest.Id == "p1" && p.Manifest.Kind == PackageKind.Plugin);
        Assert.Contains(report.Packages, p => p.Manifest.Id == "c1" && p.Manifest.Kind == PackageKind.CardPack);
    }

    [Fact]
    public void 类型与根目录不符_隔离且不阻断其余()
    {
        WritePackage(Path.Combine(_root, "plugins", "good"), PackageKind.Plugin, "good");
        WritePackage(Path.Combine(_root, "plugins", "wrong"), PackageKind.CardPack, "wrong");

        var report = PackageDiscovery.Discover(_root);

        Assert.Single(report.Packages);
        Assert.Equal("good", report.Packages[0].Manifest.Id);
        Assert.Single(report.Failures);
        Assert.Contains("不符", report.Failures[0]);
    }

    [Fact]
    public void 缺清单文件_隔离记录()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "empty"));

        var report = PackageDiscovery.Discover(_root);

        Assert.Empty(report.Packages);
        Assert.Single(report.Failures);
        Assert.Contains("缺少清单文件", report.Failures[0]);
    }

    [Fact]
    public void manifest非法_隔离且不阻断其余()
    {
        WritePackage(Path.Combine(_root, "plugins", "good"), PackageKind.Plugin, "good");
        var bad = Path.Combine(_root, "plugins", "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "manifest.json"), "{ not json ");

        var report = PackageDiscovery.Discover(_root);

        Assert.Single(report.Packages);
        Assert.Single(report.Failures);
    }

    [Fact]
    public void 根目录不存在_空报告()
    {
        var report = PackageDiscovery.Discover(Path.Combine(_root, "nowhere"));

        Assert.Empty(report.Packages);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void 显式清单_逐行加载()
    {
        var p1 = Path.Combine(_root, "custom", "p1");
        var p2 = Path.Combine(_root, "custom", "p2");
        WritePackage(p1, PackageKind.Plugin, "p1");
        WritePackage(p2, PackageKind.Plugin, "p2");
        var listPath = Path.Combine(_root, "packages.list");
        File.WriteAllLines(listPath, new[] { "# 注释", "custom/p1", "", "custom/p2" });

        var report = PackageDiscovery.DiscoverFromList(listPath);

        Assert.Empty(report.Failures);
        Assert.Equal(2, report.Packages.Count);
    }

    private static void WritePackage(string directory, PackageKind kind, string id)
    {
        Directory.CreateDirectory(directory);
        var manifest = new PackageManifest(
            kind, id, Guid.NewGuid(), PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(), Array.Empty<PackageAsset>());
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
