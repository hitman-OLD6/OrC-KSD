using System.Diagnostics.CodeAnalysis;
using Orc.Mcp.Match;

namespace Orc.Mcp.Hosting;

/// <summary>对局生命周期变更类别。</summary>
public enum MatchLifecycleChange
{
    /// <summary>对局已创建并装配到服务层（UI 可据此建立投影）。</summary>
    Created,

    /// <summary>对局已销毁/摘除（UI 必须停止投影，不再读取该对局）。</summary>
    Disposed,
}

/// <summary>对局生命周期事件参数。</summary>
public sealed record MatchLifecycleEventArgs(MatchLifecycleChange Change, Orc.Game.Match? Match);

/// <summary>事件流段取用者归属（同一时刻只有一个实际取用者）。</summary>
public enum SegmentConsumer
{
    /// <summary>服务层负责取段（默认；无头/纯 MCP 形态）。</summary>
    Service,

    /// <summary>宿主负责取段并喂给服务层（UI 形态：宿主帧循环取段，服务层只做缓存）。</summary>
    Host,
}

/// <summary>
/// 宿主集成面：供 UI 宿主（如 Godot 前端）在**进程内**接入服务层——
/// 取得活跃对局句柄、订阅对局生命周期与待决选择请求、参与选靶应答、接管段消费。
/// <para>
/// 设计要点（与需求对齐）：①服务层持有**唯一** <c>ITargeterBridge</c> 与**唯一**门禁，UI 不再直连引擎；
/// ②段取用者按 <see cref="SegmentConsumer"/> 显式声明（默认服务层）；③UI 不持有对局销毁权。
/// </para>
/// </summary>
public interface IMcpHost
{
    /// <summary>当前活跃对局（无＝null）。</summary>
    Orc.Game.Match? ActiveMatch { get; }

    /// <summary>尝试取得当前活跃对局。</summary>
    bool TryGetActiveMatch([NotNullWhen(true)] out Orc.Game.Match? match);

    /// <summary>对局创建/销毁事件（服务层侧触发；订阅者不得阻塞）。</summary>
    event EventHandler<MatchLifecycleEventArgs>? MatchChanged;

    /// <summary>
    /// 订阅**待决选择请求**（UI 渲染候选并回传）。请求由服务层的唯一桥发布；
    /// 应答经 <see cref="TrySubmitTargeting"/>（先到先服务）。
    /// </summary>
    /// <returns>退订句柄（Dispose 幂等）。</returns>
    IDisposable SubscribeTargetingRequests(Action<PendingSelection> onRequest);

    /// <summary>提交待决选择（宿主侧应答入口；与 agent 的 <c>target.submit</c> 共用同一会话状态机）。</summary>
    /// <param name="sessionId">待决请求的会话标识。</param>
    /// <param name="picks">候选序号（1 起）。</param>
    /// <param name="cancel">是否按取消提交。</param>
    /// <param name="claimant">应答者标识（默认 <see cref="Orc.Mcp.Match.TargeterBridgeAdapter.ClaimantHost"/>）。</param>
    /// <param name="reason">失败原因（成功＝null）。</param>
    bool TrySubmitTargeting(
        string sessionId,
        IReadOnlyList<int> picks,
        bool cancel,
        string? claimant,
        out string? reason);

    /// <summary>接管当前待决请求（先到先服务：已被应答的请求拒绝接管）。</summary>
    bool TryClaimTargeting(string sessionId, string claimant, out string? reason);

    /// <summary>当前段取用者归属。</summary>
    SegmentConsumer SegmentConsumer { get; }

    /// <summary>声明段取用者归属（宿主担任时服务层不再自行取段）。</summary>
    void SetSegmentConsumer(SegmentConsumer consumer);

    /// <summary>拉取服务层缓存的段（最近 N 条；<paramref name="take"/> 为 null＝全部）。</summary>
    IReadOnlyList<Orc.Output.EventSegment> PullSegments(int? take = null);

    /// <summary>宿主自取段时喂入服务层缓存（<see cref="SegmentConsumer.Host"/> 形态）。</summary>
    void IngestSegments(IReadOnlyList<Orc.Output.EventSegment> segments);
}

/// <summary>
/// 强类型动作面：与 MCP 工具**共用同一执行器、门禁与 pending 状态机**的 C# 入口——
/// UI 宿主走这里，agent 走 MCP 工具，两条路径一套语义。
/// </summary>
public interface IMcpActions
{
    /// <summary>结束当前回合（引擎入口 <c>Match.EndTurn</c>）。</summary>
    Task<ActionOutcome> EndTurnAsync(CancellationToken ct = default);

    /// <summary>指定玩家认输。</summary>
    Task<ActionOutcome> ConcedeAsync(int ownerIndex, CancellationToken ct = default);

    /// <summary>开始换牌（引擎入口 <c>Match.BeginMulliganAsync</c>）。</summary>
    Task<ActionOutcome> MulliganBeginAsync(int ownerIndex, CancellationToken ct = default);

    /// <summary>确认换牌完成（引擎入口 <c>Match.MulliganDone</c>）。</summary>
    Task<ActionOutcome> MulliganDoneAsync(int ownerIndex, CancellationToken ct = default);

    /// <summary>单位预打出（引擎入口 <c>PlayManager.BeginUnitPrePlayAsync</c>）。</summary>
    Task<ActionOutcome> PlayUnitPreAsync(string cardRef, CancellationToken ct = default);

    /// <summary>单位打出到支援线槽位（引擎入口 <c>PlayManager.PlayUnitAsync</c>）。</summary>
    Task<ActionOutcome> PlayUnitAsync(string cardRef, int ownerIndex, int slotIndex, CancellationToken ct = default);

    /// <summary>单位加入（引擎入口 <c>PlayManager.JoinUnitAsync</c>）。</summary>
    Task<ActionOutcome> JoinUnitAsync(string cardRef, int ownerIndex, int slotIndex, CancellationToken ct = default);

    /// <summary>指令预打出（引擎入口 <c>PlayManager.BeginOrderPrePlayAsync</c>）。</summary>
    Task<ActionOutcome> PlayCommandPreAsync(string cardRef, CancellationToken ct = default);

    /// <summary>指令打出（引擎入口 <c>PlayManager.PlayOrderAsync</c>）。</summary>
    Task<ActionOutcome> PlayCommandAsync(string cardRef, CancellationToken ct = default);

    /// <summary>使用反制（引擎入口 <c>PlayManager.UseCounterAsync</c>）。</summary>
    Task<ActionOutcome> UseCounterAsync(string cardRef, CancellationToken ct = default);

    /// <summary>发起指挥（移动/攻击的统一入口）。</summary>
    Task<ActionOutcome> BeginCommandAsync(string unitRef, CancellationToken ct = default);

    /// <summary>发起移动。</summary>
    Task<ActionOutcome> BeginMoveAsync(string unitRef, CancellationToken ct = default);

    /// <summary>发起攻击。</summary>
    Task<ActionOutcome> BeginAttackAsync(string unitRef, CancellationToken ct = default);

    /// <summary>离开战场。</summary>
    Task<ActionOutcome> LeaveBattlefieldAsync(string unitRef, CancellationToken ct = default);

    /// <summary>查询单位指挥可用性（纯读；UI 高亮与 agent 决策用）。</summary>
    Task<ActionOutcome> GetCommandAvailabilityAsync(string unitRef, CancellationToken ct = default);

    /// <summary>查询卡牌当前是否可打出（纯读）。</summary>
    Task<ActionOutcome> CanPlayAsync(string cardRef, CancellationToken ct = default);
}
