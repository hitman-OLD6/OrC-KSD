namespace Orc.Core;

/// <summary>
/// 包装器类别（管道式）：前置（核心前执行、可就地改写参数、不可短路）、
/// 后置（核心后执行、参与输出）、替换（与目标同签名、最后一个未注销者生效、只替换核心段）。
/// </summary>
public enum WrapperPhase
{
    /// <summary>前置包装器：核心之前按 LIFO 执行。</summary>
    Before,

    /// <summary>后置包装器：核心之后按 FIFO 执行。</summary>
    After,

    /// <summary>替换包装器：替换核心段，最后一个未注销者生效。</summary>
    Replace,
}
