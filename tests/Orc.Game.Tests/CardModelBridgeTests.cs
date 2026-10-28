using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Model;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// U5-a 验收（卡牌实例 ↔ OrchestrationEntity 桥，批次 3 遗留）：
/// <list type="bullet">
/// <item>每张卡入场时拥有**对应的编排实体**，<see cref="OrchestrationEntity.Bound"/> 指回卡实例；</item>
/// <item>实体的成员**可执行**（<see cref="OrchestrationEntity.InvokeAsync"/>）；</item>
/// <item>销毁卡时**撤销**实体（从运行时移除、Bound 清空）；</item>
/// <item>卡数据声明的 v4 组件按声明序装配到该实体（A-3：一条声明＝一个组件实例）。</item>
/// </list>
/// </summary>
public class CardModelBridgeTests
{
    [Fact]
    public async Task 卡加载_拥有编排实体且Bound为卡本身()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, match.Players[0]);

        var entity = unit.ModelEntity;
        Assert.NotNull(entity);
        Assert.Same(unit, entity!.Bound);
        Assert.Same(match.EngineRuntime, entity.Runtime);
        Assert.Equal(EntityKind.Card, entity.EntityKind);
        Assert.Contains(entity, match.EngineRuntime.Entities);
        Assert.Equal(CardProcesses.UnitPrefabId, entity.Type.Id);
    }

    [Fact]
    public async Task 卡实体成员可执行()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, match.Players[0]);

        var result = await unit.ModelEntity!.InvokeAsync(CardProcesses.Play);

        Assert.True(result.Executed);
        Assert.False(result.ValidationRejected);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task 卡销毁_实体撤销并解绑()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, match.Players[0]);
        var entity = unit.ModelEntity!;

        await match.Engine.DestroyCard(unit);

        Assert.DoesNotContain(entity, match.EngineRuntime.Entities);
        Assert.Null(entity.Bound);
        Assert.False(entity.Life.IsAlive);
    }

}
