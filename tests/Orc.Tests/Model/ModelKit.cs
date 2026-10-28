using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>内核模型测试的公共构件。</summary>
internal static class ModelKit
{
    /// <summary>包一层同步逻辑的自有边（记 trace／读写视图）。</summary>
    public static OwnedEdge Own(Action<ViewCarrier, Context> action)
        => new((KernelHandler)((view, ctx, ct) =>
        {
            action((ViewCarrier)view, ctx);
            return Task.CompletedTask;
        }));

    /// <summary>只记一条 trace 的自有边。</summary>
    public static OwnedEdge Trace(List<string> trace, string name)
        => Own((_, _) => trace.Add(name));

    /// <summary>可被挂载、执行时记一条 trace 的成员（组件声明用）。</summary>
    public static MemberDefinition Member(string name, List<string> trace)
        => new(name, ViewSpec.Empty, MemberVisibility.Mountable, ownEdges: new[] { Trace(trace, name) });

    /// <summary>引擎预制体（内核基础预制体：只声明本测试用到的内核信号）。</summary>
    public static PrefabDefinition EnginePrefab(params MemberDefinition[] members)
        => new("engine", EntityKind.Engine, members: members);

    /// <summary>薄基类卡牌预制体。</summary>
    public static PrefabDefinition CardBase(params MemberDefinition[] members)
        => new("card.base", EntityKind.Card, members: members);

    /// <summary>单位卡类型预制体。</summary>
    public static PrefabDefinition UnitCard(params MemberDefinition[] members)
        => new("card.unit", EntityKind.Card, extends: "card.base", hosts: HostKind.Unit, members: members);

    /// <summary>指令卡类型预制体。</summary>
    public static PrefabDefinition OrderCard(params MemberDefinition[] members)
        => new("card.order", EntityKind.Card, extends: "card.base", hosts: HostKind.Order, members: members);

    /// <summary>HQ 类型预制体。</summary>
    public static PrefabDefinition HqCard(params MemberDefinition[] members)
        => new("card.hq", EntityKind.Card, extends: "card.base", hosts: HostKind.Hq, members: members);

    /// <summary>反制卡类型预制体。</summary>
    public static PrefabDefinition CounterCard(params MemberDefinition[] members)
        => new("card.counter", EntityKind.Card, extends: "card.base", hosts: HostKind.Counter, members: members);
}
