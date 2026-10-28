using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
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

    // ==================== 场景构件 ====================

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
