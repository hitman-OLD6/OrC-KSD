using System.Diagnostics.CodeAnalysis;

namespace Orc.Game.Ai;

/// <summary>
/// AI 动作结局（**中性形态**）：只保留决策所需的三态——失败 / 完成 / 待决。
/// 不携带 MCP 协议载荷（<c>JsonNode</c>）与会话细节，宿主各自适配。
/// </summary>
/// <param name="Failed">是否失败（未执行/被拒绝）。取消按成功处理。</param>
/// <param name="FailureMessage">失败说明（宿主可给原文）。</param>
/// <param name="Pending">待决选择请求（非 null ＝动作挂起，需应答）。</param>
public sealed record AiActionOutcome(bool Failed, string? FailureMessage = null, AiPending? Pending = null)
{
    /// <summary>动作是否挂起在选择器上。</summary>
    public bool IsTargeting => Pending is not null;

    /// <summary>成功（含引擎三态中的取消）。</summary>
    public static AiActionOutcome Success() => new(false);

    /// <summary>失败。</summary>
    public static AiActionOutcome Fail(string? message = null) => new(true, message);

    /// <summary>挂起待决。</summary>
    public static AiActionOutcome Targeting(AiPending pending) => new(false, Pending: pending);
}

/// <summary>待决候选（会话内序号 ＋ 引擎引用键 ＋ 类别）。</summary>
public sealed record AiCandidate(int Index, string Kind, string ReferenceKey);

/// <summary>待决选择请求（中性形态：只含 AI 选目标需要的字段）。</summary>
public sealed record AiPending(
    string SessionId,
    string SelectorName,
    int Min,
    int Max,
    IReadOnlyList<AiCandidate> Candidates,
    IReadOnlyList<string> Identifiers);

/// <summary>指挥可用性（AI 决策需要的两个布尔面）。</summary>
public readonly record struct AiCommandAvailability(bool AttackAvailable, bool MoveAvailable);

/// <summary>
/// AI 需要的对局操作面（宿主实现：MCP 服务 或 游戏内 bridge）。
/// 决策能力在 <c>Orc.Ai.AiPilot</c> 与 <c>Orc.Ai.HeuristicPolicy</c>，本接口只做入口转发。
/// </summary>
public interface IAiMatchGateway
{
    /// <summary>当前活跃对局（无对局＝false）。</summary>
    bool TryGetActiveMatch([NotNullWhen(true)] out Match? match);

    /// <summary>声明后续待决请求的预期应答者（自己动作链引起的才接管）。</summary>
    void ExpectClaimant(string claimant);

    /// <summary>打出手牌单位（<paramref name="slot"/> &lt; 0 ＝由引擎给候选）。</summary>
    Task<AiActionOutcome> PlayUnitAsync(string cardRef, int seat, int slot, CancellationToken ct);

    /// <summary>打出指令（预打出，可能进入选靶）。</summary>
    Task<AiActionOutcome> PlayCommandPreAsync(string cardRef, CancellationToken ct);

    /// <summary>使用反制。</summary>
    Task<AiActionOutcome> UseCounterAsync(string cardRef, CancellationToken ct);

    /// <summary>发起攻击。</summary>
    Task<AiActionOutcome> BeginAttackAsync(string unitRef, CancellationToken ct);

    /// <summary>发起移动。</summary>
    Task<AiActionOutcome> BeginMoveAsync(string unitRef, CancellationToken ct);

    /// <summary>结束当前回合。</summary>
    Task<AiActionOutcome> EndTurnAsync(CancellationToken ct);

    /// <summary>确认换牌（不换牌）。</summary>
    Task<AiActionOutcome> MulliganDoneAsync(int seat, CancellationToken ct);

    /// <summary>查询指挥可用性（只读）。</summary>
    Task<AiCommandAvailability> GetCommandAvailabilityAsync(string unitRef, CancellationToken ct);

    /// <summary>应答待决请求（<paramref name="picks"/> 为会话内序号）。</summary>
    Task<AiActionOutcome> SubmitAsync(
        string sessionId, IReadOnlyList<int> picks, bool cancel, CancellationToken ct, string claimant);
}
