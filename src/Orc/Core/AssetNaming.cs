using System.Globalization;

namespace Orc.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 资产命名文法（资产化 S2）：`{类型名}：{资产名}@{包GUID8}#{版本}`
//   · 类型名＝加载方/来源（json 数据 / csx 逻辑 / asm 程序集）——**文件级身份**；
//   · 包GUID8＝所属包稳定身份前 8 位（包内资产继承包的 GUID）；
//   · 版本＝1..8 段纯数字（规范形右补 0；包版本不再沿用本类型）。
// 读宽写窄（对齐项目既有原则）：解析接受全角/半角冒号与大小写；写出规范（全角冒号、小写类型名与 GUID8）。
// 版本**约束**（`*`/`^` 通配、择版）归包管理器（S6），本文件只承载**具体版本**的资产名。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>资产加载方/来源（类型名）。</summary>
public enum AssetLoadKind
{
    /// <summary>数据 JSON（枚举/资产值域等）。</summary>
    Json = 0,

    /// <summary>csx 逻辑（资产服务/判定器/效果 handler 等）。</summary>
    Csx = 1,

    /// <summary>可选程序集（csx 表达不了/性能敏感的逃生舱）。</summary>
    Asm = 2,
}

/// <summary>资产作用域（P11 甲）：进程级（静态注册面；冷加载期执行）／对局级（对局装配期执行）。</summary>
public enum AssetScope
{
    /// <summary>对局级（默认）：在有对局的装配期执行（判定器/资产服务/效果 handler 等）。</summary>
    Match = 0,

    /// <summary>进程级：冷加载期执行（词条/数据组件等静态注册面）。</summary>
    Process = 1,
}

/// <summary>对局级资产的执行相（P11 甲）：CommandManager 之前／之后。</summary>
public enum AssetActivationPhase
{
    /// <summary>CommandManager 之前（判定器/资产服务——协作者以延后求值访问）。</summary>
    PreCommand = 0,

    /// <summary>CommandManager 之后（需触发器实例/玩家面——流程触发器登记、具名触发器、效果库装配）。</summary>
    PostCommand = 1,
}

