namespace Orc.Core;

/// <summary>
/// 同步脚本求值请求（判定器/资产服务包装器专用）：源码 ＋ 入口名 ＋ 超时。
/// 与 <see cref="ScriptRequest"/>（异步触发器 handler）并列——判定器包装器是同步签名，不能复用异步契约。
/// </summary>
/// <param name="Source">csx 源码文本。</param>
/// <param name="EntryName">入口具名变量（默认 <c>Handle</c>）。</param>
/// <param name="TimeoutMs">超时（0＝求值器默认）。</param>
/// <param name="CacheScope">缓存域（可选；供调用方隔离同名源码的不同语义）。</param>
public sealed record SyncScriptRequest(
    string Source,
    string EntryName = "Handle",
    int TimeoutMs = 0,
    string? CacheScope = null);

/// <summary>
/// 同步脚本求值结果（结构化；不抛异常）：成功时携带统一签名的包装器
/// <c>Func&lt;object[]?, object[]?, object[]?&gt;</c>（后置形态；前置由调用方按需适配）。
/// </summary>
public sealed class SyncScriptEvaluationResult
{
    private SyncScriptEvaluationResult(
        bool success,
        Func<object[]?, object[]?, object[]?>? handler,
        string? errorCategory,
        string? error)
    {
        Success = success;
        Handler = handler;
        ErrorCategory = errorCategory;
        Error = error;
    }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>包装器（成功时非 null）。</summary>
    public Func<object[]?, object[]?, object[]?>? Handler { get; }

    /// <summary>失败分类（sandbox / compile / entry-missing / signature / timeout / runtime）。</summary>
    public string? ErrorCategory { get; }

    /// <summary>失败消息。</summary>
    public string? Error { get; }

    /// <summary>成功产物。</summary>
    public static SyncScriptEvaluationResult Ok(Func<object[]?, object[]?, object[]?> handler)
        => new(true, handler ?? throw new ArgumentNullException(nameof(handler)), null, null);

    /// <summary>结构化失败。</summary>
    public static SyncScriptEvaluationResult Fail(string category, string error)
        => new(false, null, category, error);
}

/// <summary>
/// 同步脚本求值器（内核定义、satellite 实现）：把 csx 源码编译为判定器/资产服务包装器。
/// 契约：**不抛异常**（一切失败经 <see cref="SyncScriptEvaluationResult.Fail"/> 结构化返回）。
/// </summary>
public interface ISyncScriptEvaluator
{
    /// <summary>求值一个同步包装器入口。</summary>
    /// <exception cref="ArgumentNullException">request 为 null。</exception>
    SyncScriptEvaluationResult Evaluate(SyncScriptRequest request);
}
