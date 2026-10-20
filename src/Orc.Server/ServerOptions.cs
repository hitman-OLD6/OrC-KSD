namespace Orc.Server;

/// <summary>
/// 服务端启动选项（多人支持 S1 骨架；S2/S8 起由宿主与传输层消费）。
/// 默认值取自需求与实现 grill 的确认结果。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>卡包目录列表（房主"开启房间"时指定；服务端按此装载卡池）。</summary>
    public required IReadOnlyList<string> PackDirectories { get; init; }

    /// <summary>沙盒写根（沿用服务层唯一可写根语义）。</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>日志目录（stderr 落盘目标；<c>null</c>＝不落盘）。</summary>
    public string? LogDirectory { get; init; }

    /// <summary>监听端口（0＝由系统分配，实际端口经 stdout 就绪行回报）。</summary>
    public int Port { get; init; }

    /// <summary>座位数（本阶段固定 1v1）。</summary>
    public int SeatCount { get; init; } = 2;

    /// <summary>对局态断线后，轮到断线方的宽限时长（超时由服务端代该座位认输）。</summary>
    public TimeSpan DisconnectGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>房间态断线后的座位保留时长（0＝立即释放）。</summary>
    public TimeSpan LobbySeatRetention { get; init; } = TimeSpan.Zero;

    /// <summary>卡组张数要求（房间态校验与 AI 生成共用）。</summary>
    public int RequiredDeckSize { get; init; } = 39;

    /// <summary>卡包内容指纹（两端对账用；<c>null</c>＝不提供）。</summary>
    public string? PackFingerprint { get; init; }

    /// <summary>心跳超时（超过即关闭连接；TCP 半开连接不会产生 FIN，必须靠应用层心跳）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 是否执行完整卡组规则（生产＝true）。仅端到端/诊断场景可关闭
    /// （示例卡包卡种不足时无法组成 39 张合法构筑）；协议面不暴露该开关。
    /// </summary>
    public bool EnforceDeckRules { get; init; } = true;

    /// <summary>
    /// 父进程 PID（客户端拉起时传入）。父进程消失则本进程退出——
    /// TCP 之外没有可靠的父子存活信号，这是"客户端崩溃后服务端不残留"的兜底。
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>选靶待决等待上限（超时按取消处理并推 <c>notify/target.expired</c>）。默认 10 分钟。</summary>
    public TimeSpan TargetSelectionTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
