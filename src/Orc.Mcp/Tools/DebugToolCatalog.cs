namespace Orc.Mcp.Tools;

/// <summary>
/// 调试类工具清单（**单一来源**，无头沙盒 S3）：注册面、门禁与守卫测试共用，避免三份名单互相漂移。
/// <para>
/// 读类＝<c>observe.snapshot</c>/<c>observe.segments</c>/全知 <c>observe.view</c>/<c>sandbox.state</c>/<c>diag.*</c>
/// （以及纯查询性质的 <c>card.list</c>/<c>pack.info</c>/<c>file.*</c>/<c>editor.*_list|_get|lock_preview</c>）；
/// 写类＝会改对局/资产/编辑会话状态的那一批，默认**不注册**，需 <c>--allow-debug-writes</c>。
/// </para>
/// </summary>
public static class DebugToolCatalog
{
    /// <summary>写类调试工具（默认不注册；开启后仅"回环 ＋ 未绑定座位"的编排连接可见可用）。</summary>
    public static readonly IReadOnlySet<string> WriteToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // 对局塑造（shape.*）
        "sandbox.spawn",
        "sandbox.kill",
        "sandbox.damage",
        "sandbox.set_points",
        "sandbox.emit",

        // 沙盒调试原语（R5；与既有 shaping 工具并存、不合并）
        "sandbox.instantiate",
        "sandbox.unitize",
        "sandbox.move",
        "sandbox.remove",
        "sandbox.points",
        "sandbox.draw",

        // 沙盒生命周期
        "sandbox.create",
        "sandbox.dispose",

        // 卡/资产定义层
        "card.load",
        "card.write",
        "effect.write",
        "asset.promote",

        // 编辑器会话与资产提交
        "editor.session_begin",
        "editor.session_end",
        "editor.asset_commit",
        "editor.asset_create",
        "editor.asset_delete",
        "editor.version_set",
        "editor.dep_set",
        "editor.promote",
        "editor.reload",
    };

    /// <summary>读类调试工具（sandbox 下默认注册、所有连接可见，含已绑定座位的 agent）。</summary>
    public static readonly IReadOnlySet<string> ReadToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "observe.snapshot",
        "observe.segments",
        "sandbox.state",
        "diag.report",
        "diag.audit_chain",
        "diag.hooks",
        "diag.entry_points",
        "diag.card_load",
        "card.list",
        "pack.info",
        "asset.validate",
        "file.list",
        "file.read",
        "editor.pack_list",
        "editor.asset_list",
        "editor.asset_get",
        "editor.lock_preview",
    };

    /// <summary>是否写类调试工具。</summary>
    public static bool IsWriteTool(string? name) => name is not null && WriteToolNames.Contains(name);

    /// <summary>是否读类调试工具（<c>observe.view</c> 走专门分支，不在此列）。</summary>
    public static bool IsReadTool(string? name) => name is not null && ReadToolNames.Contains(name);

    /// <summary>是否调试类工具（读类 ∪ 写类 ∪ 全知观察面）。</summary>
    public static bool IsDebugTool(string? name)
        => IsWriteTool(name) || IsReadTool(name) || string.Equals(name, "observe.view", StringComparison.Ordinal);
}
