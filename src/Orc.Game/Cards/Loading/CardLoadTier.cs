namespace Orc.Game.Cards.Loading;

/// <summary>
/// 卡牌加载三档（R5/D40/D41）：**合法**（无任何问题）／**警报**（可玩，缺失字段已按类型默认值填充）／
/// **非法**（不生成卡，牌堆该卡位留空）。
/// <para>
/// 档位是**卡级聚合**结果：任一非法项 ⇒ 整卡非法（D41）；警报只在无非法项时成立。
/// 骨架固定为三档，插件只能追加检查项、不得改变骨架（D35）。
/// </para>
/// </summary>
public enum CardLoadTier
{
    /// <summary>合法（无问题）。</summary>
    Valid = 0,

    /// <summary>警报（可玩；缺失字段已按类型默认值填充并留痕）。</summary>
    Warning = 1,

    /// <summary>非法（不生成卡；牌堆卡位留空、开局继续）。</summary>
    Invalid = 2,
}

/// <summary>三档 token（**冻结契约**：报告 schema 的 <c>tier</c> 取值只允许这三个小写字面值）。</summary>
public static class CardLoadTierTokens
{
    /// <summary>合法档 token。</summary>
    public const string Valid = "valid";

    /// <summary>警报档 token。</summary>
    public const string Warning = "warning";

    /// <summary>非法档 token。</summary>
    public const string Invalid = "invalid";

    /// <summary>规范 token（小写）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的档位。</exception>
    public static string Token(CardLoadTier tier) => tier switch
    {
        CardLoadTier.Valid => Valid,
        CardLoadTier.Warning => Warning,
        CardLoadTier.Invalid => Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "未定义的卡牌加载档位。"),
    };

    /// <summary>解析 token（大小写不敏感；非法＝false）。</summary>
    public static bool TryParse(string? token, out CardLoadTier tier)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case Valid: tier = CardLoadTier.Valid; return true;
            case Warning: tier = CardLoadTier.Warning; return true;
            case Invalid: tier = CardLoadTier.Invalid; return true;
            default: tier = default; return false;
        }
    }
}
