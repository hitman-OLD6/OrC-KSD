using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// D62/D63/D64 验收：service kind 与子类字段、插件代码声明注册、<c>assets.meta.json</c> 与双 lock 生成、
/// lock 校验（旧版本 ⇒ stale ＋ 提示重锁）。
/// </summary>
public class PluginAssetsTests
{
    private const string PackageId = "t.plugin";
    private const string PackageGuidText = "11111111-2222-4333-8444-555555555555";

    private static PackageManifest Manifest()
        => PackageManifest.Parse($$"""
            {
              "schemaVersion": 2,
              "kind": "plugin",
              "id": "{{PackageId}}",
              "guid": "{{PackageGuidText}}",
              "version": "0123456789abcdef",
              "metadata": []
            }
            """);

    private sealed class Source(string version) : IPluginAssetSource
    {
        public string PackageId => PluginAssetsTests.PackageId;

        public IReadOnlyList<PluginAssetDescriptor> Describe()
        {
            var parsed = AssetVersion.Parse(version);
            return
            [
                new(ServiceKindTokens.Judicator, "action.turn.owner", parsed),
                new(
                    ServiceKindTokens.Judicator,
                    "combat.range",
                    parsed,
                    [PluginAssetRegistrar.ServiceReference(
                        PackageId, ServiceKindTokens.Judicator, "action.turn.owner")]),
                new(ServiceKindTokens.Keyword, "闪击", parsed),
            ];
        }
    }

    [Fact]
    public void Service_kind_token_往返()
    {
        Assert.Equal("service", AssetKindTokens.Token(AssetKind.Service));
        Assert.True(AssetKindTokens.TryParse("SERVICE", out var kind));
        Assert.Equal(AssetKind.Service, kind);
        Assert.True(ServiceKindTokens.IsDefined(ServiceKindTokens.Judicator));
        Assert.False(ServiceKindTokens.IsDefined("unknown"));
    }

    [Fact]
    public void 注册产出稳定GUID与依赖()
    {
        var result = PluginAssetRegistrar.Register(Manifest(), new Source("1.0.0"), new AssetRegistry());

        Assert.Equal(3, result.Registrations.Count);
        Assert.All(result.Registrations, registration => Assert.Equal(AssetKind.Service, registration.Kind));
        Assert.All(
            result.Registrations,
            registration => Assert.Equal(AssetMetadataSource.PluginDeclared, registration.MetadataSource));

        var combat = Assert.Single(
            result.Registrations, registration => registration.LocalId == "judicator.combat.range");
        Assert.Single(combat.Metadata.DependsOn);
        Assert.Equal("pack/t.plugin/judicator.action.turn.owner", combat.Metadata.DependsOn[0].AppId);

        // 确定性：同一身份恒得同一 GUID（跨实例、跨进程）。
        var again = PluginAssetRegistrar.Register(Manifest(), new Source("1.0.0"), new AssetRegistry());
        Assert.Equal(
            result.Registrations.Select(registration => registration.AssetGuid).OrderBy(guid => guid),
            again.Registrations.Select(registration => registration.AssetGuid).OrderBy(guid => guid));
    }

    [Fact]
    public void 重复注册被拒绝()
    {
        var registry = new AssetRegistry();
        PluginAssetRegistrar.Register(Manifest(), new Source("1.0.0"), registry);

        Assert.Throws<InvalidOperationException>(
            () => PluginAssetRegistrar.Register(Manifest(), new Source("1.0.0"), registry));
    }

    [Fact]
    public void 写出清单与双lock后可校验通过()
    {
        var root = NewTempDirectory();
        try
        {
            var manifest = Manifest();
            var result = PluginAssetRegistrar.Register(manifest, new Source("1.0.0"), new AssetRegistry());
            PluginAssetManifestWriter.Write(root, manifest, result.Registrations, "T.Assembly");

            var manifestPath = Path.Combine(root, PluginAssetRegistrar.ManifestFileName);
            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(AssetLockJson.PathFor(root, AssetDomain.Formal)));
            Assert.True(File.Exists(AssetLockJson.PathFor(root, AssetDomain.Test)));

            var sidecar = AssetSidecarJson.ReadFile(manifestPath);
            Assert.Equal("T.Assembly", sidecar.Source);
            Assert.All(sidecar.Assets, asset => Assert.Equal(AssetKind.Service, asset.Kind));
            Assert.All(sidecar.Assets, asset => Assert.False(string.IsNullOrWhiteSpace(asset.ServiceKind)));

            var verification = PluginAssetLockVerifier.Verify(manifest, result.Registrations, root);
            Assert.True(
                verification.Ok,
                string.Join("；", verification.Issues.Select(issue => issue.Message)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 插件升级未重锁时旧lock判stale并提示重锁()
    {
        var root = NewTempDirectory();
        try
        {
            var manifest = Manifest();
            var oldResult = PluginAssetRegistrar.Register(manifest, new Source("1.0.0"), new AssetRegistry());
            PluginAssetManifestWriter.Write(root, manifest, oldResult.Registrations, "T.Assembly");

            // 官方更新：版本递增 ⇒ 新 GUID；lock 仍是旧的。
            var newResult = PluginAssetRegistrar.Register(manifest, new Source("1.0.1"), new AssetRegistry());
            var verification = PluginAssetLockVerifier.Verify(manifest, newResult.Registrations, root);

            Assert.False(verification.Ok);
            var stale = verification.Issues
                .Where(issue => issue.Code == PluginAssetLockVerifier.StaleLock)
                .ToArray();
            Assert.Equal(3, stale.Length);
            Assert.All(stale, issue => Assert.Contains("重锁", issue.Message, StringComparison.Ordinal));
            Assert.Contains(
                verification.Issues, issue => issue.Code == PluginAssetLockVerifier.Unlisted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 缺少lock判失败()
    {
        var root = NewTempDirectory();
        try
        {
            var manifest = Manifest();
            var result = PluginAssetRegistrar.Register(manifest, new Source("1.0.0"), new AssetRegistry());

            var verification = PluginAssetLockVerifier.Verify(manifest, result.Registrations, root);

            Assert.False(verification.Ok);
            Assert.Contains(
                verification.Issues, issue => issue.Code == PluginAssetLockVerifier.MissingLock);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EngineHost冷加载注册插件服务资产()
    {
        var root = NewTempDirectory();
        try
        {
            var manifest = Manifest();
            var result = PluginAssetRegistrar.Register(manifest, new Source("1.0.0"), new AssetRegistry());
            PluginAssetManifestWriter.Write(root, manifest, result.Registrations, "T.Assembly");

            var emptyRoot = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyRoot);

            var host = EngineHost.FromDiscovery(
                PackageDiscovery.Discover(emptyRoot),
                extraPackages: [new LoadedPackage(manifest, root, [], [], new Source("1.0.0"))]);

            Assert.Empty(host.LoadFailures);

            // 已登记 ⇒ 同身份再注册必被拒绝（GUID 已存在）。
            Assert.Throws<InvalidOperationException>(
                () => PluginAssetRegistrar.Register(manifest, new Source("1.0.0"), host.Assets));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "orc-plugin-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
