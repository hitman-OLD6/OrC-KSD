namespace Orc.Core;

/// <summary>
/// 资产域（R14/R15/D20）：正式／测试。域标记**随实例走**（卡实例与 <c>CardLibrary</c> 条目同样携带），
/// 仅用于构筑/存档、UI 展示与诊断报告——**不得参与任何玩法逻辑判定**。
/// <para>域的来源是「是否合并测试 lock」：无测试 lock 即无测试资产，故不设对局级开关。</para>
/// </summary>
public enum AssetDomain
{
    /// <summary>正式域（正式 lock）。</summary>
    Formal = 0,

    /// <summary>测试域（测试 lock；可遮蔽同应用层 ID 的正式资产）。</summary>
    Test = 1,
}

/// <summary>资产域 token（规范小写：<c>formal</c>／<c>test</c>）读写。</summary>
public static class AssetDomainTokens
{
    /// <summary>规范 token。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的域。</exception>
    public static string Token(AssetDomain domain) => domain switch
    {
        AssetDomain.Formal => "formal",
        AssetDomain.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "未定义的资产域。"),
    };

    /// <summary>解析 token（大小写不敏感；非法＝false）。</summary>
    public static bool TryParse(string? token, out AssetDomain domain)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "formal": domain = AssetDomain.Formal; return true;
            case "test": domain = AssetDomain.Test; return true;
            default: domain = default; return false;
        }
    }
}
