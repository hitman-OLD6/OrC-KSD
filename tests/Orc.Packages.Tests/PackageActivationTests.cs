using Orc.Cards;
using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S10 验收：对局级包激活——装配序（插件先、卡包后）／注册转发／moding 应用（栈语义后声明者胜）／
/// 单包失败隔离／moding 不跨对局泄漏。
/// </summary>
public class PackageActivationTests
{
    [Fact]
    public void 激活_注册判定器并应用moding()
    {
        var package = Package("p1", PackageKind.Plugin, context =>
        {
            context.RegisterJudicator("test.j", new AddOne());
            context.DeclareReplaceWrapper(WrapperTarget.Judicator, "test.j", _ => [999]);
        });

        var judicators = new JudicatorRegistry();
        var report = PackageActivation.Activate(
            AssetCatalogSnapshot.FromPackages(new[] { package }),
            new AssetServiceRegistry(), judicators, new LogicEngine().Prefabs);

        Assert.Empty(report.Failures);
        Assert.Single(report.ActivatedPackages);
        Assert.Equal(999, judicators.Invoke("test.j", [1])![0]);
    }

    [Fact]
    public void moding栈语义_后声明者胜()
    {
        var package = Package("p1", PackageKind.Plugin, context =>
        {
            context.RegisterJudicator("test.j", new AddOne());
            context.DeclareReplaceWrapper(WrapperTarget.Judicator, "test.j", _ => [111]);
            context.DeclareReplaceWrapper(WrapperTarget.Judicator, "test.j", _ => [222]);
        });

        var judicators = new JudicatorRegistry();
        PackageActivation.Activate(
            AssetCatalogSnapshot.FromPackages(new[] { package }),
            new AssetServiceRegistry(), judicators, new LogicEngine().Prefabs);

        Assert.Equal(222, judicators.Invoke("test.j", [1])![0]);
    }

    [Fact]
    public void 装配序_插件先于卡包()
    {
        var cardPack = Package("pack1", PackageKind.CardPack, _ => { });
        var plugin = Package("plug1", PackageKind.Plugin, _ => { });

        var snapshot = AssetCatalogSnapshot.FromPackages(new[] { cardPack, plugin });

        Assert.Equal(new[] { "plug1", "pack1" }, snapshot.Packages.Select(p => p.Manifest.Id));
    }

    [Fact]
    public void 单包失败_隔离且不阻断其余()
    {
        var bad = Package("bad", PackageKind.Plugin, _ => throw new InvalidOperationException("boom"));
        var good = Package("good", PackageKind.Plugin, context => context.RegisterJudicator("g", new AddOne()));

        var judicators = new JudicatorRegistry();
        var report = PackageActivation.Activate(
            AssetCatalogSnapshot.FromPackages(new[] { bad, good }),
            new AssetServiceRegistry(), judicators, new LogicEngine().Prefabs);

        Assert.Single(report.Failures);
        Assert.Contains("boom", report.Failures[0]);
        Assert.Single(report.ActivatedPackages);
        Assert.Equal("good@1.0.0", report.ActivatedPackages[0]);
    }

    [Fact]
    public void moding不跨对局泄漏()
    {
        var package = Package("p1", PackageKind.Plugin, context =>
        {
            context.RegisterJudicator("test.j", new AddOne());
            context.DeclareReplaceWrapper(WrapperTarget.Judicator, "test.j", _ => [999]);
        });
        var snapshot = AssetCatalogSnapshot.FromPackages(new[] { package });

        var matchA = new JudicatorRegistry();
        var matchB = new JudicatorRegistry();
        PackageActivation.Activate(snapshot, new AssetServiceRegistry(), matchA, new LogicEngine().Prefabs);
        PackageActivation.Activate(snapshot, new AssetServiceRegistry(), matchB, new LogicEngine().Prefabs);

        Assert.Equal(999, matchA.Invoke("test.j", [1])![0]);
        Assert.Equal(999, matchB.Invoke("test.j", [1])![0]);

        // 另建一次不声明 moding 的激活 ⇒ 第三个对局不受影响（栈在注册表内、彼此隔离）
        var matchC = new JudicatorRegistry();
        var plain = Package("plain", PackageKind.Plugin, context => context.RegisterJudicator("test.j", new AddOne()));
        PackageActivation.Activate(
            AssetCatalogSnapshot.FromPackages(new[] { plain }),
            new AssetServiceRegistry(), matchC, new LogicEngine().Prefabs);

        Assert.Equal(2, matchC.Invoke("test.j", [1])![0]);
    }

