using Orc.Cards;
using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S8 验收：csx 注册脚本冷加载期求值 → 对局期经受控面（<see cref="PluginRegistrationContext"/>）
/// 转发到既有注册面（效果 handler／依赖声明／本包数据读取）；脚本失败＝隔离整包。
/// </summary>
public sealed class PluginRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-reg-" + Guid.NewGuid().ToString("N"));

    public PluginRegistrationTests() => Directory.CreateDirectory(_root);

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

    private const string GoodScript = """
Action<IPluginRegistrationContext> Register = ctx => {
    ctx.RegisterHandler("h1", (Action)(() => { }));
    ctx.DeclareDependency("json:table@abcdef01#1.0.0");
};
""";

    [Fact]
    public void csx注册脚本_冷加载求值_对局期转发到既有注册面()
    {
        WritePlugin("p1", "1.0.0",
            ("csx", "register.script", "csx/register.csx", GoodScript),
            ("json", "table", "data/table.json", "{\"hello\":1}"));

        var host = EngineHost.Start(_root);
        Assert.Empty(host.LoadFailures);

        var package = Assert.Single(host.Catalog.Packages);
        var script = Assert.Single(package.Scripts);

        var engine = new LogicEngine();
        var context = new PluginRegistrationContext(
            package,
            new PluginRegistrationTargets(new AssetServiceRegistry(), new JudicatorRegistry(), engine.Prefabs));

        script.Register(context);

        Assert.Contains("h1", engine.Prefabs.HandlerKeys);
        Assert.Equal(new[] { "json:table@abcdef01#1.0.0" }, context.DeclaredDependencies);
        Assert.Equal("p1", context.PackageId);
        Assert.Equal(8, context.PackageGuid8.Length);
    }

    [Fact]
    public void 脚本读取本包数据资产()
    {
        WritePlugin("p1", "1.0.0", ("json", "table", "data/table.json", "{\"hello\":1}"));

        var host = EngineHost.Start(_root);
        var package = Assert.Single(host.Catalog.Packages);
        var context = new PluginRegistrationContext(
            package,
            new PluginRegistrationTargets(new AssetServiceRegistry(), new JudicatorRegistry(), new LogicEngine().Prefabs));

        Assert.Equal("{\"hello\":1}", context.ReadAsset("table"));
        Assert.Throws<KeyNotFoundException>(() => context.ReadAsset("nope"));
    }

    [Fact]
    public void 脚本编译失败_隔离整包且不阻断其余()
    {
        WritePlugin("good", "1.0.0");
        WritePlugin("bad", "1.0.0", ("csx", "broken", "csx/broken.csx", "this is not valid C# ;;;"));

        var host = EngineHost.Start(_root);

        Assert.Single(host.Catalog.Packages);
        Assert.Equal("good", host.Catalog.Packages[0].Manifest.Id);
        Assert.Single(host.LoadFailures);
        Assert.Contains("求值失败", host.LoadFailures[0]);
    }

    [Fact]
    public void 非csx资产_不产脚本()
    {
        WritePlugin("p1", "1.0.0", ("json", "table", "data/table.json", "{}"));

        var host = EngineHost.Start(_root);

        Assert.Empty(Assert.Single(host.Catalog.Packages).Scripts);
    }

    private void WritePlugin(string id, string version, params (string Type, string Name, string Path, string Content)[] assets)
    {
        var directory = Path.Combine(_root, "plugins", id + "-" + version);
        Directory.CreateDirectory(directory);

        foreach (var (_, _, path, content) in assets)
        {
            var full = Path.Combine(directory, path);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(full, content);
        }

        var manifest = new PackageManifest(
            PackageKind.Plugin, id, Guid.NewGuid(), PackageVersion.Parse(version),
            Array.Empty<PackageDependency>(),
            assets.Select(a => new PackageAsset(a.Type, a.Name, a.Path)).ToArray());
        File.WriteAllText(Path.Combine(directory, PackageDiscovery.ManifestFileName), manifest.ToJson());
    }
}
