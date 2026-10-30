using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Mcp.Pool;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.TestSupport;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S12 验收（R5／D41-D45）：卡组诊断经读面可得的**端到端**链路
/// （PackageRegistrar → MatchCardAssembly.Inspect → PackageInspection → PoolProjection）。
/// </summary>
public sealed class DeckDiagnosticsProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-deckdiag-" + Guid.NewGuid().ToString("N"));

    public DeckDiagnosticsProjectionTests() => Directory.CreateDirectory(_root);

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
    public void 一条非法卡组是一条DeckInvalid_违规列全且可经pool投影读到()
    {
        var pack = BuildIllegalDeckPack();

        var inspection = MatchCardAssembly.Inspect([pack.Root], requiredDeckSize: 39);
        var row = Assert.Single(inspection.Packages);
        var diagnostic = Assert.Single(row.DeckDiagnostics);

        // D41：一条诊断装全部违规，而不是按违规条数拆成 N 条诊断。
        Assert.Equal(AssetDiagnosticCode.DeckInvalid, diagnostic.Code);
        Assert.NotNull(diagnostic.Violations);
        Assert.Contains(diagnostic.Violations!, violation => violation.Kind == "size");
        Assert.Contains(diagnostic.Violations!, violation => violation.Kind == "unknown-card");

        var payload = PoolProjection.Render(
            new PoolCallResult(inspection.Ok, McpCodes.Ok, "ok", inspection), PoolAudience.Sandbox);

        var rendered = payload["model"]!["packages"]!.AsArray()[0]!;
        var deckDiagnostics = rendered["deckDiagnostics"]!.AsArray();
        var entry = Assert.Single(deckDiagnostics)!;
        Assert.Equal("DeckInvalid", entry["code"]!.GetValue<string>());
        Assert.Equal("pack/diag.pack/starter", entry["appId"]!.GetValue<string>());

        var kinds = entry["violations"]!.AsArray()
            .Select(violation => violation!["kind"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("size", kinds);
        Assert.Contains("unknown-card", kinds);
    }

    [Fact]
    public void RemotePlay受众也能看到deckDiagnostics()
    {
        var pack = BuildIllegalDeckPack();

        var inspection = MatchCardAssembly.Inspect([pack.Root], requiredDeckSize: 39);
        var payload = PoolProjection.Render(
            new PoolCallResult(inspection.Ok, McpCodes.Ok, "ok", inspection), PoolAudience.RemotePlay);

        var rendered = payload["model"]!["packages"]!.AsArray()[0]!;
        Assert.Null(rendered["directory"]); // 白名单照旧
        Assert.Null(rendered["error"]);

        var entry = Assert.Single(rendered["deckDiagnostics"]!.AsArray())!;
        Assert.Equal("DeckInvalid", entry["code"]!.GetValue<string>());
        Assert.NotEmpty(entry["violations"]!.AsArray());
    }

    [Fact]
    public void 合法卡组不产生诊断_卡牌诊断的违规为null()
    {
        var pack = new DeckPackFixture(Path.Combine(_root, "ok"), "ok.pack");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        pack.AddDeck("starter", DeckPackFixture.Deck39(pack.PackageId));
        pack.Build();

        var inspection = MatchCardAssembly.Inspect([pack.Root], requiredDeckSize: 39);
        var row = Assert.Single(inspection.Packages);

        Assert.Empty(row.DeckDiagnostics);
        Assert.Null(row.DeckDiagnostics.FirstOrDefault()?.Violations);

        // 投影照旧（没有 deckDiagnostics 段，也不影响既有字段）。
        var payload = PoolProjection.Render(
            new PoolCallResult(inspection.Ok, McpCodes.Ok, "ok", inspection), PoolAudience.Sandbox);
        Assert.Null(payload["model"]!["packages"]!.AsArray()[0]!["deckDiagnostics"]);
    }

    /// <summary>一份 39 张里引用了本包不存在的卡的卡组（size 与 unknown-card 同时命中）。</summary>
    private DeckPackFixture BuildIllegalDeckPack()
    {
        var pack = new DeckPackFixture(Path.Combine(_root, "diag"), "diag.pack");
        for (var index = 0; index < 10; index++)
        {
            pack.AddCard("c" + index);
        }

        var cards = DeckPackFixture.Deck39(pack.PackageId).ToList();
        cards[0] = ("missing.card", 4);
        cards.RemoveAt(1); // 顺带少 4 张 ⇒ 同时命中 size
        pack.AddDeck("starter", cards);
        pack.Build();

        return pack;
    }
}