    [Fact]
    public void 分相激活_仅执行声明属该相的脚本()
    {
        var log = new List<string>();
        var package = PackageWithScripts("p1", PackageKind.Plugin, new[]
        {
            (AssetScope.Match, AssetActivationPhase.PreCommand, (Action<IPluginRegistrationContext>)(_ => log.Add("pre"))),
            (AssetScope.Match, AssetActivationPhase.PostCommand, (Action<IPluginRegistrationContext>)(_ => log.Add("post"))),
            (AssetScope.Process, AssetActivationPhase.PreCommand, (Action<IPluginRegistrationContext>)(_ => log.Add("process"))),
        });
        var snapshot = AssetCatalogSnapshot.FromPackages(new[] { package });

        PackageActivation.Activate(
            snapshot, new AssetServiceRegistry(), new JudicatorRegistry(), new LogicEngine().Prefabs,
            phase: AssetActivationPhase.PreCommand);

        // 进程级脚本不参与对局装配（由进程级装配承担）
        Assert.Equal(new[] { "pre" }, log);

        PackageActivation.Activate(
            snapshot, new AssetServiceRegistry(), new JudicatorRegistry(), new LogicEngine().Prefabs,
            phase: AssetActivationPhase.PostCommand);

        Assert.Equal(new[] { "pre", "post" }, log);
    }

    [Fact]
    public void postCommand相_异常上抛_failfast()
    {
        var package = PackageWithScripts("p1", PackageKind.Plugin, new[]
        {
            (AssetScope.Match, AssetActivationPhase.PostCommand,
                (Action<IPluginRegistrationContext>)(_ => throw new InvalidOperationException("绑定核验失败：x"))),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => PackageActivation.Activate(
            AssetCatalogSnapshot.FromPackages(new[] { package }),
            new AssetServiceRegistry(), new JudicatorRegistry(), new LogicEngine().Prefabs,
            phase: AssetActivationPhase.PostCommand));

        Assert.Contains("绑定核验失败", ex.Message);
    }

    private static LoadedPackage Package(string id, PackageKind kind, Action<IPluginRegistrationContext> register)
        => PackageWithScripts(id, kind,
            new[] { (AssetScope.Match, AssetActivationPhase.PreCommand, register) });

    private static LoadedPackage PackageWithScripts(
        string id, PackageKind kind,
        (AssetScope Scope, AssetActivationPhase Phase, Action<IPluginRegistrationContext> Register)[] scripts)
    {
        var manifest = new PackageManifest(
            kind, id, Guid.NewGuid(), PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(),
            scripts.Select((_, i) => new PackageAsset("csx", $"script{i}", $"csx/script{i}.csx")).ToArray());

        var assets = new List<LoadedAsset>();
        var loaded = new List<LoadedPluginScript>();
        for (var i = 0; i < scripts.Length; i++)
        {
            var (scope, phase, register) = scripts[i];
            var descriptor = new PackageAsset("csx", $"script{i}", $"csx/script{i}.csx", scope, phase);
            var asset = new LoadedAsset(
                descriptor,
                new AssetName(AssetLoadKind.Csx, $"script{i}", manifest.Guid8, new AssetVersion(1, 0, 0)),
                "unused");
            assets.Add(asset);
            loaded.Add(new LoadedPluginScript(asset, register));
        }

        return new LoadedPackage(manifest, "root", assets, loaded);
    }

    private sealed class AddOne : Judicator
    {
        public override object[]? Invoke(object[]? args) => [Unpack<int>(args, 0) + 1];
    }
}
