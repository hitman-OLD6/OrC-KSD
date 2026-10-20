namespace Orc.Packages.Fs;

/// <summary>
/// 路径违规（越界／UNC／<c>..</c>／符号链接不可解析）——纯判定层异常，不做 IO。
/// </summary>
public sealed class PathViolationException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="attemptedPath">被拒绝的原始输入。</param>
    /// <param name="reason">拒绝原因（人读）。</param>
    public PathViolationException(string attemptedPath, string reason)
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
/// **归一化后的绝对路径**（<see cref="PathNormalizer"/> 的产出）：分隔符统一、去尾分隔符、
/// 已解析符号链接；比较在 Windows 下按大小写不敏感（<see cref="PathNormalizer.PathComparison"/>）。
/// </summary>
public readonly struct NormalizedPath : IEquatable<NormalizedPath>
{
    /// <summary>由已归一化的绝对路径构造（调用方保证已归一；一般经 <see cref="PathNormalizer.Normalize"/>）。</summary>
    /// <param name="value">绝对路径。</param>
    public NormalizedPath(string value) => Value = value;

    /// <summary>绝对路径文本（不保证以分隔符结尾）。</summary>
    public string Value { get; }

    /// <summary>是否为空（未初始化）。</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>直接父目录（盘根＝null）。</summary>
    public NormalizedPath? Parent
    {
        get
        {
            if (IsEmpty)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(Value);
            return string.IsNullOrEmpty(parent) ? null : new NormalizedPath(parent);
        }
    }

    /// <summary>本路径是否等于 <paramref name="root"/> 或位于其下（按平台大小写规则）。</summary>
    /// <param name="root">候选根（已归一）。</param>
    public bool IsUnderOrEqual(NormalizedPath root)
    {
        if (IsEmpty || root.IsEmpty)
        {
            return false;
        }

        if (Value.Equals(root.Value, PathNormalizer.PathComparison))
        {
            return true;
        }

        if (!root.Value.EndsWith(Path.DirectorySeparatorChar))
        {
            return Value.StartsWith(root.Value + Path.DirectorySeparatorChar, PathNormalizer.PathComparison);
        }

        return Value.StartsWith(root.Value, PathNormalizer.PathComparison);
    }

    /// <inheritdoc />
    public bool Equals(NormalizedPath other)
        => Value.Equals(other.Value, PathNormalizer.PathComparison);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NormalizedPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => Value is null
            ? 0
            : (OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(Value)
                : StringComparer.Ordinal.GetHashCode(Value));

    /// <summary>判等（平台大小写规则）。</summary>
    public static bool operator ==(NormalizedPath left, NormalizedPath right) => left.Equals(right);

    /// <summary>不等。</summary>
    public static bool operator !=(NormalizedPath left, NormalizedPath right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// **路径归一化的唯一实现**（S8；I8 定案）：把路径转成可比较的规范绝对路径。
/// <para>
/// 规则（默认全开，无开关）：
/// ①拒绝空白；②拒绝 UNC（<c>\\server\share</c> / <c>//server/share</c>）；③只接受**完全限定**的绝对路径；
/// ④拒绝 <c>..</c> 路径段（先于任何拼接/规范化判定，杜绝「校验后拼接」）；⑤<see cref="Path.GetFullPath(string)"/> 统一分隔符；
/// ⑥**realpath 归一**：逐段解析符号链接／junction 到最终目标（无法解析的链接＝拒绝）；
/// ⑦去尾分隔符（盘根除外）；⑧比较在 Windows 下按大小写不敏感。
/// </para>
/// <para>
/// 本类型**不做授权判定**——「是否落在授权根内」由 <see cref="IEditableRootPolicy"/> 承担；
/// 全仓（含沙盒守卫）只允许经本类型归一，避免两份规则漂移。
/// </para>
/// </summary>
public static class PathNormalizer
{
    /// <summary>符号链接解析的最大轮数（防链接环）。</summary>
    private const int MaxLinkDepth = 8;

    /// <summary>平台路径比较规则（Windows＝大小写不敏感）。</summary>
    public static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>是否为 UNC 路径（<c>\\server\share</c> 或 <c>//server/share</c>）。</summary>
    /// <param name="path">候选路径（null／空白＝false）。</param>
    public static bool IsUncPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal));

    /// <summary>是否含 <c>..</c> 路径段（逐段精确匹配，<c>a..b</c> 不算）。</summary>
    /// <param name="path">候选路径（null／空白＝false）。</param>
    public static bool ContainsParentSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    /// <summary>归一化绝对路径（违规＝<see cref="PathViolationException"/>）。</summary>
    /// <param name="path">候选绝对路径。</param>
    /// <exception cref="PathViolationException">空白／UNC／相对路径／含 <c>..</c>／符号链接不可解析。</exception>
    public static NormalizedPath Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PathViolationException(path ?? string.Empty, "路径为空");
        }

        var input = path.Trim();
        if (IsUncPath(input))
        {
            throw new PathViolationException(input, "不允许 UNC 路径");
        }

        if (!Path.IsPathFullyQualified(input))
        {
            throw new PathViolationException(input, "不允许相对路径（须为绝对路径）");
        }

        if (ContainsParentSegment(input))
        {
            throw new PathViolationException(input, "不允许 '..' 路径段");
        }

        string full;
        try
        {
            full = Path.GetFullPath(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PathViolationException(input, $"路径无法解析（{ex.GetType().Name}）");
        }

        return new NormalizedPath(TrimTrailingSeparator(ResolveLinks(full, input)));
    }

    /// <summary>尝试归一化（失败＝false ＋ 原因；不抛错）。</summary>
    /// <param name="path">候选绝对路径。</param>
    /// <param name="normalized">归一结果（失败＝default）。</param>
    /// <param name="error">失败原因（成功＝null）。</param>
    public static bool TryNormalize(string? path, out NormalizedPath normalized, out string? error)
    {
        try
        {
            normalized = Normalize(path);
            error = null;
            return true;
        }
        catch (PathViolationException ex)
        {
            normalized = default;
            error = ex.Reason;
            return false;
        }
    }

    /// <summary>
    /// 在已归一的根下组合相对路径并归一（相对路径不得为 UNC／绝对路径／含 <c>..</c>）。
    /// </summary>
    /// <param name="root">已归一的根。</param>
    /// <param name="relativePath">根内相对路径。</param>
    /// <exception cref="PathViolationException">相对路径非法。</exception>
    public static NormalizedPath Combine(NormalizedPath root, string? relativePath)
    {
        if (root.IsEmpty)
        {
            throw new PathViolationException(relativePath ?? string.Empty, "根路径为空");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new PathViolationException(relativePath ?? string.Empty, "路径为空");
        }

        var input = relativePath.Trim();
        if (IsUncPath(input))
        {
            throw new PathViolationException(input, "不允许 UNC 路径");
        }

        if (Path.IsPathRooted(input))
        {
            throw new PathViolationException(input, "不允许绝对路径（只接受根内相对路径）");
        }

        if (ContainsParentSegment(input))
        {
            throw new PathViolationException(input, "不允许 '..' 路径段");
        }

        return Normalize(Path.Combine(root.Value, input));
    }

    /// <summary>去尾分隔符（盘根保留其分隔符）。</summary>
    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    /// <summary>逐段解析符号链接／junction（realpath；链接环＝拒绝）。</summary>
    private static string ResolveLinks(string fullPath, string attemptedPath)
    {
        var current = fullPath;
        for (var round = 0; round < MaxLinkDepth; round++)
        {
            var next = ResolveOnce(current, attemptedPath);
            if (string.Equals(next, current, StringComparison.Ordinal))
            {
                return current;
            }

            current = next;
        }

        throw new PathViolationException(attemptedPath, $"符号链接层数过深（超过 {MaxLinkDepth} 层）");
    }

    /// <summary>一次逐段解析；无链接＝原样返回。</summary>
    private static string ResolveOnce(string fullPath, string attemptedPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        var changed = false;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var target = ResolveLinkTarget(current, attemptedPath);
            if (target is not null)
            {
                current = target;
                changed = true;
            }
        }

        return changed ? current : fullPath;
    }

    /// <summary>若该位置是重解析点则返回最终目标（否则 null）；指向不存在的目标＝拒绝。</summary>
    private static string? ResolveLinkTarget(string path, string attemptedPath)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            info.Refresh();
            if (!info.Exists && !Directory.Exists(path))
            {
                return null; // 不存在（尚待创建的路径）：不是链接
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return null;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw new PathViolationException(attemptedPath, $"符号链接无法解析：'{path}'");
            }

            return Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 链接本身不可读：按拒绝处理（不静默放行）。
            throw new PathViolationException(attemptedPath, $"符号链接无法解析：'{path}'（{ex.GetType().Name}）");
        }
    }
}
