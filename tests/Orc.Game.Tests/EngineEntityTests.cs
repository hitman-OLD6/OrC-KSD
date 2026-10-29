using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Collections;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S7（引擎实体）的游戏层验收：内核基础预制体 <c>engine</c> ＋游戏层 <c>extends engine.game</c> 在同一
/// <c>engine</c> 根下跨层合并；39 个内建信号与 24 个引擎流程成员（9 个批次 2 成员 ＋ U1 的 13 个动作管道成员
/// ＋ U3 的 2 个指令流程成员）可枚举；旧显示名映射与预制体成员同源；<c>host#targeting</c> 在卡与 HQ 上都解析；
/// 对局内只有一个引擎实体。U4 起动作端点的自有边位次由 <see cref="EngineProcesses.ActionEndPositions"/> 统一标注。
/// </summary>
public sealed class EngineEntityTests
{
    private const string TestCardPrefab = "test.card";

    // ==================== ① 跨层合并（R1e 第 7 条） ====================

    /// <summary>内核 4 个信号＋游戏层 35 个信号同处 engine 根，解析对来源层无差别，来源层可标出。</summary>
    [Fact]
    public void EnginePrefab_MergesKernelAndGameSignals_UnderOneRoot()
    {
        var runtime = GameEnginePrefabs.CreateRuntime();

        Assert.Equal(39, runtime.Signals.Count);
        Assert.Equal(4, runtime.Signals.Count(static m => m.Layer == PrefabLayer.Kernel));
        Assert.Equal(35, runtime.Signals.Count(static m => m.Layer == PrefabLayer.Game));

        // 分属两层的两个成员都从同一个 engine 根解析出来（跨层合并，解析无差别）。
        Assert.Equal(PrefabLayer.Kernel, runtime.RequireSignal(Updates.CardDestroyed).Layer);
        Assert.Equal(PrefabLayer.Game, runtime.RequireSignal(GameUpdates.UnitPositionChanged).Layer);

        // 成员名零转换（含点号）：原 hooks 项原样可寻址。
        Assert.Equal(
            GameUpdates.UnitPositionChanged,
            MemberAddress.Create(AddressScope.Engine, GameUpdates.UnitPositionChanged).Member);
        Assert.Equal(GameUpdates.CardLoad, runtime.Signals.Single(static m => m.SignalTiming == SignalTiming.Load).Name);
    }

    /// <summary>引擎成员集合可由预制体完整枚举，且与 C# 常量表**同源**（R1j 的守护测试：集合相等）。</summary>
    [Fact]
    public void EngineMemberConstants_MatchPrefabMembers()
    {
        // 展开后的成员表（跨层合并：内核 4＋游戏层 35＋流程成员 24）。
        var merged = GameEnginePrefabs.CreateCatalog().Resolve(GameEnginePrefabs.PrefabId);
        var runtime = GameEnginePrefabs.CreateRuntime();

        var fromPrefab = merged.Members.Select(static m => m.Name).ToHashSet(StringComparer.Ordinal);
        var fromConstants = KernelSignals.Names
            .Concat(GameSignals.Names)
            .Concat(EngineProcesses.MemberNames)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(fromConstants.Count, fromPrefab.Count);
        Assert.True(fromPrefab.SetEquals(fromConstants));
        Assert.Equal(fromPrefab.Count, runtime.Engine.Members.Count);

        // 39 个信号＋25 个流程成员（9 个批次 2 成员 ＋ U1 的 13 个动作管道成员 ＋ U3 的 2 个指令流程成员
        // ＋ U4 拆出的死亡免死守卫 death.veto）。
        Assert.Equal(39, runtime.Signals.Count);
        Assert.Equal(25, runtime.Engine.Members.Count(static m => !m.IsSignal));
    }

