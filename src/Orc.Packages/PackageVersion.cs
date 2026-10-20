using System.Security.Cryptography;
using System.Text;

namespace Orc.Packages;

/// <summary>
/// **包版本**（D29/D32；S4）：内容指纹串——<c>hash(包 ID ＋ 包内元数据)</c>，临时包加 <c>-tmp</c>。
/// <para>
/// **不参与任何解析**：不进依赖（依赖一律资产级）、不进 lock、不进资产 GUID，也不参与择版；
/// 只作内容指纹（展示/诊断/快照审计）。**不含时间戳**（同一内容恒得同一指纹）。
/// </para>
/// </summary>
public readonly struct PackageVersion : IEquatable<PackageVersion>
{
    /// <summary>临时包标记后缀。</summary>
    public const string TemporarySuffix = "-tmp";

    private readonly string? _value;

    private PackageVersion(string value) => _value = value;

    /// <summary>指纹（不含 <c>-tmp</c>）。</summary>
    public string Fingerprint
        => IsTemporary ? Text[..^TemporarySuffix.Length] : Text;

    /// <summary>是否临时（草稿）包。</summary>
    public bool IsTemporary
        => Text.EndsWith(TemporarySuffix, StringComparison.Ordinal);

    /// <summary>完整版本串（含 <c>-tmp</c>）。</summary>
    public string Text => _value ?? "";

    /// <summary>
    /// 计算包版本：<c>SHA-256(包 ID ＋ 排序后的元数据列表)</c>（小写十六进制，32 字符截断以保持可读）。
    /// </summary>
    /// <param name="packageId">包 id（人类可读稳定身份之一）。</param>
    /// <param name="metadata">包内元数据条目（sidecar 相对路径；顺序不影响指纹）。</param>
    /// <exception cref="ArgumentException">packageId 为 null/空白。</exception>
    public static PackageVersion Compute(string packageId, IEnumerable<string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var builder = new StringBuilder();
        builder.Append(packageId.Trim()).Append('\n');
        if (metadata is not null)
        {
            foreach (var entry in metadata.Where(e => !string.IsNullOrWhiteSpace(e)).OrderBy(e => e, StringComparer.Ordinal))
            {
                builder.Append(entry.Trim()).Append('\n');
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new PackageVersion(Convert.ToHexString(hash, 0, 16).ToLowerInvariant());
    }

    /// <summary>解析包版本串（<c>&lt;指纹&gt;[-tmp]</c>；非法＝false）。</summary>
    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            return false;
        }

        var fingerprint = trimmed.EndsWith(TemporarySuffix, StringComparison.Ordinal)
            ? trimmed[..^TemporarySuffix.Length]
            : trimmed;
        if (fingerprint.Length == 0)
        {
            return false;
        }

        foreach (var c in fingerprint)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        version = new PackageVersion(trimmed);
        return true;
    }

    /// <summary>解析包版本串；非法＝<see cref="FormatException"/>。</summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    public static PackageVersion Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException($"非法包版本 '{text}'（期望内容指纹串，可带 -tmp）。");

    /// <summary>同指纹的临时包版本（<c>-tmp</c>）。</summary>
    public PackageVersion AsTemporary() => IsTemporary ? this : new PackageVersion(Text + TemporarySuffix);

    /// <summary>同指纹的正式包版本（去掉 <c>-tmp</c>）。</summary>
    public PackageVersion AsPromoted() => IsTemporary ? new PackageVersion(Fingerprint) : this;

    /// <inheritdoc />
    public bool Equals(PackageVersion other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PackageVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>判等（含 <c>-tmp</c> 标记）。</summary>
    public static bool operator ==(PackageVersion left, PackageVersion right) => left.Equals(right);

    /// <summary>不等。</summary>
    public static bool operator !=(PackageVersion left, PackageVersion right) => !left.Equals(right);

    /// <summary>版本串（含 <c>-tmp</c>）。</summary>
    public override string ToString() => Text;

    /// <summary>供诊断输出的 <c>包id@版本</c>。</summary>
    public string Describe(string packageId) => $"{packageId}@{Text}";
}
