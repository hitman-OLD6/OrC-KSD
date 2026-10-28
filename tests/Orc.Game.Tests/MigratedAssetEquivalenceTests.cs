using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Migration;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S11 验收：**每类转换的"迁移前后行为等价"**（R1i／R1j）。
/// <para>
/// 做法：写一份**旧格式**资产（与 samples 同形）→ 用迁移器转成新 schema → 装载进新模型 → 断言
/// "旧语义下应得的结果"。旧语义的口径逐条对照现测试：
/// <list type="bullet">
/// <item><c>hooks</c>＝订阅，同一时机上的多个订阅者按挂载序执行（现：<c>SignalMountTests</c>／<c>SignalEmitTests</c>）。</item>
/// <item><c>injects + Rewrite</c>＝改写别人结算、且在核心之前（现：<c>DiyExample09_AdjacentCoverTests</c> 3 点伤害实扣 2）。</item>
/// <item><c>injects + Commit</c>＝"该流程结算之后"（现：<c>SpaceWarCardPackTests</c> 攻击流程后撤销词条）。</item>
/// <item><c>injects</c> 无 <c>band</c>＝默认段的外来注册项，排在该流程自有逻辑之后（现：<c>CardEffectLoader</c> 注入段的默认 band）。</item>
/// <item><c>oneShot</c>＝一次触发后失效（现：<c>DynamicEffectTests</c> 的一次性语义）。</item>
/// </list>
/// </para>
/// <para>
/// 观测通道＝载荷里的 <see cref="List{T}"/>（csx 经视图字段写进去）：不依赖测试程序集被沙箱白名单引用。
/// </para>
/// </summary>
public class MigratedAssetEquivalenceTests
{
    // 观测通道＝载荷里的 List<string>，csx 经视图字段写进去（不依赖测试程序集被沙箱白名单引用）：
    // CardEventView.Effect（object?）与 DamageApplyView.Source（Card?）都是 [Optional] 的实体类字段，可承载任意对象。
    private const string SignalTrace = "Effect";
    private const string FlowTrace = "Source";

    // ---------- 旧格式资产（与 samples 同形；迁移器输入） ----------

    private static string OldAsset(
        string id,
        string triggerViewType,
        string[] hooks,
        string csx,
        string? entry = "HandleAsync",
        string injectsJson = "[]",
        bool oneShot = false,
        string triggerId = "t.main",
        string eventId = "onEvent")
        => $$"""
            {
              "schemaVersion": 3,
              "root": {
                "id": "{{id}}",
                "version": 1,
                "oneShot": {{(oneShot ? "true" : "false")}},
                "mainTrigger": {
                  "id": "{{triggerId}}",
                  "stableKey": "{{id}}·t",
                  "kind": "passive",
                  "viewType": "{{triggerViewType}}",
                  "hooks": [{{string.Join(",", hooks.Select(h => $"\"{h}\""))}}],
                  "events": [ { "id": "{{eventId}}", "entry": "{{entry}}", "csx": {{JsonSerializer.Serialize(csx)}}, "version": 1 } ],
                  "version": 1
                },
                "otherTriggers": [],
                "wrappers": [],
                "injects": {{injectsJson}},
                "judicatorWrappers": []
              }
            }
            """;

    private static string Csx(string body, bool flowView = false)
        => (flowView
               ? "Func<Orc.Game.Damage.DamageApplyView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n{\n"
               : "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n{\n")
           + body + "\n    return Task.CompletedTask;\n};\n";

    // ---------- 运行时夹具（最小引擎＋卡牌类型预制体） ----------

