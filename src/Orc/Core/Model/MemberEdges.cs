namespace Orc.Core.Model;

/// <summary>
/// 成员边的**强类型包一层**（C# 侧的边工厂）。
/// <para>
/// 新模型的 handler 形态是 <see cref="KernelHandler"/>（承载视图＋ctx→Task），csx 直接吃动态视图；
/// 但宿主程序集里的处理器是用 <c>[ContextView]</c> 强类型视图写的（<c>MoveActionView</c>、<c>CardEventView</c>…）。
/// 本类把这类处理器接成边，不复制、不改写它们的视图契约：每次执行按 <paramref name="handler"/> 的视图类型
/// 从**同一份 <see cref="Context"/> 数据面**绑定一次视图（<see cref="ContextViewBinder.Create{TView}"/>），执行完即封。
/// </para>
/// <para>
/// **这不是兼容桥**：视图绑定器从来是内核的公共件（csx／判定器／旧动作端共用同一份 <c>ctx.Data</c>）；
/// 这里只是把"处理器"与"边"两种形态对上，边的段序（band）、停止语义、包装相位仍全部由
/// <see cref="OrchestrationEntity"/> 的成员执行序决定——自有边吃成员的
/// <see cref="MemberDefinition.Band"/>，外来边的段名经
/// <see cref="OrchestrationRuntime.MountAtRuntime(OrchestrationEntity, string, string, string?)"/>／
/// <see cref="MemberEdgeTable.Attach"/> 的 <c>band</c> 参数给出。
/// </para>
/// </summary>
public static class MemberEdges
{
    /// <summary>
    /// 把强类型处理器接成一条自有边（位次由成员声明决定；<c>ctx.Stop()</c> 语义与其它边一致）。
    /// </summary>
    /// <typeparam name="TView">处理器读取的视图类型（须标 <c>[ContextView]</c>）。</typeparam>
    /// <param name="handler">处理器（视图＋ctx＋ct）。</param>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public static OwnedEdge For<TView>(Func<TView, Context, CancellationToken, Task> handler)
        where TView : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        return new OwnedEdge((_, ctx, ct) => RunAsync(handler, ctx, ct));
    }

    private static async Task RunAsync<TView>(
        Func<TView, Context, CancellationToken, Task> handler, Context ctx, CancellationToken ct)
        where TView : class
    {
        var view = ContextViewBinder.Create<TView>(ctx);
        try
        {
            await handler(view, ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            ((IContextView)(object)view).Seal();
        }
    }
}