    /// <summary>
    /// U4（S13 位次落地）＋ R1i band 修订（2026-10-29）：**21 个动作端点**逐条按
    /// <see cref="EngineProcesses.ActionEndPositions"/> 声明，自有边段的段名一律来自旧末步段
    /// （转发＝Commit／落地＝Apply／结算＝Resolve），因此"本成员的末步"恒晚于承载门禁与改写的段。
    /// <para>
    /// **断言对象沿革**：U1 按"触发端＝流程型／处理端＝落地型"分组；U4 换成"自有边是否为链上末步"（<c>last</c>）；
    /// 本批 <c>own-edge-position</c> 被 <c>band</c> 取代——"最前／最后"不再独立存在，只问"自有边落在哪一段"。
    /// 强度只增：逐条比对段序方案＋段名，并要求段名确实在该方案里（构造期即 fail-fast）。
    /// </para>
    /// </summary>
    [Fact]
    public void ActionPipelineMembers_AreDeclared_WithBandByKind()
    {
        var engine = GameEnginePrefabs.CreateRuntime().Engine;
        var table = EngineProcesses.ActionEndPositions;

        // 21 端、成员名唯一。
        Assert.Equal(21, EngineProcesses.ActionEndCount);
        Assert.Equal(EngineProcesses.ActionEndCount, table.Count);
        Assert.Equal(table.Count, table.Select(static e => e.Member).Distinct(StringComparer.Ordinal).Count());

        // 逐条：引擎实体上确有该成员，且段序方案／段名与表一致（漂移即红）。
        foreach (var end in table)
        {
            var member = engine.FindMember(end.Member);
            Assert.NotNull(member);
            Assert.Equal(end.Bands, member!.Bands!);
            Assert.Equal(end.Band, member.Band);
            Assert.Contains(end.Band, member.Bands!);
            Assert.True(member.BandIndexOf(member.Band) >= 0, "段名未在成员自己的方案里（构造期本应 fail-fast）");
            Assert.False(string.IsNullOrWhiteSpace(end.Role));
            Assert.False(string.IsNullOrWhiteSpace(end.OldTail));
            Assert.False(string.IsNullOrWhiteSpace(end.Rationale));
        }

        // 自有边都**不在首段**（转发／落地／结算是本成员的末步），因此"更早的段"承载门禁与改写。
        // 其中 20/21 恰落在方案末段（触发端 Commit／处理端 Apply）；order.cast 是例外：它的末步是
        // Resolve（结算），而 Finalize（收尾）是**另一个成员** order.finalize 的段——成员拆分的直接结果。
        foreach (var end in table)
        {
            Assert.True(end.Bands.ToList().IndexOf(end.Band) > 0, $"'{end.Member}' 的自有边落在首段，早于它的门禁/改写将无处安放");
            if (string.Equals(end.Member, EngineProcesses.OrderCast, StringComparison.Ordinal))
            {
                Assert.Equal(BandSchemes.Resolve, end.Band);
                continue;
            }

            Assert.Equal(end.Bands[^1], end.Band);
        }

        // 端清单＝引擎成员表 − 4 个非端成员（同源，防"表里有、预制体没有"）。
        var nonAction = new[]
        {
            EngineProcesses.Command,
            EngineProcesses.UnitMove,
            EngineProcesses.ActionBudget,
            EngineProcesses.DeathVeto, // 死亡处理端拆出的免死守卫：处理端的内部步骤，不是新的端
        };
        var expected = EngineProcesses.MemberNames.Except(nonAction, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(table.Select(static e => e.Member)), "动作端清单与引擎流程成员表不同源");

        // 非端成员：三个引擎级流程成员保持**单段方案**（null＝所有边同段、按声明序，等价旧默认 first）。
        foreach (var name in new[]
                 {
                     EngineProcesses.Command, EngineProcesses.UnitMove, EngineProcesses.ActionBudget,
                 })
        {
            var member = engine.FindMember(name)!;
            Assert.Null(member.Band);
            Assert.Null(member.Bands);
        }

        // 死亡免死守卫：处理端方案里的 Veto 段（拆成员前它是同一成员内的一道守卫）。
        var veto = engine.FindMember(EngineProcesses.DeathVeto)!;
        Assert.Equal(BandSchemes.ActionHandle, veto.Bands!);
        Assert.Equal(BandSchemes.Veto, veto.Band);
    }

