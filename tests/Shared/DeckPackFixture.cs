using Orc.Core;
using Orc.Game.Cards;
using Orc.Packages;

namespace Orc.TestSupport;

/// <summary>
/// **卡组测试包夹具**（唯一的写盘实现）：按真实落盘形状生成一个卡包——manifest、`cards/` 与 `decks/` 的
/// 源文件与其 sidecar、以及正式 lock。
/// <para>
/// 手写包而不是走迁移工具，是因为迁移工具只认 <c>*.card.json</c>／<c>*.prefab.json</c>（卡组不由它产出），
/// 而卡组测试需要「卡与卡组在同一个包里、lock 精确覆盖谁」这类可裁剪的控制。
/// 身份字段一律按 <see cref="AssetIdentity.DeriveAssetGuid"/> 派生，不手填 GUID。
/// </para>
/// </summary>
public sealed class DeckPackFixture
{
    private readonly List<AssetMetadata> _assets = new();
    private readonly List<string> _lockedAppIds = new();

    /// <summary>创建夹具。</summary>
    /// <param name="root">包根目录。</param>
    /// <param name="packageId">包 id。</param>
    public DeckPackFixture(string root, string packageId)
    {
        Root = root;
        PackageId = packageId;
        Guid = DerivePackageGuid(packageId);
        Directory.CreateDirectory(root);
    }

    /// <summary>包根目录。</summary>
    public string Root { get; }

    /// <summary>包 id。</summary>
    public string PackageId { get; }

    /// <summary>包稳定身份（由包 id 确定性派生）。</summary>
    public Guid Guid { get; }

    /// <summary>清单（未写盘也可直接交给注册面）。</summary>
    public PackageManifest Manifest => new(
        PackageKind.CardPack,
        PackageId,
        Guid,
        PackageVersion.Parse("1.0.0"),
        Array.Empty<PackageDependency>(),
        Array.Empty<PackageAsset>(),
        Name: "卡组夹具 " + PackageId,
        Metadata: Array.Empty<string>());

    /// <summary>写一张卡（源文件 ＋ sidecar）。</summary>
    /// <param name="localId">局部 ID。</param>
    /// <param name="faction">阵营。</param>
    /// <param name="deckable">是否可构筑（false 才落盘该字段）。</param>
    /// <param name="sidecarIdOverride">sidecar 声明的 id（制造 id 不一致用）。</param>
    /// <param name="lockIt">是否进 lock（false＝文件在但不参与装载）。</param>
    /// <param name="veteranOf">非空＝声明为老兵版本（内建不可构筑，R9/D23）。</param>
    public DeckPackFixture AddCard(
        string localId,
        string faction = Faction.Germany,
        bool deckable = true,
        string? sidecarIdOverride = null,
        bool lockIt = true,
        string? veteranOf = null)
    {
        var relative = $"cards/{localId}.card.json";
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"卡 " + localId + "\", "
            + (deckable ? string.Empty : "\"deckable\": false, ")
            + "\"components\": ["
            + "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }, "
            + "{ \"component\": \"factionCost\", \"faction\": \"" + faction + "\", \"kredits\": 1 }, "
            + "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 2 }, "
            + "{ \"component\": \"tagData\", \"rarity\": \"Standard\" }"
            + (veteranOf is null
                ? string.Empty
                : ", { \"component\": \"keywords\", \"attributes\": [\"VeteranOf:" + veteranOf + "\"] }")
            + " ] }");

        var declaredId = sidecarIdOverride ?? localId;
        var metadata = Meta(AssetKind.Card, declaredId);
        WriteSidecar(path, relative, metadata);
        _assets.Add(metadata);
        if (lockIt && sidecarIdOverride is null)
        {
            _lockedAppIds.Add(AppId(localId));
        }

