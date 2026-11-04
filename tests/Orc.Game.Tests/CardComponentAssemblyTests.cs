using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;
using Orc.Game.Collections;
using Orc.Game.Model;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S8（组件实体）的游戏层验收：用**真实类型预制体**（<c>card.unit</c>／<c>card.hq</c>）承载组件实体——
/// 一张卡可携带多个组件且互不干扰、卡牌与 HQ 装配路径同一条、<c>host#</c> 共有成员校验、
/// 宿主销毁即组件卸载（逐句柄撤销）。
/// </summary>
public sealed class CardComponentAssemblyTests
{
    /// <summary>一张卡可携带多个组件，互不干扰（R3c）。</summary>
    [Fact]
    public async Task Card_CarriesMultipleComponents_Independently()
    {
        var log = new List<string>();
        var runtime = Runtime(log);
        var card = runtime.CreateEntity(CardProcesses.UnitPrefabId, "单位卡");

        var a = runtime.AddComponent(card, "comp.aura", "光环");
        var b = runtime.AddComponent(card, "comp.mark", "标记");

        Assert.Equal(2, runtime.ComponentsOf(card).Count);
        Assert.True(a.IsAssembled);
        Assert.True(b.IsAssembled);

        await runtime.EmitAsync(GameUpdates.CardDamaged, Payload(card));

        Assert.Equal(new[] { "aura", "mark" }, log); // 各自执行一次、互不覆盖
        Assert.Equal(2, runtime.BuildAuditGraph().EdgesBySource(EdgeSource.FrameworkDisposal).Count);

        // 卸载其一：另一条不受影响（组件注销不依赖宿主遍历成员表，R3c）。
        runtime.DisposeComponent(a);
        Assert.Single(runtime.ComponentsOf(card));
        Assert.True(b.IsAssembled);
        Assert.Single(runtime.BuildAuditGraph().EdgesBySource(EdgeSource.FrameworkDisposal));
    }

    /// <summary>卡牌与 HQ 装配路径同一条（R3d）：同一组件预制体挂到 <c>card.unit</c> 与 <c>card.hq</c> 都成立。</summary>
    [Fact]
    public void SameComponent_AssemblesOnUnitCardAndHq_ViaCommonMember()
    {
        var log = new List<string>();
        var runtime = Runtime(log);
        var card = runtime.CreateEntity(CardProcesses.UnitPrefabId, "单位卡");
        var hq = runtime.CreateEntity(CardProcesses.HqPrefabId, "总部");

        // comp.targeting 的 requires-host＝any，但只引用两者共有成员 host#targeting（R3h 的合法形态）。
        var onCard = runtime.AddComponent(card, "comp.targeting", "卡上指向");
        var onHq = runtime.AddComponent(hq, "comp.targeting", "总部指向");

        Assert.True(onCard.IsAssembled);
        Assert.True(onHq.IsAssembled);
        Assert.Equal(CardProcesses.HqPrefabId, onHq.Host.Type.Id);
        Assert.Equal(CardProcesses.UnitPrefabId, onCard.Host.Type.Id);
    }

    /// <summary>宿主销毁＝组件卸载（R3i）：组件实例与它的边全部消失。</summary>
    [Fact]
    public void HostDestroy_DisposesItsComponents()
    {
        var log = new List<string>();
        var runtime = Runtime(log);
        var card = runtime.CreateEntity(CardProcesses.UnitPrefabId, "单位卡");
        var a = runtime.AddComponent(card, "comp.aura", "光环");

        Assert.Single(runtime.BuildAuditGraph().EdgesBySource(EdgeSource.FrameworkDisposal));

        card.Destroy();

        Assert.True(a.IsDisposed);
        Assert.Empty(runtime.ComponentsOf(card));
        Assert.Empty(runtime.BuildAuditGraph().EdgesBySource(EdgeSource.FrameworkDisposal));
        Assert.DoesNotContain(card, runtime.Entities);
    }

    /// <summary>装配时点由组件自己的 <c>active-when</c> 决定（卡牌侧不参与决定）。</summary>
    [Fact]
    public void ActiveWhen_IsDecidedByComponentDeclaration()
    {
        var log = new List<string>();
        var runtime = Runtime(log);
        var card = runtime.CreateEntity(CardProcesses.UnitPrefabId, "单位卡");

        var handComponent = runtime.ConstructComponent(card, "comp.loaded", "在手生效");
        var fieldComponent = runtime.ConstructComponent(card, "comp.aura", "入场生效");

        Assert.Equal(1, runtime.AssembleComponents(card, ActiveWhen.Loaded));
        Assert.True(handComponent.IsAssembled);
        Assert.False(fieldComponent.IsAssembled);

        Assert.Equal(1, runtime.AssembleComponents(card, ActiveWhen.Placed));
        Assert.True(fieldComponent.IsAssembled);
    }

    // ==================== ② 装配失败的隔离口径（真实 MountComponent 链路） ====================

