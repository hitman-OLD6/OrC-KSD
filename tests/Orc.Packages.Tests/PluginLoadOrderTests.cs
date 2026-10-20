using Orc.Packages;
using Orc.Plugins.BuiltIn;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S4 验收：插件装载序由 <c>LoadAfter[]</c> 决定；三项启动断言（①覆盖全部已发现插件／②无环／
/// ③无未知包 ID）均有用例，且 <see cref="EngineHost.FromDiscovery"/> 违规即启动失败并指名。
/// </summary>
public class PluginLoadOrderTests
{
    [Fact]
    public void 按LoadAfter排序_同层按声明序()
    {
        var order = PluginLoadOrder.Sort(
            [
                new PackageLoadOrderEntry("c", ["b"]),
                new PackageLoadOrderEntry("a", []),
                new PackageLoadOrderEntry("b", ["a"]),
            ],
            ["c", "b", "a"],
            out var violations);

        Assert.Empty(violations);
        Assert.Equal(["a", "b", "c"], order);
    }

    [Fact]
    public void 断言一_未覆盖已发现插件_违规并指名()
    {
        PluginLoadOrder.Sort(
            [new PackageLoadOrderEntry("a", [])],
            ["a", "ghost"],
            out var violations);

        var violation = Assert.Single(violations);
        Assert.Contains("ghost", violation);
        Assert.Contains("未覆盖", violation);
    }

    [Fact]
    public void 断言二_环_违规并输出环路径()
    {
        PluginLoadOrder.Sort(
            [
                new PackageLoadOrderEntry("a", ["b"]),
                new PackageLoadOrderEntry("b", ["a"]),
            ],
            ["a", "b"],
            out var violations);

        var violation = Assert.Single(violations);
        Assert.Contains("环", violation);
        Assert.Contains("a", violation);
        Assert.Contains("b", violation);
    }

    [Fact]
    public void 断言三_未知包ID_违规并指名()
    {
        PluginLoadOrder.Sort(
            [new PackageLoadOrderEntry("a", ["unknown.pack"])],
            ["a"],
            out var violations);

        var violation = Assert.Single(violations);
        Assert.Contains("unknown.pack", violation);
        Assert.Contains("未知包 ID", violation);
    }

    [Fact]
    public void 内置包注册清单_覆盖三个内置插件()
    {
        var order = PluginLoadOrder.Sort(
            BuiltInPluginPackage.LoadOrder,
            [
                BuiltInPluginPackage.PackageId,
                BuiltInPluginPackage.UiPackageId,
                BuiltInPluginPackage.FoundationActionsPackageId,
            ],
            out var violations);

        Assert.Empty(violations);
        Assert.Equal(BuiltInPluginPackage.PackageId, order[0]); // core 先于其余
        Assert.Equal(3, order.Count);
    }

    [Fact]
    public void 宿主启动_装载序违规即失败并指名()
    {
        var plugin = new LoadedPackage(
            new PackageManifest(
                PackageKind.Plugin,
                "only.pack",
                Guid.NewGuid(),
                PackageVersion.Parse("v1"),
                Array.Empty<PackageDependency>(),
                Array.Empty<PackageAsset>()),
            "root",
            Array.Empty<LoadedAsset>(),
            Array.Empty<LoadedPluginScript>());

        var error = Assert.Throws<InvalidOperationException>(() => EngineHost.FromDiscovery(
            new PackageDiscoveryReport(Array.Empty<DiscoveredPackage>(), Array.Empty<string>()),
            evaluator: new NoOpEvaluator(),
            extraPackages: [plugin],
            pluginLoadOrder: [new PackageLoadOrderEntry("other.pack", [])]));

        Assert.Contains("only.pack", error.Message);
        Assert.Contains("启动失败", error.Message);
    }

    [Fact]
    public void 宿主启动_按声明序装载插件()
    {
        var a = Plugin("a");
        var b = Plugin("b");

        var host = EngineHost.FromDiscovery(
            new PackageDiscoveryReport(Array.Empty<DiscoveredPackage>(), Array.Empty<string>()),
            evaluator: new NoOpEvaluator(),
            extraPackages: [b, a], // 传入序与声明序相反
            pluginLoadOrder:
            [
                new PackageLoadOrderEntry("b", ["a"]),
                new PackageLoadOrderEntry("a", []),
            ]);

        Assert.Equal(["a", "b"], host.Catalog.Packages.Select(p => p.Manifest.Id));
    }

    private static LoadedPackage Plugin(string id) => new(
        new PackageManifest(
            PackageKind.Plugin,
            id,
            Guid.NewGuid(),
            PackageVersion.Parse("v1"),
            Array.Empty<PackageDependency>(),
            Array.Empty<PackageAsset>()),
        "root/" + id,
        Array.Empty<LoadedAsset>(),
        Array.Empty<LoadedPluginScript>());

    private sealed class NoOpEvaluator : Orc.Core.IPluginScriptEvaluator
    {
        public Orc.Core.PluginScriptResult Evaluate(Orc.Core.PluginScriptRequest request)
            => throw new NotSupportedException("本用例不装载 csx 脚本。");
    }
}