/// <summary>
/// 资产版本：<c>1..8</c> 段纯数字（<c>.</c> 分隔、非负、每段 ≤ <see cref="int.MaxValue"/>）＋<b>规范形</b>语义。
/// <para>
/// 规范形＝右补 0 至 <see cref="MaxSegments"/> 段：<b>比较、相等、哈希一律按规范形</b>（故 <c>1.2</c> ≡ <c>1.2.0</c>）；
/// <see cref="ToString"/> 保留**书写段数**（<c>1.0.0</c> 不被写成 <c>1</c>）——展示用，**禁止以字符串比对版本**（R17/D26）。
/// </para>
/// <para>递增＝严格大于旧版（允许增段；降级与补 0 后相等皆非法）；默认自动递增＝<see cref="Increment"/>（尾段 +1）。</para>
/// </summary>
public readonly struct AssetVersion : IEquatable<AssetVersion>, IComparable<AssetVersion>, IComparable
{
    /// <summary>版本段数上限（R17）。</summary>
    public const int MaxSegments = 8;

    private readonly int[]? _segments;

    /// <summary>三段构造（等同 <c>FromSegments([major, minor, patch])</c>；既有调用面保留）。</summary>
    public AssetVersion(int major, int minor, int patch)
        : this([major, minor, patch])
    {
    }

    /// <summary>内部构造：1..8 段、非负——**全部构造路径在此统一校验**（R17）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">段数不在 1..8，或任一段为负。</exception>
    private AssetVersion(int[] segments)
    {
        if (segments.Length is < 1 or > MaxSegments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segments), segments.Length, $"资产版本须为 1..{MaxSegments} 段。");
        }

        foreach (var segment in segments)
        {
            if (segment < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(segments), segment, "资产版本段不得为负。");
            }
        }

        _segments = segments;
    }

    /// <summary>由 1..8 段构造（构造时拷贝 ⇒ 对外不可变）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">段数不在 1..8，或任一段为负。</exception>
    public static AssetVersion FromSegments(ReadOnlySpan<int> segments) => new(segments.ToArray());

    /// <summary>书写段数（<c>1.2</c>＝2；<c>default</c>＝0）。</summary>
    public int SegmentCount => _segments?.Length ?? 0;

    /// <summary>只读段视图（书写段数；<c>default</c>＝空）。</summary>
    public ReadOnlySpan<int> Segments => _segments ?? ReadOnlySpan<int>.Empty;

    /// <summary>取第 <paramref name="index"/> 段（缺失段＝0；负索引＝0）。</summary>
    public int SegmentAt(int index)
        => index >= 0 && index < SegmentCount ? _segments![index] : 0;

    /// <summary>主版本（≡ <c>SegmentAt(0)</c>）。</summary>
    public int Major => SegmentAt(0);

    /// <summary>次版本（≡ <c>SegmentAt(1)</c>；缺失＝0）。</summary>
    public int Minor => SegmentAt(1);

    /// <summary>修订版本（≡ <c>SegmentAt(2)</c>；缺失＝0）。</summary>
    public int Patch => SegmentAt(2);

    /// <summary>解析 <c>1..8</c> 段版本串（非负整数；非法＝false）。</summary>
    public static bool TryParse(string? text, out AssetVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > MaxSegments)
        {
            return false;
        }

        var segments = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out segments[i]))
            {
                return false;
            }
        }

        version = new AssetVersion(segments);
        return true;
    }

    /// <summary>解析版本串；非法＝<see cref="FormatException"/>。</summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    public static AssetVersion Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException(
                $"非法资产版本 '{text}'（期望 1..{MaxSegments} 段非负整数，以 . 分隔）。");

    /// <summary>默认自动递增：尾段 +1（保留书写段数；<c>1.2</c>→<c>1.3</c>、<c>1.0.0</c>→<c>1.0.1</c>）。</summary>
    /// <exception cref="InvalidOperationException">尾段已为 <see cref="int.MaxValue"/>（无法递增）。</exception>
    public AssetVersion Increment()
    {
        var segments = SegmentCount == 0 ? [0] : _segments!.ToArray();
        var last = segments.Length - 1;
        if (segments[last] == int.MaxValue)
        {
            throw new InvalidOperationException($"资产版本 '{this}' 的尾段已达上限，无法递增。");
        }

        segments[last]++;
        return new AssetVersion(segments);
    }

    /// <summary>是否**严格大于**旧版（D26 的递增判定：降级与补 0 后相等皆不成立）。</summary>
    public bool IsUpgradeOver(AssetVersion previous) => CompareTo(previous) > 0;

    /// <summary>按规范形（右补 0 至 8 段）逐段比较，第一处不等决定大小。</summary>
    public int CompareTo(AssetVersion other)
    {
        for (var i = 0; i < MaxSegments; i++)
        {
            var c = SegmentAt(i).CompareTo(other.SegmentAt(i));
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        AssetVersion other => CompareTo(other),
        _ => throw new ArgumentException($"不可与类型 '{obj.GetType()}' 比较。", nameof(obj)),
    };

    /// <summary>按规范形相等（<c>1.2</c> ≡ <c>1.2.0</c>）。</summary>
    public bool Equals(AssetVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AssetVersion other && Equals(other);

    /// <summary>按规范形取哈希（与 <see cref="Equals(AssetVersion)"/> 一致——registry 以版本作字典键）。</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < MaxSegments; i++)
        {
            hash.Add(SegmentAt(i));
        }

        return hash.ToHashCode();
    }

    /// <summary>判等（规范形）。</summary>
    public static bool operator ==(AssetVersion left, AssetVersion right) => left.Equals(right);

    /// <summary>不等（规范形）。</summary>
    public static bool operator !=(AssetVersion left, AssetVersion right) => !left.Equals(right);

    /// <summary>小于（规范形比较）。</summary>
    public static bool operator <(AssetVersion left, AssetVersion right) => left.CompareTo(right) < 0;

    /// <summary>小于或等于（规范形比较）。</summary>
    public static bool operator <=(AssetVersion left, AssetVersion right) => left.CompareTo(right) <= 0;

    /// <summary>大于（规范形比较）。</summary>
    public static bool operator >(AssetVersion left, AssetVersion right) => left.CompareTo(right) > 0;

    /// <summary>大于或等于（规范形比较）。</summary>
    public static bool operator >=(AssetVersion left, AssetVersion right) => left.CompareTo(right) >= 0;

    /// <summary>按**书写段数**写出（展示用；<c>1.0.0</c> 不被写成 <c>1</c>）。</summary>
    public override string ToString()
    {
        if (SegmentCount == 0)
        {
            return "0";
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < SegmentCount; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            builder.Append(_segments![i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>
/// 资产全名（全局唯一）：<c>{类型名}：{资产名}@{包GUID8}#{版本}</c>。
/// </summary>
/// <param name="Kind">加载方/来源。</param>
/// <param name="LocalName">资产短名（包内唯一）。</param>
/// <param name="PackageGuid8">所属包稳定身份前 8 位（小写十六进制）。</param>
/// <param name="Version">具体版本。</param>
public sealed record AssetName(AssetLoadKind Kind, string LocalName, string PackageGuid8, AssetVersion Version)
{
    /// <summary>规范分隔符（写作用；全角冒号）。</summary>
    public const char CanonicalSeparator = '：';

    /// <summary>类型名 token（规范小写）。</summary>
    public string KindToken => Kind switch
    {
        AssetLoadKind.Json => "json",
        AssetLoadKind.Csx => "csx",
        AssetLoadKind.Asm => "asm",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "未定义的资产加载方。"),
    };

    /// <summary>解析类型名 token（大小写不敏感）。</summary>
    public static bool TryParseKind(string? token, out AssetLoadKind kind)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "json": kind = AssetLoadKind.Json; return true;
            case "csx": kind = AssetLoadKind.Csx; return true;
            case "asm": kind = AssetLoadKind.Asm; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>按规范形式写出（读宽写窄）。</summary>
    public override string ToString()
        => $"{KindToken}{CanonicalSeparator}{LocalName}@{PackageGuid8}#{Version}";

    /// <summary>
    /// 解析资产全名（宽容：接受全角/半角冒号、大小写；<paramref name="guid8"/> 与 <paramref name="localName"/> 统一小写规整）。
    /// </summary>
    public static bool TryParse(string? text, out AssetName? name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hashIndex = text.LastIndexOf('#');
        if (hashIndex <= 0 || hashIndex == text.Length - 1)
        {
            return false;
        }

        if (!AssetVersion.TryParse(text[(hashIndex + 1)..], out var version))
        {
            return false;
        }

        var head = text[..hashIndex];
        var atIndex = head.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == head.Length - 1)
        {
            return false;
        }

        var guid8 = head[(atIndex + 1)..].Trim();
        if (guid8.Length != 8 || !IsHex(guid8))
        {
            return false;
        }

        var ownerAndKind = head[..atIndex];
        var sepIndex = ownerAndKind.IndexOfAny([CanonicalSeparator, ':']);
        if (sepIndex <= 0)
        {
            return false;
        }

        if (!TryParseKind(ownerAndKind[..sepIndex], out var kind))
        {
            return false;
        }

        var localName = ownerAndKind[(sepIndex + 1)..].Trim();
        if (localName.Length == 0)
        {
            return false;
        }

        name = new AssetName(kind, localName, guid8.ToLowerInvariant(), version);
        return true;
    }

    /// <summary>解析资产全名；非法＝<see cref="FormatException"/>。</summary>
    public static AssetName Parse(string text)
        => TryParse(text, out var name)
            ? name!
            : throw new FormatException(
                $"非法资产名 '{text}'（期望 {{类型名}}{CanonicalSeparator}{{资产名}}@{{包GUID8}}#{{1..8 段版本}}）。");

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
