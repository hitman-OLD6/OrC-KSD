using Orc.Core;
using Orc.Game.Cards;
using Orc.Packages;
using Xunit;

namespace Orc.Manager.Tests;

/// <summary>
/// S6 验收（R3/D9）：<see cref="AssetLibrary"/> 在包集合变化后**整体重算**卡组阶段——
/// 允许「非法→合法」也允许「合法→非法」，不能依赖「只增不减所以只会变好」这个巧合。
/// </summary>
public sealed class AssetLibraryDeckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-libdeck-" + Guid.NewGuid().ToString("N"));

    public AssetLibraryDeckTests() => Directory.CreateDirectory(_root);

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
    public void 追加依赖包后此前非法的卡组转为合法()
    {
        var cardsPack = OpenPack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cardsPack.AddCard("c" + index);
        }

        var decksPack = OpenPack("decks.pack");
        decksPack.AddCard("filler");
        decksPack.AddDeck("starter", cardsPack.Id);
        cardsPack.Build();
        decksPack.Build();

        var library = new AssetLibrary();

        var first = library.Register([decksPack.Root]);
        Assert.Single(first.Registered);
        var deck = FindDeck(library, decksPack.Id);
        Assert.Null(deck.Deck); // 缺包 ⇒ 非法
        Assert.Single(library.DeckDiagnostics);

        library.Register([cardsPack.Root]);

        var recomputed = FindDeck(library, decksPack.Id);
        Assert.NotNull(recomputed.Deck); // 整体重算后转合法
        Assert.Equal(39, recomputed.Deck!.CardCount);
        Assert.Empty(library.DeckDiagnostics);
    }

    private static AssetRegistration FindDeck(AssetLibrary library, string packageId)
    {
        // 读面与注册面同源：注册表由资产库持有（卡组条目带 Deck 槽）。
        var deck = library.RegisteredAssets
            .Single(asset => asset.Kind == AssetKind.Deck && asset.PackageId == packageId);
        return deck;
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private sealed class Pack(string root, string id)
    {
        private readonly List<AssetMetadata> _assets = new();

        public string Root { get; } = root;

        public string Id { get; } = id;

        public Guid Guid { get; } = AssetLoadGuid(id);

        public void AddCard(string localId)
        {
            var relative = $"cards/{localId}.card.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"卡 " + localId + "\", \"components\": ["
                + "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }, "
                + "{ \"component\": \"factionCost\", \"faction\": \"germany\", \"kredits\": 1 }, "
                + "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 2 }, "
                + "{ \"component\": \"tagData\", \"rarity\": \"Standard\" } ] }");

            var metadata = Meta(AssetKind.Card, localId);
            WriteSidecar(path, relative, metadata);
            _assets.Add(metadata);
        }

        public void AddDeck(string localId, string cardPackageId)
        {
            var relative = $"decks/{localId}.deck.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var cards = string.Join(
                ", ",
                Enumerable.Range(0, 10).Select(index =>
                    "{ \"id\": \"pack/" + cardPackageId + "/c" + index + "\", \"count\": " + (index < 9 ? 4 : 3) + " }"));
            File.WriteAllText(
                path,
                "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"示例先遣队\", "
                + "\"deckConfig\": { \"mainFaction\": \"germany\" }, \"cards\": [ " + cards + " ] }");

            var metadata = Meta(AssetKind.Deck, localId);
            WriteSidecar(path, relative, metadata);
            _assets.Add(metadata);
        }

        public void Build()
        {
            Directory.CreateDirectory(Root);
            var manifest = new PackageManifest(
                PackageKind.CardPack,
                Id,
                Guid,
                PackageVersion.Parse("1.0.0"),
                Array.Empty<PackageDependency>(),
                Array.Empty<PackageAsset>(),
                Name: "资产库示例 " + Id,
                Metadata: Array.Empty<string>());
            File.WriteAllText(Path.Combine(Root, PackageDiscovery.ManifestFileName), manifest.ToJson());

            var @lock = AssetLock.Create(
                Id,
                AssetDomain.Formal,
                _assets.Select(asset => new AssetLockEntry(
                    asset.Guid, asset.Kind, AssetAppId.Compose(Id, asset.Id), asset.Version)));
            File.WriteAllText(
                Path.Combine(Root, AssetLockJson.FileNameFor(AssetDomain.Formal)),
                AssetLockJson.ToJson(@lock));
        }

        private AssetMetadata Meta(AssetKind kind, string localId)
        {
            var version = AssetVersion.Parse("1.0.0");
            return new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(Guid, kind, localId, version),
                kind,
                localId,
                version,
                Anchor: null,
                Loader: null,
                DependsOn: [],
                ConflictsWith: []);
        }

        private void WriteSidecar(string sourcePath, string relativeSource, AssetMetadata metadata)
            => AssetSidecarJson.WriteFile(
                sourcePath + AssetSidecarJson.SidecarExtension,
                new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, Id, relativeSource, [metadata]));

        private static Guid AssetLoadGuid(string packageId)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(packageId));
            return new Guid(bytes.AsSpan(0, 16));
        }
    }

    private Pack OpenPack(string id) => new(Path.Combine(_root, id), id);
}
