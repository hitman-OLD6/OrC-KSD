namespace Orc.Packages.Editing;

/// <summary>编辑器错误码（结构化；供 MCP 工具面映射为人读/错误码）。</summary>
public enum AssetEditorErrorCode
{
    /// <summary>没有活动编辑会话（未 begin 或已 end）。</summary>
    NoSession = 0,

    /// <summary>已有活动编辑会话（同时只允许一个）。</summary>
    SessionAlreadyActive = 1,

    /// <summary>包目录不存在或缺少 manifest。</summary>
    PackageNotFound = 2,

    /// <summary>同名包目录／临时包已存在（**不清理、不复用**）。</summary>
    TemporaryPackageExists = 3,

    /// <summary>路径越界或未获授权（UNC／<c>..</c>／符号链接逃逸／父目录未授权）。</summary>
    PathDenied = 4,

    /// <summary>资产未声明。</summary>
    AssetNotFound = 5,

    /// <summary>资产已声明（同 kind ＋ 局部 ID 只能有一个来源）。</summary>
    AssetExists = 6,

    /// <summary>版本号格式非法（非 1..8 段纯数字）。</summary>
    InvalidVersion = 7,

    /// <summary>版本未严格大于旧版（降级或补 0 后相等）。</summary>
    VersionNotIncreasing = 8,

    /// <summary>内容不可解析／与 sidecar 不一致／不支持的提交类型。</summary>
    InvalidContent = 9,

    /// <summary>包结构非法（manifest／sidecar 不可解析、同一资产重复声明）。</summary>
    InvalidPackage = 10,

    /// <summary>依赖声明非法（空白、约束不可解析、依赖自身）。</summary>
    DependencyInvalid = 11,

    /// <summary>新建包缺少期望依赖包 ID（R8 硬要求）。</summary>
    DependenciesRequired = 12,

    /// <summary>转正落地失败（已回滚；临时包保留）——见消息内的失败项与回滚明细。</summary>
    PromoteFailed = 13,
}

/// <summary>
/// 后端编辑器异常（结构化错误码 ＋ 人读原因）：所有**预期失败**都走本类型，
/// 便于前端与 agent 以同一份判定消费。
/// </summary>
public sealed class AssetEditorException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">人读原因。</param>
    /// <param name="inner">内层异常。</param>
    public AssetEditorException(AssetEditorErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
        => Code = code;

    /// <summary>错误码。</summary>
    public AssetEditorErrorCode Code { get; }
}
