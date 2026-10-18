using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S-C7/S-C8/S-C9/S-C10 验收：预制体管理器（注册/解析/本地目录）、效果快照 JSON 往返、
/// 动态效果实例化与装载（被动挂载触发、moding、主动施放）。
/// </summary>
public class DynamicEffectTests
{
    private const string Hook = "dyn.hook";

    private static TriggerPrefab PassiveMain(params EventPrefab[] events) =>
        new("t.main", "dyn.main", TriggerKind.Passive, hooks: new[] { Hook }, events: events);

    private static TriggerPrefab ActiveMain(params EventPrefab[] events) =>
        new("t.cast", "dyn.cast", TriggerKind.Active, events: events);

    private static EffectSnapshot Snapshot(TriggerPrefab main, IEnumerable<TriggerPrefab>? others = null, IEnumerable<WrapperPrefab>? wrappers = null) =>
        new(new EffectPrefab("prefab.dyn", main, others, wrappers));

    // ---------- S-C7 预制体管理器 ----------

    [Fact]
    public void 预制体管理器_处理器重复注册_拒绝()
    {
        var engine = new LogicEngine();
        Delegate handler = (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) => Task.CompletedTask);

        engine.Prefabs.RegisterHandler("k", handler);

        Assert.Throws<InvalidOperationException>(() => engine.Prefabs.RegisterHandler("k", handler));
        Assert.Equal(new[] { "k" }, engine.Prefabs.HandlerKeys);
    }

    [Fact]
    public void 预制体管理器_解析程序集处理器_命中与缺失()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler("test.hit", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("hit");
            return Task.CompletedTask;
        }));

        var hit = engine.Prefabs.ResolveHandler(new EventPrefab("e", assemblyKey: "test.hit"), typeof(CardEventView));
        Assert.True(hit.Success);

        var miss = engine.Prefabs.ResolveHandler(new EventPrefab("e", assemblyKey: "test.miss"), typeof(CardEventView));
        Assert.False(miss.Success);
        Assert.Equal("handler-missing", miss.ErrorCategory);
    }

    [Fact]
    public void 预制体管理器_解析csx_未装配求值器_结构化失败()
    {
        var engine = new LogicEngine();

        var result = engine.Prefabs.ResolveHandler(new EventPrefab("e", csxSource: "var x = 1;"), typeof(CardEventView));

        Assert.False(result.Success);
        Assert.Equal("evaluator-missing", result.ErrorCategory);
    }

    [Fact]
    public void 预制体管理器_本地明文目录加载()
    {
        var engine = new LogicEngine();
        var dir = Path.Combine(Path.GetTempPath(), "orc-prefab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var json = PrefabJson.Serialize(Snapshot(PassiveMain(new EventPrefab("onHook", assemblyKey: "test.hit"))));
            File.WriteAllText(Path.Combine(dir, "dyn.prefab.json"), json);
            File.WriteAllText(Path.Combine(dir, "broken.prefab.json"), "{ not json ");

            var report = engine.Prefabs.LoadDirectory(dir);

            Assert.Equal(1, report.Loaded);
            Assert.Single(report.Failures);
            Assert.Contains("prefab.dyn", engine.Prefabs.PrefabIds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- S-C5/S-C10 快照 JSON ----------

    [Fact]
    public void 效果快照_JSON往返_结构保留()
    {
        var main = PassiveMain(new EventPrefab("onHook", assemblyKey: "test.hit", downstream: new[] { "dyn.cast" }));
        var other = new TriggerPrefab("t.watch", "dyn.watch", TriggerKind.Passive, hooks: new[] { "dyn.turn.end" },
            events: new[] { new EventPrefab("watch", csxSource: "Func<CardEventView, Context, CancellationToken, Task> watch = (v,c,t) => Task.CompletedTask;") });
        var snapshot = Snapshot(main, new[] { other },
            new[] { new WrapperPrefab("replace", "onHook", null, new EventPrefab("onHookOverride", assemblyKey: "test.override")) });

        var json = PrefabJson.Serialize(snapshot, indented: true);
        var read = PrefabJson.Deserialize(json);

        Assert.Equal(snapshot.Version, read.Version);
        Assert.Equal("prefab.dyn", read.Root.Id);
        Assert.Equal("dyn.main", read.Root.MainTrigger.StableKey);
        Assert.Equal(TriggerKind.Passive, read.Root.MainTrigger.Kind);
        Assert.Equal(new[] { Hook }, read.Root.MainTrigger.Hooks);
        Assert.Equal(new[] { "dyn.cast" }, read.Root.MainTrigger.Events[0].Downstream);
        Assert.Equal("Orc.Cards.CardEventView", read.Root.MainTrigger.ViewTypeName);
        Assert.Equal("dyn.watch", read.Root.OtherTriggers[0].StableKey);
        Assert.True(read.Root.OtherTriggers[0].Events[0].IsInline);
        Assert.Equal("onHookOverride", read.Root.Wrappers[0].Handler.Id);
        Assert.Equal(read.Root.MountedHooks, new[] { Hook, "dyn.turn.end" });
    }

    [Fact]
    public void 效果快照_版本不符_结构化失败()
    {
        var json = PrefabJson.Serialize(Snapshot(PassiveMain(new EventPrefab("onHook", assemblyKey: "k"))))
            .Replace($"\"schemaVersion\":{EffectSnapshot.SchemaVersion}", "\"schemaVersion\":99");

        Assert.False(PrefabJson.TryDeserialize(json, out _, out var error));
        Assert.Contains("版本不符", error);
    }

    // ---------- S-C8 动态被动效果 ----------

    [Fact]
    public async Task 动态被动效果_实例化并挂载_事件可触发()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler("test.counter", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("fired");
            return Task.CompletedTask;
        }));

        var snapshot = Snapshot(PassiveMain(new EventPrefab("onHook", assemblyKey: "test.counter")));
        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot);

        Assert.True(instantiation.Success, instantiation.Error);
        Assert.NotNull(instantiation.Plan);
        Assert.Equal(EffectLoadStepKind.MountMainTrigger, instantiation.Plan!.Steps[0].Kind);

        var card = new Card(engine, "测试卡");
        card.AddEffect(instantiation.Effect!); // Add 即装载 → 挂载动态主触发器

        Assert.Contains(engine.Bus.EnumerateHooks(), h => h.Hook == Hook);
        Assert.Empty(trace); // 装载不执行事件

        await engine.Emit(Hook);
        Assert.Equal(new[] { "fired" }, trace);

        // 审查链可见：动态触发器进入编排管理器（种类节点）
        var node = engine.Orchestration.BuildAuditChain().Nodes.Single(n => n.StableKey == "dyn.main");
        Assert.Equal(TriggerKind.Passive, node.Kind);
    }

    [Fact]
    public async Task 动态被动效果_moding生效()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler("test.base", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("base");
            return Task.CompletedTask;
        }));
        engine.Prefabs.RegisterHandler("test.override", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("override");
            return Task.CompletedTask;
        }));

        var snapshot = Snapshot(
            PassiveMain(new EventPrefab("onHook", assemblyKey: "test.base")),
            wrappers: new[] { new WrapperPrefab("replace", "onHook", null, new EventPrefab("override", assemblyKey: "test.override")) });

        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot);
        Assert.True(instantiation.Success, instantiation.Error);

        var card = new Card(engine, "测试卡");
        card.AddEffect(instantiation.Effect!);

        await engine.Emit(Hook);

        Assert.Equal(new[] { "override" }, trace); // moding 替换基础逻辑
    }

    [Fact]
    public async Task 动态效果_快照往返后等价()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler("test.counter", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("fired");
            return Task.CompletedTask;
        }));

        var snapshot = Snapshot(PassiveMain(new EventPrefab("onHook", assemblyKey: "test.counter")));
        var json = PrefabJson.Serialize(snapshot);
        var readBack = PrefabJson.Deserialize(json);

        var instantiation = DynamicEffectFactory.Instantiate(engine, readBack);
        Assert.True(instantiation.Success, instantiation.Error);

        var card = new Card(engine, "测试卡");
        card.AddEffect(instantiation.Effect!);
        await engine.Emit(Hook);

        Assert.Equal(new[] { "fired" }, trace);
    }

    [Fact]
    public void 动态效果_视图类型无法解析_结构化失败()
    {
        var engine = new LogicEngine();
        var main = new TriggerPrefab("t.main", "dyn.main", TriggerKind.Passive, hooks: new[] { Hook },
            events: new[] { new EventPrefab("onHook", assemblyKey: "k") }, viewTypeName: "No.Such.Type");

        var result = DynamicEffectFactory.Instantiate(engine, Snapshot(main));

        Assert.False(result.Success);
        Assert.Equal("view-type", result.ErrorCategory);
    }

    [Fact]
    public void 动态效果_按注册id实例化_未注册失败()
    {
        var engine = new LogicEngine();

        var result = DynamicEffectFactory.InstantiateRegistered(engine, "not.registered");

        Assert.False(result.Success);
        Assert.Equal("prefab-missing", result.ErrorCategory);
    }

    // ---------- S-C10 动态主动效果 ----------

    [Fact]
    public async Task 动态主动效果_施放()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler("test.cast", (Func<CardEventView, Context, CancellationToken, Task>)((v, c, ct) =>
        {
            trace.Add("cast");
            return Task.CompletedTask;
        }));

        var snapshot = Snapshot(ActiveMain(new EventPrefab("cast", assemblyKey: "test.cast")));
        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot);

        Assert.True(instantiation.Success, instantiation.Error);
        var active = Assert.IsType<DynamicActiveEffect>(instantiation.Effect);

        // 主动＝纯动作型：无挂载步骤、不进总线
        Assert.DoesNotContain(instantiation.Plan!.Steps, s => s.Kind == EffectLoadStepKind.MountMainTrigger);

        await active.CastAsync(engine);
        Assert.Equal(new[] { "cast" }, trace);
    }

    [Fact]
    public void 动态主动效果_可被被动触发器声明的下游边指向()
    {
        var watcher = new TriggerPrefab("t.watch", "dyn.watch", TriggerKind.Passive, hooks: new[] { "dyn.turn.end" },
            events: new[] { new EventPrefab("w", assemblyKey: "k", downstream: new[] { "dyn.cast" }) });

        var effect = new EffectPrefab("p", watcher, otherTriggers: new[] { ActiveMain(new EventPrefab("cast", assemblyKey: "c")) });

        Assert.Equal(new[] { "dyn.cast" }, effect.MainTrigger.Events[0].Downstream);
    }
}