    // ==================== ② 旧显示名映射（R1j） ====================
    /// <summary>4 个在用显示名的映射与 R1j 表逐条一致。</summary>
    [Fact]
    public void InjectTargetMapping_MatchesR1j()
    {
        Assert.Equal(
            new[]
            {
                ("伤害处理触发器", "engine#damage-apply"),
                ("单位攻击触发器", "engine#unit-attack"),
                ("行动额度触发器", "engine#action-budget"),
                ("指向触发器", "host#targeting"),
            },
            EngineProcesses.InjectTargets.Select(static m => (m.DisplayName, m.Address)));

        // 旧 band 一并留档：S11 按 Rewrite→wrap/before、Commit→wrap/after、无 band→外来挂载边转换。
        Assert.Equal("Rewrite", EngineProcesses.Find("伤害处理触发器")!.LegacyBand);
        Assert.Equal("Commit", EngineProcesses.Find("单位攻击触发器")!.LegacyBand);
        Assert.Null(EngineProcesses.Find("行动额度触发器")!.LegacyBand);
    }

    /// <summary>未被资产引用的具名触发器全部保留为成员（卡级 8＋引擎级 6），且都能在预制体里找到。</summary>
    [Fact]
    public void RetainedTriggers_AreDeclaredAsMembers()
    {
        Assert.Equal(14, EngineProcesses.Retained.Count);

        var catalog = GameEnginePrefabs.CreateCatalog();
        var engine = catalog.Resolve(GameEnginePrefabs.PrefabId);
        var unit = catalog.Resolve(CardProcesses.UnitPrefabId);

        foreach (var mapping in EngineProcesses.Retained.Where(static m => m.Scope == AddressScope.Engine))
        {
            Assert.True(engine.HasMember(mapping.MemberName), mapping.Address);
        }

        foreach (var mapping in EngineProcesses.Retained.Where(static m => m.Scope == AddressScope.Host))
        {
            Assert.True(unit.HasMember(mapping.MemberName), mapping.Address);
        }

        // 8 个卡级成员不能出现在引擎成员表里（成员表按实体类型分域）。
        Assert.DoesNotContain(CardProcesses.Deploy, engine.MemberNames);
    }

    // ==================== ③ host# 解析（R1j／R3h） ====================

