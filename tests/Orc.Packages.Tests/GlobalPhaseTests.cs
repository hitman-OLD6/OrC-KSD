using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Plugins;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 批次 3 验收：全局（进程级）装配相——冷加载期执行 `scope: process` 脚本装配静态注册面（词条）；
/// 全局相**拒绝对局级注册**（fail-fast）。
/// </summary>
public sealed class GlobalPhaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-global-" + Guid.NewGuid().ToString("N"));
    private readonly string _keywordId = "e2e.global.keyword." + Guid.NewGuid().ToString("N");

    public GlobalPhaseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        KeywordRegistry.Unregister(_keywordId);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>进程级脚本：经全局注册面登记词条（注意不得出现沙箱屏蔽词）。</summary>
    private string Script => $$"""
Action<IPluginRegistrationContext> Register = ctx =>
{
    var g = (Orc.Game.Plugins.IGlobalPluginContext)ctx;
    g.Global.RegisterKeyword("{{_keywordId}}", (card, value) => new Orc.Game.Cards.PlainKeywordComponent("{{_keywordId}}"));
    ctx.DeclareDependency("json:global@cafebabe#1.0.0");
};
""";

    [Fact]
    public void 冷加载期_执行进程级脚本并装配静态注册面()
    {
        WriteProcessPlugin("global", Script);

        var host = EngineHost.Start(_root);

        Assert.Empty(host.LoadFailures);
        Assert.Empty(host.GlobalActivation.Failures);
        Assert.Contains($"{WaitForId}@1.0.0", host.GlobalActivation.ActivatedPackages);
        Assert.Contains(_keywordId, KeywordRegistry.Registered);

        // 进程级脚本不参与对局相装配
        var snapshot = host.CreateSnapshot();
        var activation = PackageActivation.Activate(
            snapshot, new AssetServiceRegistry(), new JudicatorRegistry(), new LogicEngine().Prefabs);
        Assert.Empty(activation.ActivatedPackages);
    }

    [Fact]
    public void 全局相_拒绝对局级注册()
    {
        var package = new LoadedPackage(
            new PackageManifest(
                PackageKind.Plugin, "p1", Guid.NewGuid(), new AssetVersion(1, 0, 0),
                Array.Empty<PackageDependency>(), Array.Empty<PackageAsset>()),
            "root", Array.Empty<LoadedAsset>(), Array.Empty<LoadedPluginScript>());

        var context = new GlobalRegistrationContext(package, new GlobalRegistrationServices());

        Assert.Throws<InvalidOperationException>(() => context.RegisterJudicator("x", null!));
        Assert.Throws<InvalidOperationException>(() => context.RegisterAssetService("x", null!));
        Assert.Throws<InvalidOperationException>(() => context.RegisterHandler("x", null!));
        Assert.Throws<InvalidOperationException>(() => context.DeclareReplaceWrapper(WrapperTarget.Judicator, "x", null!));
    }

    private const string WaitForId = "global";

    private void WriteProcessPlugin(string id, string script)
    {
        var directory = Path.Combine(_root, "plugins", id);
        Directory.CreateDirectory(Path.Combine(directory, "csx"));
        File.WriteAllText(Path.Combine(directory, "csx", "register.csx"), script);

        var manifest = new PackageManifest(
            PackageKind.Plugin, id, Guid.NewGuid(), new AssetVersion(1, 0, 0),
            Array.Empty<PackageDependency>(),
            new[]
            {
                new PackageAsset("csx", "register", "csx/register.csx", AssetScope.Process, AssetActivationPhase.PreCommand),
            });
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
