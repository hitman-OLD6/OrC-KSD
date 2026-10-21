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
    /// **布局指纹**（旧语义；只哈希「包 ID ＋ sidecar 路径列表」）：顺序不影响指纹。
    /// <para>
    /// 仅用于「布局」审计与既有测试；<c>manifest.version</c> 必须走 <see cref="ComputeContent"/>（F1）。
    /// </para>
    /// </summary>
    /// <param name="packageId">包 id（人类可读稳定身份之一）。</param>
    /// <param name="metadata">包内元数据条目（sidecar 相对路径；顺序不影响指纹）。</param>
    /// <exception cref="ArgumentException">packageId 为 null/空白。</exception>
    public static PackageVersion ComputeLayout(string packageId, IEnumerable<string>? metadata = null)
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

    /// <summary>
    /// **内容指纹**（F1/D29）：<c>SHA-256(包 ID ＋ 每份 sidecar 的规范化内容)</c>——同一内容恒得同一指纹。
    /// <para>
    /// 规范化（唯一一处）：<c>assets[]</c> 按 <c>(Kind, Id)</c> 稳定排序 → <see cref="AssetSidecarJson.ToJson"/>
    /// 写窄（固定键序／2 空格／LF／UTF-8 无 BOM）→ 各份按相对路径 <see cref="StringComparer.Ordinal"/> 排序 →
    /// 逐段写入「段字节长度 ＋ 相对路径 ＋ 序列化文本」防拼接歧义 → 整体哈希取前 16 字节小写十六进制。
    /// </para>
    /// <para>
    /// 参与字段＝写窄产出的全部字段（依赖／冲突／anchor／loader／serviceKind 变化会改指纹；
    /// 注释、未知字段、缩进、键序、尾逗号不会）。防御性排除 <c>versions/**</c> 与 <c>assets.meta.json</c>。
    /// </para>
    /// </summary>
    /// <param name="packageId">包 id（须与每份 sidecar 的 <c>packageId</c> 一致）。</param>
    /// <param name="sidecars">当前版 sidecar 集合（相对路径 ＋ 已解析 sidecar）。</param>
    /// <exception cref="ArgumentException">packageId 为空白，或某份 sidecar 的 packageId 与入参不一致。</exception>
    public static PackageVersion ComputeContent(
        string packageId,
        IEnumerable<(string RelativePath, AssetSidecar Sidecar)> sidecars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(sidecars);

        var id = packageId.Trim();
        var builder = new StringBuilder();
        builder.Append(id).Append('\n');

        var entries = sidecars
            .Where(entry => !IsExcludedFromContent(entry.RelativePath))
            .Select(entry =>
            {
                if (!string.Equals(entry.Sidecar.PackageId, id, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"sidecar '{entry.RelativePath}' 的 packageId '{entry.Sidecar.PackageId}' 与入参 '{id}' 不一致。",
                        nameof(sidecars));
                }

                var normalized = entry.Sidecar with
                {
                    Assets = entry.Sidecar.Assets
                        .OrderBy(asset => asset.Kind)
                        .ThenBy(asset => asset.Id, StringComparer.Ordinal)
                        .ToArray(),
                };

                return (
                    RelativePath: entry.RelativePath.Replace('\\', '/'),
                    Text: AssetSidecarJson.ToJson(normalized));
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        foreach (var (relativePath, text) in entries)
        {
            builder.Append(Encoding.UTF8.GetByteCount(text)).Append('\n')
                .Append(relativePath).Append('\n')
                .Append(text).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new PackageVersion(Convert.ToHexString(hash, 0, 16).ToLowerInvariant());
    }

    /// <summary>内容指纹的防御性排除：<c>versions/**</c>（历史归档）与插件清单镜像。</summary>
    private static bool IsExcludedFromContent(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith(PackageScanner.VersionsDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileName(normalized),
            PluginAssetRegistrar.ManifestFileName,
            StringComparison.OrdinalIgnoreCase);
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