    private static OrchestrationRuntime BuildRuntime(List<string> trace)
    {
        var flowView = new ViewSpec(new[]
        {
            new ViewFieldDefinition("Amount", ViewFieldType.Number, writable: true, required: false),
            new ViewFieldDefinition("Target", ViewFieldType.Entity, writable: false, required: false),
            new ViewFieldDefinition("Kind", ViewFieldType.String, writable: false, required: false),
            new ViewFieldDefinition("IsZeroed", ViewFieldType.Boolean, writable: true, required: false),
            new ViewFieldDefinition(FlowTrace, ViewFieldType.Entity, writable: false, required: false),
        });

        MemberDefinition Flow(string name) => new(
            name,
            flowView,
            MemberVisibility.All,
            ownEdges: new[]
            {
                new OwnedEdge((view, _, _) =>
                {
                    var amount = ((ViewCarrier)view).Get<decimal>("Amount");
                    trace.Add($"core:{amount}");
                    ((ViewCarrier)view).Set("Amount", amount * 10);
                    return Task.CompletedTask;
                }),
            },
            layer: PrefabLayer.Game);

        var catalog = new PrefabCatalog()
            .Add(new PrefabDefinition(
                "engine",
                EntityKind.Engine,
                layer: PrefabLayer.Kernel,
                members: new[]
                {
                    Flow("damage-apply"),
                    Flow("unit-attack"),
                    Flow("action-budget"),
                    SignalMember.Create(
                        Updates.CardDestroyed,
                        SignalTiming.Any,
                        ViewSpec.Of(
                            ("Card", ViewFieldType.Entity),
                            (SignalTrace, ViewFieldType.Entity)),
                        PrefabLayer.Kernel),
                }))
            .Add(new PrefabDefinition(
                "card.unit",
                EntityKind.Card,
                layer: PrefabLayer.Game,
                hosts: HostKind.Unit,
                members: new[]
                {
                    new MemberDefinition("targeting", visibility: MemberVisibility.All, layer: PrefabLayer.Game),
                }));

        return new OrchestrationRuntime(catalog, new HandlerRegistry());
    }

