using Orc.Core;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.TestSupport;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S13 验收（R9／D14）：**合成包**（10 张卡定义凑出 39 张卡组）里同时放入
/// <c>deckable: false</c> 的衍生卡与 <c>VeteranOf</c> 非空的老兵卡，
/// 卡组只要引用它们就必须命中 <c>not-deckable</c> 并点名——这是「卡牌标记 → 卡组判定」的整链验证。
/// </summary>
public sealed class DeckExclusionEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-deckexcl-" + Guid.NewGuid().ToString("N"));

    public DeckExclusionEndToEndTests() => Directory.CreateDirectory(_root);

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
    public void 卡组引用不可构筑与老兵卡_命中not_deckable并点名()
    {
        var pack = new DeckPackFixture(Path.Combine(_root, "pack"), "excl.pack");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddCard("derived", deckable: false);
        pack.AddCard("veteran", veteranOf: "c0");

        var cards = DeckPackFixture.Deck39(pack.PackageId).ToList();
        cards[0] = ("derived", 4);
        cards[1] = ("veteran", 4);
        pack.AddDeck("starter", cards);
        pack.Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);

        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);
        Assert.Null(deck.Deck); // 非法：不生成卡组对象

        var diagnostic = Assert.Single(assembly.DeckDiagnostics);
        var named = diagnostic.Violations!
            .Where(violation => violation.Kind == "not-deckable")
            .Select(violation => violation.CardId)
            .ToArray();

        Assert.Contains(pack.AppId("derived"), named);
        Assert.Contains(pack.AppId("veteran"), named);
    }

    [Fact]
    public void 同包另一份只用合法卡的卡组照常生成()
    {
        var pack = new DeckPackFixture(Path.Combine(_root, "pack2"), "excl2.pack");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddCard("derived", deckable: false);
        pack.AddDeck("starter", DeckPackFixture.Deck39(pack.PackageId));
        pack.Build();

        var assembly = MatchCardAssembly.Register([pack.Root], requiredDeckSize: 39);
        var deck = Assert.Single(assembly.Registry.All, asset => asset.Kind == AssetKind.Deck);

        Assert.NotNull(deck.Deck);
        Assert.Empty(assembly.DeckDiagnostics);
    }
}
