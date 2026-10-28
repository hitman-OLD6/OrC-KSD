using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Tests;

/// <summary>
/// 游戏层测试脚手架（v4 边表形态）：把"自写监听 handler"与"产出目标"两种旧触发器用法接到新面上。
/// <para>
/// 旧写法：<c>new Trigger&lt;TView&gt;(...hooks: [...])</c> + <c>engine.Mount(...)</c>＝订阅；
/// <c>new Trigger&lt;TView&gt;(...) + Register(...)</c> + <c>InvokeAsync</c>＝一次可调用逻辑。
/// 新写法：订阅＝<see cref="LogicEngine.AttachMemberEdge"/>（信号成员上的一条外来边）；
/// 可调用逻辑＝<see cref="OrchestrationRuntime.RegisterMember"/> 注册的成员（地址 <c>engine#&lt;名&gt;</c>）。
/// </para>
/// </summary>
internal static class GameKernelTestKit
{
    /// <summary>在信号成员上挂一条强类型视图的订阅边（返回撤销句柄）。</summary>
    internal static IDisposable Watch<TView>(
        this LogicEngine engine, string signal, string name, object? owner,
        Func<TView, Context, CancellationToken, Task> handler,
        int priority = UpdatePriorities.Normal)
        where TView : class
        => engine.AttachMemberEdge(signal, name, owner, MemberEdges.For(handler), priority);

    /// <summary>在成员地址上挂一条强类型视图的边（动作端检查位／改写位用）。</summary>
    internal static IDisposable WatchOn<TView>(
        this LogicEngine engine, string memberName, string name, object? owner,
        Func<TView, Context, CancellationToken, Task> handler,
        int priority = 0)
        where TView : class
        => engine.AttachMemberEdge(memberName, name, owner, MemberEdges.For(handler), priority);

    /// <summary>
    /// 注册一个"产出目标"成员（等同旧主动触发器：调用一次＝执行一次），返回成员名。
    /// 名字加随机后缀避免同引擎重名（成员名即地址，重名注册会 fail-fast）。
    /// </summary>
    internal static string Sink(LogicEngine engine, string name, Action onFire)
    {
        var runtime = engine.Model
            ?? throw new InvalidOperationException("引擎未挂编排运行时（对局初始化后才有）。");
        var member = new MemberDefinition(
            $"{name}#{Guid.NewGuid():N}",
            ViewSpec.Empty,
            MemberVisibility.Callable,
            new[]
            {
                new OwnedEdge((KernelHandler)((_, _, _) =>
                {
                    onFire();
                    return Task.CompletedTask;
                })),
            },
            layer: PrefabLayer.Runtime);
        runtime.RegisterMember(runtime.Engine, member);
        return member.Name;
    }

    /// <summary>调用"产出目标"一次。</summary>
    internal static Task FireAsync(LogicEngine engine, string sink, CancellationToken ct = default)
        => engine.InvokeMemberAsync(sink, (IDictionary<string, object?>?)null, ct);
}
