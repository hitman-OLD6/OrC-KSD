namespace Orc.Core.Model;

/// <summary>
/// 成员地址（R1d：<c>作用域#成员名</c>）。
/// <para>
/// **解析规则（唯一契约）**：按**第一个 <c>#</c>** 切分——之前是作用域标识（<c>engine</c>／<c>host</c>／<c>self</c>），
/// 之后**整体**是成员名。不按固定段数切分、不按 <c>.</c> 再切、不做任何分隔符替换。
/// </para>
/// <para>
/// 因此含点号的内置信号名（<c>unit.position.changed</c>、<c>card.destroyed</c> …）可零转换寻址：
/// 旧 <c>hooks: ["unit.position.changed"]</c> → <c>engine#unit.position.changed</c>（无损、可逆）。
/// </para>
/// <para>
/// **禁止**出现任何"显示名"字符串（R1d／R1j）：显示名是本次重构要消灭的东西。
/// </para>
/// </summary>
public readonly struct MemberAddress : IEquatable<MemberAddress>
{
    private const char Separator = '#';

    private MemberAddress(AddressScope scope, string member)
    {
        Scope = scope;
        Member = member;
    }

    /// <summary>作用域。</summary>
    public AddressScope Scope { get; }

    /// <summary>成员名（原样保留，允许含 <c>.</c> 及后续 <c>#</c>）。</summary>
    public string Member { get; }

    /// <summary>构造地址（不做解析；供代码内的常量式引用使用）。</summary>
    /// <exception cref="ArgumentNullException">member 为 null。</exception>
    /// <exception cref="ArgumentException">member 为空白，或含首个分隔符以外的非法字符（此处仅校验空白）。</exception>
    public static MemberAddress Create(AddressScope scope, string member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return new MemberAddress(scope, member);
    }

    /// <summary>解析 <c>作用域#成员名</c>。</summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    /// <exception cref="ArgumentException">缺少分隔符、作用域未知、成员名为空或纯空白（错误消息指出作用域与成员名）。</exception>
    public static MemberAddress Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = text.IndexOf(Separator);
        if (index < 0)
        {
            throw new ArgumentException(
                $"成员地址 '{text}' 非法：缺少作用域分隔符 '#'（正确形态＝'作用域#成员名'，作用域∈engine/host/self）。",
                nameof(text));
        }

        var scopeText = text[..index];
        var member = text[(index + 1)..];

        if (string.IsNullOrWhiteSpace(member))
        {
            throw new ArgumentException(
                $"成员地址 '{text}' 非法：作用域 '{scopeText}' 之后的成员名为空。", nameof(text));
        }

        var scope = scopeText switch
        {
            "engine" => AddressScope.Engine,
            "host" => AddressScope.Host,
            "self" => AddressScope.Self,
            _ => throw new ArgumentException(
                $"成员地址 '{text}' 非法：未知作用域 '{scopeText}'（只允许 engine／host／self）。", nameof(text)),
        };

        return new MemberAddress(scope, member);
    }

    /// <summary>尝试解析（不抛异常；失败经 <paramref name="error"/> 给出原因）。</summary>
    public static bool TryParse(string? text, out MemberAddress address, out string? error)
    {
        address = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "成员地址为空。";
            return false;
        }

        try
        {
            address = Parse(text);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>作用域标识（<c>engine</c>／<c>host</c>／<c>self</c>）。</summary>
    public string ScopeText => Scope switch
    {
        AddressScope.Engine => "engine",
        AddressScope.Host => "host",
        AddressScope.Self => "self",
        _ => throw new ArgumentOutOfRangeException(nameof(Scope), Scope, "未定义的作用域。"),
    };

    /// <inheritdoc />
    public bool Equals(MemberAddress other) => Scope == other.Scope && string.Equals(Member, other.Member, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MemberAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine((int)Scope, Member is null ? 0 : StringComparer.Ordinal.GetHashCode(Member));

    /// <summary>相等运算符。</summary>
    public static bool operator ==(MemberAddress left, MemberAddress right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(MemberAddress left, MemberAddress right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"{ScopeText}{Separator}{Member}";
}
