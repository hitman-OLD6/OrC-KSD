namespace Orc.Mcp;

/// <summary>
/// 服务层运行模式（沙盒模式＝服务层的运行模式，**不是引擎的运行态**）。
/// </summary>
public enum McpServiceMode
{
    /// <summary>卡牌编辑者（沙盒）：暴露全部调试类工具（B 档：资产/定义层 ＋ 对局塑造）。</summary>
    Authoring,

    /// <summary>agent 玩家：只暴露与真人玩家对称的交互面（本轮不交付，仅保留模式位）。</summary>
    Player,
}

/// <summary>
/// 服务层运行选项。
/// </summary>
public sealed class McpServiceOptions
{
    /// <summary>运行模式（决定工具集与门控）。</summary>
    public McpServiceMode Mode { get; init; } = McpServiceMode.Authoring;

    /// <summary>
    /// 沙盒写根（**唯一可写根**）：全部写类工具的路径必须位于其下。
    /// 沙盒卡包结构＝<c>&lt;SandboxRoot&gt;/pack/{manifest.json,cards/,effects/}</c>。
    /// </summary>
    public required string SandboxRoot { get; init; }

    /// <summary>正式资产读根（只读；缺省＝null＝不暴露正式卡包目录）。</summary>
    public string? PacksRoot { get; init; }

    /// <summary>插件根（只读；缺省＝null）。</summary>
    public string? PluginsRoot { get; init; }

    /// <summary>
    /// 是否启用产物提升（沙盒 → 正式卡包）。**默认关闭**；开启后仍要求"无活跃沙盒局"且目标卡包已存在。
    /// </summary>
    public bool PromoteEnabled { get; init; }

    /// <summary>
    /// 是否启用 csx 编辑/求值能力。**默认关闭**（引擎沙箱为"尽力而为的弱沙箱、非安全边界"）。
    /// </summary>
    public bool CsxEnabled { get; init; }

    /// <summary>目标选择待决会话的等待上限（超时按取消处理）。默认 10 分钟。</summary>
    public TimeSpan TargetSelectionTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>服务层保留的事件流段缓存容量（最近 N 段；段本身"取走即移除"）。默认 200。</summary>
    public int SegmentCacheCapacity { get; init; } = 200;

    /// <summary>服务层契约版本（独立于引擎内部格式版本；经 <c>service.info</c> 暴露）。</summary>
    public int ContractSchemaVersion { get; init; } = 1;

    /// <summary>对局标识（引擎侧无对局 id 概念；由宿主/服务层提供——投影与工具返回使用）。</summary>
    public string MatchId { get; init; } = "sandbox";

    /// <summary>沙盒卡包 id（脚手架 manifest 的 <c>id</c>）。</summary>
    public string SandboxPackId { get; init; } = "sandbox";

    /// <summary>沙盒脚手架中的占位卡 id（满足引擎"卡组名单非空"构造契约）。</summary>
    public string PlaceholderCardId { get; init; } = "sandbox.placeholder";

    /// <summary>随机种子（缺省＝null＝引擎自动生成；沙盒可指定以复现）。</summary>
    public int? Seed { get; init; }

    /// <summary>
    /// agent 的座位（player 模式语义，R9d）：player 模式下 agent 占一个座位、视图按座位过滤隐藏信息。
    /// 缺省＝null＝未指定（player 模式下的观察面将拒绝并提示需配置座位）。
    /// </summary>
    public int? AgentSeat { get; init; }

    /// <summary>
    /// 是否注册**写类**调试工具（无头沙盒 S3）。缺省 <c>true</c>＝保留服务层既有调用方行为；
    /// 服务端 <c>sandbox</c> 模式显式传入（缺省 <c>false</c>）以实现"默认零写面"。
    /// <para>开启后写类仍只对"回环 ＋ 未绑定座位"的编排连接可见可用；已绑定座位的会话不可见。</para>
    /// </summary>
    public bool AllowDebugWrites { get; init; } = true;
}
