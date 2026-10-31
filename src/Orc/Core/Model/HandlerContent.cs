using System.Security.Cryptography;
using System.Text;

namespace Orc.Core.Model;

/// <summary>
/// **handler 的内容寻址身份**（R28／S11／D9）：handler id 由 csx **文本内容**派生，不是分配出来的稳定名。
/// <para>
/// <b>两个「id 稳定」不是一回事，代码里不得混用</b>：
/// <list type="bullet">
///   <item><b>产物资产 id 稳定</b>——moding 产物必须是一个**新资产 id**（Constraints「id 唯一」），
///     二次摊平只在同一 id 上按既有版本规则递增（R8）。</item>
///   <item><b>handler id 跟内容走</b>——同一份文本恒得同一个 id；文本一变（改 DSL、改视图导致生成的
///     <c>view.X</c> 访问不同），id 随之变化，旧 id 消失、新 id 出现。这是**有意**的：
///     它让「同 id 必然同源码」成为构造性事实，于是 <c>HandlerRegistry.RegisterScript</c> 那条
///     「同 id 已被另一份源码占用＝拒绝」在这种形态下**永远不会被触发**，跨资产引用同一文本也自动共享一个注册项。</item>
/// </list>
/// </para>
/// <para>
/// 形式（D9）：<c>hdsl/v1/&lt;sha256(csx) 前 16 字节 hex&gt;</c>（由 DSL 生成）／
/// <c>csx/v1/&lt;sha256(csx) 前 16 字节 hex&gt;</c>（原样复制的手写 csx）。
/// 别把 handler id 当可读标识：可读性走 <see cref="HandlerGeneratedBlock.Source"/>。
/// </para>
/// </summary>
public static class HandlerContent
{
    /// <summary>由 DSL 生成的 handler id 前缀。</summary>
    public const string GeneratedPrefix = "hdsl/v1/";

    /// <summary>原样复制／手写的 csx handler id 前缀。</summary>
    public const string CopiedPrefix = "csx/v1/";

    /// <summary>内容哈希的文本前缀（与 <see cref="HandlerGeneratedBlock"/> 的 <c>sha256:…</c> 同形）。</summary>
    public const string HashPrefix = "sha256:";

    /// <summary>id 里取用的哈希字节数（前 16 字节 ⇒ 32 个 hex 字符）。</summary>
    public const int AddressHashBytes = 16;

    /// <summary>
    /// csx 文本的内容哈希（<c>sha256:&lt;64 hex&gt;</c>，小写、不变文化）。
    /// 这是 <see cref="HandlerGeneratedBlock.TextHash"/>／<c>dslHash</c> 的同一口径。
    /// </summary>
    /// <exception cref="ArgumentNullException">text 为 null。</exception>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HashPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>文本哈希是否与给定哈希一致（大小写不敏感，允许省略 <c>sha256:</c> 前缀）。</summary>
    public static bool Matches(string text, string expected)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expected);

        var normalized = expected.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase)
            ? expected[HashPrefix.Length..]
            : expected;

        return string.Equals(Hash(text), HashPrefix + normalized.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 按文本派生 handler id（内容寻址）。
    /// </summary>
    /// <param name="csx">csx 文本（逐字节参与哈希，不做任何规范化）。</param>
    /// <param name="generated">true＝由 DSL 生成（<c>hdsl/v1/</c>）；false＝原样复制／手写（<c>csx/v1/</c>）。</param>
    /// <exception cref="ArgumentNullException">csx 为 null。</exception>
    public static string AddressOf(string csx, bool generated)
    {
        ArgumentNullException.ThrowIfNull(csx);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(csx));
        var hex = Convert.ToHexString(digest.AsSpan(0, AddressHashBytes)).ToLowerInvariant();
        return (generated ? GeneratedPrefix : CopiedPrefix) + hex;
    }

    /// <summary>id 是否由 DSL 生成（<c>hdsl/v1/</c> 前缀）。</summary>
    public static bool IsGenerated(string? handlerId)
        => handlerId is not null && handlerId.StartsWith(GeneratedPrefix, StringComparison.Ordinal);

    /// <summary>id 是否是内容寻址形态（<c>hdsl/v1/…</c> 或 <c>csx/v1/…</c>）。</summary>
    public static bool IsContentAddressed(string? handlerId)
        => handlerId is not null
           && (handlerId.StartsWith(GeneratedPrefix, StringComparison.Ordinal)
               || handlerId.StartsWith(CopiedPrefix, StringComparison.Ordinal));
}
