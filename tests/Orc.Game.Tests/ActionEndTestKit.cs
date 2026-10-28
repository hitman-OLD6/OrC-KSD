using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Actions;

namespace Orc.Game.Tests;

/// <summary>
/// U3 测试侧夹具：**按成员地址**取动作端的身份（与宿主同一条解析路径 <see cref="ActionEnds"/>）。
/// <para>
/// 迁移前测试直接持有 <c>action.Handle</c>（<c>Trigger&lt;TView&gt;</c>）；U3 起契约只给地址，
/// 因此测试也必须"先拿地址、再挂边"——这与宿主（<c>CommandManager</c> 的 Rewrite 改写位、
/// <c>DamagePipeline</c> 的门面）完全相同，等价断言的可信度因此不依赖测试侧的私有捷径。
/// </para>
/// </summary>
internal static class ActionEndTestKit
{
    /// <summary>死亡处理端地址（<c>engine#death.handle</c>）。</summary>
    internal static MemberAddress HandleOf(this IDeathAction action)
        => ActionEnds.EndOf(action.Ends, action.Handle).Member;

    /// <summary>死亡触发端地址（<c>engine#death</c>）。</summary>
    internal static MemberAddress TriggerOf(this IDeathAction action)
        => ActionEnds.EndOf(action.Ends, action.Trigger).Member;

    /// <summary>在动作端地址上挂一条外来边（旧"RegisterMounted"的等价面；视图由强类型处理器绑定）。</summary>
    internal static IDisposable Mount<TView>(
        this MemberAddress address,
        LogicEngine engine,
        string name,
        Func<TView, Context, CancellationToken, Task> handler,
        int priority = 0)
        where TView : class
        => engine.AttachMemberEdge(address.Member, name, owner: null, MemberEdges.For(handler), priority);
}
