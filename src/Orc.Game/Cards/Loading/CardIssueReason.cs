namespace Orc.Game.Cards.Loading;

/// <summary>
/// 字段/资产问题的**原因枚举**（I10 冻结：报告 <c>fields[].reason</c> 只允许取下表所列值，
/// **禁止自由文本**——自动补全（D44/D45）以本枚举为稳定输入契约）。
/// <para>
/// **加值而非改值**：新增原因＝在表尾追加（旧 token 仍然合法，报告 <c>schemaVersion</c> 不动）；
/// 既有值的名字与 token 一律不得改（否则旧报告无法解析）。
/// </para>
/// </summary>
public enum CardIssueReason
{
    /// <summary>字段（或所引用的资产）缺失。</summary>
    Missing = 0,

    /// <summary>字段存在但类型不符（不可反序列化／结构不是期望形态）。</summary>
    WrongType = 1,

    /// <summary>取值越界（枚举标识未定义、数值超出允许域）。</summary>
    OutOfRange = 2,

    /// <summary>源文件缺失（含孤儿 meta、卡图文件不存在）。</summary>
    SourceMissing = 3,

    /// <summary>sidecar 缺失（按「无元数据」走三档判定）。</summary>
    SidecarMissing = 4,

    /// <summary>sidecar 与源文件 <c>id</c> 不一致（R2/D48：该资产非法）。</summary>
    IdMismatch = 5,

    /// <summary>版本号非法（R17/D27：该资产非法，不生成）。</summary>
    VersionInvalid = 6,

    /// <summary>
    /// <c>loader</c> 引用未注册加载器（或卡包自带加载器）——**结构性拒绝**：
    /// 不是卡级问题，整批不装载（D34）。
    /// </summary>
    LoaderUnregistered = 7,

    /// <summary>本次加载集合内出现双向硬冲突（R16/D52）。</summary>
    Conflict = 8,

    /// <summary>资产 GUID 重复声明/重复注册（结构性）。</summary>
    DuplicateGuid = 9,

    /// <summary>组件脚本（csx）无法在 v4 承载视图下编译——该卡的效果永远不可能生效。</summary>
    ScriptCompileFailed = 10,

    /// <summary>
    /// 声明被内建规则覆盖（R9/D23：老兵卡的显式 <c>deckable</c> 无效）——**警报档**，卡照常生成。
    /// </summary>
    IgnoredByRule = 11,
}

/// <summary>原因 token（**冻结契约**：camelCase 字面值，报告序列化与工具面共用）。</summary>
public static class CardIssueReasonTokens
{
    /// <summary>规范 token（小写驼峰）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的原因。</exception>
    public static string Token(CardIssueReason reason) => reason switch
    {
        CardIssueReason.Missing => "missing",
        CardIssueReason.WrongType => "wrongType",
        CardIssueReason.OutOfRange => "outOfRange",
        CardIssueReason.SourceMissing => "sourceMissing",
        CardIssueReason.SidecarMissing => "sidecarMissing",
        CardIssueReason.IdMismatch => "idMismatch",
        CardIssueReason.VersionInvalid => "versionInvalid",
        CardIssueReason.LoaderUnregistered => "loaderUnregistered",
        CardIssueReason.Conflict => "conflict",
        CardIssueReason.DuplicateGuid => "duplicateGuid",
        CardIssueReason.ScriptCompileFailed => "scriptCompileFailed",
        CardIssueReason.IgnoredByRule => "ignoredByRule",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未定义的问题原因。"),
    };

    /// <summary>解析 token（大小写不敏感；非法＝false）。</summary>
    public static bool TryParse(string? token, out CardIssueReason reason)
    {
        foreach (var candidate in Enum.GetValues<CardIssueReason>())
        {
            if (string.Equals(Token(candidate), token?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reason = candidate;
                return true;
            }
        }

        reason = default;
        return false;
    }
}
