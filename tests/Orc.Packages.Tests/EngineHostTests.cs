using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S7 验收：宿主冷加载（发现→解析→装载定位）／资产目录（多版本并存）／对局快照（不可变 ＋ 版本锁定 ＋ 逐局独立）。
/// </summary>
public sealed class EngineHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-host-" + Guid.NewGuid().ToString("N"));

    public EngineHostTests() => Directory.CreateDirectory(_root);

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
    public void 冷加载_装载定位资产并派生全名()
    {
        WritePlugin("p1", "1.0.0", ("json", "faction.table", "data/factions.json"));

        var host = EngineHost.Start(_root);

        Assert.Empty(host.LoadFailures);
        var package = Assert.Single(host.Catalog.Packages);
        var asset = Assert.Single(package.Assets);
        Assert.Equal(AssetLoadKind.Json, asset.FullName.Kind);
        Assert.Equal("faction.table", asset.FullName.LocalName);
        Assert.True(File.Exists(asset.AbsolutePath));
    }

    [Fact]
    public void 资产资源缺失_隔离整包且不阻断其余()
    {
        WritePlugin("good", "1.0.0");
        WritePlugin("bad", "1.0.0", ("csx", "x", "missing.csx"));
        File.Delete(Path.Combine(_root, "plugins", "bad-1.0.0", "missing.csx"));

        var host = EngineHost.Start(_root);

        Assert.Single(host.Catalog.Packages);
        Assert.Equal("good", host.Catalog.Packages[0].Manifest.Id);
        Assert.Single(host.LoadFailures);
        Assert.Contains("资源缺失", host.LoadFailures[0]);
    }

    [Fact]
    public void 目录_支持同包多版本并存()
    {
        WritePlugin("p1", "1.0.0");
        WritePlugin("p1", "1.2.0");

        var host = EngineHost.Start(_root);

        Assert.Empty(host.LoadFailures);
        Assert.Equal(2, host.Catalog.ById("p1").Count);
        Assert.NotNull(host.Catalog.Find("p1", PackageVersion.Parse("1.2.0")));
        Assert.Null(host.Catalog.Find("p1", PackageVersion.Parse("9.0.0")));
    }

    [Fact]
    public void 快照_版本锁定与逐局独立()
    {
        WritePlugin("p1", "1.0.0");
        WritePlugin("p2", "2.1.0");

        var host = EngineHost.Start(_root);
        var snapshotA = host.CreateSnapshot();
        var snapshotB = host.CreateSnapshot();

        Assert.NotSame(snapshotA, snapshotB);
        Assert.Equal(new[] { "p1@1.0.0", "p2@2.1.0" }, snapshotA.VersionLocks);
        Assert.Equal(snapshotA.VersionLocks, snapshotB.VersionLocks);
    }

    [Fact]
    public void 版本锁定_为id加内容指纹_审计记录()
    {
        // 包版本＝内容指纹：VersionLocks 记录 id@指纹（不含时间戳、不参与择版）。
        var fingerprint = PackageVersion.ComputeLayout("p1", new[] { "a.json" });
        WritePlugin("p1", fingerprint.ToString(), ("json", "a", "a.json"));

        var host = EngineHost.Start(_root);
        var snapshot = host.CreateSnapshot();

        Assert.Equal(new[] { $"p1@{fingerprint}" }, snapshot.VersionLocks);
        Assert.NotNull(snapshot.Find("p1", fingerprint));
        Assert.Null(snapshot.Find("p1", PackageVersion.Parse("1.0.0")));
    }

    private void WritePlugin(string id, string version, params (string Type, string Name, string Path)[] assets)
    {
        var directory = Path.Combine(_root, "plugins", id + "-" + version);
        Directory.CreateDirectory(directory);

        foreach (var (_, _, path) in assets)
        {
            var full = Path.Combine(directory, path);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(full, "{}");
        }

        var manifest = new PackageManifest(
            PackageKind.Plugin, id, Guid.NewGuid(), PackageVersion.Parse(version),
            Array.Empty<PackageDependency>(),
            assets.Select(a => new PackageAsset(a.Type, a.Name, a.Path)).ToArray());
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
