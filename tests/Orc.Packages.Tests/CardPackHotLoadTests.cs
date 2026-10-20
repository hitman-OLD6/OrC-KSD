using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S9 验收（S4 修订）：卡包热加载（只对新对局生效／插件不可热加载）／快照装配优先级（插件先、卡包后）／
/// 当前对局显式注入。包级依赖已退役——热加载不再因包级 dependsOn 拒绝。
/// </summary>
public sealed class CardPackHotLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-hot-" + Guid.NewGuid().ToString("N"));

    public CardPackHotLoadTests() => Directory.CreateDirectory(_root);

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
    public void 热加载卡包_只对新建对局生效()
    {
        WritePackage("plugins", "core", PackageKind.Plugin, "1.0.0");
        var host = EngineHost.Start(_root);
        var before = host.CreateSnapshot();

        WritePackage("packs", "kards-base", PackageKind.CardPack, "1.0.0");
        var ok = host.TryLoadCardPack(Path.Combine(_root, "packs", "kards-base"), out var failure);

        Assert.True(ok, failure);
        var after = host.CreateSnapshot();

        Assert.Empty(before.CardPacks); // 已冻结快照不受影响
        Assert.Single(after.CardPacks);
        Assert.Equal("kards-base", after.CardPacks[0].Manifest.Id);
    }

    [Fact]
    public void 插件不可热加载_拒绝并记录()
    {
        WritePackage("plugins", "core", PackageKind.Plugin, "1.0.0");
        var host = EngineHost.Start(_root);

        var ok = host.TryLoadCardPack(Path.Combine(_root, "plugins", "core"), out var failure);

        Assert.False(ok);
        Assert.Contains("只能冷加载", failure);
    }

    [Fact]
    public void 包级dependsOn已退役_缺失依赖不再阻断热加载()
    {
        var host = EngineHost.Start(_root);
        WritePackage("packs", "need-plugin", PackageKind.CardPack, "1.0.0", dependsOn: ("missing", "1.0.0"));

        var ok = host.TryLoadCardPack(Path.Combine(_root, "packs", "need-plugin"), out var failure);

        Assert.True(ok, failure); // 依赖一律资产级：包级 dependsOn 只产迁移告警，不再拒绝
    }

    [Fact]
    public void 快照装配优先级_插件先于卡包()
    {
        WritePackage("packs", "pack1", PackageKind.CardPack, "1.0.0");
        WritePackage("plugins", "plug1", PackageKind.Plugin, "1.0.0");

        var host = EngineHost.Start(_root);
        var snapshot = host.CreateSnapshot();

        Assert.Equal(new[] { "plug1", "pack1" }, snapshot.Packages.Select(p => p.Manifest.Id));
    }

    [Fact]
    public async Task 当前对局注入_校验快照版本锁定()
    {
        WritePackage("packs", "pack1", PackageKind.CardPack, "1.0.0");
        var host = EngineHost.Start(_root);
        var snapshot = host.CreateSnapshot();
        var injector = new RecordingInjector();

        // 版本不在本局快照 ⇒ 拒绝
        var rejected = await CardPackInjection.InjectEffectsAsync(
            injector, snapshot, "pack1", PackageVersion.Parse("9.0.0"), new[] { "e1" });
        Assert.Single(rejected);
        Assert.Empty(injector.Injected);

        // 存在于快照 ⇒ 逐条注入
        var accepted = await CardPackInjection.InjectEffectsAsync(
            injector, snapshot, "pack1", PackageVersion.Parse("1.0.0"), new[] { "e1", "e2" });
        Assert.Empty(accepted);
        Assert.Equal(new[] { "e1", "e2" }, injector.Injected);
    }

    private sealed class RecordingInjector : ICurrentMatchInjector
    {
        public List<string> Injected { get; } = new();

        public Task<bool> InjectEffectPrefabAsync(string prefabId, CancellationToken ct = default)
        {
            Injected.Add(prefabId);
            return Task.FromResult(true);
        }
    }

    private void WritePackage(
        string rootName, string id, PackageKind kind, string version,
        (string Id, string Version)? dependsOn = null)
    {
        var directory = Path.Combine(_root, rootName, id);
        Directory.CreateDirectory(directory);

        var manifest = new PackageManifest(
            kind, id, Guid.NewGuid(), PackageVersion.Parse(version),
            dependsOn is { } dep ? new[] { new PackageDependency(dep.Id, dep.Version) } : Array.Empty<PackageDependency>(),
            Array.Empty<PackageAsset>());
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