        return this;
    }

    /// <summary>写一份卡组（源文件 ＋ sidecar）。</summary>
    /// <param name="localId">局部 ID。</param>
    /// <param name="cards">卡引用（原文 ID ＋ 张数）。</param>
    /// <param name="mainFaction">主国。</param>
    /// <param name="allyFaction">盟国（缺省＝不写，走推导）。</param>
    /// <param name="name">展示名（null＝不写）。</param>
    /// <param name="declaredDependencies">sidecar 里写的依赖（模拟过期缓存）。</param>
    /// <param name="extraField">额外声明 anchor／loader／serviceKind。</param>
    /// <param name="corruptGuid">GUID 与身份不符。</param>
    /// <param name="lockIt">是否进 lock。</param>
    public DeckPackFixture AddDeck(
        string localId,
        IReadOnlyList<(string Id, int Count)> cards,
        string mainFaction = Faction.Germany,
        string? allyFaction = null,
        string? name = "示例先遣队",
        IReadOnlyList<AssetReference>? declaredDependencies = null,
        string? extraField = null,
        bool corruptGuid = false,
        bool lockIt = true)
    {
        var relative = $"decks/{localId}.deck.json";
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var config = allyFaction is null
            ? "\"deckConfig\": { \"mainFaction\": \"" + mainFaction + "\" }"
            : "\"deckConfig\": { \"mainFaction\": \"" + mainFaction + "\", \"allyFaction\": \"" + allyFaction + "\" }";
        var nameJson = name is null ? string.Empty : "\"name\": \"" + name + "\", ";
        var list = string.Join(
            ", ",
            cards.Select(card => "{ \"id\": \"" + card.Id + "\", \"count\": " + card.Count + " }"));
        File.WriteAllText(
            path,
            "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", " + nameJson
            + config + ", \"cards\": [ " + list + " ] }");

        var version = AssetVersion.Parse("1.0.0");
        var metadata = new AssetMetadata(
            corruptGuid
                ? Guid.NewGuid()
                : AssetIdentity.DeriveAssetGuid(Guid, AssetKind.Deck, localId, version),
            AssetKind.Deck,
            localId,
            version,
            Anchor: extraField == "anchor" ? "stableKey.host" : null,
            Loader: extraField == "loader" ? "builtin.card" : null,
            DependsOn: declaredDependencies ?? [],
            ConflictsWith: [],
            ServiceKind: extraField == "serviceKind" ? "judicator" : null);
        WriteSidecar(path, relative, metadata);
        _assets.Add(metadata);
        if (lockIt && !corruptGuid)
        {
            _lockedAppIds.Add(AppId(localId));
        }

        return this;
    }

    /// <summary>落盘 manifest 与正式 lock。</summary>
    public DeckPackFixture Build()
    {
        File.WriteAllText(Path.Combine(Root, PackageDiscovery.ManifestFileName), Manifest.ToJson());

        var entries = _assets
            .Where(asset => _lockedAppIds.Contains(AppId(asset.Id)))
            .Select(asset => new AssetLockEntry(
                asset.Guid, asset.Kind, AppId(asset.Id), asset.Version))
            .ToArray();
        File.WriteAllText(
            Path.Combine(Root, AssetLockJson.FileNameFor(AssetDomain.Formal)),
            AssetLockJson.ToJson(AssetLock.Create(PackageId, AssetDomain.Formal, entries)));
        return this;
    }

    /// <summary>应用层 ID。</summary>
    public string AppId(string localId) => AssetAppId.Compose(PackageId, localId);

    /// <summary>按「4 张 × 9 ＋ 3 张」凑出 39 张（同名上限 4 下唯一的简单解）。</summary>
    public static IReadOnlyList<(string Id, int Count)> Deck39(string packageId, string prefix = "c")
        => Enumerable.Range(0, 10)
            .Select(index => ("pack/" + packageId + "/" + prefix + index, index < 9 ? 4 : 3))
            .ToArray();

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
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, PackageId, relativeSource, [metadata]));

    private static Guid DerivePackageGuid(string packageId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(packageId));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
