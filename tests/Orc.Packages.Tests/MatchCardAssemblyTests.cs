using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Collections;
using Orc.Game.Triggers;
using Orc.Packages.Loading;
using Orc.Plugins.BuiltIn;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S7「对局接线」验收（装载链侧）：<see cref="MatchCardAssembly"/>（资产加载器 → 卡牌加载器 → 卡库）
/// 与 <see cref="Match"/> 的真实接线。
/// <para>
/// 覆盖：注册期定版进中间层、装配期整批落库（含资产域）、实例化不查中间层／注册面、
/// 双方牌堆一次整批装载且水位线时机不变、局内新增卡走单卡批次并同步中间层、以及「加载严格按元数据」。
/// </para>
/// </summary>
public sealed class MatchCardAssemblyTests : IDisposable
{
    private const string PackId = "s7.pack";
    private const string Prefix = "pack/" + PackId + "/";

    private static readonly string[] LocalIds = ["rifle", "smg", "mg", "mortar", "token"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-s7-" + Guid.NewGuid().ToString("N"));

    public MatchCardAssemblyTests() => Directory.CreateDirectory(_root);

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

    // ─────────────────────────────────────────────── 注册期与装配期

    [Fact]
    public void Register_Binds_Version_Decisions_And_Attach_Installs_Locked_Cards()
    {
        var assembly = CreateAssembly();

        // 注册期（无卡库）：定版已写进中间层——卡组解析因此可在建局前完成。
        foreach (var localId in LocalIds)
        {
            var resolution = assembly.Bridge.Resolve(AssetKind.Card, localId);
            Assert.Equal(IdResolutionStatus.Resolved, resolution.Status);
            Assert.Equal(Prefix + localId, resolution.AppId);
        }

        var library = new CardLibrary(new LogicEngine());
        assembly.Attach(library);

        // 装配期：合法卡进卡库（含资产域）。
        foreach (var localId in LocalIds)
        {
            Assert.True(library.Contains(Prefix + localId), localId);
            Assert.Equal(AssetDomain.Formal, library.DomainOf(Prefix + localId));
        }

        Assert.Equal(AssetLoadStatus.Loaded, assembly.LoadResult!.Status);

        // 重复接入＝明确拒绝（一个装载链只服务一个对局）。
        Assert.Throws<InvalidOperationException>(() => assembly.Attach(library));
    }

    [Fact]
    public void Instantiation_Does_Not_Consult_Bridge_Or_Registry()
    {
        var assembly = CreateAssembly();
        var library = new CardLibrary(new LogicEngine());
        assembly.Attach(library);

        // 清空中间层（依赖解析面）——实例化仍必须成功：实例化不做任何依赖查找。
        assembly.Bridge.Clear();
        Assert.Equal(
            IdResolutionStatus.NotFound,
            assembly.Bridge.Resolve(AssetKind.Card, Prefix + "rifle").Status);

        var card = library.Instantiate(Prefix + "rifle");

        Assert.Equal("s7 rifle", card.Name);
        Assert.Equal(AssetDomain.Formal, card.AssetDomain);
        Assert.False(card.IsTestAsset);
    }

    [Fact]
    public void Directory_Without_Manifest_Is_Rejected()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(Path.Combine(bare, "cards"));
        File.WriteAllText(Path.Combine(bare, "cards", "x.card.json"), CardJson("x"));

        var error = Assert.Throws<InvalidDataException>(() => MatchCardAssembly.Register(new[] { bare }));
        Assert.Contains("manifest.json", error.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────── 整批装载（双方牌堆一次）

    [Fact]
    public async Task Both_Decks_Load_In_A_Single_Batch_And_Watermark_Stays_After_Deck_Loading()
    {
        var assembly = CreateAssembly();
        var deck = Enumerable.Range(0, 3).Select(i => Prefix + LocalIds[i % 3]).ToArray();

        var match = new Match(
            new CardList(deck),
            new CardList(deck),
            assembly.Definitions,
            seed: 7,
            options: new MatchOptions
            {
                SkipMulligan = true,
                OpeningHandSizeFirstPlayer = 0,
                OpeningHandSizeSecondPlayer = 0,
            },
            packageActivator: Activator(),
            cardLoader: assembly);

        await match.Initialize();

        // 一次整批覆盖双方牌堆（3＋3 卡位；玩家 0 在前）。
        var slots = assembly.LastReport!.Slots;
        Assert.Equal(6, slots.Count);
        Assert.Equal(
            Enumerable.Repeat("0", 3).Concat(Enumerable.Repeat("1", 3)),
            slots.Select(slot => slot.PlayerId));
        // 无卡图/描述＝警报（D40），但**可玩**：不是非法即可装载。
        Assert.All(slots, slot => Assert.NotEqual(CardLoadTier.Invalid, slot.Tier));

        // 水位线语义：双方全量实例化后快照 ⇒ 构筑内的每一张都不是"构筑之外"（装载段不占号）。
        Assert.Equal(3, match.Players[0].Deck.Count);
        Assert.Equal(3, match.Players[1].Deck.Count);
        foreach (var player in match.Players)
        {
            for (var i = 0; i < player.Deck.Count; i++)
            {
                Assert.False(match.IsOutsideDeck(player.Deck.PeekInstance(i)));
            }
        }

        // 卡库装的是**套件补全后**的定义（可实例化）：实例化后仍不查中间层。
        assembly.Bridge.Clear();
        Assert.Equal("s7 rifle", match.CardLibrary.Instantiate(Prefix + "rifle").Name);
    }

    // ─────────────────────────────────────────────── 局内新增卡（单卡批次）

    [Fact]
    public async Task In_Match_Card_Uses_Single_Card_Batch_And_Syncs_Bridge()
    {
        var assembly = CreateAssembly();
        var tokenAppId = Prefix + "token";
        var deck = new[] { Prefix + "rifle", Prefix + "smg" };

        var match = new Match(
            new CardList(deck),
            new CardList(deck),
            assembly.Definitions,
            seed: 11,
            options: new MatchOptions
            {
                SkipMulligan = true,
                OpeningHandSizeFirstPlayer = 0,
                OpeningHandSizeSecondPlayer = 0,
            },
            packageActivator: Activator(),
            cardLoader: assembly);

        await match.Initialize();

        var created = await match.CardService.CreateAsync(tokenAppId, match.Players[0]);

        // 单卡批次：一元清单（不是再来一次整批）。
        var single = Assert.Single(assembly.LastReport!.Slots);
        Assert.Equal(tokenAppId, single.AppId);
        Assert.NotEqual(CardLoadTier.Invalid, single.Tier);

        // 中间层同步：(域, 种类, 应用层 ID) → 本次装载产出的 GUID。
        var registration = Assert.Single(
            assembly.Registry.ByAppId(tokenAppId), asset => asset.Domain == AssetDomain.Formal);
        Assert.True(assembly.Bridge.TryGet(AssetDomain.Formal, AssetKind.Card, tokenAppId, out var bound));
        Assert.Equal(registration.AssetGuid, bound);

        // 定义已按单卡装载路径落库并实例化；ID ＞ 水位线＝构筑之外。
        Assert.True(match.CardLibrary.Contains(tokenAppId));
        Assert.Equal("s7 token", created.Name);
        Assert.True(match.IsOutsideDeck(created));

        // 未知卡＝明确失败（不产生实例、不污染中间层）。
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => match.CardService.CreateAsync(Prefix + "ghost", match.Players[0]));
    }

    [Fact]
    public async Task In_Match_Card_Resolution_Goes_Through_Bridge_Only()
    {
        var assembly = CreateAssembly();
        var deck = new[] { Prefix + "rifle" };

        var match = new Match(
            new CardList(deck),
            new CardList(deck),
            assembly.Definitions,
            seed: 13,
            options: new MatchOptions
            {
                SkipMulligan = true,
                OpeningHandSizeFirstPlayer = 0,
                OpeningHandSizeSecondPlayer = 0,
            },
            packageActivator: Activator(),
            cardLoader: assembly);

        await match.Initialize();

        // 清空中间层：单卡路径不再能解析（不回退到注册面扫描——解析唯一实现＝中间层）。
        assembly.Bridge.Clear();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => match.CardService.CreateAsync(Prefix + "mg", match.Players[0]));
    }

