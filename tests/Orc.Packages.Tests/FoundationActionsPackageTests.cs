using System.Reflection;
using Orc.Foundation.Actions;
using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// P5b 验收（S4 修订）：底层逻辑集的**物理包**（清单解析、身份校验、装载序）。
/// 不建对局——只验证包发现/装载与 <c>LoadAfter[]</c> 显式装配序（包级 dependsOn 已退役）。
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

        // ② 清单解析：kind/id/guid/version/空资产声明合法（包级 dependsOn 已退役，读入仅为迁移告警）
        var loaded = FoundationActionsPluginPackage.Load();
        Assert.Equal(PackageKind.Plugin, loaded.Manifest.Kind);
        Assert.Equal(FoundationActionsPluginPackage.PackageId, loaded.Manifest.Id);
        Assert.Equal(FoundationActionsPluginPackage.PackageGuid, loaded.Manifest.Guid);
        Assert.Equal("1.0.0", loaded.Manifest.Version.ToString());
        Assert.Empty(loaded.Manifest.Assets);
        var entry = Assert.Single(loaded.Assets);
        Assert.Equal("register.postCommand", entry.Asset.Name);

        // ③ 装载序：由注册清单的 LoadAfter[] 决定（foundation 声明须在 core 之后）
        var order = PluginLoadOrder.Sort(
            BuiltInPluginPackage.LoadOrder,
            new[] { FoundationActionsPluginPackage.PackageId, BuiltInPluginPackage.PackageId },
            out var violations);

        Assert.Empty(violations);
        Assert.Equal(
            new[] { BuiltInPluginPackage.PackageId, FoundationActionsPluginPackage.PackageId },
            order);
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