    /// <summary>
    /// 坏组件经**真实装载链路**（<c>LoadAsync</c> → <c>LoadModelComponents</c> → 私有
    /// <c>MountComponent</c> 的 catch）装配失败：日志说「隔离：该组件不生效」，行为必须与之一致——
    /// 边通道与包装通道**都不生效**，且失败不产生任何业务事件（不经 <c>Dispose</c> ⇒ 无 <c>effect.removed</c>）。
    /// <para>
    /// 不 mock／不反射：失败只能在 <c>AddComponent</c> 抛出时被那条 catch 收下，日志存在即证明走的是该链路。
    /// 「同一包装在装配成功时会改写数值」由内核重试用例对照证明，本用例不重复。
    /// </para>
    /// </summary>
    [Fact]
    public async Task BrokenComponent_IsIsolated_OnRealMountPath_AndLogMatchesBehaviour()
    {
        var log = new List<string>();
        var handlers = new HandlerRegistry();
        handlers.Register("h.broken.rewrite", (view, _, _) =>
        {
            log.Add("wrap");
            ((ViewCarrier)view).Set("Amount", 0); // damage-apply 的可写字段
            return Task.CompletedTask;
        });

        var catalog = GameEnginePrefabs.CreateCatalog()
            .Add(new PrefabDefinition(
                BrokenComponentId,
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[] { Member("onTurnStart", log) },
                mounts: new[] { new MountDeclaration($"engine#{GameUpdates.TurnStart}", "onTurnStart") },
                wraps: new[]
                {
                    WrapDeclaration.ForMember(
                        $"engine#{EngineProcesses.DamageApply}", WrapperPhase.Before, "h.broken.rewrite"),
                },
                disposalSignal: "no.such.signal"));

        var runtime = new OrchestrationRuntime(catalog, handlers, GameEnginePrefabs.PrefabId);
        var engine = new LogicEngine();
        engine.Model = runtime; // 首次 Emit／挂载之前赋值（替换守卫不触发）
        using var recorder = new UpdateRecorder(engine);

        var card = new UnitCard(engine, BrokenCardDefinition());
        await card.LoadAsync(new Player(0, new CardList(), engine)); // 失败被 MountComponent 就地 catch ⇒ 本行不抛

        // ① 日志存在：证明走了 MountComponent 的 catch（同一组件 id、Error 级）。
        var entry = Assert.Single(
            engine.RootStream.Entries,
            e => e.Message.Contains("装配失败（隔离：该组件不生效）", StringComparison.Ordinal));
        Assert.Contains(BrokenComponentId, entry.Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Error, entry.Level);

        // ② 边通道不生效（turn.start 上有边时 onTurnStart 会被执行）。
        await runtime.EmitAsync(GameUpdates.TurnStart);
        Assert.DoesNotContain("onTurnStart", log);

        // ③ 包装通道不生效（未撤销时该 before 包装会改写 Amount 并留下 wrap 痕迹）。
        await runtime.RunMemberAsync(
            runtime.Engine,
            EngineProcesses.DamageApply,
            new Dictionary<string, object?> { ["Amount"] = 5 });
        Assert.DoesNotContain("wrap", log);

        // ④ 回滚不产生业务事件。
        Assert.DoesNotContain(Updates.EffectRemoved, recorder.Types);

        // ⑤ 口径固化（非验收项）：失败条目仍占用宿主的组件集合（L2 现状，由宿主 DisposeAll 收拾）。
        Assert.NotNull(card.ModelEntity);
        Assert.Contains(runtime.ComponentsOf(card.ModelEntity!), a => a.Type.Id == BrokenComponentId);
    }

    // ==================== 场景构件 ====================

    private const string BrokenComponentId = "comp.broken";

    /// <summary>
    /// 携带坏组件的卡定义：效果引用 <c>comp.broken</c> ＋ 单位卡必需的内置组件（国籍／稀有度）。
    /// </summary>
    private static CardDefinition BrokenCardDefinition()
        => new(
            "破组件卡",
            new ICardDataComponentDefinition[]
            {
                new FactionCostDefinition(Faction.Germany, 1),
                new TagDataDefinition(Rarity.Standard, Array.Empty<string>()),
                new EffectsDefinition(new[] { BrokenComponentId }),
            });

    /// <summary>游戏层引擎（真实 39 信号）＋两个组件预制体（贴在 card.damaged 上，测试用）。</summary>
    private static OrchestrationRuntime Runtime(List<string> log)
    {
        var catalog = GameEnginePrefabs.CreateCatalog()
            .Add(Component("comp.aura", "aura", log))
            .Add(Component("comp.mark", "mark", log))
            .Add(new PrefabDefinition(
                "comp.loaded",
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[] { Member("hand", log) },
                mounts: new[] { new MountDeclaration($"engine#{GameUpdates.CardDamaged}", "hand") },
                activeWhen: ActiveWhen.Loaded))
            .Add(new PrefabDefinition(
                "comp.targeting",
                EntityKind.Component,
                requiresHost: HostKind.Any,
                members: new[] { Member("probe", log) },
                mounts: new[] { new MountDeclaration($"host#{CardProcesses.Targeting}", "probe") }));

        return new OrchestrationRuntime(catalog, new HandlerRegistry(), GameEnginePrefabs.PrefabId);
    }

    private static PrefabDefinition Component(string id, string member, List<string> log)
        => new(
            id,
            EntityKind.Component,
            requiresHost: HostKind.Any,
            members: new[] { Member(member, log) },
            mounts: new[] { new MountDeclaration($"engine#{GameUpdates.CardDamaged}", member) });

    private static MemberDefinition Member(string name, List<string> log)
        => new(
            name,
            ViewSpec.Empty,
            MemberVisibility.Mountable,
            ownEdges: new[]
            {
                new OwnedEdge((KernelHandler)((_, _, _) =>
                {
                    log.Add(name);
                    return Task.CompletedTask;
                })),
            });

    private static IReadOnlyDictionary<string, object?> Payload(OrchestrationEntity card)
        => new Dictionary<string, object?> { [Orc.Cards.PayloadKeys.Card] = card };
}
