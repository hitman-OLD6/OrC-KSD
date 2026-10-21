using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 反制效果装配验收（R1–R5）：数据侧 <c>effects</c> 声明的反制效果在装载期装配为 <see cref="CounterCard"/> 的
/// 效果 handler 集（激活时进 <c>OrderFlow</c> 的 Counter 段），**不**挂主触发器／otherTriggers（无双触发）；
/// 一条效果恰一个 handler（内部串行、共用外层 ctx）；不支持形态／求值失败单条隔离并留 Error；空效果 Warn 不产 handler；
/// 已装配集合可观测；卡销毁清理；非反制卡装载路径不变；官方样例不静默失效。
/// </summary>
public class CounterEffectAssemblyTests
{
    private const string CounterId = "cnt.assembly";
    private const string OrderId = "ord.assembly";
    private const string UnitId = "unit.assembly";

    // ---------- S1：装载分支落点 ----------

    [Fact]
    public async Task Counter_CardEffect_NotMountedOnBus_NotTriggeredByCardPlayed()
    {
        var fired = 0;
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.plain" },
            snapshots: new[]
            {
                Snapshot("cnt.plain", MainTrigger("cnt.plain/main", new EventPrefab("onPlayed", assemblyKey: "cnt.plain.onPlayed"))),
            },
            handlers: new (string, Delegate)[]
            {
                ("cnt.plain.onPlayed", (Func<CardTriggerView, Context, CancellationToken, Task>)((_, _, _) =>
                {
                    fired++;
                    return Task.CompletedTask;
                })),
            });

