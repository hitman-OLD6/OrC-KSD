namespace Orc.Game.Cards.Loading;

/// <summary>字段问题的严重度（**内部判定面**；报告 schema 不含本字段——档位由卡级聚合承载）。</summary>
public enum CardIssueSeverity
{
    /// <summary>警报（可玩；字段已补类型默认值）。</summary>
    Warning = 0,

    /// <summary>非法（整卡不生成）。</summary>
    Invalid = 1,
}

/// <summary>
/// 一条**字段问题描述**（R5/D41；报告 <c>fields[]</c> 的元素）：字段路径 ＋ 期望类型 ＋ 默认值 ＋ 原因枚举。
/// <para>
/// 这是自动补全机制（D44/D45）的输入契约：键名与原因取值均冻结（I10），补全方据此把默认值/生成值写回残缺卡。
/// 严重度只在进程内判定与聚合时使用，**不序列化**（schema 无该键）。
/// </para>
/// </summary>
/// <param name="Path">字段路径（数据体点路径，如 <c>name</c>／<c>components.factionCost</c>／<c>components.effects.prefabs[0]</c>）。</param>
/// <param name="ExpectedType">期望类型（<c>string</c>／<c>integer</c>／<c>object</c>／<c>array</c>／<c>AssetVersion</c> 等）。</param>
/// <param name="DefaultValue">缺失/不可用时的类型默认值（0／null／空串；复杂类型＝null）。</param>
/// <param name="Reason">原因（**只取枚举值**）。</param>
/// <param name="Severity">严重度（进程内判定用；不序列化）。</param>
public sealed record CardFieldIssue(
    string Path,
    string ExpectedType,
    object? DefaultValue,
    CardIssueReason Reason,
    CardIssueSeverity Severity)
{
    /// <summary>是否为非法项（卡级聚合：任一非法项 ⇒ 整卡非法）。</summary>
    public bool IsInvalid => Severity == CardIssueSeverity.Invalid;

    /// <summary>创建非法项。</summary>
    public static CardFieldIssue Invalid(string path, string expectedType, object? defaultValue, CardIssueReason reason)
        => new(path, expectedType, defaultValue, reason, CardIssueSeverity.Invalid);

    /// <summary>创建警报项。</summary>
    public static CardFieldIssue Warning(string path, string expectedType, object? defaultValue, CardIssueReason reason)
        => new(path, expectedType, defaultValue, reason, CardIssueSeverity.Warning);
}
