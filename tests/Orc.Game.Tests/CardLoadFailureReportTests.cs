using System.Text;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Collections;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡级装载失败（§33／§34）：**csx 在 v4 承载视图下编译不过 → 卡牌判加载失败 → 卡牌加载报告标 invalid**。
/// <para>
/// 背景（§32.5 第 2 条）：该情形此前按"单条隔离"处理——只写一条 Error 日志、卡照常加载；
/// 于是症状（效果没发生／伤害 0）与原因（编译失败）隔了两层。用户口径：「编译不过时，卡牌判加载失败，写入那个报告」。
/// </para>
/// <para>
/// 落点（§34）：**已有的 <see cref="CardLoadReport"/>（I10 冻结 schema）**——批装载在实例化之前就产出了报告
/// （<c>slots</c> 覆盖每个牌堆卡位），实例装配阶段才发现的失败经 <see cref="Match.ComponentFailureSink"/>
/// 交给宿主、回填成「命中的条目降 invalid ＋ 字段问题」「命中的卡位降 invalid ＋ 路径 components」。
/// 不再另开一套卡级记录面（§33 的平行面已撤销）：<c>LoadGateReport</c> 只留包级结局。
/// </para>
/// <para>
/// 本用例走**真实装配链**（临时卡包 → <see cref="MatchCardAssembly"/> → <see cref="Match"/>）：
/// 只有真实装载才会产出报告，报告才谈得上"被回填"。
/// </para>
/// </summary>
public class CardLoadFailureReportTests
{
    private const string PackId = "lf";
    private const string BrokenLocalId = "broken-csx";
    private const string RifleLocalId = "rifle";
    private const string BrokenCardName = "坏脚本兵";
    private const string BrokenSnapshotId = "load-failure.broken-csx";

    /// <summary>
    /// 内联 csx 编译不过的形态：旧资产引用 **v4-only 已删除的类型**（<c>Orc.Cards.Effect</c> 家族）——
    /// 迁移器把入口委托首参改写成 <c>dynamic</c>（承载视图）后，这段 csx 在装载期预编译即失败（类型不存在）。
    /// <para>
    /// 为什么不用 §32 记录的 `is not T v || …` 形态：实测在当前 Roslyn 下该形态在 <c>dynamic</c> 视图里**能编过**
    /// （`||` 右操作数执行时模式已匹配、`v` 确实已赋值），不再等价于"编译不过"；此处取一条确定性的编译失败形态，
    /// 断言的对象仍是"编译不过 ⇒ 卡牌加载失败 ⇒ 报告标 invalid"这条链。
    /// </para>
    /// </summary>
    private const string BrokenCsx = """
Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    // 旧来源写法：v4-only 之后 `Orc.Cards.Effect` 已随旧链删除——承载面预编译直接失败。
    var effect = view.Host as Orc.Cards.Effect;
    if (effect is null)
    {
        return;
    }

    await Task.CompletedTask;
};
""";

    [Fact]
    public async Task 内联csx编译不过_卡牌判加载失败_卡牌加载报告标invalid()
    {
        var root = NewTempDir();
        var pack = Path.Combine(root, "card-pack-" + PackId);
        try
        {
            Directory.CreateDirectory(Path.Combine(pack, "cards"));
            WriteUtf8File(Path.Combine(pack, "cards", BrokenLocalId + ".card.json"), BrokenCardJson());
            WriteUtf8File(Path.Combine(pack, "cards", RifleLocalId + ".card.json"), RifleCardJson());

            // 元数据（sidecar ＋ manifest v2 ＋ 两 lock）由迁移工具按 S10a 口径产出——与正式卡包同一条装配链。
            AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));

            var assembly = MatchCardAssembly.Register(new[] { pack });
            var brokenAppId = ResolveAppId(assembly, BrokenLocalId);
            var rifleAppId = ResolveAppId(assembly, RifleLocalId);

            var effectRegistry = new CardEffectRegistry();
            foreach (var (cardId, prefabIds) in assembly.Declarations)
            {
                effectRegistry.DeclarePrefab(cardId, prefabIds);
            }

            // 坏卡在玩家A 的卡组里（洗牌后仍会逐张加载——加载是全覆盖的）。
            var match = new Match(
                new CardList(new[] { brokenAppId }.Concat(Enumerable.Repeat(rifleAppId, 9))),
                new CardList(Enumerable.Repeat(rifleAppId, 10)),
                assembly.Definitions,
                seed: 42,
                options: new MatchOptions { SkipMulligan = true },
                targeterBridge: CommandTestKit.AutoPickBridge(),
                effectRegistry: effectRegistry,
                packageActivator: TestHost.Activator,
                cardLoader: assembly);

            // 内联效果须在 Initialize 前注册（卡装载链在 Initialize 内解析效果引用）——与宿主同一时序。
            foreach (var snapshot in assembly.InlinePrefabs)
            {
                match.Engine.Prefabs.RegisterPrefab(snapshot);
            }

            match.Engine.ScriptEvaluator ??= new CSharpScriptEvaluator();
            match.Engine.SyncScriptEvaluator ??= new CSharpSyncScriptEvaluator();

            // 宿主接线（§34）：游戏层只把事实交出来，映射与回填写在报告侧。
            match.ComponentFailureSink = failures =>
                assembly.RecordComponentFailures(CardComponentFailureMapper.ToReportFailures(failures));

            // ① 初始化不因一张坏卡而爆炸（§33 第 2 条：卡级裁决，不是整局裁决）。
            await match.Initialize();
            Assert.Equal(MatchState.InProgress, match.State);

