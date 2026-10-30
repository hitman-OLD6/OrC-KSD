using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S5 验收（R11／D55-D58）：<c>decks/</c> 扫描与声明面。
/// 核心是 D56 的双来源——卡靠「卡文件同目录」进 sidecar 字典，卡组必须显式并进去，
/// 否则就是「文件在、永不注册、还不报错」这个静默坑。
/// </summary>
public sealed class DeckScanTests : IDisposable
{
    private const string PackId = "demo.deckscan";
    private static readonly Guid PackGuid = Guid.Parse("d0c40000-0000-4000-8000-0000000000d1");

    private readonly string _base = Path.Combine(Path.GetTempPath(), "orc-deckscan-" + Guid.NewGuid().ToString("N"));

    public DeckScanTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PackRoot => Path.Combine(_base, "packs", PackId);

    [Fact]
    public void 未在manifest声明也能被发现并注册()
    {
        WriteDeck("decks", "starter", "demo.starter");

        var scan = PackageScanner.Scan(PackRoot, Manifest());
        var file = Assert.Single(scan.DeckFiles);
        Assert.Equal("decks/starter.deck.json", file.RelativePath);
        Assert.False(file.IsHistorical);
        Assert.NotNull(file.SidecarPath);

        var registry = new AssetRegistry();
        var result = PackageRegistrar.Register(PackRoot, Manifest(), registry);

        // 第一段只留「卡组声明」（R3/D9：卡组统一在第二段注册与判定）。
        Assert.Empty(result.Assets);
        var deferred = Assert.Single(result.Decks);
        Assert.Equal(AssetKind.Deck, deferred.Metadata.Kind);
        Assert.Equal("pack/demo.deckscan/demo.starter", deferred.AppId);
        Assert.Equal("decks/starter.deck.json", deferred.RelativeSource);
    }

    [Fact]
    public void manifest声明了缺失的卡组sidecar_包级结构性非法()
    {
        WriteManifest("decks/missing.deck.json.meta.json");

        Assert.Throws<InvalidDataException>(
            () => PackageRegistrar.Register(PackRoot, Manifest("decks/missing.deck.json.meta.json"), new AssetRegistry()));
    }

    [Fact]
    public void manifest声明versions下的卡组sidecar_包级结构性非法()
    {
        var declared = "versions/1.0.0/decks/old.deck.json.meta.json";
        WriteManifest(declared);
        WriteDeck("versions/1.0.0/decks", "old", "demo.old");

        var failure = Assert.Throws<InvalidDataException>(
            () => PackageRegistrar.Register(PackRoot, Manifest(declared), new AssetRegistry()));

        Assert.Contains("卡组不保留历史版本", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void decks子目录未声明不发现_显式声明可注册()
    {
        WriteDeck("decks/nested", "deep", "demo.deep");

        var undeclared = PackageRegistrar.Register(PackRoot, Manifest(), new AssetRegistry());
        Assert.Empty(undeclared.Assets);

        var declaredPath = "decks/nested/deep.deck.json.meta.json";
        var registry = new AssetRegistry();
        var declared = PackageRegistrar.Register(PackRoot, Manifest(declaredPath), registry);

        Assert.Empty(declared.Assets);
        var deferred = Assert.Single(declared.Decks);
        Assert.Equal(AssetKind.Deck, deferred.Metadata.Kind);
        Assert.Equal("pack/demo.deckscan/demo.deep", deferred.AppId);
    }

    [Fact]
    public void decks前缀变体目录不被当作卡组目录()
    {
        WriteDeck("decks-extra", "odd", "demo.odd");

        var scan = PackageScanner.Scan(PackRoot, Manifest());

        Assert.Empty(scan.DeckFiles);
    }

    [Fact]
    public void 手工丢进versions的卡组未声明则静默忽略()
    {
        WriteDeck("versions/1.0.0/decks", "old", "demo.old");

        var scan = PackageScanner.Scan(PackRoot, Manifest());
        Assert.Empty(scan.DeckFiles);
        Assert.DoesNotContain(scan.SidecarFiles, path => path.Contains("decks/", StringComparison.Ordinal));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), new AssetRegistry());
        Assert.Empty(result.Assets);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void 内容指纹随卡组sidecar变化()
    {
        WriteDeck("decks", "starter", "demo.starter");
        var before = AssetLoadTestKit.RecomputeContent(PackRoot, PackId);

        WriteDeck("decks", "second", "demo.second");
        var after = AssetLoadTestKit.RecomputeContent(PackRoot, PackId);

        Assert.NotEqual(before, after);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private PackageManifest Manifest(params string[] metadata) => new(
        PackageKind.CardPack,
        PackId,
        PackGuid,
        PackageVersion.Parse("1.0.0"),
        Array.Empty<PackageDependency>(),
        Array.Empty<PackageAsset>(),
        Name: "卡组扫描示例",
        Metadata: metadata);

    private void WriteManifest(params string[] metadata)
    {
        Directory.CreateDirectory(PackRoot);
        File.WriteAllText(Path.Combine(PackRoot, PackageDiscovery.ManifestFileName), Manifest(metadata).ToJson());
    }

    private void WriteDeck(string relativeDirectory, string fileName, string localId)
    {
        var directory = Path.Combine(PackRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        var source = Path.Combine(directory, fileName + ".deck.json");
        File.WriteAllText(source, DeckBody(localId));

        var version = AssetVersion.Parse("1.0.0");
        AssetSidecarJson.WriteFile(
            source + AssetSidecarJson.SidecarExtension,
            new AssetSidecar(
                AssetSidecarJson.CurrentSchemaVersion,
                PackId,
                $"{relativeDirectory}/{fileName}.deck.json",
                [
                    new AssetMetadata(
                        AssetIdentity.DeriveAssetGuid(PackGuid, AssetKind.Deck, localId, version),
                        AssetKind.Deck,
                        localId,
                        version,
                        Anchor: null,
                        Loader: null,
                        DependsOn: [],
                        ConflictsWith: []),
                ]));
    }

    private static string DeckBody(string localId)
        => $"{{ \"schemaVersion\": 1, \"id\": \"{localId}\", \"name\": \"示例\", "
           + "\"deckConfig\": { \"mainFaction\": \"germany\" }, \"cards\": [] }";
}