    // ─────────────────────────────────────────────── 夹具

    private static SnapshotPackageActivator Activator()
    {
        // 进程级默认面（词条/组件注册 ＋ 动作实现集）：建局前必须就位（与宿主建局同源）。
        BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();

        return new(AssetCatalogSnapshot.FromPackages(new[]
        {
            BuiltInPluginPackage.Load(),
            FoundationPluginPackage(),
        }));
    }

    private static LoadedPackage FoundationPluginPackage()
        => Orc.Foundation.Actions.FoundationActionsPluginPackage.Load();

    private MatchCardAssembly CreateAssembly()
    {
        // 包 id 由目录名去 `card-pack-` 前缀派生（迁移工具口径）⇒ 目录名须为 card-pack-{包id}。
        var pack = Path.Combine(_root, "card-pack-" + PackId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));
        foreach (var localId in LocalIds)
        {
            File.WriteAllText(Path.Combine(pack, "cards", localId + ".card.json"), CardJson(localId));
        }

        // 元数据（sidecar ＋ manifest v2 ＋ 两 lock）由迁移工具按 S10a 口径产出——不手写身份字段。
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));

        return MatchCardAssembly.Register(new[] { pack });
    }

    private static string CardJson(string localId)
        => $$"""
        {
          "schemaVersion": 1,
          "id": "{{localId}}",
          "name": "s7 {{localId}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 2 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """;
}
