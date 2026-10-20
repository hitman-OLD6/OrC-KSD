using System.Globalization;
using Orc.Core;

namespace Orc.Packages;

/// <summary>版本约束的段语义。</summary>
public enum VersionSegmentKind
{
    /// <summary>精确值（须相等）。</summary>
    Exact = 0,

    /// <summary>任意（<c>*</c>）。</summary>
    Any = 1,

    /// <summary>下界向上取新（<c>^</c>／<c>^N</c>）：该段 ≥ 下界（<c>^</c> 省略数字＝下界 0，即任意）；不设上限。</summary>
    AtLeast = 2,
}

/// <summary>
/// 版本约束（依赖引用用；S6）：形如 <c>1.2.3</c> / <c>1.*.*</c> / <c>1.^.*</c>。
/// 语义：**第一段（主版）恒精确锁定**；第二、三段可为精确值／<c>*</c>（任意）／<c>^</c>（下界向上取新）。
/// 匹配：<c>Exact</c> 须相等；<c>Any</c> 与 <c>AtLeast</c> 均不设上限。
/// <para>
/// S4 起**只服务资产级依赖**（<c>sidecar</c> 的 <c>dependsOn</c>）：包级 <c>dependsOn</c> 已退役，
/// 包版本不再参与任何择版（<c>SelectHighest</c> 已退役）。
/// </para>
/// </summary>
public readonly struct AssetVersionConstraint : IEquatable<AssetVersionConstraint>
{
    private AssetVersionConstraint(
        int major, VersionSegmentKind minorKind, int minor, VersionSegmentKind patchKind, int patch)
    {
        Major = major;
        MinorKind = minorKind;
        Minor = minor;
        PatchKind = patchKind;
        Patch = patch;
    }

    /// <summary>主版（精确锁定）。</summary>
    public int Major { get; }

    /// <summary>次版段语义。</summary>
    public VersionSegmentKind MinorKind { get; }

    /// <summary>次版值（<c>Exact</c>＝精确值；<c>AtLeast</c>＝下界）。</summary>
    public int Minor { get; }

    /// <summary>修订段语义。</summary>
    public VersionSegmentKind PatchKind { get; }

    /// <summary>修订值（<c>Exact</c>＝精确值；<c>AtLeast</c>＝下界）。</summary>
    public int Patch { get; }

    /// <summary>解析版本约束（<c>x.y.z</c>；第二、三段可为 <c>*</c>/<c>^</c>）；非法＝false。</summary>
    public static bool TryParse(string? text, out AssetVersionConstraint constraint)
    {
        constraint = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        if (!TryParseSegment(parts[1], out var minorKind, out var minor)
            || !TryParseSegment(parts[2], out var patchKind, out var patch))
        {
            return false;
        }

        constraint = new AssetVersionConstraint(major, minorKind, minor, patchKind, patch);
        return true;
    }

    /// <summary>解析版本约束；非法＝<see cref="FormatException"/>。</summary>
    public static AssetVersionConstraint Parse(string text)
        => TryParse(text, out var constraint)
            ? constraint
            : throw new FormatException($"非法版本约束 '{text}'（期望 x.y.z，第二/三段可为 * 或 ^）。");

    /// <summary>给定版本是否满足本约束。</summary>
    public bool IsSatisfiedBy(AssetVersion version)
        => version.Major == Major
            && Satisfies(MinorKind, Minor, version.Minor)
            && Satisfies(PatchKind, Patch, version.Patch);

    private static bool Satisfies(VersionSegmentKind kind, int expected, int actual)
        => kind switch
        {
            VersionSegmentKind.Exact => actual == expected,
            VersionSegmentKind.Any => true,
            VersionSegmentKind.AtLeast => actual >= expected,
            _ => false,
        };

    private static bool TryParseSegment(string text, out VersionSegmentKind kind, out int value)
    {
        var trimmed = text.Trim();
        value = 0;

        if (trimmed == "*")
        {
            kind = VersionSegmentKind.Any;
            return true;
        }

        if (trimmed.StartsWith('^'))
        {
            // `^` ＝下界 0（任意）；`^N` ＝下界 N（该段 ≥ N，向上取新）。
            var bound = trimmed[1..].Trim();
            if (bound.Length == 0)
            {
                kind = VersionSegmentKind.AtLeast;
                return true;
            }

            if (int.TryParse(bound, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                kind = VersionSegmentKind.AtLeast;
                return true;
            }

            kind = default;
            return false;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            kind = VersionSegmentKind.Exact;
            return true;
        }

        kind = default;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(AssetVersionConstraint other)
        => Major == other.Major
            && MinorKind == other.MinorKind
            && Minor == other.Minor
            && PatchKind == other.PatchKind
            && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AssetVersionConstraint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, MinorKind, Minor, PatchKind, Patch);

    /// <inheritdoc />
    public override string ToString()
    {
        static string Segment(VersionSegmentKind kind, int value) => kind switch
        {
            VersionSegmentKind.Any => "*",
            VersionSegmentKind.AtLeast => value == 0 ? "^" : $"^{value}",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        return $"{Major}.{Segment(MinorKind, Minor)}.{Segment(PatchKind, Patch)}";
    }
}
