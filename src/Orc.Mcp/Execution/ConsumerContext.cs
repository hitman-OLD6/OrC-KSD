namespace Orc.Mcp.Execution;

/// <summary>
/// 串行消费者上下文标记（多人支持 S4）：由宿主的串行执行管道在进入消费者时开启。
/// <para>
/// 用途：只读执行面（<c>MatchExecutor.ReadOnly</c>）要求"当前是唯一写者"——不取执行器门禁、
/// 不判 <c>Busy</c>，仅断言本标记为真。误用（在网络回调/后台线程直接读引擎）立即抛错，
/// 而不是静默产生并发访问。
/// </para>
/// </summary>
public static class ConsumerContext
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>当前是否在串行消费者上下文内。</summary>
    public static bool IsActive => Active.Value;

    /// <summary>进入消费者上下文（Dispose 退出）。</summary>
    public static IDisposable Enter()
    {
        Active.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Active.Value = false;
        }
    }
}
