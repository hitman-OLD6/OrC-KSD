#pragma warning disable CS8618 // 视图属性值由框架在绑定时提供；声明期不做初始化（视图类不应写属性初始化器）。

using Orc.Core;
using Orc.Core.Model;

namespace Orc.Tests;

/// <summary>S3 总线测试共享视图：部署载荷场景（Card 必填；Amount/Marker 可选）。</summary>
[ContextView]
public class BusPayloadView
{
    [Read]
    public virtual Ref<Entity> Card { get; set; }

    [Optional]
    [Mutate]
    public virtual int Amount { get; set; }

    [Optional]
    [Read]
    public virtual string? Marker { get; set; }
}

/// <summary>
/// S3 信号测试共享工具（v4 边表形态）：订阅＝在 <c>engine#&lt;信号&gt;</c> 上挂一条外来边，
/// 不再有可独立存在的触发器实例，也没有"按所有者批量卸载"——撤销落在**句柄**上
/// （<see cref="LogicEngine.AttachMemberEdge"/> 返回的 <see cref="IDisposable"/>）。
/// </summary>
internal static class SignalTestHelpers
{
    /// <summary>记录型订阅边（执行时把 name 追加进 trace；端点视图为空视图，载荷经 ctx 读取）。</summary>
    internal static OwnedEdge Recording(string name, List<string> trace)
        => new((KernelHandler)((view, ctx, ct) =>
        {
            trace.Add(name);
            return Task.CompletedTask;
        }));

    /// <summary>在单个信号上挂记录型订阅。</summary>
    internal static IDisposable Subscribe(
        LogicEngine engine, string signal, string name, List<string> trace,
        int priority = UpdatePriorities.Normal, object? owner = null)
        => Subscribe(engine, new[] { signal }, name, trace, priority, owner);

    /// <summary>在多个信号上各挂一条记录型订阅（同一 name；撤销句柄一并 Dispose）。</summary>
    internal static IDisposable Subscribe(
        LogicEngine engine, IReadOnlyList<string> signals, string name, List<string> trace,
        int priority = UpdatePriorities.Normal, object? owner = null)
    {
        // 先确定运行时身份、再挂边：`Model` 一旦被内核兜底运行时带走，之后就不允许换成另一个运行时
        // （否则已挂的边会留在旧引擎实体上、Emit 时静默不执行）。测试里显式前置这一步，等价于生产
        // 装配方"装配期先赋 Model、再发射"的顺序。
        KernelTestKit.Runtime(engine);

        var handles = new List<IDisposable>(signals.Count);
        foreach (var signal in signals)
        {
            handles.Add(engine.AttachMemberEdge(signal, name, owner, Recording(name, trace), priority));
        }

        return new Handles(handles);
    }

    /// <summary>在单个信号上挂任意边体（自定义行为：转发／嵌套发射等）。</summary>
    internal static IDisposable Subscribe(
        LogicEngine engine, string signal, string name, object? owner,
        Func<Context, CancellationToken, Task> body,
        int priority = UpdatePriorities.Normal)
    {
        KernelTestKit.Runtime(engine); // 同上一重载：先定身份、后挂边
        return engine.AttachMemberEdge(
            signal,
            name,
            owner,
            new OwnedEdge((KernelHandler)((view, ctx, ct) => body(ctx, ct))),
            priority);
    }

    /// <summary>多个撤销句柄的合并。</summary>
    internal sealed class Handles : IDisposable
    {
        private readonly IReadOnlyList<IDisposable> _items;

        internal Handles(IReadOnlyList<IDisposable> items) => _items = items;

        public void Dispose()
        {
            foreach (var item in _items)
            {
                item.Dispose();
            }
        }
    }
}
