using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Orc.Game.Ai;
using Orc.Mcp.Match;
using GameMatch = Orc.Game.Match;

namespace Orc.Mcp;

/// <summary>
/// <see cref="IAiMatchGateway"/> 的 MCP 服务实现：把 AI 的中性操作面转发到既有服务入口，
/// 并把 <see cref="ActionOutcome"/>（含 MCP 协议载荷）适配成 <see cref="AiActionOutcome"/>——
/// 只保留决策所需三态（失败 / 完成 / 待决），不把会话协议带进 <c>Orc.Ai</c>。
/// </summary>
public sealed partial class McpService : IAiMatchGateway
{
    bool IAiMatchGateway.TryGetActiveMatch([NotNullWhen(true)] out GameMatch? match) => TryGetActiveMatch(out match);

    void IAiMatchGateway.ExpectClaimant(string claimant) => ExpectClaimant(claimant);

    async Task<AiActionOutcome> IAiMatchGateway.PlayUnitAsync(string cardRef, int seat, int slot, CancellationToken ct)
        => ToAi(slot < 0
            ? await PlayUnitPreAsync(cardRef, ct).ConfigureAwait(false)
            : await PlayUnitAsync(cardRef, seat, slot, ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.PlayCommandPreAsync(string cardRef, CancellationToken ct)
        => ToAi(await PlayCommandPreAsync(cardRef, ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.UseCounterAsync(string cardRef, CancellationToken ct)
        => ToAi(await UseCounterAsync(cardRef, ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.BeginAttackAsync(string unitRef, CancellationToken ct)
        => ToAi(await BeginAttackAsync(unitRef, ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.BeginMoveAsync(string unitRef, CancellationToken ct)
        => ToAi(await BeginMoveAsync(unitRef, ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.EndTurnAsync(CancellationToken ct)
        => ToAi(await EndTurnAsync(ct).ConfigureAwait(false));

    async Task<AiActionOutcome> IAiMatchGateway.MulliganDoneAsync(int seat, CancellationToken ct)
        => ToAi(await MulliganDoneAsync(seat, ct).ConfigureAwait(false));

    async Task<AiCommandAvailability> IAiMatchGateway.GetCommandAvailabilityAsync(string unitRef, CancellationToken ct)
    {
        var outcome = await GetCommandAvailabilityAsync(unitRef, ct).ConfigureAwait(false);
        var payload = outcome.Payload as JsonObject;
        return new AiCommandAvailability(IsTrue(payload?["attackAvailable"]), IsTrue(payload?["moveAvailable"]));
    }

    async Task<AiActionOutcome> IAiMatchGateway.SubmitAsync(
        string sessionId, IReadOnlyList<int> picks, bool cancel, CancellationToken ct, string claimant)
    {
        // 归属座位校验：本网关只服务 **AI 驱动的座位**；待决请求若属于 agent 座位，
        // 必须由该座位的 agent 应答，AI 不得代答。
        // 判据方向必须是「属于非 AI 座位才拒绝」：AI 自己发起的选靶 Seat＝AI 座位，
        // 反向判定（不属于非 AI 座位就拒绝）会把 AI 自己的请求一并拒掉 → 对局死锁在 AI 回合（实测过）。
        if (Bridge.CurrentPending is { Seat: { } ownerSeat }
            && Options.AiSeats.Count > 0
            && !Options.AiSeats.Contains(ownerSeat))
        {
            return AiActionOutcome.Fail($"该选靶属于 agent 座位 {ownerSeat}，AI 控制器不应答。");
        }

        return ToAi(await Executor.SubmitAsync(sessionId, picks, cancel, ct, claimant).ConfigureAwait(false));
    }

    private static bool IsTrue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>MCP 结局 → AI 中性结局（取消按成功处理，与 <c>AiStepResult.Succeeded</c> 口径一致）。</summary>
    private static AiActionOutcome ToAi(ActionOutcome outcome)
    {
        if (outcome.Status == ActionOutcome.StatusFailed)
        {
            return AiActionOutcome.Fail(outcome.FailureMessage ?? outcome.FailureCode);
        }

        return outcome.Pending is { } pending
            ? AiActionOutcome.Targeting(new AiPending(
                pending.SessionId,
                pending.SelectorName,
                pending.Min,
                pending.Max,
                pending.Candidates.Select(c => new AiCandidate(c.Index, c.Kind, c.ReferenceKey)).ToArray(),
                pending.Identifiers))
            : AiActionOutcome.Success();
    }
}
