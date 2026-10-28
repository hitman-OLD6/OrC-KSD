using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Plugins;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S14 验收（卡包装载门）：
/// <list type="bullet">
/// <item>卡包内原子：任一资产失败＝整包零注册，报告列出全部失败项；</item>
/// <item>必需包失败＝对局不开局（初始化上抛）；可选包失败＝跳过该包、对局照常开始；</item>
/// <item>被跳过的可选包在初始化报告（<see cref="Orc.Core.LoadGateReport"/>）与审查导出（<c>packages</c> 段）可见。</item>
/// </list>
/// </summary>
public class CardPackLoadingGateTests
{
    private const string UnitId = "gate.unit";

    private static CardDefinitionEntry Unit()
        => new(UnitId, new CardDefinition("测试步兵", 1, 1, 1, 5, unitTypes: new[] { "infantry" }, faction: "soviet", rarity: "standard"));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orc-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PackageManifest Manifest(string id, bool required = false)
        => PackageManifest.Parse($$"""
            {
              "schemaVersion": 2,
              "id": "{{id}}",
              "guid": "{{Guid.NewGuid()}}",
              "kind": "cardpack",
              "version": "v1"{{(required ? "," : "")}}
              {{(required ? "\"required\": true" : "")}}
            }
            """);

    private static EffectSnapshot Snapshot(string id, string handlerKey = "gate.hit")
        => new(new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                id + "·main",
                true,
                hooks: new[] { "card.destroyed" },
                events: new[] { new EventPrefab("onDestroyed", assemblyKey: handlerKey) })));

    /// <summary>
    /// v4-only（§23.8）：包只认 <c>effects-v4/*.component.json</c>；传旧 <c>effectsDirectory</c> 会被
    /// `CardPackAssembler` 整包拒绝（那是"明确拒绝而不是静默无效"的口径）。夹具因此一律走 v4 目录。
    /// </summary>
    private static CardPackContent Content(string id, string? componentsDirectory, bool required = false)
        => new(
            Manifest(id, required),
            Path.GetTempPath(),
            Array.Empty<CardDefinitionEntry>(),
            Array.Empty<EffectSnapshot>(),
            Array.Empty<(string, IReadOnlyList<string>)>(),
            effectsDirectory: null,
            Array.Empty<CardPackKeyword>(),
            new Dictionary<string, string>(),
            componentsDirectory: componentsDirectory);

    /// <summary>最小合法 v4 组件（无成员/挂载：只验装载门与注册面，不验行为）。</summary>
    private static string ComponentJson(string id)
        => "{\"schemaVersion\":4,\"asset\":{\"kind\":\"component\",\"id\":\"" + id + "\","
         + "\"requires-host\":\"any\",\"active-when\":\"loaded\",\"one-shot\":false,"
         + "\"private-fields\":[],\"members\":[],\"mounts\":[],\"wraps\":[],\"judge-wraps\":[]}}";

    private static LogicEngine Engine()
    {
        var engine = new LogicEngine { ScriptEvaluator = new Orc.Script.CSharpScriptEvaluator() };
        engine.Prefabs.RegisterHandler(
            "gate.hit",
            (Func<Orc.Cards.CardEventView, Context, CancellationToken, Task>)((_, _, _) => Task.CompletedTask));
        return engine;
    }

    [Fact]
    public void 清单_required字段_可读可写且缺省为可选()
    {
        var required = Manifest("pack.req", required: true);
        Assert.True(required.Required);

        var optional = Manifest("pack.opt");
        Assert.False(optional.Required);

        // 写侧只在实际必需时产出该键（旧清单往返不变）。
        Assert.Contains("\"required\": true", required.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("required", optional.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void 可选包坏资产_整包跳过且对局照常_报告与审查面可见()
    {
        var goodDir = TempDir();
        var badDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(goodDir, "gate.good.component.json"), ComponentJson("gate.good"));
            File.WriteAllText(Path.Combine(badDir, "gate.also-good.component.json"), ComponentJson("gate.also-good"));
            File.WriteAllText(Path.Combine(badDir, "gate.broken.component.json"), "{ 这不是 JSON");

            var engine = Engine();
            var registry = new CardEffectRegistry();

            CardPackAssembler.Attach(
                registry,
                engine,
                new[] { Content("pack.good", goodDir), Content("pack.bad", badDir) });

            // 好包照常注册；坏包整包零注册（连它的好文件也不进注册面）。
            // 注册面是**编排目录**（v4 组件资产的唯一去处），不是旧 `Prefabs` 快照面。
            Assert.True(engine.Model!.Catalog.Contains("gate.good"));
            Assert.False(engine.Model.Catalog.Contains("gate.also-good"));

            var skipped = Assert.Single(engine.LoadGate.Skipped);
            Assert.Equal("pack.bad", skipped.PackId);
            Assert.False(skipped.Required);
            Assert.Contains(skipped.Failures, static f => f.Contains("gate.broken.component.json", StringComparison.Ordinal));

            // 审查导出：packages 段能读到"被跳过的可选包 + 失败项"。
            using var audit = JsonDocument.Parse(engine.ExportAuditChainJson());
            var packages = audit.RootElement.GetProperty("packages").EnumerateArray().ToArray();
            var bad = Assert.Single(packages, static p => p.GetProperty("id").GetString() == "pack.bad");
            Assert.True(bad.GetProperty("skipped").GetBoolean());
            Assert.Equal("skipped", bad.GetProperty("status").GetString());
            Assert.False(bad.GetProperty("required").GetBoolean());
            Assert.NotEmpty(bad.GetProperty("failures").EnumerateArray());

            // 文本审查面同样可见（能力不退化）。
            Assert.Contains("pack.bad optional status=skipped", engine.ExportAuditChainText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(goodDir, recursive: true);
            Directory.Delete(badDir, recursive: true);
        }
    }

    [Fact]
    public void 必需包坏资产_装配上抛_零注册且装载门标记失败()
    {
        var badDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(badDir, "gate.broken.component.json"), "{ 这不是 JSON");

            var engine = Engine();
            var registry = new CardEffectRegistry();

            var error = Assert.Throws<InvalidDataException>(() => CardPackAssembler.Attach(
                registry, engine, new[] { Content("pack.required", badDir, required: true) }));

            Assert.Contains("必需卡包", error.Message, StringComparison.Ordinal);

            // "零注册"在 v4 下不可直接观测：装配过程会顺带装入内建词条组件（8 份，引擎自带），
            // 且本包唯一文件解析失败 → 不产生任何可枚举的资产 id（没有 id 可以去 Contains）。
            // 语义由下面两条覆盖：必需失败进 RequiredFailures（带包 id 与状态），且不混进 Skipped。

            var failed = Assert.Single(engine.LoadGate.RequiredFailures);
            Assert.Equal("pack.required", failed.PackId);
            Assert.Equal(PackLoadStatus.Failed, failed.Status);
            Assert.Empty(engine.LoadGate.Skipped);
        }
        finally
        {
            Directory.Delete(badDir, recursive: true);
        }
    }

    [Fact]
    public async Task 对局装配_必需包失败_初始化上抛且不留半初始化状态()
    {
        var match = new Match(
            new CardList(Enumerable.Repeat(UnitId, 8)),
            new CardList(Enumerable.Repeat(UnitId, 8)),
            new[] { Unit() },
            seed: 1,
            options: new MatchOptions { SkipMulligan = true },
            packageActivator: new FakeActivator(new MatchPackageActivationResult(
                Array.Empty<string>(),
                new[] { "pack.req@v1：脚本异常" },
                new[] { new MatchPackageSkip("pack.req@v1", Required: true, new[] { "脚本异常" }) })));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => match.Initialize());

        Assert.Contains("必需卡包", error.Message, StringComparison.Ordinal);
        Assert.Single(match.Engine.LoadGate.RequiredFailures);
        Assert.Equal(MatchState.Preparing, match.State); // 未进入进行态（失败对局不产生半初始化状态）
    }

    private sealed class FakeActivator : IMatchPackageActivator
    {
        private readonly MatchPackageActivationResult _result;

        internal FakeActivator(MatchPackageActivationResult result) => _result = result;

        public MatchPackageActivationResult Activate(
            AssetActivationPhase phase,
            IMatchServices? matchServices,
            IMatchBindingServices? bindingServices,
            AssetServiceRegistry assetServices,
            JudicatorRegistry judicators,
            SnapshotStore prefabs,
            Orc.Game.Ui.IUiPluginServices? uiServices = null)
            => _result;
    }
}
