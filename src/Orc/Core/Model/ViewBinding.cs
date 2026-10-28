namespace Orc.Core.Model;

/// <summary>
/// 视图静态检查的一条诊断（S5 第 8 条）：字段名＋精确位置（行／列）＋行内片段＋原因。
/// </summary>
/// <param name="Kind">诊断种类：<c>undeclared-view</c>／<c>readonly-write</c>／<c>undeclared-owner</c>／<c>unverified-pass</c>。</param>
/// <param name="Field">涉及的字段／成员名。</param>
/// <param name="Line">行号（1 起）。</param>
/// <param name="Column">列号（1 起）。</param>
/// <param name="Snippet">行内片段（定位用）。</param>
/// <param name="Message">人类可读原因。</param>
public sealed record ViewBindingDiagnostic(
    string Kind,
    string Field,
    int Line,
    int Column,
    string Snippet,
    string Message);

/// <summary>
/// 视图静态检查结果（S5／W-16）。
/// <para>
/// 两份清单：①<see cref="Errors"/>（＝<c>binding</c> 失败，拒绝装载）；②<see cref="UnverifiedViewPasses"/>
/// （**放行但留痕**：形如把 <c>view</c> 交给方法／局部函数／返回值的写法——只追踪直接别名，不追踪经方法传递者；
/// 这一类的保护是概率性的，靠运行期 setter 闸门兜底，审查面须能计数与列出，R1a／S10）。
/// </para>
/// </summary>
public sealed class ViewBindingAnalysis
{
    /// <summary>无诊断（空结果）。</summary>
    public static readonly ViewBindingAnalysis Empty = new(
        Array.Empty<ViewBindingDiagnostic>(), Array.Empty<ViewBindingDiagnostic>());

    /// <summary>创建结果。</summary>
    public ViewBindingAnalysis(
        IReadOnlyList<ViewBindingDiagnostic> errors,
        IReadOnlyList<ViewBindingDiagnostic> unverifiedViewPasses)
    {
        Errors = errors;
        UnverifiedViewPasses = unverifiedViewPasses;
    }

    /// <summary>绑定错误（非空＝拒绝装载）。</summary>
    public IReadOnlyList<ViewBindingDiagnostic> Errors { get; }

    /// <summary>未经静态验证的 <c>view</c> 传递（放行 + 留痕）。</summary>
    public IReadOnlyList<ViewBindingDiagnostic> UnverifiedViewPasses { get; }

    /// <summary>是否通过静态检查。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>未经静态验证的传递处数（审查面计数字段）。</summary>
    public int UnverifiedCount => UnverifiedViewPasses.Count;

    /// <summary>错误摘要（装载期拒绝消息）。</summary>
    public string Describe()
    {
        if (IsValid)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            Errors.Select(static e => $"第 {e.Line} 行第 {e.Column} 列 [{e.Kind}] {e.Message}（片段：{e.Snippet}）"));
    }
}
