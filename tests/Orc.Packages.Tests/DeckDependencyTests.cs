using Orc.Core;
using Orc.Game.Cards;
using Orc.Packages;
using Orc.Packages.Decks;
using Orc.Packages.Loading;
using Orc.TestSupport;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S8 验收（R4／D31-D35）：依赖派生与加载分层。
/// 权威恒为 <c>cards</c>；sidecar 的 <c>dependsOn</c> 只是派生缓存——不一致只留警报、不判非法。
/// </summary>
public sealed class DeckDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-deckdep-" + Guid.NewGuid().ToString("N"));

    public DeckDependencyTests() => Directory.CreateDirectory(_root);

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
    public void 卡组层号严格大于全部被引用卡_即使sidecar未写依赖()
    {
        var pack = NewPack("packa");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddDeck("starter", DeckPackFixture.Deck39(pack.PackageId)).Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);
        var plan = LoadListPlanner.Plan(assembly.Registry, [AssetLockJson.ReadFile(
            Path.Combine(pack.Root, AssetLockJson.FileNameFor(AssetDomain.Formal)))]);

        var layerOf = plan.Formal.Layers
            .SelectMany(layer => layer.Items.Select(item => (item.AppId, layer.Index)))
            .ToDictionary(pair => pair.AppId, pair => pair.Index);

        var deckLayer = layerOf[pack.AppId("starter")];
        foreach (var card in Enumerable.Range(0, 10).Select(index => pack.AppId("c" + index)))
        {
            Assert.True(layerOf[card] < deckLayer, $"{card} 应排在卡组之前。");
        }
    }

    [Fact]
    public void 派生清单按卡去重且用全名与星约束()
    {
        var pack = NewPack("packb");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddDeck("starter", DeckPackFixture.Deck39(pack.PackageId)).Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);
        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);

        Assert.Equal(10, deck.Metadata.DependsOn.Count);
        Assert.All(deck.Metadata.DependsOn, reference =>
        {
            Assert.Equal(AssetKind.Card, reference.Kind);
            Assert.StartsWith("pack/packb/", reference.AppId, StringComparison.Ordinal);
            Assert.Equal(DeckRegistrar.VersionIndependentConstraint, reference.VersionConstraint);
        });
        Assert.Equal(
            Enumerable.Range(0, 10).Select(index => pack.AppId("c" + index)).ToArray(),
            deck.Metadata.DependsOn.Select(reference => reference.AppId).ToArray());
    }

    [Fact]
    public void sidecar依赖过期_以重算为准且只留警报()
    {
        var pack = NewPack("packc");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        // sidecar 还写着「依赖 c0」，而 cards 实际引用的是 c0..c9（编辑器提交后前端直写的情形）。
        pack.AddDeck(
                "starter",
                DeckPackFixture.Deck39(pack.PackageId),
                declaredDependencies: [new AssetReference(AssetKind.Card, "pack/packc/c0", "1.*.*")])
            .Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.NotNull(deck.Deck); // 不判非法
        Assert.Equal(10, deck.Metadata.DependsOn.Count); // 以重算为准
        Assert.Contains(assembly.Warnings, warning => warning.Contains("dependsOn", StringComparison.Ordinal));
        Assert.Empty(assembly.DeckDiagnostics);
    }

    [Fact]
    public void 注册流程不写盘()
    {
        var pack = NewPack("packd");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddDeck(
                "starter",
                DeckPackFixture.Deck39(pack.PackageId),
                declaredDependencies: [new AssetReference(AssetKind.Card, "pack/packd/c0", "1.*.*")])
            .Build();

        var sidecar = pack.Root;
        var before = Snapshot(sidecar);
        MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);
        var after = Snapshot(sidecar);

        Assert.Equal(before, after);
    }

    [Fact]
    public void R4漏洞用例_直写换卡后重算得到新依赖_planner剔除卡组但不整段失败()
    {
        var pack = NewPack("packe");

        // c0..c9 已入 lock；grenadier 的源文件在、但**未入 lock**（前端直写新卡，还没走编辑器）。
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddCard("grenadier", lockIt: false);

        var cards = DeckPackFixture.Deck39(pack.PackageId).ToList();
        cards[0] = ("grenadier", 4); // 作者把第一张换成了未入 lock 的卡
        pack.AddDeck(
                "starter",
                cards,
                declaredDependencies: [new AssetReference(AssetKind.Card, "pack/packe/c0", "1.*.*")])
            .Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);
        var plan = LoadListPlanner.Plan(assembly.Registry, [AssetLockJson.ReadFile(
            Path.Combine(pack.Root, AssetLockJson.FileNameFor(AssetDomain.Formal)))]);

        // 卡组引用未入 lock 的卡 ⇒ 该卡组被剔除（不可用），但计划本身仍有效、其他资产照常装载。
        Assert.True(plan.IsValid);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == AssetLoadIssueCode.DependencyMissing
                     && issue.AppId == pack.AppId("starter"));
        Assert.DoesNotContain(plan.Formal.Items, item => item.Kind == AssetKind.Deck);
        Assert.Contains(plan.Formal.Items, item => item.AppId == pack.AppId("c1"));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private DeckPackFixture NewPack(string packageId)
        => new(Path.Combine(_root, packageId), packageId);

    /// <summary>目录下所有文件的（相对路径 → 内容）快照。</summary>
    private static Dictionary<string, string> Snapshot(string root)
        => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => File.ReadAllText(path),
                StringComparer.Ordinal);
}
