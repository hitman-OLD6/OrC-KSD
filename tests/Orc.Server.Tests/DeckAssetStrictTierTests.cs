using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.Server.Rooms;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S7 验收（R10 末条／D10／D12）：规则档位**只作用于手写卡列表通路**；
/// 卡组资产通路恒按 strict 判定——把服务端档位配成 lenient，卡组资产的结论不变。
/// </summary>
public sealed class DeckAssetStrictTierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-tier-" + Guid.NewGuid().ToString("N"));

    public DeckAssetStrictTierTests() => Directory.CreateDirectory(_root);

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
    public void 房间校验器_strict报归属与配额_lenient放宽()
    {
        var pool = BuildPool();
        var config = new RoomDeckConfig(Faction.Germany, Faction.Soviet);
        var strict = new DeckRulesValidator(() => pool, enforce: true, requiredDeckSize: 0, tier: DeckRuleTier.Strict);
        var lenient = new DeckRulesValidator(() => pool, enforce: true, requiredDeckSize: 0, tier: DeckRuleTier.Lenient);

        // 含第三国卡 ⇒ strict 拒绝、lenient 放行。
        var foreign = new[] { "g1", "s1", "u1" };
        var rejected = strict.Validate(foreign, config);
        Assert.NotNull(rejected);
        Assert.Equal(McpCodes.DeckInvalid, rejected!.Code);
        Assert.Contains("阵营", rejected.Message, StringComparison.Ordinal);
        Assert.Null(lenient.Validate(foreign, config));

        // 盟国卡 13 张 ⇒ strict 拒绝（配额）；12 张放行。
        var thirteen = new[] { "s1", "s1", "s1", "s1", "s2", "s2", "s2", "s2", "s3", "s3", "s3", "s3", "s4" };
        Assert.NotNull(strict.Validate(thirteen, config));
        Assert.Null(strict.Validate(thirteen[..12], config));
        Assert.Null(lenient.Validate(thirteen, config));
    }

    [Fact]
    public void 卡组资产通路恒strict_同一份内容在手写通路lenient下放行()
    {
        var pack = new PackBuilder(Path.Combine(_root, "pack"), "tier.pack");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("u" + index, Faction.USA);
        }

        // 主国 germany ＋ 盟国 soviet，但 39 张全是美国卡 ⇒ 归属违例（无论档位）。
        pack.AddDeck("starter", Faction.Germany, Faction.Soviet, ForbiddenDeck(pack.Id));
        pack.Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck.Deck);
        var diagnostic = Assert.Single(assembly.DeckDiagnostics);
        Assert.Equal(AssetDiagnosticCode.DeckInvalid, diagnostic.Code);
        Assert.Contains(diagnostic.Violations!, violation => violation.Kind == "faction-foreign");

        // 同一份卡 id 列表走手写通路、把档位配成 lenient ⇒ 放行（R10 末条：两套量尺分开）。
        var pool = assembly.Definitions.ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
        var handWritten = assembly.Registry.All
            .Single(asset => asset.Kind == AssetKind.Deck)
            .Metadata.Id;
        Assert.False(string.IsNullOrWhiteSpace(handWritten));

        var lenient = new DeckRulesValidator(() => pool, enforce: true, requiredDeckSize: 0, tier: DeckRuleTier.Lenient);
        var deckIds = Enumerable.Range(0, 10)
            .SelectMany(index => Enumerable.Repeat(
                "pack/" + pack.Id + "/u" + index, index < 9 ? 4 : 3))
            .ToArray();
        Assert.Null(lenient.Validate(deckIds, new RoomDeckConfig(Faction.Germany, Faction.Soviet)));
    }

    private static IReadOnlyList<(string Id, int Count)> ForbiddenDeck(string packageId)
        => Enumerable.Range(0, 10)
            .Select(index => ("pack/" + packageId + "/u" + index, index < 9 ? 4 : 3))
            .ToArray();

    private static IReadOnlyDictionary<string, CardDefinition> BuildPool()
        => new Dictionary<string, CardDefinition>(StringComparer.Ordinal)
        {
            ["g1"] = new("德国兵", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard),
            ["s1"] = new("苏联兵一", 1, 1, 1, 1, faction: Faction.Soviet, rarity: Rarity.Standard),
            ["s2"] = new("苏联兵二", 1, 1, 1, 1, faction: Faction.Soviet, rarity: Rarity.Standard),
            ["s3"] = new("苏联兵三", 1, 1, 1, 1, faction: Faction.Soviet, rarity: Rarity.Standard),
            ["s4"] = new("苏联兵四", 1, 1, 1, 1, faction: Faction.Soviet, rarity: Rarity.Standard),
            ["u1"] = new("美国兵", 1, 1, 1, 1, faction: Faction.USA, rarity: Rarity.Standard),
        };

    private sealed class PackBuilder(string root, string id)
    {
        private readonly List<AssetMetadata> _assets = new();

        public string Root { get; } = root;

        public string Id { get; } = id;

        public Guid Guid { get; } = DerivePackageGuid(id);

        public void AddCard(string localId, string faction)
        {
            var relative = $"cards/{localId}.card.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"卡 " + localId + "\", \"components\": ["
                + "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }, "
                + "{ \"component\": \"factionCost\", \"faction\": \"" + faction + "\", \"kredits\": 1 }, "
                + "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 2 }, "
                + "{ \"component\": \"tagData\", \"rarity\": \"Standard\" } ] }");

            var metadata = Meta(AssetKind.Card, localId);
            WriteSidecar(path, relative, metadata);
            _assets.Add(metadata);
        }

        public void AddDeck(
            string localId,
            string mainFaction,
            string allyFaction,
            IReadOnlyList<(string Id, int Count)> cards)
        {
            var relative = $"decks/{localId}.deck.json";
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var list = string.Join(
                ", ",
                cards.Select(card => "{ \"id\": \"" + card.Id + "\", \"count\": " + card.Count + " }"));
            File.WriteAllText(
                path,
                "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"越界示例\", "
                + "\"deckConfig\": { \"mainFaction\": \"" + mainFaction + "\", \"allyFaction\": \"" + allyFaction + "\" }, "
                + "\"cards\": [ " + list + " ] }");

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
                Name: "档位示例 " + Id,
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

        private static Guid DerivePackageGuid(string packageId)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(packageId));
            return new Guid(bytes.AsSpan(0, 16));
        }
    }
}