        // R1 第二条：主触发器没有挂到总线（card.played 不触发该效果）。
        Assert.DoesNotContain("cnt.plain/main", match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));
        await GameUpdates.EmitCardPlayed(match.Engine, card, match.CurrentPlayer!);
        Assert.Equal(0, fired);

        // 但 handler 面已就绪（已装配、未注册）。
        Assert.Equal(new[] { "cnt.plain" }, card.AssembledEffectHandlerNames);
        Assert.Empty(card.RegisteredEffectHandlerNames);
    }

    [Fact]
    public async Task Order_And_Unit_EffectLoading_Unchanged()
    {
        var orderFired = 0;
        var unitFired = 0;
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(OrderId, new[] { "ord.effect" });
        registry.DeclarePrefab(UnitId, new[] { "unit.effect" });

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry,
            extraDefinitions: new[] { OrderDefinition, UnitDefinition });
        await match.Initialize();

        match.Engine.Prefabs.RegisterHandler(
            "ord.effect.onPlayed",
            (Func<CardTriggerView, Context, CancellationToken, Task>)((_, _, _) =>
            {
                orderFired++;
                return Task.CompletedTask;
            }));
        match.Engine.Prefabs.RegisterHandler(
            "unit.effect.onJoined",
            (Func<CardEventView, Context, CancellationToken, Task>)((_, _, _) =>
            {
                unitFired++;
                return Task.CompletedTask;
            }));

        match.Engine.Prefabs.RegisterPrefab(Snapshot(
            "ord.effect",
            MainTrigger("ord.effect/main", new EventPrefab("onPlayed", assemblyKey: "ord.effect.onPlayed"))));
        match.Engine.Prefabs.RegisterPrefab(Snapshot(
            "unit.effect",
            new TriggerPrefab(
                "t.main",
                "unit.effect/main",
                TriggerKind.Passive,
                hooks: new[] { GameUpdates.UnitJoined },
                events: new[] { new EventPrefab("onJoined", assemblyKey: "unit.effect.onJoined") })));

        var order = (OrderCard)match.CardLibrary.Instantiate(OrderId);
        await order.LoadAsync(match.CurrentPlayer!);
        var unit = (UnitCard)match.CardLibrary.Instantiate(UnitId);
        await unit.LoadAsync(match.CurrentPlayer!);

        // 非反制卡：主触发器照常挂总线（装载路径逐字节不变）。
        var passive = Assert.IsType<DynamicPassiveEffect>(Assert.Single(order.Effects));
        Assert.True(passive.IsMounted);
        Assert.Contains("ord.effect/main", match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));
        await GameUpdates.EmitCardPlayed(match.Engine, order, match.CurrentPlayer!);
        Assert.Equal(1, orderFired);

        Assert.True(Assert.Single(unit.Effects).IsMounted);
        Assert.Contains("unit.effect/main", match.Engine.Bus.GetSubscribers(GameUpdates.UnitJoined));
        await match.Engine.Emit(GameUpdates.UnitJoined, new Dictionary<string, object?> { ["Unit"] = unit });
        Assert.Equal(1, unitFired);
    }

    // ---------- S2：一条效果 → 一个 handler ----------

    [Fact]
    public async Task Counter_CardEffect_OneEffectOneHandler()
    {
        var (_, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.two", "cnt.one", "cnt.empty" },
            snapshots: new[]
            {
                Snapshot("cnt.two", MainTrigger(
                    "cnt.two/main",
                    new EventPrefab("e1", assemblyKey: "cnt.two.e1"),
                    new EventPrefab("e2", assemblyKey: "cnt.two.e2"))),
                Snapshot("cnt.one", MainTrigger("cnt.one/main", new EventPrefab("e1", assemblyKey: "cnt.one.e1"))),
                Snapshot("cnt.empty", MainTrigger("cnt.empty/main")),
            },
            handlers: new (string, Delegate)[]
            {
                ("cnt.two.e1", Noop),
                ("cnt.two.e2", Noop),
                ("cnt.one.e1", Noop),
            });

        // 条数＝产出 handler 的效果数（两事件的效果只产一个 handler；空效果不产）。
        Assert.Equal(new[] { "cnt.two", "cnt.one" }, card.AssembledEffectHandlerNames);
        Assert.Equal(2, card.AssembledEffectHandlerNames.Count);
    }

    [Fact]
    public async Task Counter_CardEffect_SerialEvents_SharedContext()
    {
        var log = new List<(string Event, Context Ctx)>();
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.serial", "cnt.stop" },
            snapshots: new[]
            {
                Snapshot("cnt.serial", MainTrigger(
                    "cnt.serial/main",
                    new EventPrefab("e1", assemblyKey: "cnt.serial.e1"),
                    new EventPrefab("e2", assemblyKey: "cnt.serial.e2"))),
                Snapshot("cnt.stop", MainTrigger(
                    "cnt.stop/main",
                    new EventPrefab("e1", assemblyKey: "cnt.stop.e1"),
                    new EventPrefab("e2", assemblyKey: "cnt.stop.e2"))),
            },
            handlers: new (string, Delegate)[]
            {
                ("cnt.serial.e1", RecordEvent(log, "serial.e1")),
                ("cnt.serial.e2", RecordEvent(log, "serial.e2")),
                ("cnt.stop.e1", (Func<CardTriggerView, Context, CancellationToken, Task>)((_, ctx, _) =>
                {
                    log.Add(("stop.e1", ctx));
                    ctx.Stop();
                    return Task.CompletedTask;
                })),
                ("cnt.stop.e2", RecordEvent(log, "stop.e2")),
            });

        Assert.True((await match.PlayManager.UseCounterAsync(card)).Status == PlayResultStatus.Success);

        // R1.2（回合门禁）：反制只在**对手**回合生效——用例意图（串行／共享 ctx／Stop 后余下不执行／命中即消耗）
        // 与回合归属无关，故把行动方让给对手；原写法恰好构造了「己方回合内己方反制生效」这一被规则禁止的场景。
        await match.EndTurn();

        var (source, target) = CreateFlowCards(match.Engine);
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);

        // 列表序串行；同一 handler 内共用同一个 ctx；Stop 后本级余下事件不再执行。
        Assert.Equal(new[] { "serial.e1", "serial.e2", "stop.e1" }, log.Select(item => item.Event));
        Assert.Same(log[0].Ctx, log[1].Ctx);
        Assert.Same(log[0].Ctx, log[2].Ctx);
        Assert.True(card.IsConsumed); // 命中即消耗（一次性）
    }

    [Fact]
    public async Task Counter_CardEffect_UnsupportedFaces_NotAssembled()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.other", "cnt.wrap", "cnt.ok" },
            snapshots: new[]
            {
                Snapshot(
                    "cnt.other",
                    MainTrigger("cnt.other/main", new EventPrefab("e1", assemblyKey: "cnt.other.e1")),
                    others: new[]
                    {
                        new TriggerPrefab(
                            "t.other",
                            "cnt.other/other",
                            TriggerKind.Passive,
                            hooks: new[] { GameUpdates.CardPlayed },
                            events: new[] { new EventPrefab("o1", assemblyKey: "cnt.other.o1") },
                            viewTypeName: typeof(CardTriggerView).FullName!),
                    }),
                Snapshot(
                    "cnt.wrap",
                    MainTrigger("cnt.wrap/main", new EventPrefab("e1", assemblyKey: "cnt.wrap.e1")),
                    wrappers: new[]
                    {
                        new WrapperPrefab("after", "e1", null, new EventPrefab("w1", assemblyKey: "cnt.wrap.w1")),
                    }),
                Snapshot("cnt.ok", MainTrigger("cnt.ok/main", new EventPrefab("e1", assemblyKey: "cnt.ok.e1"))),
            },
            handlers: new (string, Delegate)[]
            {
                ("cnt.other.e1", Noop),
                ("cnt.other.o1", Noop),
                ("cnt.wrap.e1", Noop),
                ("cnt.wrap.w1", Noop),
                ("cnt.ok.e1", Noop),
            });

        // 单条隔离：其它效果照常装配、不整卡 fail-fast。
        Assert.Equal(new[] { "cnt.ok" }, card.AssembledEffectHandlerNames);

        var errors = EffectAssemblyEntries(match, LogLevel.Error);
        Assert.Contains(errors, entry =>
            entry.Keywords.Contains("unsupported-other-triggers") && entry.Message.Contains("cnt.other"));
        Assert.Contains(errors, entry =>
            entry.Keywords.Contains("unsupported-wrappers") && entry.Message.Contains("cnt.wrap"));
        Assert.All(errors, entry => Assert.Contains("effect", entry.Keywords));
    }

    // ---------- S4：失败与留痕分级 ----------

    [Fact]
    public async Task Counter_CardEffect_NoCallableEvent_WarnsWithoutHandler()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.disposalonly" },
            snapshots: new[]
            {
                Snapshot("cnt.disposalonly", MainTrigger(
                    "cnt.disposalonly/main",
                    new EventPrefab("cleanup", assemblyKey: "cnt.disposalonly.cleanup", band: "disposing"))),
            },
            handlers: new (string, Delegate)[] { ("cnt.disposalonly.cleanup", Noop) });

        // 空效果（只有 disposing）：Warn、不产 handler、不算 Error、不阻断加载。
        Assert.Empty(card.AssembledEffectHandlerNames);
        Assert.Contains(
            EffectAssemblyEntries(match, LogLevel.Warning),
            entry => entry.Keywords.Contains("warn") && entry.Message.Contains("cnt.disposalonly"));
        Assert.DoesNotContain(
            EffectAssemblyEntries(match, LogLevel.Error),
            entry => entry.Message.Contains("cnt.disposalonly"));
    }

    [Fact]
    public async Task Counter_CardEffect_Assembled_LeavesInfoTrace()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.trace" },
            snapshots: new[]
            {
                Snapshot("cnt.trace", MainTrigger(
                    "cnt.trace/main",
                    new EventPrefab("e1", assemblyKey: "cnt.trace.e1"),
                    new EventPrefab("e2", assemblyKey: "cnt.trace.e2"))),
            },
            handlers: new (string, Delegate)[] { ("cnt.trace.e1", Noop), ("cnt.trace.e2", Noop) });

        Assert.Single(card.AssembledEffectHandlerNames);
        var info = Assert.Single(EffectAssemblyEntries(match, LogLevel.Info));
        Assert.Contains("assembled", info.Keywords);
        Assert.Contains("cnt.trace", info.Keywords);
        Assert.Contains("折叠 2 个事件", info.Message);
    }

    [Fact]
    public async Task Counter_CardEffect_ResolveFailure_CardStillActivatable()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.badsig", "cnt.badview" },
            snapshots: new[]
            {
                // ① 签名不符（csx 入口类型是 CardEventView，但主触发器声明 CardTriggerView）＝实例化即隔离。
                Snapshot("cnt.badsig", new TriggerPrefab(
                    "t.main",
                    "cnt.badsig/main",
                    TriggerKind.Passive,
                    hooks: new[] { GameUpdates.CardPlayed },
                    events: new[]
                    {
                        new EventPrefab(
                            "e1",
                            csxSource: "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync"
                                       + " = (view, ctx, ct) => Task.CompletedTask;\n",
                            band: null),
                    },
                    viewTypeName: typeof(CardTriggerView).FullName!)),
                // ② 声明了其它视图类型（能实例化，但装配期按单条隔离）。
                Snapshot("cnt.badview", new TriggerPrefab(
                    "t.main",
                    "cnt.badview/main",
                    TriggerKind.Passive,
                    hooks: new[] { GameUpdates.CardPlayed },
                    events: new[]
                    {
                        new EventPrefab(
                            "e1",
                            csxSource: "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync"
                                       + " = (view, ctx, ct) => Task.CompletedTask;\n",
                            band: null),
                    },
                    viewTypeName: "Orc.Cards.CardEventView")),
            },
            handlers: null);

        // 装载不中断：两条都缺席、都有 Error 痕迹（签名不符在实例化即隔离，视图类型不符在装配期隔离）。
        Assert.Empty(card.AssembledEffectHandlerNames);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("cnt.badsig"));
        Assert.Contains(
            EffectAssemblyEntries(match, LogLevel.Error),
            entry => entry.Keywords.Contains("viewType") && entry.Message.Contains("cnt.badview"));

        // 效果缺席但卡仍可正常激活（R1 第三条）。
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(card)).Status);
        Assert.True(card.GetData<CounterActivationData>().IsActive);
    }

    // ---------- S3：挂载拆分 ----------

    [Fact]
    public async Task Counter_CardEffect_MainTriggerNotMounted_DisposalMounted()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.dispose" },
            snapshots: new[]
            {
                Snapshot("cnt.dispose", MainTrigger("cnt.dispose/main", new EventPrefab("e1", assemblyKey: "cnt.dispose.e1"))),
            },
            handlers: new (string, Delegate)[] { ("cnt.dispose.e1", Noop) });

        var effect = Assert.Single(card.Effects);
        Assert.True(effect.IsMounted); // IsMounted 照常置位（完整销毁链的前提）
        Assert.DoesNotContain("cnt.dispose/main", match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));
        Assert.Contains(
            match.Engine.Bus.GetSubscribers(Updates.CardDestroyed),
            name => name.StartsWith("cnt.dispose", StringComparison.Ordinal)); // disposing 已挂
    }

    [Fact]
    public async Task MountPassiveEffects_DefaultBehavior_Unchanged()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        match.Engine.Prefabs.RegisterHandler("cnt.default.e1", Noop);
        var snapshot = Snapshot(
            "cnt.default", MainTrigger("cnt.default/main", new EventPrefab("e1", assemblyKey: "cnt.default.e1")));
        match.Engine.Prefabs.RegisterPrefab(snapshot);

        // 默认参数（true）：非反制卡装载路径照常挂主触发器（Add 即装载），重复 MountPassiveEffects 幂等。
        var instantiated = DynamicEffectFactory.Instantiate(match.Engine, snapshot, "cnt.default");
        Assert.True(instantiated.Success, instantiated.Error);
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        unit.AddEffect(instantiated.Effect!);

        Assert.True(instantiated.Effect!.IsMounted);
        Assert.Contains("cnt.default/main", match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));

        unit.MountPassiveEffects(); // 默认 true：已装载＝幂等跳过（不重复挂载、不抛错）
        Assert.True(instantiated.Effect!.IsMounted);
        Assert.Single(
            match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed),
            name => name == "cnt.default/main");

        // 反制卡装载路径（false）：事后以默认参数兜底亦不会回头挂主触发器（已装载＝幂等跳过）。
        var (counterMatch, counterCard) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.frozen" },
            snapshots: new[]
            {
                Snapshot("cnt.frozen", MainTrigger("cnt.frozen/main", new EventPrefab("e1", assemblyKey: "cnt.frozen.e1"))),
            },
            handlers: new (string, Delegate)[] { ("cnt.frozen.e1", Noop) });

        counterCard.MountPassiveEffects();
        Assert.DoesNotContain("cnt.frozen/main", counterMatch.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));
    }

    [Fact]
    public async Task Counter_CardEffect_Destroyed_DisposesFully()
    {
        DisposalProbeEffect? probe = null;
        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: RegistryWithCodeEffect("effect.disposal", _ => probe!),
            extraDefinitions: new[] { CounterDefinition });
        await match.Initialize();
        probe = new DisposalProbeEffect("销毁探针", () => match.Engine.OrderFlow.Trigger);

        var card = (CounterCard)match.CardLibrary.Instantiate(CounterId);
        await card.LoadAsync(match.CurrentPlayer!);
        var effect = Assert.Single(card.Effects);
        Assert.True(effect.IsMounted);

        using var recorder = new UpdateRecorder(match.Engine);
        await match.Engine.DestroyCard(card);

        // 完整销毁链（不是「未装载」短路分支）：容器移除、卸载钩子恰一次、teardown 执行、effect.removed 恰一次。
        Assert.Empty(card.Effects);
        Assert.Equal(1, probe.UnmountCalls);
        Assert.Equal(1, probe.TeardownCalls);
        Assert.Equal(1, recorder.Types.Count(type => type == Updates.EffectRemoved));
        Assert.False(effect.IsMounted);

        // injects 撤销：注入目标不再执行该 handler。
        var (source, target) = CreateFlowCards(match.Engine);
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(0, probe.HandlerCalls);
    }

    // ---------- S5：已装配集合可观测面 ----------

    [Fact]
    public async Task Counter_AssembledHandlers_ObservableBeforeActivation()
    {
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.a", "cnt.b" },
            snapshots: new[]
            {
                Snapshot("cnt.a", MainTrigger("cnt.a/main", new EventPrefab("e1", assemblyKey: "cnt.a.e1"))),
                Snapshot("cnt.b", MainTrigger("cnt.b/main", new EventPrefab("e1", assemblyKey: "cnt.b.e1"))),
            },
            handlers: new (string, Delegate)[] { ("cnt.a.e1", Noop), ("cnt.b.e1", Noop) });

        // 未激活：已装配（2）但未注册（0）。
        Assert.Equal(new[] { "cnt.a", "cnt.b" }, card.AssembledEffectHandlerNames);
        Assert.Empty(card.RegisteredEffectHandlerNames);

        // 激活：两者逐项相等（同序）。
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(card)).Status);
        Assert.Equal(card.AssembledEffectHandlerNames, card.RegisteredEffectHandlerNames);

        // 取消：注册复归为空、装配不变。
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(card)).Status);
        Assert.Empty(card.RegisteredEffectHandlerNames);
        Assert.Equal(new[] { "cnt.a", "cnt.b" }, card.AssembledEffectHandlerNames);
    }

    // ---------- S6：激活中卡被销毁的清理 ----------

    [Fact]
    public async Task Counter_DestroyedWhileActive_UnregistersHandlers()
    {
        var calls = 0;
        var (match, card) = await CreateCounterAsync(
            prefabIds: new[] { "cnt.active" },
            snapshots: new[]
            {
                Snapshot("cnt.active", MainTrigger("cnt.active/main", new EventPrefab("e1", assemblyKey: "cnt.active.e1"))),
            },
            handlers: new (string, Delegate)[]
            {
                ("cnt.active.e1", (Func<CardTriggerView, Context, CancellationToken, Task>)((_, _, _) =>
                {
                    calls++;
                    return Task.CompletedTask;
                })),
            });

        var player = match.CurrentPlayer!;
        var pointsBefore = player.Points;
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.UseCounterAsync(card)).Status);
        Assert.Single(card.RegisteredEffectHandlerNames);
        Assert.Equal(pointsBefore - card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost), player.Points);

        // 激活中被销毁（如满手爆牌）。
        await match.Engine.DestroyCard(card);

        Assert.Empty(card.RegisteredEffectHandlerNames);
        Assert.False(card.GetData<CounterActivationData>().IsActive);
        Assert.Equal(pointsBefore - card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost), player.Points); // 不退费

        // Counter 段不再残留其 handler。
        var (source, target) = CreateFlowCards(match.Engine);
        await match.Engine.OrderFlow.ExecuteAsync(source, (ICastAction?)null, target, 1);
        Assert.Equal(0, calls);

        // 销毁后不可再激活：返回**结果对象**（Failed + CounterUnavailable），不抛异常。
        // 理由：PlayManager 的契约是「装配性错误＝异常；玩家动作级失败＝结果对象」——「点一张已经没了的卡」
        // 属后者；此前的异常来自 CounterUseJudicator 的「载荷须为存活卡引用」fail-fast，本不该由玩家动作触发。
        var refused = await match.PlayManager.UseCounterAsync(card);
        Assert.Equal(PlayResultStatus.Failed, refused.Status);
        Assert.Equal(PlayFailureReason.CounterUnavailable, refused.FailureReason);
        Assert.False(card.GetData<CounterActivationData>().IsActive);
        Assert.Empty(card.RegisteredEffectHandlerNames);
    }

    // ---------- S8：官方样例的语义归属 ----------

    [Fact]
    public async Task SampleCounterCard_AssemblesWithoutIsolation()
    {
        var source = Path.Combine(RepoRoot(), "samples", "card-pack-ai", "cards", "ai.sample.counter.card.json");
        Assert.True(File.Exists(source), source);

        var directory = Path.Combine(Path.GetTempPath(), "orc-counter-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(source, Path.Combine(directory, "ai.sample.counter.card.json"));
            var loaded = CardDataLoader.LoadDirectory(directory);
            Assert.Empty(loaded.Failures);

            var registry = new CardEffectRegistry();
            foreach (var (cardId, prefabIds) in loaded.EffectDeclarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            var match = CommandTestKit.CreateCommandMatch(
                effectRegistry: registry, extraDefinitions: loaded.Definitions);
            foreach (var snapshot in loaded.Prefabs)
            {
                match.Engine.Prefabs.RegisterPrefab(snapshot);
            }

            await match.Initialize();

            var card = (CounterCard)match.CardLibrary.Instantiate("ai.sample.counter");
            await card.LoadAsync(match.CurrentPlayer!);

            // 不被隔离（无 Error）、不双触发（主触发器未挂总线）、不静默失效（handler 面就绪）。
            Assert.Equal(new[] { "ai.sample.counter.effect" }, card.AssembledEffectHandlerNames);
            Assert.DoesNotContain(
                match.Engine.RootStream.Entries,
                entry => entry.Level == LogLevel.Error && entry.Message.Contains("ai.sample.counter"));
            Assert.DoesNotContain(
                "AI 样例·反制（空效果）",
                match.Engine.Bus.GetSubscribers(GameUpdates.CardPlayed));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- 夹具 ----------

    private static readonly Func<CardTriggerView, Context, CancellationToken, Task> Noop
        = (_, _, _) => Task.CompletedTask;

    private static Func<CardTriggerView, Context, CancellationToken, Task> RecordEvent(
        List<(string Event, Context Ctx)> log, string name)
        => (_, ctx, _) =>
        {
            log.Add((name, ctx));
            return Task.CompletedTask;
        };

    private static CardDefinitionEntry CounterDefinition => new(
        CounterId,
        new CardDefinition(
            "装配反制", deployCost: 0, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard));

    private static CardDefinitionEntry OrderDefinition => new(
        OrderId,
        new CardDefinition(
            "装配指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));

    private static CardDefinitionEntry UnitDefinition => new(
        UnitId,
        new CardDefinition(
            "装配单位", deployCost: 1, operateCost: 1, attack: 2, defense: 3,
            CardCategory.Unit, faction: Faction.Germany, rarity: Rarity.Standard));

    private static EffectSnapshot Snapshot(
        string prefabId,
        TriggerPrefab main,
        IEnumerable<TriggerPrefab>? others = null,
        IEnumerable<WrapperPrefab>? wrappers = null)
        => new(new EffectPrefab(prefabId, main, others, wrappers));

    private static TriggerPrefab MainTrigger(string stableKey, params EventPrefab[] events)
        => new(
            "t.main",
            stableKey,
            TriggerKind.Passive,
            hooks: new[] { GameUpdates.CardPlayed },
            events: events,
            viewTypeName: typeof(CardTriggerView).FullName!);

    private static CardEffectRegistry RegistryWithCodeEffect(string effectId, Func<CardBase, Effect> factory)
    {
        var registry = new CardEffectRegistry();
        registry.Register(effectId, factory);
        registry.Declare(CounterId, new[] { effectId });
        return registry;
    }

    private static async Task<(Match Match, CounterCard Card)> CreateCounterAsync(
        string[] prefabIds,
        EffectSnapshot[] snapshots,
        (string Key, Delegate Handler)[]? handlers,
        (string Id, Func<CardBase, Effect> Factory)[]? codeEffects = null)
    {
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(CounterId, prefabIds);
        if (codeEffects is not null)
        {
            foreach (var (id, factory) in codeEffects)
            {
                registry.Register(id, factory);
            }

            registry.Declare(CounterId, codeEffects.Select(item => item.Id));
        }

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry, extraDefinitions: new[] { CounterDefinition });
        await match.Initialize();

        if (handlers is not null)
        {
            foreach (var (key, handler) in handlers)
            {
                match.Engine.Prefabs.RegisterHandler(key, handler);
            }
        }

        foreach (var snapshot in snapshots)
        {
            match.Engine.Prefabs.RegisterPrefab(snapshot);
        }

        var card = (CounterCard)match.CardLibrary.Instantiate(CounterId);
        await card.LoadAsync(match.CurrentPlayer!);
        match.CurrentPlayer!.Hand.Add(card);
        return (match, card);
    }

    private static IReadOnlyList<LogEntry> EffectAssemblyEntries(Match match, LogLevel level)
        => match.Engine.RootStream.Entries
            .Where(entry => entry.Source == "反制效果装配" && entry.Level == level)
            .ToArray();

    private static (Card Source, Card Target) CreateFlowCards(LogicEngine engine)
    {
        var source = new Card(engine, "流程源");
        source.AddData(new HealthData { Hp = 5 });
        var target = new Card(engine, "流程标");
        target.AddData(new HealthData { Hp = 5 });
        return (source, target);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "samples", "card-pack-ai")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("未找到仓库根（samples/card-pack-ai）。");
    }

    // ---------- 测试效果 ----------

    /// <summary>销毁链探针：注入流程段 ＋ 登记 teardown；记录卸载/注入/teardown 调用。</summary>
    private sealed class DisposalProbeEffect : PassiveEffect
    {
        private readonly Func<Trigger<OrderFlowView>?> _target;

        public DisposalProbeEffect(string name, Func<Trigger<OrderFlowView>?> target)
            : base(name)
            => _target = target;

        public int UnmountCalls { get; private set; }

        public int TeardownCalls { get; private set; }

        public int HandlerCalls { get; private set; }

        protected override void OnMount()
        {
            AddTeardownRegistration(() => TeardownCalls += 1);
            if (_target() is { } target)
            {
                Inject(target, Name, OrderFlowBands.Resolve, OnFlowAsync);
            }
        }

        protected override void OnUnmount() => UnmountCalls += 1;

        private Task OnFlowAsync(OrderFlowView view, Context ctx, CancellationToken ct)
        {
            HandlerCalls += 1;
            return Task.CompletedTask;
        }
    }
}
