namespace Orc.Packages.Fs;

/// <summary>
/// **可编辑根策略**（S8；I8 定案）：判定一条**绝对路径**是否允许被编辑器写入。
/// <para>
/// 契约要点（硬约束）：
/// <list type="bullet">
///   <item><b>纯判定</b>：不写盘、不建目录、不改状态；同一输入恒得同一结果。</item>
///   <item><b>授权根构造期固定</b>：会话期不得扩权（无 <c>AddRoot</c> 之类入口）——这正是排除「多根白名单动态扩展」方案的理由。</item>
///   <item><b>授权单位＝目标路径的直接父目录</b>：仅前缀匹配会被 <c>&lt;root&gt;/../../etc</c> 与符号链接绕过；
///     要求「父目录命中授权根」既能覆盖 <c>&lt;包目录&gt;.tmp/</c> 这类同级派生，又不需要运行期扩权。</item>
///   <item><b>先归一后判定</b>：一律经 <see cref="PathNormalizer"/> realpath 归一（解析符号链接、大小写归一、拒绝 UNC 与 <c>..</c>），
///     再比较父目录。</item>
/// </list>
/// </para>
/// </summary>
public interface IEditableRootPolicy
{
    /// <summary>授权根（归一化绝对路径；构造期固定，只读）。</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// 判定绝对路径是否可写（纯判定；非法路径一律 false，不抛错）。
    /// </summary>
    /// <param name="absolutePath">待写目标的绝对路径。</param>
    bool Approve(string absolutePath);

    /// <summary>带原因的判定（供诊断与报错指名；纯判定）。</summary>
    /// <param name="absolutePath">待写目标的绝对路径。</param>
    /// <param name="reason">拒绝原因（通过＝null）。</param>
    bool TryApprove(string absolutePath, out string? reason);
}

/// <summary>
/// 默认实现：**构造期给定根列表**，之后不再改变。
/// <para>
/// 沙盒模式＝单根（<c>Orc.Mcp.Sandbox.SandboxPathGuard</c> 由本类型委托实现）；非沙盒＝前端配置根列表。
/// 规则只有一份（<see cref="PathNormalizer"/>），本类型只做「直接父目录命中授权根」这一条判定。
/// </para>
/// </summary>
public sealed class EditableRootPolicy : IEditableRootPolicy
{
    private readonly NormalizedPath[] _roots;

    /// <summary>由授权根构造（每项经 realpath 归一；空白项＝拒绝；重复项去重）。</summary>
    /// <param name="roots">授权根（绝对路径）。</param>
    /// <exception cref="ArgumentException">未提供任何根，或某项为空白路径。</exception>
    public EditableRootPolicy(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var normalized = new List<NormalizedPath>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("授权根不得为空白路径。", nameof(roots));
            }

            var path = PathNormalizer.Normalize(root);
            if (!normalized.Contains(path))
            {
                normalized.Add(path);
            }
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("至少需要一个授权根。", nameof(roots));
        }

        _roots = normalized.ToArray();
    }

    /// <summary>单根便捷构造（沙盒模式）。</summary>
    /// <param name="root">授权根（绝对路径）。</param>
    public EditableRootPolicy(string root)
        : this([root])
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roots => _roots.Select(root => root.Value).ToArray();

    /// <inheritdoc />
    public bool Approve(string absolutePath) => TryApprove(absolutePath, out _);

    /// <inheritdoc />
    public bool TryApprove(string absolutePath, out string? reason)
    {
        if (!PathNormalizer.TryNormalize(absolutePath, out var target, out var pathError))
        {
            reason = pathError;
            return false;
        }

        var parent = target.Parent;
        if (parent is null)
        {
            reason = $"路径 '{target}' 没有父目录（不能作为写入目标）。";
            return false;
        }

        foreach (var root in _roots)
        {
            if (parent.Value.IsUnderOrEqual(root))
            {
                reason = null;
                return true;
            }
        }

        reason = $"目标路径的直接父目录 '{parent.Value}' 不在授权根内（授权根：{string.Join("、", Roots)}）。";
        return false;
    }
}