            // ② 报告面：真实装载产出了报告（不是手工造的）。
            var report = assembly.LastReport;
            Assert.NotNull(report);

            // ③ 卡位：坏卡所在卡位降 invalid，字段路径含 components（诊断锚点＝"第几个卡位为什么空了"）。
            var slot = Assert.Single(report.Slots, candidate => candidate.AppId == brokenAppId);
            Assert.Equal(CardLoadTier.Invalid, slot.Tier);
            Assert.Contains("components", slot.FieldPaths);
            Assert.NotNull(slot.AssetGuid); // 该卡位解析到了资产（不是"留空位"那种解析失败）
            Assert.Equal("0", slot.PlayerId); // 牌堆归属＝玩家A
            Assert.Equal(0, slot.Index);      // 卡位序号＝批装载（洗牌前）的牌堆位置

            // ④ 条目：同一资产的条目降 invalid，fields 含 scriptCompileFailed（冻结 token），路径指向出错资产。
            var item = Assert.Single(report.Items, candidate => candidate.AssetGuid == slot.AssetGuid);
            Assert.Equal(CardLoadTier.Invalid, item.Tier);
            var issue = Assert.Single(item.Fields, field => field.Reason == CardIssueReason.ScriptCompileFailed);
            Assert.Equal("scriptCompileFailed", CardIssueReasonTokens.Token(issue.Reason));
            Assert.Equal(BrokenSnapshotId, issue.Path);
            Assert.Equal(1, report.Items.Count(candidate => candidate.AssetGuid == slot.AssetGuid)); // 不新增条目

            // ⑤ 汇总：按**新 items** 重算；条目集合不因回填而增减（冻结 schema 的条目由批次决定）。
            Assert.Equal(report.Items.Count, report.Summary.Total);
            Assert.Equal(1, report.Summary.Invalid);
            Assert.Equal(
                report.Items.Count(candidate => candidate.Tier == CardLoadTier.Invalid),
                report.Summary.Invalid);
            Assert.Equal(20, report.Summary.TotalRefs); // 双方牌堆各 10 个卡位（都是已解析资产）

            // ⑥ 台账：回填＝**同一批的修订**——最近一批就是修订后的报告，批次数不增、refCount 不翻倍。
            Assert.Same(report, assembly.Ledger.Latest);
            Assert.Equal(1, assembly.Ledger.BatchCount);
            var ledgerView = assembly.Ledger.Snapshot();
            var ledgerItem = Assert.Single(ledgerView.Items, candidate => candidate.AssetGuid == slot.AssetGuid);
            Assert.Equal(CardLoadTier.Invalid, ledgerItem.Tier);
            Assert.Equal(1, ledgerItem.RefCount); // 修订不累加引用计数
            Assert.Equal(1, ledgerView.Summary.Invalid);
            Assert.Equal(20, ledgerView.Summary.TotalRefs);

            // ⑦ 对局不被阻断＋卡牌判失败＝该卡不产生：卡组条目摘除、其余卡照常装载
            //    （10 张里只少了这一张；起手装载已抽走若干张，故按「卡组＋手牌」合计断言总量）。
            var playerA = match.Players[0];
            Assert.DoesNotContain(brokenAppId, playerA.Deck);
            Assert.Equal(9, playerA.Deck.Count + playerA.Hand.Count);
            Assert.All(
                Enumerable.Range(0, playerA.Deck.Count),
                index => Assert.NotNull(playerA.Deck.PeekInstance(index)));

            // ⑧ 其余卡仍可正常取件（不留下"未装配实例的悬空卡位"）。
            var deckBefore = playerA.Deck.Count;
            var drawn = playerA.Deck.DrawInstance();
            Assert.NotNull(drawn);
            Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    /// <summary>应用层 ID 解析（与宿主同源：中间层是唯一解析实现）。</summary>
    private static string ResolveAppId(MatchCardAssembly assembly, string localId)
    {
        var resolution = assembly.Bridge.Resolve(AssetKind.Card, localId);
        Assert.Equal(IdResolutionStatus.Resolved, resolution.Status);
        return resolution.AppId!;
    }

    private static string BrokenCardJson()
    {
        var csxJson = JsonSerializer.Serialize(BrokenCsx);
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{BrokenLocalId}}",
          "name": "{{BrokenCardName}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 3 },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "effects", "inline": [
              {
                "schemaVersion": 2,
                "root": {
                  "id": "{{BrokenSnapshotId}}",
                  "version": 1,
                  "mainTrigger": {
                    "id": "t.main",
                    "stableKey": "装载失败用例·编译不过",
                    "kind": "passive",
                    "viewType": "Orc.Cards.CardEventView",
                    "hooks": ["unit.deployed"],
                    "events": [
                      { "id": "onUnitDeployed", "entry": "HandleAsync", "csx": {{csxJson}} }
                    ],
                    "version": 1
                  },
                  "otherTriggers": [],
                  "wrappers": [],
                  "injects": []
                }
              }
            ] }
          ]
        }
        """;
    }

    /// <summary>同包内的对照卡（无效果）——装载链的"其余卡照常"要有实物才谈得上。</summary>
    private static string RifleCardJson()
        => $$"""
        {
          "schemaVersion": 1,
          "id": "{{RifleLocalId}}",
          "name": "对照步兵",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 2 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """;

    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "orc-card-load-failure-" + Guid.NewGuid().ToString("N"));

    private static void WriteUtf8File(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void DeleteTempDir(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
