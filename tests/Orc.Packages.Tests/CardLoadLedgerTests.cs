using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Triggers;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S9「MCP 工具与门控」验收（台账侧）：<see cref="CardLoadLedger"/> 是本局累积视图的**唯一持有者**——
/// 按资产 GUID 合并、<c>refCount</c> 累加；模型只有一份（报告 schema 在 <c>Orc.Packages</c>，工具面不复制）。
/// </summary>
public sealed class CardLoadLedgerTests
{
    // ─────────────────────────────────────────────── 合并语义

    [Fact]
    public void Record_Merges_By_Guid_And_Accumulates_RefCount()
    {
        var ledger = new CardLoadLedger();
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();

        ledger.Record(Report("batch-1", Item(alpha, "pack/p/alpha", refCount: 2), Item(beta, "pack/p/beta", refCount: 1)));
        ledger.Record(Report("batch-2", Item(alpha, "pack/p/alpha", refCount: 3)));

        var snapshot = ledger.Snapshot();

        Assert.Equal(2, snapshot.Items.Count); // GUID 合并：alpha 不新增条目
        Assert.Equal(2, snapshot.BatchCount);
        Assert.Equal("batch-2", snapshot.LastBatchId);
        Assert.Equal(5, snapshot.Items.Single(item => item.AssetGuid == alpha).RefCount); // 2 + 3
        Assert.Equal(1, snapshot.Items.Single(item => item.AssetGuid == beta).RefCount);

        // 汇总计的是合并后条目；totalRefs＝Σ refCount
        Assert.Equal(2, snapshot.Summary.Total);
        Assert.Equal(6, snapshot.Summary.TotalRefs);
    }

    [Fact]
    public void Record_Keeps_First_Seen_Order_And_Takes_Latest_NonCount_Fields()
    {
        var ledger = new CardLoadLedger();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        ledger.Record(Report("batch-1", Item(first, "pack/p/first", refCount: 1), Item(second, "pack/p/second", refCount: 1)));
        ledger.Record(Report(
            "batch-2",
            Item(second, "pack/p/second", refCount: 1, tier: CardLoadTier.Invalid, withIssue: true),
            Item(first, "pack/p/first", refCount: 1)));

        var snapshot = ledger.Snapshot();

        // 顺序＝首次出现顺序（确定性），与后一批的报告顺序无关
        Assert.Equal(new[] { first, second }, snapshot.Items.Select(item => item.AssetGuid).ToArray());

        // 档位／字段问题取最近一次判定结果
        var secondItem = snapshot.Items.Single(item => item.AssetGuid == second);
        Assert.Equal(CardLoadTier.Invalid, secondItem.Tier);
        Assert.Single(secondItem.Fields);
        Assert.Equal(CardLoadTier.Warning, snapshot.Items.Single(item => item.AssetGuid == first).Tier);

        // 汇总按合并后的档位计（first 仍是 warning，second 已变 invalid）
        Assert.Equal(1, snapshot.Summary.Warning);
        Assert.Equal(1, snapshot.Summary.Invalid);
        Assert.Equal(0, snapshot.Summary.Valid);
    }

    [Fact]
    public void Latest_Is_The_Last_Recorded_Report_And_Empty_Is_Stable()
    {
        var ledger = new CardLoadLedger();
        var empty = ledger.Snapshot();

        Assert.Null(ledger.Latest);
        Assert.Equal(0, ledger.BatchCount);
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.Summary.Total);

        var report = Report("batch-7", Item(Guid.NewGuid(), "pack/p/x", refCount: 4));
        ledger.Record(report);

        Assert.Same(report, ledger.Latest);

