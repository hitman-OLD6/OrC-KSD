using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Script;
using Xunit;
using Xunit.Abstractions;

namespace Orc.Game.Tests;

/// <summary>
/// 效果快照文件级往返（批 2）：数据等值（全字段逐层——csx/assemblyKey 两形态＋modings/injects 非空）、
/// 装载活性（经往返后注册面上 Instantiate 成功＋挂载状态断言；csx 真编译/assemblyKey 真注册 handler）、
/// 既有样本保真副本链（读入→写出→读回等值＋首次写出后字节级收敛）与仓库样本直读增强（只读；不可读＝可辨识跳过）、
/// 主动效果建议级（往返→实例化→CastAsync 成功）。无 outputs 依赖；样本内嵌自含；临时目录（测后清理）。
/// </summary>
public class PrefabRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public PrefabRoundTripTests(ITestOutputHelper output) => _output = output;

    // ─────────────────────────────────────────────────────────────────────────
    // ① 数据等值：文件级往返（写→读→注册取回）逐层逐字段。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Csx_Snapshot_Round_Trips_With_Full_Field_Equality()
        => AssertFileRoundTripPreservesAllFields(RichCsxSnapshot("e2e.rt.eq.csx"));

    [Fact]
    public void AssemblyKey_Snapshot_Round_Trips_With_Full_Field_Equality()
        => AssertFileRoundTripPreservesAllFields(RichAssemblySnapshot("e2e.rt.eq.assembly"));

    // ─────────────────────────────────────────────────────────────────────────
    // ② 装载活性：经往返后注册面上 Instantiate 成功＋挂载状态（两形态各 ≥1 例）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssemblyKey_Snapshot_After_File_Round_Trip_Instantiates_And_Mounts()
    {
        const string prefabId = "e2e.rt.assembly";
        const string hook = "e2e.rt.assembly.hook";
        var fired = 0;

        var engine = new LogicEngine();
        engine.Prefabs.RegisterHandler(prefabId, (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) =>
        {
            fired++;
            return Task.CompletedTask;
        }));

        var directory = NewTempDir();
        try
        {
            var original = new EffectSnapshot(new EffectPrefab(
                prefabId,
                new TriggerPrefab(
                    "t.main",
                    "样本·装配",
                    TriggerKind.Passive,
                    hooks: new[] { hook },
                    events: new[] { new EventPrefab("e1", assemblyKey: prefabId) })));

            var save = PrefabWriter.SaveDirectory(directory, new[] { original });
            Assert.Empty(save.Failures);

            // 经文件往返后的注册面（LoadDirectory 装载注册）。
            var load = engine.Prefabs.LoadDirectory(directory);
            Assert.Equal(1, load.Loaded);
            Assert.Empty(load.Failures);
            Assert.True(engine.Prefabs.TryGetPrefab(prefabId, out _));

            // 注册面上真执行 Instantiate：成功 ＋ 动态效果类型 ＋ 装载计划非空（主触发器挂载步在场）。
            var instantiation = DynamicEffectFactory.InstantiateRegistered(engine, prefabId);
            Assert.True(instantiation.Success, instantiation.Error);
            var effect = Assert.IsType<DynamicPassiveEffect>(instantiation.Effect);
            Assert.NotNull(instantiation.Plan);
            Assert.Contains(instantiation.Plan!.Steps, step => step.Kind == EffectLoadStepKind.MountMainTrigger);

            // 挂载（公开装载面「Add 即装载」）＋ 挂载状态断言（IsMounted、总线 hooks）。
            var card = new UnitCard(engine, ProbeDefinition("装配探针"));
            card.AddEffect(effect);
            Assert.True(effect.IsMounted);
            Assert.Contains(engine.Bus.EnumerateHooks(), entry => entry.Hook == hook);

            // 增强：Emit＋trace（挂载面接线辅助证据——手动 Emit 仅辅助，非唯一驱动）。
            await engine.Emit(hook);
            Assert.Equal(1, fired);
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    [Fact]
    public async Task Csx_Snapshot_After_File_Round_Trip_Compiles_Instantiates_And_Mounts()
    {
        const string prefabId = "e2e.rt.csx";
        const string hook = "e2e.rt.csx.hook";
        const string signal = "e2e.rt.csx.observed";

        var engine = new LogicEngine();
        engine.ScriptEvaluator = new CSharpScriptEvaluator(); // csx 真编译（satellite 求值器）。

        var observed = 0;
        using var subscription = engine.Subscribe((type, _, _) =>
        {
            if (string.Equals(type, signal, StringComparison.Ordinal))
            {
                observed++;
            }

            return Task.CompletedTask;
        });

        var directory = NewTempDir();
        try
        {
            var original = new EffectSnapshot(new EffectPrefab(
                prefabId,
                new TriggerPrefab(
                    "t.main",
                    "样本·csx",
                    TriggerKind.Passive,
                    hooks: new[] { hook },
                    events: new[] { new EventPrefab("e1", csxSource: CsxSignalSource) })));

            var save = PrefabWriter.SaveDirectory(directory, new[] { original });
            Assert.Empty(save.Failures);

            var load = engine.Prefabs.LoadDirectory(directory);
            Assert.Equal(1, load.Loaded);
            Assert.Empty(load.Failures);

            var instantiation = DynamicEffectFactory.InstantiateRegistered(engine, prefabId);
            Assert.True(instantiation.Success, instantiation.Error);
            var effect = Assert.IsType<DynamicPassiveEffect>(instantiation.Effect);
            Assert.NotNull(instantiation.Plan);

            var card = new UnitCard(engine, ProbeDefinition("csx 探针"));
            card.AddEffect(effect);
            Assert.True(effect.IsMounted);
            Assert.Contains(engine.Bus.EnumerateHooks(), entry => entry.Hook == hook);

            // 真编译链走通 ＋ 行为证据：csx handler 经 ctx.Engine 发射信号（订阅面观察）。
            await engine.Emit(hook);
            Assert.Equal(1, observed);
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ③ 既有样本保真副本链（内嵌常量恒跑）：读入→写出→读回等值＋首次写出后收敛稳定。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Embedded_Sample_Copies_Round_Trip_Semantically_Equal_And_Converged()
    {
        var samples = new[]
        {
            ("keyword.blitz.deploy-set.prefab.json", SampleAssemblyKeyCopy),
            ("keyword.blitz.deploy-set.csx.prefab.json", SampleCsxCopy),
        };

        foreach (var (name, json) in samples)
        {
            var original = PrefabJson.Deserialize(json);
            var firstDirectory = NewTempDir();
            var secondDirectory = NewTempDir();
            try
            {
                var first = PrefabWriter.SaveFile(firstDirectory, original);
                Assert.Empty(first.Failures);
                var written1 = File.ReadAllText(first.Succeeded[0].File);

                var readBack = PrefabJson.Deserialize(written1);
                AssertSnapshotEqual(original, readBack);

                var second = PrefabWriter.SaveFile(secondDirectory, readBack);
                Assert.Empty(second.Failures);
                var written2 = File.ReadAllText(second.Succeeded[0].File);

                // 首次写出后收敛稳定（J1==J2 字节级幂等；允许相对原样本的文本规范化）。
                Assert.Equal(written1, written2);
            }
            finally
            {
                DeleteTempDir(firstDirectory);
                DeleteTempDir(secondDirectory);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ④ 仓库样本直读增强（只读兼容；不可读＝可辨识跳过）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Repository_Sample_Files_Converge_After_First_Write()
    {
        List<string> sampleFiles = new();
        var directory = KeywordPrefabLibrary.DefaultDirectory;
        if (Directory.Exists(directory))
        {
            sampleFiles.AddRange(Directory.EnumerateFiles(directory, "*.prefab.json", SearchOption.TopDirectoryOnly));
        }

        if (sampleFiles.Count == 0)
        {
            _output.WriteLine($"仓库样本不可读（程序集复制目录缺失或为空：{directory}）——增强项可辨识跳过。");
            return;
        }

        foreach (var file in sampleFiles)
        {
            var original = PrefabJson.Deserialize(File.ReadAllText(file));
            var firstDirectory = NewTempDir();
            var secondDirectory = NewTempDir();
            try
            {
                var first = PrefabWriter.SaveFile(firstDirectory, original);
                Assert.Empty(first.Failures);
                var readBack = PrefabJson.Deserialize(File.ReadAllText(first.Succeeded[0].File));
                Assert.Equal(
                    PrefabJson.Serialize(original),
                    PrefabJson.Serialize(readBack));

                var second = PrefabWriter.SaveFile(secondDirectory, readBack);
                Assert.Empty(second.Failures);
                Assert.Equal(
                    File.ReadAllText(first.Succeeded[0].File),
                    File.ReadAllText(second.Succeeded[0].File));
            }
            finally
            {
                DeleteTempDir(firstDirectory);
                DeleteTempDir(secondDirectory);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ⑤ 建议级（非硬性）：主动效果——往返→实例化→CastAsync 成功。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Active_Snapshot_Round_Trips_And_CastAsync_Succeeds()
    {
        const string prefabId = "e2e.rt.active";
        const string signal = "e2e.rt.active.cast";

        var engine = new LogicEngine();
        engine.ScriptEvaluator = new CSharpScriptEvaluator();

        var casts = 0;
        using var subscription = engine.Subscribe((type, _, _) =>
        {
            if (string.Equals(type, signal, StringComparison.Ordinal))
            {
                casts++;
            }

            return Task.CompletedTask;
        });

        var directory = NewTempDir();
        try
        {
            var original = new EffectSnapshot(new EffectPrefab(
                prefabId,
                new TriggerPrefab(
                    "t.main",
                    "样本·主动",
                    TriggerKind.Active,
                    events: new[] { new EventPrefab("e1", csxSource: ActiveCsxSource) })));

            var save = PrefabWriter.SaveDirectory(directory, new[] { original });
            Assert.Empty(save.Failures);

            var load = engine.Prefabs.LoadDirectory(directory);
            Assert.Equal(1, load.Loaded);
            Assert.Empty(load.Failures);

            var instantiation = DynamicEffectFactory.InstantiateRegistered(engine, prefabId);
            Assert.True(instantiation.Success, instantiation.Error);
            var effect = Assert.IsType<DynamicActiveEffect>(instantiation.Effect);

            // 施放（CastAsync）成功 ＋ 行为证据（csx 经 ctx.Engine 发射信号）。
            var stream = await effect.CastAsync(engine);
            Assert.NotNull(stream);
            Assert.Equal(1, casts);

            // 往返保真补充（主动形态等值）。
            Assert.True(engine.Prefabs.TryGetPrefab(prefabId, out var readBack));
            AssertSnapshotEqual(original, readBack);
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：往返流程 / 等值比较 / 样本构造。
    // ─────────────────────────────────────────────────────────────────────────

    private static void AssertFileRoundTripPreservesAllFields(EffectSnapshot original)
    {
        var directory = NewTempDir();
        try
        {
            var save = PrefabWriter.SaveDirectory(directory, new[] { original });
            Assert.Empty(save.Failures);

            var engine = new LogicEngine();
            var load = engine.Prefabs.LoadDirectory(directory);
            Assert.Equal(1, load.Loaded);
            Assert.Empty(load.Failures);
            Assert.True(engine.Prefabs.TryGetPrefab(original.Root.Id, out var readBack));

            // 数据等值：逐层逐字段（id/version/触发器/事件/modings/injects 全字段）。
            AssertSnapshotEqual(original, readBack);

            // 再序列化比较（确定性——同序列化器同输入同输出）。
            Assert.Equal(PrefabJson.Serialize(original), PrefabJson.Serialize(readBack));
        }
        finally
        {
            DeleteTempDir(directory);
        }
    }

    private static void AssertSnapshotEqual(EffectSnapshot expected, EffectSnapshot actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        AssertEffectPrefabEqual(expected.Root, actual.Root);
    }

    private static void AssertEffectPrefabEqual(EffectPrefab expected, EffectPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Version, actual.Version);
        AssertTriggerEqual(expected.MainTrigger, actual.MainTrigger);

        Assert.Equal(expected.OtherTriggers.Count, actual.OtherTriggers.Count);
        for (var i = 0; i < expected.OtherTriggers.Count; i++)
        {
            AssertTriggerEqual(expected.OtherTriggers[i], actual.OtherTriggers[i]);
        }

        Assert.Equal(expected.Wrappers.Count, actual.Wrappers.Count);
        for (var i = 0; i < expected.Wrappers.Count; i++)
        {
            Assert.Equal(expected.Wrappers[i].TargetEventId, actual.Wrappers[i].TargetEventId);
            AssertEventEqual(expected.Wrappers[i].Handler, actual.Wrappers[i].Handler);
        }

        Assert.Equal(expected.Injects.Count, actual.Injects.Count);
        for (var i = 0; i < expected.Injects.Count; i++)
        {
            Assert.Equal(expected.Injects[i].TargetTriggerName, actual.Injects[i].TargetTriggerName);
            Assert.Equal(expected.Injects[i].BandName, actual.Injects[i].BandName);
            Assert.Equal(expected.Injects[i].EventId, actual.Injects[i].EventId);
            Assert.Equal(expected.Injects[i].Priority, actual.Injects[i].Priority);
        }

        Assert.Equal(expected.MountedHooks, actual.MountedHooks);
    }

    private static void AssertTriggerEqual(TriggerPrefab expected, TriggerPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.StableKey, actual.StableKey);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.ViewTypeName, actual.ViewTypeName);
        Assert.Equal(expected.Hooks, actual.Hooks);
        Assert.Equal(expected.Version, actual.Version);

        Assert.Equal(expected.Events.Count, actual.Events.Count);
        for (var i = 0; i < expected.Events.Count; i++)
        {
            AssertEventEqual(expected.Events[i], actual.Events[i]);
        }
    }

    private static void AssertEventEqual(EventPrefab expected, EventPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.EntryName, actual.EntryName);
        Assert.Equal(expected.CsxSource, actual.CsxSource);
        Assert.Equal(expected.AssemblyKey, actual.AssemblyKey);
        Assert.Equal(expected.Downstream, actual.Downstream);
        Assert.Equal(expected.Version, actual.Version);
    }

    private static CardDefinition ProbeDefinition(string name)
        => new(
            name,
            deployCost: 1,
            operateCost: 1,
            attack: 2,
            defense: 4,
            unitTypes: new[] { UnitType.Infantry },
            faction: Faction.Germany,
            rarity: Rarity.Standard);

    private const string SampleCsxHandler =
        "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;\n";

    private const string CsxSignalSource =
        "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n" +
        "{\n" +
        "    if (ctx.Engine is not null)\n" +
        "    {\n" +
        "        await ctx.Engine.Emit(\"e2e.rt.csx.observed\");\n" +
        "    }\n" +
        "};\n";

    private const string ActiveCsxSource =
        "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n" +
        "{\n" +
        "    if (ctx.Engine is not null)\n" +
        "    {\n" +
        "        await ctx.Engine.Emit(\"e2e.rt.active.cast\");\n" +
        "    }\n" +
        "};\n";

    private static EffectSnapshot RichCsxSnapshot(string id) => new(
        new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                "样本·csx 主",
                TriggerKind.Passive,
                hooks: new[] { "unit.deployed" },
                events: new[]
                {
                    new EventPrefab("e1", csxSource: SampleCsxHandler, downstream: new[] { "t.extra" }),
                }),
            otherTriggers: new[]
            {
                new TriggerPrefab(
                    "t.extra",
                    "样本·csx 其它",
                    TriggerKind.Passive,
                    hooks: new[] { "turn.start" },
                    events: new[] { new EventPrefab("e2", assemblyKey: id + ".extra") }),
            },
            wrappers: new[]
            {
                new WrapperPrefab("replace", "e1", null, new EventPrefab("m1", csxSource: SampleCsxHandler)),
            },
            injects: new[]
            {
                new InjectPrefab("DeployKeyword", "DeployBand", "e2", 7),
            }));

    private static EffectSnapshot RichAssemblySnapshot(string id) => new(
        new EffectPrefab(
            id,
            new TriggerPrefab(
                "t.main",
                "样本·装配 主",
                TriggerKind.Passive,
                hooks: new[] { "unit.deployed" },
                events: new[] { new EventPrefab("e1", assemblyKey: id, downstream: new[] { "t.extra" }) }),
            otherTriggers: new[]
            {
                new TriggerPrefab(
                    "t.extra",
                    "样本·装配 其它",
                    TriggerKind.Passive,
                    hooks: new[] { "turn.start" },
                    events: new[] { new EventPrefab("e2", csxSource: SampleCsxHandler) }),
            },
            wrappers: new[]
            {
                new WrapperPrefab("replace", "e1", null, new EventPrefab("m1", assemblyKey: id + ".moding")),
            },
            injects: new[]
            {
                new InjectPrefab("DeployKeyword", null, "e1", 2),
            }));

    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "orc-prefabrt-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempDir(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 既有样本保真副本（内嵌常量；由 KeywordPrefabs 样本文件生成——恒跑，无 outputs 依赖）。
    // ─────────────────────────────────────────────────────────────────────────

    private const string SampleAssemblyKeyCopy = """
{
  "schemaVersion": 2,
  "root": {
    "id": "keyword.blitz.deploy-set",
    "version": 1,
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "闪击·部署置位",
      "kind": "passive",
      "viewType": "Orc.Cards.CardEventView",
      "hooks": ["unit.deployed"],
      "events": [
        {
          "id": "onUnitDeployed",
          "entry": "HandleAsync",
          "assemblyKey": "keyword.blitz.deploy-set",
          "version": 1
        }
      ],
      "version": 1
    },
    "otherTriggers": [],
    "modings": [],
    "injects": []
  }
}
""";

    private const string SampleCsxCopy = """
{
  "schemaVersion": 2,
  "root": {
    "id": "keyword.blitz.deploy-set.csx",
    "version": 1,
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "闪击·部署置位（csx）",
      "kind": "passive",
      "viewType": "Orc.Cards.CardEventView",
      "hooks": ["unit.deployed"],
      "events": [
        {
          "id": "onUnitDeployed",
          "entry": "HandleAsync",
          "csx": "// 闪击·部署置位（csx 行为引用样本）：unit.deployed → Host==载荷单位 → 置位 CanMove/CanAttack。\nFunc<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n{\n    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host))\n    {\n        return Task.CompletedTask; // 非本单位部署：不处理（Host==载荷过滤）\n    }\n\n    if (deployed.TryGetData<Orc.Game.Cards.CommandData>(out var command))\n    {\n        command.CanMove = true; // 置位（覆盖部署初值 false/false；重复信号幂等）\n        command.CanAttack = true;\n    }\n\n    return Task.CompletedTask; // 无 CommandData（防御）：跳过、不抛错\n};\n",
          "version": 1
        }
      ],
      "version": 1
    },
    "otherTriggers": [],
    "modings": [],
    "injects": []
  }
}
""";
}
