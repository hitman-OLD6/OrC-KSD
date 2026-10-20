using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 批次 5 验收：包管理器高级能力——**卸载**（仅卡包）／**重载**（失败即回滚原状）／
/// **回滚**（目录恢复到快照资产集）；已冻结快照永不受影响。
/// </summary>
public sealed class PackageLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-life-" + Guid.NewGuid().ToString("N"));

    public PackageLifecycleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 卸载卡包_新快照不含_旧快照不变()
    {
        WriteCardPack("pack", "1.0.0");
        var host = EngineHost.Start(_root);
        var before = host.CreateSnapshot();
        Assert.Single(before.CardPacks);

        Assert.True(host.TryUnloadCardPack("pack", PackageVersion.Parse("1.0.0"), out var failure), failure);

        Assert.Single(before.CardPacks); // 已冻结快照不变
        Assert.Empty(host.CreateSnapshot().CardPacks);
    }

    [Fact]
    public void 卸载插件_被拒绝()
    {
        WritePlugin("plug", "1.0.0");
        var host = EngineHost.Start(_root);

        Assert.False(host.TryUnloadCardPack("plug", PackageVersion.Parse("1.0.0"), out var failure));
        Assert.Contains("只能冷加载", failure);
    }

    [Fact]
    public void 批量卸载_仅处理卡包()
    {
        WriteCardPack("pack", "1.0.0");
        WriteCardPack("pack", "1.2.0");
        WritePlugin("pack", "2.0.0"); // 同 id 的插件版本：不参与卸载
        var host = EngineHost.Start(_root);

        Assert.Equal(2, host.UnloadCardPacks("pack"));
        Assert.Single(host.Catalog.ById("pack"));
        Assert.Equal(PackageKind.Plugin, host.Catalog.ById("pack")[0].Manifest.Kind);
    }

    [Fact]
    public void 重载_成功替换内容()
    {
        WriteCardPack("pack", "1.0.0");
        var host = EngineHost.Start(_root);

        // 改内容后重载（同 id/version）
        var directory = Path.Combine(_root, "packs", "pack-1.0.0");
        File.WriteAllText(Path.Combine(directory, "cards.json"), "{\"cards\":[1]}");
        Assert.True(host.TryReloadCardPack(directory, out var failure), failure);

        Assert.Single(host.Catalog.ById("pack"));
        var asset = Assert.Single(host.Catalog.ById("pack")[0].Assets);
        Assert.Contains("[1]", File.ReadAllText(asset.AbsolutePath));
    }

    [Fact]
    public void 重载失败_回滚原状()
    {
        WriteCardPack("pack", "1.0.0");
        var host = EngineHost.Start(_root);
        var directory = Path.Combine(_root, "packs", "pack-1.0.0");
        File.Delete(Path.Combine(directory, "cards.json")); // 资源缺失 ⇒ 重载必失败

        Assert.False(host.TryReloadCardPack(directory, out var failure));
        Assert.Contains("资源缺失", failure);

        // 回滚：目录仍保有原包（原状保留），且资源仍在原位置（未被清理）
        var still = Assert.Single(host.Catalog.ById("pack"));
        Assert.Equal(PackageVersion.Parse("1.0.0"), still.Manifest.Version);
    }

    [Fact]
    public void 回滚_目录恢复到快照资产集()
    {
        WritePlugin("plug", "1.0.0");
        var host = EngineHost.Start(_root);
        var baseline = host.CreateSnapshot();

        WriteCardPack("pack", "1.0.0");
        Assert.True(host.TryLoadCardPack(Path.Combine(_root, "packs", "pack-1.0.0"), out var failure), failure);
        Assert.Single(host.Catalog.ById("pack"));

        var count = host.RestoreFrom(baseline);

        Assert.Equal(1, count);
        Assert.Empty(host.Catalog.ById("pack"));
        Assert.Single(host.Catalog.ById("plug"));
    }

    private void WriteCardPack(string id, string version)
    {
        var directory = Path.Combine(_root, "packs", id + "-" + version);
        Directory.CreateDirectory(directory);
        var manifest = new PackageManifest(
            PackageKind.CardPack, id, Guid.NewGuid(), PackageVersion.Parse(version),
            Array.Empty<PackageDependency>(), new[] { new PackageAsset("json", "cards", "cards.json") });
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
        File.WriteAllText(Path.Combine(directory, "cards.json"), "{\"cards\":[]}");
    }

    private void WritePlugin(string id, string version)
    {
        var directory = Path.Combine(_root, "plugins", id + "-" + version);
        Directory.CreateDirectory(directory);
        var manifest = new PackageManifest(
            PackageKind.Plugin, id, Guid.NewGuid(), PackageVersion.Parse(version),
            Array.Empty<PackageDependency>(), Array.Empty<PackageAsset>());
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
