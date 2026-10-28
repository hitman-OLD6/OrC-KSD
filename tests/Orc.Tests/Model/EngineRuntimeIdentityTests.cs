using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// 引擎运行时**身份唯一性**（R6b 的引擎级推论）：一个 <see cref="LogicEngine"/> 只能有一个引擎实体。
/// <para>
/// 回归背景（§二十九 根因）：<c>Emit</c> 与 <c>Attach</c> 各自读 <c>LogicEngine.SignalRuntime</c>；该属性有两级槽位
/// （外部赋的 <c>Model</c> ＋ 懒建的内核兜底）。若先挂边（走兜底）、后赋 <c>Model</c>，边留在兜底运行时的引擎实体上，
/// 而 <c>Emit</c> 落到新运行时——更新条目照写、订阅者一次都不执行（静默失效，最坏的一类退化）。
/// 本组用例把"身份不可替换"钉成契约，并给出生产形态（v4 组件 <c>mounts: engine#&lt;信号&gt;</c>）的端到端正向用例。
/// </para>
/// </summary>
public class EngineRuntimeIdentityTests
{
    private const string EnginePrefab = "engine.identity-test";
    private const string TurnStart = "turn.start";

    /// <summary>记录型边（无视图：只验证"被执行到"）。</summary>
    private static OwnedEdge Trace(List<string> log, string name)
        => new((KernelHandler)((_, _, _) =>
        {
            log.Add(name);
            return Task.CompletedTask;
        }));

    /// <summary>含一个额外信号（<c>turn.start</c>）的引擎预制体目录（继承内核 <c>engine</c>）。</summary>
    private static PrefabCatalog CatalogWithTurnStart()
        => KernelSignals.CreateCatalog().Add(new PrefabDefinition(
            EnginePrefab,
            EntityKind.Engine,
            extends: KernelSignals.PrefabId,
            layer: PrefabLayer.Game,
            members: new[]
            {
                SignalMember.Create(TurnStart, SignalTiming.Any, layer: PrefabLayer.Game),
            }));

    // ==================== ① 根因守护：边未消失、订阅者被执行 ====================

    /// <summary>
    /// **根因回归**：先挂边、再执行引擎成员（测试里 <c>KernelTestKit</c> 会补建 <c>Model</c>）——
    /// 订阅者必须照旧执行。修复前 <c>Emit</c> 落在一个新建的运行时上，边留在旧实体上（trace 恒空）。
    /// </summary>
    [Fact]
    public async Task Subscribers_AttachedBeforeMemberRegistration_StillRun_OnEmit()
    {
        var engine = new LogicEngine();
        var log = new List<string>();
        SignalTestHelpers.Subscribe(engine, "outer", "P1", log);

        await KernelTestKit.RunAsync(
            engine,
            KernelTestKit.Member("发射者", new OwnedEdge((KernelHandler)(async (_, _, _) =>
            {
                log.Add("E");
                await engine.Emit("outer");
            }))));

        Assert.Equal(new[] { "E", "P1" }, log); // 成员内的发射执行到 P1
    }

    /// <summary>
    /// **v4 组件端到端**：<c>mounts: engine#turn.start</c> 的组件装配后，<c>Emit("turn.start")</c>
    /// 必须真正调用到该组件的成员 handler（这正是 v4 组件的唯一生效通路：
    /// <c>SignalBridge.Attach</c>／<c>Bind</c> → 边表 → <c>Emit</c> 执行成员边）。
    /// </summary>
    [Fact]
    public async Task Mounted_V4Component_On_TurnStart_RunsMemberHandler_On_Emit()
    {
        var log = new List<string>();

        var catalog = CatalogWithTurnStart()
            .Add(new PrefabDefinition(
                "card.identity-probe",
                EntityKind.Card,
                layer: PrefabLayer.Asset,
                hosts: HostKind.Unit,
                components: new[] { "effect.on-turn" }))
            .Add(new PrefabDefinition(
                "effect.on-turn",
                EntityKind.Component,
                layer: PrefabLayer.Asset,
                requiresHost: HostKind.Any,
                members: new[]
                {
                    new MemberDefinition(
                        "onTurn",
                        ViewSpec.Empty,
                        MemberVisibility.Mountable,
                        ownEdges: new[] { Trace(log, "onTurn") }),
                },
                mounts: new[] { new MountDeclaration("engine#" + TurnStart, "onTurn") },
                activeWhen: ActiveWhen.Loaded));

        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry(), EnginePrefab);
        var engine = new LogicEngine();
        engine.Model = runtime; // 装配期先定身份（生产同序）

        var card = runtime.CreateEntity("card.identity-probe", "单位卡");
        runtime.AddComponent(card, "effect.on-turn", "回合效果");

        // 边确实落在引擎实体的该信号上（装配成功）
        Assert.Single(runtime.Engine.EdgesFor(TurnStart).Snapshot());

        await engine.Emit(TurnStart);

        Assert.Equal(new[] { "onTurn" }, log);
    }

    // ==================== ② 契约：身份不可替换（fail-fast，不静默丢边） ====================

    /// <summary>
    /// 兜底运行时已承载边 → 赋另一个运行时＝**拒绝**：替换会让边挂在旧引擎实体上、<c>Emit</c> 静默不执行。
    /// 拒绝之后原订阅仍然有效（引擎没有进入半替换状态）。
    /// </summary>
    [Fact]
    public async Task Model_Swap_AfterEdgesExist_IsRejected_AndExistingEdgesSurvive()
    {
        var engine = new LogicEngine();
        var log = new List<string>();
        SignalTestHelpers.Subscribe(engine, "u", "T", null, (ctx, ct) =>
        {
            log.Add("T");
            return Task.CompletedTask;
        });

        var replacement = new OrchestrationRuntime(
            KernelSignals.CreateCatalog(), new HandlerRegistry());

        var error = Assert.Throws<InvalidOperationException>(() => engine.Model = replacement);
        Assert.Contains("不可替换", error.Message, StringComparison.Ordinal);

        await engine.Emit("u");
        Assert.Equal(new[] { "T" }, log);
    }

    /// <summary>
    /// 兜底运行时**未承载任何状态**（仅被审查图导出物化）→ 允许折叠成正式运行时：
    /// 不丢任何东西，因此不必让"先看审查图、后装配"这种无害顺序也失败。
    /// </summary>
    [Fact]
    public void Model_Swap_BeforeRuntimeCarriesState_IsAllowed()
    {
        var engine = new LogicEngine();
        _ = engine.BuildAuditGraph(); // 物化兜底运行时（零边、零运行期成员）

        var runtime = new OrchestrationRuntime(CatalogWithTurnStart(), new HandlerRegistry(), EnginePrefab);
        engine.Model = runtime;

        Assert.Same(runtime, engine.Model);
    }
}