    /// <summary><c>host#targeting</c> 在单位卡与 HQ 上都解析到各自宿主实例的 targeting 成员。</summary>
    [Fact]
    public void HostTargeting_ResolvesOnUnitAndHq()
    {
        // 注意：宿主类型集合里只要有一个类型没有该成员，host# 就是装载期错误（R3h 的"按允许的全部宿主类型校验"）。
        // 因此本场景不引入额外的宿主类型预制体——只验证卡与 HQ 这两个真实类型。
        var catalog = GameEnginePrefabs.CreateCatalog()
            .Add(new PrefabDefinition(
                "comp.targeting",
                EntityKind.Component,
                layer: PrefabLayer.Asset,
                requiresHost: HostKind.Any,
                members: new[] { new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Mountable) },
                mounts: new[] { new MountDeclaration("host#" + CardProcesses.Targeting, "probe") }));
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());

        var unit = runtime.CreateEntity(CardProcesses.UnitPrefabId, "unit");
        var hq = runtime.CreateEntity(CardProcesses.HqPrefabId, "hq");

        var onUnit = runtime.CreateEntity("comp.targeting", "fx@unit", unit);
        var onHq = runtime.CreateEntity("comp.targeting", "fx@hq", hq);

        runtime.Bind(onUnit);
        runtime.Bind(onHq);

        // 边落在**宿主实例**上（host# 按当前宿主解析，天然消歧）；边的声明方是各自的组件实例。
        var unitEdge = Assert.Single(unit.FindEdges(CardProcesses.Targeting)!.All);
        var hqEdge = Assert.Single(hq.FindEdges(CardProcesses.Targeting)!.All);
        Assert.Equal(CardProcesses.Targeting, unitEdge.Target.Member);
        Assert.Same(onUnit, unitEdge.Declaring);
        Assert.Same(onHq, hqEdge.Declaring);
    }

    /// <summary>引用某宿主类型独有的成员却声明 <c>requires-host: any</c>＝装载期拒绝（R3h）。</summary>
    [Fact]
    public void HostOnlyMember_OnAnyRequiresHost_IsRejected()
    {
        var catalog = HostScene(componentMount: "host#" + CardProcesses.Deploy);
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var unit = runtime.CreateEntity(CardProcesses.UnitPrefabId, "unit");
        var component = runtime.CreateEntity("comp.targeting", "fx", unit);

        var error = Assert.Throws<PrefabLoadException>(() => runtime.Bind(component));
        Assert.Contains(CardProcesses.OrderPrefabId, error.Message, StringComparison.Ordinal);
        Assert.Contains(CardProcesses.Deploy, error.Message, StringComparison.Ordinal);
    }

    // ==================== ④ 新效果无需 hooks 字符串／触发器壳（R6） ====================

    /// <summary>一个效果＝一个组件＋一条挂载声明；不写 hooks 字符串、不写主触发器壳即可生效。</summary>
    [Fact]
    public async Task NewEffect_RunsWithoutHooksStringOrTriggerShell()
    {
        var trace = new List<string>();
        var catalog = GameEnginePrefabs.CreateCatalog()
            .Add(new PrefabDefinition(
                TestCardPrefab,
                EntityKind.Card,
                layer: PrefabLayer.Asset,
                hosts: HostKind.Unit,
                components: new[] { "effect.kit" }))
            .Add(new PrefabDefinition(
                "effect.kit",
                EntityKind.Component,
                layer: PrefabLayer.Asset,
                members: new[]
                {
                    new MemberDefinition(
                        "apply",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        ownEdges: new[] { OwnTrace(trace, "applied") }),
                },
                mounts: new[] { new MountDeclaration("engine#" + GameUpdates.CardLoad, "apply") },
                activeWhen: ActiveWhen.Loaded));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry(), GameEnginePrefabs.PrefabId);
        var host = runtime.CreateEntity(TestCardPrefab, "host");
        var effect = runtime.CreateEntity("effect.kit", "fx", host);
        runtime.Bind(effect);

        await runtime.EmitAsync(GameUpdates.CardLoad);

        Assert.Equal(new[] { "applied" }, trace);
    }

    // ==================== ⑤ 对局级单例（R6b） ====================

    /// <summary>对局持有唯一引擎实体；不能再造第二个。</summary>
    [Fact]
    public void Match_ExposesSingleEngineRuntime()
    {
        var match = new Match(
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions(),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true });

        Assert.Equal(39, match.EngineRuntime.Signals.Count);
        Assert.Equal(EntityKind.Engine, match.EngineRuntime.Engine.EntityKind);
        Assert.True(match.EngineRuntime.IsSignal(GameUpdates.CardLoad));
        Assert.Throws<InvalidOperationException>(
            () => match.EngineRuntime.CreateEntity(GameEnginePrefabs.PrefabId, "engine#2"));
    }

    /// <summary>
    /// 共存边界（A2 后的新口径，取代批次 3 的"生产仍走旧总线"绊线）：
    /// <para>
    /// ①**正向**（S8 的接管点）：组件装配建立的边在信号上确实存在、且带 <c>framework:disposal</c> 来源；
    /// ②**绊线（A2）**：生产发射**只有一个漏斗**——<c>engine.Emit</c> → <c>SignalBridge.EmitAsync</c> →
    ///    <c>engine#&lt;信号&gt;</c> 成员；除漏斗本体外，src 里没有第二处 <c>EmitAsync</c> 调用点
    ///    （否则会有"绕过外部观察者／更新条目"的第二条发射路）。
    /// ③**绊线（b5）**：生产代码不再直接调用总线——`Bus` 类型已删除（`src/Orc/Core/Bus.cs` 不存在），
    ///    且全 src 无 `.Bus.` 调用形态；A2 的双写期口径（"只剩门面定义"）由本轮的"门面也删掉"收口。
    /// </para>
    /// <para>
    /// ②③替换的是批次 3 的 `Assert.Empty(offenders)`（当时守"生产不接新面"）；A2 把方向反过来，
    /// 守的是"生产只有新面、且只有一条"。断言强度不变（都是全量源码扫描），方向由本轮的退出条件决定。
    /// </para>
    /// </summary>
    [Fact]
    public void ComponentEdgesAreLive_AndProductionEmitsThroughEngineSignals()
    {
        // ① 正向：组件装配 → 边已建立（作者边 + 框架注销边），审查面可见。
        var log = new List<string>();
        var catalog = GameEnginePrefabs.CreateCatalog().Add(new PrefabDefinition(
            "comp.probe",
            EntityKind.Component,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Mountable, ownEdges: new[] { OwnTrace(log, "probe") }),
            },
            mounts: new[] { new MountDeclaration($"engine#{GameUpdates.CardDamaged}", "probe") }));
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry(), GameEnginePrefabs.PrefabId);
        var card = runtime.CreateEntity(CardProcesses.UnitPrefabId, "单位卡");
        runtime.AddComponent(card, "comp.probe", "探针");

        var edges = runtime.Engine.EdgesFor(GameUpdates.CardDamaged).Snapshot();
        Assert.Single(edges);
        Assert.Equal(EdgeSource.Explicit, edges[0].Source);

        var disposal = runtime.Engine.EdgesFor(Updates.CardDestroyed).Snapshot();
        Assert.Single(disposal);
        Assert.Equal(EdgeSource.FrameworkDisposal, disposal[0].Source);

        // ② 绊线（A2）：EmitAsync 只允许出现在漏斗（LogicEngine.Emit 的唯一转发点 ＋ 桥本体）与成员面定义。
        var root = Path.GetFullPath(Path.Combine(RepoRoot(), "src"));
        var allowed = new[]
        {
            Path.Combine("Orc", "Core", "LogicEngine.cs"),
            Path.Combine("Orc", "Core", "SignalBridge.cs"),
            Path.Combine("Orc", "Core", "Model", "Signals.cs"),
        };

        var emitOffenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !allowed.Any(suffix => file.EndsWith(suffix, StringComparison.Ordinal)))
            .Where(file => File.ReadAllLines(file)
                .Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(static line => !line.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Any(line => line.Contains(".EmitAsync(", StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(emitOffenders);

        // ③ 绊线（b5）：总线类型已删除，且生产源码里没有任何 `.Bus.` 调用形态。
        Assert.False(
            File.Exists(Path.Combine(root, "Orc", "Core", "Bus.cs")),
            "总线门面应已删除（b5：生产执行只剩 engine#<信号> 一条路）。");

        var busOffenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllLines(file)
                .Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(static line => !line.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .Any(line => line.Contains(".Bus.", StringComparison.Ordinal)
                             || line.Contains("Bus.Mount(", StringComparison.Ordinal)
                             || line.Contains("Bus.Emit(", StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(busOffenders);
        Assert.Empty(log); // 装配本身不执行业务逻辑（初始化期零执行，R6）
    }

    private static string RepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", ".."));

    // ==================== 场景构件 ====================

    /// <summary>宿主场景：完整游戏层目录＋一个 <c>requires-host: any</c> 的组件预制体（挂点可参数化）。</summary>
    private static PrefabCatalog HostScene(string componentMount)
        => GameEnginePrefabs.CreateCatalog()
            .Add(new PrefabDefinition(
                "comp.targeting",
                EntityKind.Component,
                layer: PrefabLayer.Asset,
                requiresHost: HostKind.Any,
                members: new[]
                {
                    new MemberDefinition("probe", ViewSpec.Empty, MemberVisibility.Mountable),
                },
                mounts: new[] { new MountDeclaration(componentMount, "probe") }));

    private static OwnedEdge OwnTrace(List<string> trace, string label)
        => new((KernelHandler)((_, _, _) =>
        {
            trace.Add(label);
            return Task.CompletedTask;
        }));
}
