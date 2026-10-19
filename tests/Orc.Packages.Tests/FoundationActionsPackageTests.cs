using System.Reflection;
using Orc.Foundation.Actions;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// P5b 验收：底层逻辑集的**物理包**（清单解析、身份校验、依赖排序）。
/// 不建对局——只验证包发现/装载与 <c>dependsOn</c> 拓扑序。
/// </summary>
public class FoundationActionsPackageTests
{
    [Fact]
    public void Package_Discovery_Manifest_And_Dependency_Ordering_Puts_Core_First()
    {
        // ① 物理发现：程序集输出下的 package/plugins（core 与 foundation 同目录）
        var packageRoot = Path.Combine(AppContext.BaseDirectory, "package");
        var discovery = PackageDiscovery.Discover(packageRoot);

        Assert.Empty(discovery.Failures);
        Assert.Contains(discovery.Packages, p => p.Manifest.Id == BuiltInPluginPackage.PackageId);
        Assert.Contains(discovery.Packages, p => p.Manifest.Id == FoundationActionsPluginPackage.PackageId);

        // ② 清单解析：kind/id/guid/version/依赖/空资产声明合法
        var loaded = FoundationActionsPluginPackage.Load();
        Assert.Equal(PackageKind.Plugin, loaded.Manifest.Kind);
        Assert.Equal(FoundationActionsPluginPackage.PackageId, loaded.Manifest.Id);
        Assert.Equal(FoundationActionsPluginPackage.PackageGuid, loaded.Manifest.Guid);
        Assert.Equal("1.0.0", loaded.Manifest.Version.ToString());
        Assert.Contains(
            loaded.Manifest.Dependencies,
            d => d.Id == BuiltInPluginPackage.PackageId && d.Version == "1.*.*");
        Assert.Empty(loaded.Manifest.Assets);
        var entry = Assert.Single(loaded.Assets);
        Assert.Equal("register.postCommand", entry.Asset.Name);

        // ③ 依赖排序：故意把 foundation 放在声明序前面，解析后 core 必须先于 foundation
        var core = BuiltInPluginPackage.Load();
        var declared = new PackageDiscoveryReport(
            new[]
            {
                new DiscoveredPackage(loaded.Manifest, loaded.RootDirectory),
                new DiscoveredPackage(core.Manifest, core.RootDirectory),
            },
            Array.Empty<string>());

        var resolution = PackageResolver.Resolve(declared);
        Assert.Empty(resolution.Failures);
        Assert.Equal(
            new[] { BuiltInPluginPackage.PackageId, FoundationActionsPluginPackage.PackageId },
            resolution.LoadOrder.Select(p => p.Manifest.Id).ToArray());
    }

    [Fact]
    public void Package_Load_Throws_FileNotFound_When_Manifest_Missing()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "orc-fa-missing-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<FileNotFoundException>(
            () => FoundationActionsPluginPackage.Load(missingRoot));
        Assert.Contains(FoundationActionsPluginPackage.DirectoryName, error.Message);
    }

    [Fact]
    public void Foundation_Assembly_Has_No_InternalsVisibleTo()
    {
        var assembly = typeof(FoundationActions).Assembly;
        Assert.Empty(assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>());
    }
}