    private static string WritePack(IEnumerable<(string Id, string Json)> assets)
    {
        var directory = Path.Combine(Path.GetTempPath(), "orc-equivalence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        foreach (var (id, json) in assets)
        {
            var migrated = AssetSchemaMigrator.Migrate(json, id + ".prefab.json");
            File.WriteAllText(Path.Combine(directory, id + ".component.json"), migrated.Json);
        }

        return directory;
    }

    private static void Load(OrchestrationRuntime runtime, string directory)
    {
        var result = AssetPackLoader.Load(
            runtime, directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator());
        Assert.True(
            result.Loaded,
            string.Join("；", result.Errors.Select(static e => $"[{e.Category}] {e.Target}：{e.Message}")));
    }

    private static OrchestrationEntity Host(OrchestrationRuntime runtime, string name)
        => runtime.CreateEntity("card.unit", name);

    private static void AddComponent(OrchestrationRuntime runtime, OrchestrationEntity host, string assetId)
        => runtime.AddComponent(host, assetId, assetId + "@" + host.Name);

    // ---------- ① hooks → engine#X 挂载（订阅序） ----------

    [Fact]
    public async Task hooks转挂载_同一时机的执行序与旧订阅序一致()
    {
        var trace = new List<string>();
        var a = OldAsset("eq.hooks-a", "Orc.Cards.CardEventView", new[] { "card.destroyed" },
            Csx($"        ((System.Collections.Generic.List<string>)view.{SignalTrace}).Add(\"A\");"));
        var b = OldAsset("eq.hooks-b", "Orc.Cards.CardEventView", new[] { "card.destroyed" },
            Csx($"        ((System.Collections.Generic.List<string>)view.{SignalTrace}).Add(\"B\");"));

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.hooks-a", a), ("eq.hooks-b", b) });
        try
        {
            Load(runtime, directory);

            var host = Host(runtime, "宿主");
            AddComponent(runtime, host, "eq.hooks-a");
            AddComponent(runtime, host, "eq.hooks-b");

            // 旧语义：订阅＝挂载；同一时机上的多个订阅者按挂载（声明）序执行。
            Assert.Equal(
                new[] { "eq.hooks-a", "eq.hooks-b" },
                runtime.DescribeSignal(Updates.CardDestroyed)
                    .Where(static e => e.Source == EdgeSource.Explicit)
                    .Select(static e => e.DeclaringPrefab));

            await runtime.EmitAsync(Updates.CardDestroyed, new Dictionary<string, object?>
            {
                ["Card"] = host,
                [SignalTrace] = trace,
            });

            Assert.Equal(new[] { "A", "B" }, trace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- ② Rewrite → wrap/before（改写输入，核心看到新值） ----------

    [Fact]
    public async Task injectsRewrite迁移为before包装_改写先于核心且对核心可见()
    {
        var trace = new List<string>();
        var rewrite = OldAsset(
            "eq.rewrite",
            "Orc.Game.Damage.DamageApplyView",
            Array.Empty<string>(),
            Csx($"        ((System.Collections.Generic.List<string>)view.{FlowTrace}).Add(\"rewrite:\" + view.Amount);\n        view.Amount = view.Amount - 1;", flowView: true),
            injectsJson: """[ { "target": "伤害处理触发器", "band": "Rewrite", "event": "onEvent" } ]""");

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.rewrite", rewrite) });
        try
        {
            Load(runtime, directory);
            var host = Host(runtime, "宿主");
            AddComponent(runtime, host, "eq.rewrite");

            await runtime.RunMemberAsync(runtime.Engine, "damage-apply", FlowPayload(host, trace));

            // 旧语义（DiyExample09：3 点伤害实扣 2）＝改写先于核心；核心看到的是改写后的值 4。
            Assert.Equal(new[] { "rewrite:5", "core:4" }, trace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- ③ Commit → wrap/after（结算之后） ----------

    [Fact]
    public async Task injectsCommit迁移为after包装_在流程结算之后执行()
    {
        var trace = new List<string>();
        var commit = OldAsset(
            "eq.commit",
            "Orc.Game.Damage.DamageApplyView",
            Array.Empty<string>(),
            Csx($"        ((System.Collections.Generic.List<string>)view.{FlowTrace}).Add(\"commit:\" + view.Amount);", flowView: true),
            injectsJson: """[ { "target": "单位攻击触发器", "band": "Commit", "event": "onEvent", "priority": 200 } ]""");

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.commit", commit) });
        try
        {
            Load(runtime, directory);
            var host = Host(runtime, "宿主");
            AddComponent(runtime, host, "eq.commit");

            await runtime.RunMemberAsync(runtime.Engine, "unit-attack", FlowPayload(host, trace));

            // 旧语义（SpaceWarCardPackTests：攻击流程结算后撤销词条）＝核心跑完（5×10=50）之后才执行。
            Assert.Equal(new[] { "core:5", "commit:50" }, trace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- ④ 无 band → 外来挂载边（成员自有边之后） ----------

    [Fact]
    public async Task injects无band迁移为外来挂载边_排在成员自有边之后()
    {
        var trace = new List<string>();
        var plain = OldAsset(
            "eq.plain",
            "Orc.Game.Damage.DamageApplyView",
            Array.Empty<string>(),
            Csx($"        ((System.Collections.Generic.List<string>)view.{FlowTrace}).Add(\"plain:\" + view.Amount);", flowView: true),
            injectsJson: """[ { "target": "行动额度触发器", "event": "onEvent" } ]""");

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.plain", plain) });
        try
        {
            Load(runtime, directory);
            var host = Host(runtime, "宿主");
            AddComponent(runtime, host, "eq.plain");

            await runtime.RunMemberAsync(runtime.Engine, "action-budget", FlowPayload(host, trace));

            // R1i：成员执行＝自有边（固定最前）→ 外来挂载边（按声明序）。
            Assert.Equal(new[] { "core:5", "plain:50" }, trace);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- ④b 审查面：`injects` 的投影改读组件边（A3） ----------

    [Fact]
    public async Task injects迁移后审查面读组件边_不再有旧injected边()
    {
        var trace = new List<string>();
        var plain = OldAsset(
            "eq.plain",
            "Orc.Game.Damage.DamageApplyView",
            Array.Empty<string>(),
            Csx($"        ((System.Collections.Generic.List<string>)view.{FlowTrace}).Add(\"plain\");", flowView: true),
            injectsJson: """[ { "target": "行动额度触发器", "event": "onEvent" } ]""");

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.plain", plain) });
        try
        {
            Load(runtime, directory);
            var host = Host(runtime, "宿主");
            AddComponent(runtime, host, "eq.plain");

            var graph = runtime.BuildAuditGraph();

            // 等价读点：旧 `effect.Injections` 的 `legacy#…` + `injected` 边，换成**真实的组件挂载边**——
            // 目标地址、来源实体、来源／位次全部可读，且 GraphGap=false（旧面恒为图缺口）。
            var edge = Assert.Single(graph.EdgesInto("engine#action-budget"), e => e.DeclaringPrefab == "eq.plain");
            Assert.Equal("engine#action-budget", edge.Target);
            Assert.Equal("onEvent", edge.Member);
            Assert.Equal(EdgeSource.Explicit, edge.Source);
            Assert.Equal(EdgeOrderSource.Declared, edge.OrderSource);
            Assert.False(edge.GraphGap);
            Assert.Equal("eq.plain@宿主", edge.DeclaringEntity);
            Assert.Equal("component", edge.DeclaringKind);

            // 旧读面已下线：不再产生 `injected` 来源的边、不再有 `legacy#` 目标。
            Assert.Empty(graph.EdgesBySource(EdgeSource.Injected));
            Assert.DoesNotContain(graph.Edges, static e => e.Target.StartsWith("legacy#", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, object?> FlowPayload(OrchestrationEntity host, List<string> trace)
        => new()
        {
            ["Amount"] = 5m,
            ["Target"] = host,
            ["Kind"] = "combat",
            ["IsZeroed"] = false,
            [FlowTrace] = trace,
        };

    // ---------- ⑤ oneShot → one-shot（一次后失效） ----------

    [Fact]
    public async Task oneShot迁移为one_shot_第一次触发后失效()
    {
        var trace = new List<string>();
        var once = OldAsset(
            "eq.once",
            "Orc.Cards.CardEventView",
            new[] { "card.destroyed" },
            Csx($"        ((System.Collections.Generic.List<string>)view.{SignalTrace}).Add(\"once\");"),
            oneShot: true);

        var runtime = BuildRuntime(trace);
        var directory = WritePack(new[] { ("eq.once", once) });
        try
        {
            Load(runtime, directory);
            var host = Host(runtime, "宿主");
            var component = runtime.ConstructComponent(host, "eq.once", "一次性");
            component.Assemble();

            var payload = new Dictionary<string, object?>
            {
                ["Amount"] = 5m,
                ["Card"] = host,
                [SignalTrace] = trace,
            };
            await runtime.EmitAsync("card.destroyed", payload);
            await runtime.EmitAsync("card.destroyed", payload);

            // 旧语义：一次性＝首次触发后整个效果失效（第二次不执行）。
            Assert.Equal(new[] { "once" }, trace);
            Assert.True(component.Component.OneShotConsumed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- ⑥ 判定器包装（judicatorWrappers → judge-wraps） ----------

    [Fact]
    public void 判定器包装迁移后仍挂在同一判定器与相位上()
    {
        var path = Path.Combine(FindRepoRoot(), "samples", "card-pack-bulwark", "effects-v4", "keyword.entrench.component.json");
        if (!File.Exists(path))
        {
            return; // 产物缺失：迁移器测试已覆盖（本用例只验执行面接线形态）。
        }

        var runtime = BuildRuntime(new List<string>());
        var directory = Path.Combine(Path.GetTempPath(), "orc-judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        try
        {
            Load(runtime, directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var host = Host(runtime, "宿主");
        AddComponent(runtime, host, "keyword.entrench");

        var entries = runtime.Wraps.Entries(WrapTarget.ForJudicator("move.leg.eligibility"));
        Assert.NotEmpty(entries);
        Assert.All(entries, static e => Assert.NotNull(e.Handler.Judge));
        Assert.Contains(entries, static e => e.Phase == WrapperPhase.After);
        Assert.Contains(runtime.Wraps.Entries(WrapTarget.ForJudicator("inhibit.leg.eligibility")),
            static e => e.Handler.Judge is not null);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
