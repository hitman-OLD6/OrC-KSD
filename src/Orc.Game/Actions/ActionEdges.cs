using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;

namespace Orc.Game.Actions;

/// <summary>
/// 动作端 → 引擎成员边的接入面（批次 7／§27）。
/// <para>
/// 动作类不再持有触发器实例：每个端＝一个**成员地址**（<c>engine#hand.add</c>…），端的默认流程＝该成员上的
/// **自有边**（接入序＝执行序）。本类只做"把强类型处理器接成成员边"这一件事，并把"引擎没有编排运行时"
/// 变成构造期的明确失败——否则动作会在运行期静默什么都不做。
/// </para>
/// </summary>
public static class ActionEdges
{
    /// <summary>
    /// 给引擎成员接一条自有边（动作构造期调用一次）。
    /// </summary>
    /// <typeparam name="TView">处理器读取的视图类型。</typeparam>
    /// <param name="engine">对局引擎（模型面来源）。</param>
    /// <param name="memberName">成员名（<see cref="EngineProcesses"/> 常量）。</param>
    /// <param name="handler">处理器。</param>
    /// <exception cref="InvalidOperationException">引擎未装配编排运行时（成员无处可挂）。</exception>
    public static void Attach<TView>(
        LogicEngine engine, string memberName, Func<TView, Context, CancellationToken, Task> handler)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(handler);

        var runtime = engine.Model
            ?? throw new InvalidOperationException(
                $"动作端 '{memberName}' 无处可挂：引擎未装配编排运行时（Model 为 null）。"
                + "动作端＝引擎成员上的自有边，必须先由对局装配 Model。");

        runtime.Engine.AttachOwnedEdge(memberName, MemberEdges.For(handler));
    }
}