        ledger.Clear();
        Assert.Null(ledger.Latest);
        Assert.True(ledger.Snapshot().IsEmpty);
        Assert.Null(CardLoadLedgerSnapshot.Empty.LastBatchId);
    }

    [Fact]
    public void Snapshot_Json_Reuses_Frozen_Key_Names_And_Omits_Slots()
    {
        var ledger = new CardLoadLedger();
        var guid = Guid.NewGuid();
        ledger.Record(Report("batch-1", Item(guid, "pack/p/alpha", refCount: 2)));

        var json = ledger.Snapshot().ToJson();

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"batchCount\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"lastBatchId\": \"batch-1\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"assetGuid\": \"{guid:D}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"appId\": \"pack/p/alpha\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refCount\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"totalRefs\": 2", json, StringComparison.Ordinal);

        // 累积视图不含 slots（卡位序号只对单批有意义）
        Assert.DoesNotContain("\"slots\"", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────── 与真实装载链同源

    [Fact]
    public async Task MatchCardAssembly_Records_Every_Batch_Into_The_Ledger()
    {
        var assembly = CreateAssembly(out var pack);
        try
        {
            var library = new CardLibrary(new LogicEngine());
            assembly.Attach(library);

            // 装配期（资产加载器）不产批次报告：台账只记「卡牌加载器整批」。
            Assert.Equal(0, assembly.Ledger.BatchCount);

            await assembly.LoadBatchAsync(
            [
                MatchCardSlot.ForPlayer(0, 0, "rifle"),
                MatchCardSlot.ForPlayer(1, 0, "rifle"),
                MatchCardSlot.ForPlayer(0, 1, "mg"),
            ]);

            var first = assembly.Ledger.Snapshot();
            var rifleGuid = assembly.Registry.ByAppId("pack/" + PackId + "/rifle").Single().AssetGuid;
            Assert.Equal(1, first.BatchCount);
            Assert.Equal(2, first.Items.Single(item => item.AssetGuid == rifleGuid).RefCount);
            Assert.Same(assembly.LastReport, assembly.Ledger.Latest);

            // 局内新增卡＝一元清单（同一入口）⇒ 第二批：同 GUID 累加、不新增条目
            var itemCount = first.Items.Count;
            await assembly.LoadBatchAsync([MatchCardSlot.ForPlayer(0, 2, "rifle")]);

            var second = assembly.Ledger.Snapshot();
            Assert.Equal(2, second.BatchCount);
            Assert.Equal(itemCount, second.Items.Count);
            Assert.Equal(3, second.Items.Single(item => item.AssetGuid == rifleGuid).RefCount);
            Assert.Equal(first.Summary.TotalRefs + 1, second.Summary.TotalRefs);
        }
        finally
        {
            Directory.Delete(pack, recursive: true);
        }
    }

    // ─────────────────────────────────────────────── 夹具

    private const string PackId = "s9.ledger";

    private MatchCardAssembly CreateAssembly(out string pack)
    {
        pack = Path.Combine(Path.GetTempPath(), "orc-s9-ledger-" + Guid.NewGuid().ToString("N"), "card-pack-" + PackId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));

        foreach (var localId in new[] { "rifle", "mg" })
        {
            File.WriteAllText(Path.Combine(pack, "cards", localId + ".card.json"), CardJson(localId));
        }

        // 元数据（sidecar ＋ manifest v2 ＋ 两 lock）由迁移工具按 S10a 口径产出。
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));
        return MatchCardAssembly.Register(new[] { pack });
    }

    private static string CardJson(string localId)
        => $$"""
        {
          "schemaVersion": 1,
          "id": "{{localId}}",
          "name": "s9 {{localId}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 2 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """;

    private static CardLoadReport Report(string batchId, params CardLoadReportItem[] items)
    {
        var summary = new CardLoadSummary(
            items.Length,
            items.Count(item => item.Tier == CardLoadTier.Valid),
            items.Count(item => item.Tier == CardLoadTier.Warning),
            items.Count(item => item.Tier == CardLoadTier.Invalid),
            items.Sum(item => item.RefCount));

        return new CardLoadReport(batchId, items, Array.Empty<CardLoadSlot>(), summary);
    }

    private static CardLoadReportItem Item(
        Guid guid,
        string appId,
        int refCount,
        CardLoadTier tier = CardLoadTier.Warning,
        bool withIssue = false)
        => new(
            guid,
            "card：" + appId + "@00000000#1.0.0",
            appId,
            AssetKind.Card,
            AssetDomain.Formal,
            tier,
            withIssue
                ? new[] { CardFieldIssue.Warning("image", "string", null, CardIssueReason.SourceMissing) }
                : Array.Empty<CardFieldIssue>(),
            refCount);
}
