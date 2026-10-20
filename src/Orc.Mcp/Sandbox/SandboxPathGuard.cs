using Orc.Packages.Fs;

namespace Orc.Mcp.Sandbox;

/// <summary>
/// 沙盒路径违规（映射为 <see cref="Contracts.McpCodes.PathDenied"/>）。
/// </summary>
public sealed class SandboxPathViolationException : Exception
{
    public SandboxPathViolationException(string attemptedPath, string reason)
        : base($"路径被拒绝：{reason}（输入：'{attemptedPath}'）。")
    {
        AttemptedPath = attemptedPath;
        Reason = reason;
    }

    /// <summary>被拒绝的原始输入。</summary>
    public string AttemptedPath { get; }

    /// <summary>拒绝原因。</summary>
    public string Reason { get; }
}

/// <summary>
/// 沙盒路径守卫：把"相对沙盒根的路径"解析为规范化绝对路径，并保证结果**落在根内**。
/// <para>
/// 自 S8 起本类型只是 <see cref="IEditableRootPolicy"/> 的一个实现（单根）：路径归一规则
/// **只有一份**（<see cref="PathNormalizer"/>，位于 <c>Orc.Packages</c>），本类型只负责
/// ①把相对路径拼到根上；②按「直接父目录命中授权根」判定写目标。
/// </para>
/// <para>
/// 全防要点（默认策略，无开关）：
/// ①拒绝空白路径；②拒绝 UNC 路径（<c>\\server\share</c> / <c>//server/share</c>）；
/// ③拒绝绝对路径（一律只接受根内相对路径）；④拒绝 <c>..</c> 穿越；
/// ⑤拒绝经由**符号链接/重解析点**逃逸出根（realpath 归一后复核）；⑥Windows 下按大小写不敏感比较；
/// ⑦写前对**规范化后的最终路径**复核（防"校验后拼接"类绕过）。
/// </para>
/// </summary>
public sealed class SandboxPathGuard : IEditableRootPolicy
{
    private readonly NormalizedPath _root;
    private readonly EditableRootPolicy _policy;

    public SandboxPathGuard(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = PathNormalizer.Normalize(root);
        _policy = new EditableRootPolicy(_root.Value);
    }

    /// <summary>沙盒根（规范化绝对路径；含尾分隔符）。</summary>
    public string Root => _root.Value + Path.DirectorySeparatorChar;

    /// <summary>授权根（沙盒模式＝单根）。</summary>
    public IReadOnlyList<string> Roots => _policy.Roots;

    /// <summary>把绝对路径解析为根内路径为可写目标（纯判定，不建目录）。</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new SandboxPathViolationException(relativePath ?? string.Empty, "路径为空");
        }

        var input = relativePath.Trim();
        if (PathNormalizer.IsUncPath(input))
        {
            throw new SandboxPathViolationException(relativePath, "不允许 UNC 路径");
        }

        if (Path.IsPathRooted(input))
        {
            throw new SandboxPathViolationException(relativePath, "不允许绝对路径（只接受沙盒根内的相对路径）");
        }

        if (PathNormalizer.ContainsParentSegment(input))
        {
            throw new SandboxPathViolationException(relativePath, "路径越出沙盒根");
        }

        NormalizedPath full;
        try
        {
            full = PathNormalizer.Combine(_root, input);
        }
        catch (PathViolationException ex)
        {
            throw new SandboxPathViolationException(relativePath, ex.Reason);
        }

        if (!full.IsUnderOrEqual(_root))
        {
            // 含符号链接逃逸：realpath 归一后落在根外。
            throw new SandboxPathViolationException(relativePath, "路径越出沙盒根");
        }

        return full.Value;
    }

    /// <summary>尝试解析（失败＝false，不抛错）。</summary>
    public bool TryResolve(string relativePath, out string resolved, out string? error)
    {
        try
        {
            resolved = Resolve(relativePath);
            error = null;
            return true;
        }
        catch (SandboxPathViolationException ex)
        {
            resolved = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>解析**写**目标（在 <see cref="Resolve"/> 之上再做一次授权判定；写前必须调用）。</summary>
    public string ResolveWrite(string relativePath)
    {
        var full = Resolve(relativePath);

        // 写前复核：最终路径的直接父目录须命中授权根（防"校验后拼接"类绕过）。
        if (!_policy.TryApprove(full, out var reason))
        {
            throw new SandboxPathViolationException(relativePath, $"写目标越出沙盒根：{reason}");
        }

        return full;
    }

    /// <inheritdoc />
    public bool Approve(string absolutePath) => _policy.Approve(absolutePath);

    /// <inheritdoc />
    public bool TryApprove(string absolutePath, out string? reason) => _policy.TryApprove(absolutePath, out reason);
}
