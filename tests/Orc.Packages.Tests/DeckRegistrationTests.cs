using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Packages;
using Orc.Packages.Decks;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S6 验收（R3／D5-D13／D59-D63）：两段注册与卡组注册阶段。
/// 关键点：卡组必须能看到**本次注册集合的全部包**（跨包合法）；卡组阶段不改动第一段的产出；
/// 字段越界的卡组在注册表里恒为「非内含」。
/// </summary>
public sealed class DeckRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-deckreg-" + Guid.NewGuid().ToString("N"));

    public DeckRegistrationTests() => Directory.CreateDirectory(_root);

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
    public void 跨包引用合法_卡组阶段能看到全部包的卡()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        var decks = Pack("decks.pack");
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39(cards.Id));

        var assembly = Assemble(39, cards, decks);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.NotNull(deck.Deck);
        Assert.Equal("pack/decks.pack/starter", deck.AppId);
        Assert.Equal(Faction.Soviet, deck.Deck!.DeckConfig.AllyFaction);
        Assert.Equal(39, deck.Deck.CardCount);
        Assert.Empty(assembly.DeckDiagnostics);
    }

    [Fact]
    public void 缺包则同一份卡组判非法_且身份仍在()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        var decks = Pack("decks.pack");
        decks.AddCard("filler", Faction.Germany); // 包内至少要有一张卡，否则装配阶段先按「空卡池」拒绝
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39(cards.Id));

        var assembly = Assemble(39, decks);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck.Deck); // D6：非法不生成，身份保留
        var diagnostic = Assert.Single(assembly.DeckDiagnostics);
        Assert.Equal(AssetDiagnosticCode.DeckInvalid, diagnostic.Code);
        // 张数本身仍是 39（条目都在），缺的是卡——所以只应报 unknown-card，不报 size。
        Assert.Contains(diagnostic.Violations!, v => v.Kind == "unknown-card");
        Assert.DoesNotContain(diagnostic.Violations!, v => v.Kind == "size");
    }

    [Fact]
    public void 卡组阶段不改变第一段产出()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        var decks = Pack("decks.pack");
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39(cards.Id));

        var assembly = Assemble(39, cards, decks);
        var cardAssets = assembly.Registry.All.Where(asset => asset.Kind == AssetKind.Card).ToArray();

        Assert.Equal(10, cardAssets.Length);
        foreach (var card in cardAssets)
        {
            Assert.NotNull(card.Card);
            Assert.Equal(
                AssetIdentity.DeriveAssetGuid(card.PackageGuid, AssetKind.Card, card.LocalId, card.Version),
                card.AssetGuid);
            Assert.Single(cardAssets, other => other.AppId == card.AppId);
        }
    }

    [Fact]
    public void 第一段只产出卡组声明_不产出注册条目()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        var decks = Pack("decks.pack");
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39(cards.Id));

        var registry = new AssetRegistry();
        var result = PackageRegistrar.Register(decks.Root, decks.Manifest, registry);

        Assert.Empty(result.Assets);
        Assert.Single(result.Decks);
        Assert.DoesNotContain(registry.All, asset => asset.Kind == AssetKind.Deck);
    }

    [Fact]
    public void 注册函数签名保持不变()
    {
        // R3：不得把单包注册改成两段式（第二段是独立入口）。
        var method = typeof(PackageRegistrar).GetMethod(
            nameof(PackageRegistrar.Register),
            [typeof(string), typeof(PackageManifest), typeof(AssetRegistry), typeof(AssetDomain)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(PackageRegistrationResult), method!.ReturnType);
    }

    [Fact]
    public void 张数参数生效_三十九与三十结论不同()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        var decks = Pack("decks.pack");
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39(cards.Id));

        var strict = Assemble(39, cards, decks);
        var loose = Assemble(30, cards, decks);

        Assert.NotNull(Assert.Single(strict.Registry.All, asset => asset.Kind == AssetKind.Deck).Deck);

        var deck30 = Assert.Single(loose.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck30.Deck);
        Assert.Contains(loose.DeckDiagnostics, d => d.Violations!.Any(v => v.Kind == "size"));
    }

    [Fact]
    public void 被sidecar判非法的卡不进卡池_引用它报unknown_card()
    {
        var cards = Pack("cards.pack");
        for (var index = 0; index < 10; index++)
        {
            cards.AddCard("c" + index, Faction.Germany);
        }

        // 这张卡的 sidecar 声明了另一个 id ⇒ 该卡不注册（D13：不进 pool）。
        cards.AddCard("broken", Faction.Germany, sidecarIdOverride: "someone.else");

        var decks = Pack("decks.pack");
        var cards39 = DeckOf39(cards.Id);
        cards39[0] = ("broken", 4);
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, cards39);

        var assembly = Assemble(39, cards, decks);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck.Deck);
        var diagnostic = Assert.Single(assembly.DeckDiagnostics);
        // 裸局部 ID 归一后落在卡组自己的包前缀上——这正是「本包没有这张卡」的正确指认。
        Assert.Contains(
            diagnostic.Violations!,
            v => v.Kind == "unknown-card" && v.CardId == "pack/decks.pack/broken");
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("loader")]
    [InlineData("serviceKind")]
    public void 卡组字段越界_资产级非法且注册表里没有内含卡组(string field)
    {
        var decks = Pack("decks.pack");
        decks.AddCard("filler", Faction.Germany);
        decks.AddDeck("starter", "示例先遣队", Faction.Germany, DeckOf39("decks.pack"), extraField: field);

        var assembly = Assemble(39, decks);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck.Deck);
        Assert.False(deck.IsEmbedded); // D63：deck 的 IsEmbedded 恒 false
        Assert.Null(deck.Metadata.Anchor);
        Assert.Contains(assembly.DeckDiagnostics, d => d.Code == AssetDiagnosticCode.FieldNotAllowed);
        Assert.DoesNotContain(assembly.DeckDiagnostics, d => d.Code == AssetDiagnosticCode.DeckInvalid);
    }

    [Fact]
    public void 判定优先级_身份问题先于字段越界()
    {
        var decks = Pack("decks.pack");
        decks.AddCard("filler", Faction.Germany);
        decks.AddDeck(
            "starter", "示例先遣队", Faction.Germany, DeckOf39("decks.pack"), extraField: "anchor", corruptGuid: true);
        decks.Build();

        // 第一段产出：身份不对 ⇒ 连声明都不成立（不延后、不注册），更谈不上字段合规。
        var registry = new AssetRegistry();
        var result = PackageRegistrar.Register(decks.Root, decks.Manifest, registry);

        Assert.DoesNotContain(registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Empty(result.Decks);
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.GuidMismatch);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AssetDiagnosticCode.FieldNotAllowed);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    /// <summary>凑够 39 张：10 张卡按 4/4/…/3 分。</summary>
    private static List<(string Id, int Count)> DeckOf39(string packageId)
    {
        var cards = new List<(string, int)>();
        for (var index = 0; index < 10; index++)
        {
            cards.Add(($"pack/{packageId}/c{index}", index < 9 ? 4 : 3));
        }

        return cards;
    }

    private sealed class PackBuilder(string root, string id, Guid guid)
    {
        private readonly List<(string LocalId, AssetKind Kind, AssetMetadata Metadata, string SourceRelative)> _assets = new();
        private readonly List<string> _decks = new();

        public string Root { get; } = root;

        public string Id { get; } = id;

        public Guid Guid { get; } = guid;

        public PackageManifest Manifest => new(
            PackageKind.CardPack,
            Id,
            Guid,
            PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(),
            Array.Empty<PackageAsset>(),
            Name: "卡组注册示例 " + Id,
            Metadata: Array.Empty<string>());

        public void AddCard(
            string localId, string faction, string? sidecarIdOverride = null, bool deckable = true)
        {
            var relative = $"cards/{localId}.card.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CardBody(localId, faction, deckable));

            var version = AssetVersion.Parse("1.0.0");
            var declaredId = sidecarIdOverride ?? localId;
            var metadata = new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(Guid, AssetKind.Card, declaredId, version),
                AssetKind.Card,
                declaredId,
                version,
                Anchor: null,
                Loader: null,
                DependsOn: [],
                ConflictsWith: []);
            WriteSidecar(path, relative, [metadata]);
            _assets.Add((localId, AssetKind.Card, metadata, relative));
        }

        public void AddDeck(
            string localId,
            string name,
            string mainFaction,
            IReadOnlyList<(string Id, int Count)> cards,
            string? allyFaction = null,
            string? extraField = null,
            bool corruptGuid = false)
        {
            var relative = $"decks/{localId}.deck.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DeckBody(localId, name, mainFaction, allyFaction, cards));

            var version = AssetVersion.Parse("1.0.0");
            var derived = AssetIdentity.DeriveAssetGuid(Guid, AssetKind.Deck, localId, version);
            var metadata = new AssetMetadata(
                corruptGuid ? Guid.NewGuid() : derived,
                AssetKind.Deck,
                localId,
                version,
                Anchor: extraField == "anchor" ? "stableKey.host" : null,
                Loader: extraField == "loader" ? "builtin.card" : null,
                DependsOn: [],
                ConflictsWith: [],
                ServiceKind: extraField == "serviceKind" ? "judicator" : null);
            WriteSidecar(path, relative, [metadata]);
            _assets.Add((localId, AssetKind.Deck, metadata, relative));
            _decks.Add(localId);
        }

        /// <summary>写 manifest ＋ 正式 lock（lock 是「什么会被装载」的唯一声明）。</summary>
        public void Build()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, PackageDiscovery.ManifestFileName), Manifest.ToJson());

            var entries = _assets
                .Select(asset => new AssetLockEntry(
                    asset.Metadata.Guid, asset.Kind, AssetAppId.Compose(Id, asset.LocalId), asset.Metadata.Version))
                .ToArray();
            var @lock = AssetLock.Create(Id, AssetDomain.Formal, entries);
            File.WriteAllText(
                Path.Combine(Root, AssetLockJson.FileNameFor(AssetDomain.Formal)),
                AssetLockJson.ToJson(@lock));
        }

        private void WriteSidecar(string sourcePath, string relativeSource, IReadOnlyList<AssetMetadata> assets)
            => AssetSidecarJson.WriteFile(
                sourcePath + AssetSidecarJson.SidecarExtension,
                new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, Id, relativeSource, assets));

        private static string CardBody(string localId, string faction, bool deckable)
            => "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"卡 " + localId + "\", "
               + (deckable ? string.Empty : "\"deckable\": false, ")
               + "\"components\": ["
               + "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }, "
               + "{ \"component\": \"factionCost\", \"faction\": \"" + faction + "\", \"kredits\": 1 }, "
               + "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 2 }, "
               + "{ \"component\": \"tagData\", \"rarity\": \"Standard\" } ] }";

        private static string DeckBody(
            string localId,
            string name,
            string mainFaction,
            string? allyFaction,
            IReadOnlyList<(string Id, int Count)> cards)
        {
            var config = allyFaction is null
                ? "\"deckConfig\": { \"mainFaction\": \"" + mainFaction + "\" }"
                : "\"deckConfig\": { \"mainFaction\": \"" + mainFaction + "\", \"allyFaction\": \"" + allyFaction + "\" }";
            var list = string.Join(
                ", ",
                cards.Select(card => "{ \"id\": \"" + card.Id + "\", \"count\": " + card.Count + " }"));
            return "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"" + name + "\", "
                   + config + ", \"cards\": [ " + list + " ] }";
        }
    }

    private PackBuilder Pack(string id)
    {
        var guid = AssetLoadTestKit.PackageGuidOf(id);
        var builder = new PackBuilder(Path.Combine(_root, id), id, guid);
        Directory.CreateDirectory(builder.Root);
        return builder;
    }

    /// <summary>装配（先落盘 manifest／lock，再走两段注册）。</summary>
    private static MatchCardAssembly Assemble(int requiredDeckSize, params PackBuilder[] packs)
    {
        foreach (var pack in packs)
        {
            pack.Build();
        }

        return MatchCardAssembly.Register(packs.Select(pack => pack.Root).ToArray(), requiredDeckSize);
    }
}
