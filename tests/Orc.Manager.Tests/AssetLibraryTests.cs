using System.Text.Json.Nodes;
using Orc.Protocol;
using Xunit;

namespace Orc.Manager.Tests;

/// <summary>
/// S4 管理器资产职责（R2）：注册结果、依赖分析、两份 lock 的差异、并集判定与缺口点名。
/// </summary>
public class AssetLibraryTests
{
    [Fact]
    public void Register_ReturnsPackages_AndDependencyAnalysisIsAvailable()
    {
        using var temp = new PackFixture();
        var library = new AssetLibrary();

        var registration = library.Register([temp.PackDirectory]);

        Assert.Single(registration.Registered);
        Assert.Empty(registration.Rejected);
        Assert.Equal(PackageId, registration.Registered[0].PackageId);

        var analysis = library.AnalyzeDependencies();
        Assert.True(analysis["ok"]!.GetValue<bool>(), analysis.ToJsonString());
        Assert.Single(analysis["packages"]!.AsArray());
    }

    [Fact]
    public void Plan_MissingPackage_IsNamedPerSide()
    {
        using var temp = new PackFixture();
        var library = new AssetLibrary();
        library.Register([temp.PackDirectory]);

        var outcome = library.Plan(
            new PackageDeclaration("seat0", [new PackageRef(PackageId, Version, Guid)]),
            new PackageDeclaration("seat1", [new PackageRef("no.such.pack", "1.0.0", null)]));

        Assert.False(outcome.Ok);
        Assert.Equal(ManagerCodes.MissingAssets, outcome.Code);

        var missing = outcome.Issues
            .SelectMany(node => node?["missing"]?.AsArray() ?? new JsonArray())
            .ToArray();

        Assert.Contains(missing, node => node!["side"]!.GetValue<string>() == "seat1");
        Assert.DoesNotContain(missing, node => node!["side"]!.GetValue<string>() == "seat0");
    }

    [Fact]
    public void Plan_VersionMismatch_IsLockConflict_NamingBothSides()
    {
        using var temp = new PackFixture();
        using var other = new PackFixture(version: "9.9.9", sameId: true);

        var library = new AssetLibrary();
        library.Register([temp.PackDirectory, other.PackDirectory]);

        var outcome = library.Plan(
            new PackageDeclaration("seat0", [new PackageRef(PackageId, Version, Guid)]),
            new PackageDeclaration("seat1", [new PackageRef(PackageId, "9.9.9", Guid)]));

        Assert.False(outcome.Ok);
        Assert.Equal(ManagerCodes.LockConflict, outcome.Code);

        var conflict = outcome.Issues
            .SelectMany(node => node?["conflicts"]?.AsArray() ?? new JsonArray())
            .First();

        Assert.Equal("seat0", conflict!["leftSide"]!.GetValue<string>());
        Assert.Equal("seat1", conflict["rightSide"]!.GetValue<string>());
        Assert.Equal(Version, conflict["left"]!.GetValue<string>());
        Assert.Equal("9.9.9", conflict["right"]!.GetValue<string>());
    }

    [Fact]
    public void Plan_SameDeclarations_IsReproducible_AndReturnsDirectories()
    {
        using var temp = new PackFixture();
        var library = new AssetLibrary();
        library.Register([temp.PackDirectory]);

        var left = new PackageDeclaration("seat0", [new PackageRef(PackageId, Version, Guid)]);
        var right = new PackageDeclaration("seat1", [new PackageRef(PackageId, Version, Guid)]);

        var first = library.Plan(left, right);
        var second = library.Plan(left, right);

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        Assert.Equal(first.Plan!.PackDirectories, second.Plan!.PackDirectories);
        Assert.Equal(first.Plan.Assets, second.Plan.Assets);
        Assert.Contains(temp.PackDirectory, first.Plan.PackDirectories);
    }

    [Fact]
    public void DiffLocks_ReportsVersionMismatch_AndAssetDifferences()
    {
        using var temp = new PackFixture();
        using var other = new PackFixture(version: "9.9.9", sameId: true);

        var library = new AssetLibrary();
        library.Register([temp.PackDirectory, other.PackDirectory]);

        var diff = library.DiffLocks(
            new PackageDeclaration("seat0", [new PackageRef(PackageId, Version, Guid)]),
            new PackageDeclaration("seat1", [new PackageRef(PackageId, "9.9.9", Guid)]));

        Assert.False(diff["ok"]!.GetValue<bool>());
        Assert.Single(diff["versionMismatch"]!.AsArray());
    }

    // ---------------------------------------------------------------- 夹具

    private static string PackageId => "ai.sample";
    private static string Version => SampleVersion;
    private static string Guid => SampleGuid;

    private static readonly string SampleVersion = ReadManifest("version");
    private static readonly string SampleGuid = ReadManifest("guid");

    private static string ReadManifest(string field)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(SamplePackDirectory(), "manifest.json")))!;
        return manifest[field]!.GetValue<string>();
    }

    internal static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }

    /// <summary>把示例卡包复制到临时目录（可选改写版本号用于构造"同 id 多版本"）。</summary>
    private sealed class PackFixture : IDisposable
    {
        private readonly string _root;

        public PackFixture(string? version = null, bool sameId = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "orc-manager-tests", System.Guid.NewGuid().ToString("N"));
            PackDirectory = Path.Combine(_root, "pack");
            CopyDirectory(SamplePackDirectory(), PackDirectory);

            if (version is { Length: > 0 })
            {
                var manifestPath = Path.Combine(PackDirectory, "manifest.json");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
                manifest["version"] = version;
                if (!sameId)
                {
                    manifest["id"] = manifest["id"]!.GetValue<string>() + "-copy";
                }

                File.WriteAllText(manifestPath, manifest.ToJsonString());
            }
        }

        public string PackDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
            }
        }
    }
}
