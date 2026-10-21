using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp.Tools;

/// <summary>
/// 目标选择应答工具：会话化 pending 的推进入口（与宿主 UI 的应答共用同一会话状态机，**先到先服务**）。
/// </summary>
internal static class TargetTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "target.submit",
            "target",
            "应答当前待决选择请求：picks 为候选**序号**数组（1 起；取自待决请求的 candidates[].index）。"
            + "提交后返回动作结局，或再次待决（多步选择）。已被另一方认领的请求拒绝应答（先到先服务）。",
            """
            {
              "type": "object",
              "properties": {
                "sessionId": { "type": "string", "description": "待决请求的 sessionId" },
                "picks": { "type": "array", "items": { "type": "integer" }, "description": "候选序号（1 起）" }
              },
              "required": ["sessionId"],
              "additionalProperties": false
            }
            """,
            (call, ct) => SubmitAsync(service, call, ct, cancel: false));

        service.AddTool(
            "target.cancel",
            "target",
            "取消当前待决选择请求（等价引擎 CancelEvent；结果按 Cancelled 成功处理）。",
            """
            {
              "type": "object",
              "properties": {
                "sessionId": { "type": "string", "description": "待决请求的 sessionId" }
              },
              "required": ["sessionId"],
              "additionalProperties": false
            }
            """,
            (call, ct) => SubmitAsync(service, call, ct, cancel: true));

        service.AddTool(
            "target.claim",
            "target",
            "接管当前待决选择请求（先到先服务：已被应答的请求拒绝接管）。claimant 缺省＝service（agent）。",
            """
            {
              "type": "object",
              "properties": {
                "sessionId": { "type": "string" },
                "claimant": { "type": "string", "description": "认领者标识（缺省 service；宿主 UI 用 host）" }
              },
              "required": ["sessionId"],
              "additionalProperties": false
            }
            """,
            (call, ct) =>
            {
                var sessionId = ToolArgs.RequireString(call.Arguments, "sessionId");
                var claimant = ToolArgs.OptionalString(call.Arguments, "claimant") ?? TargeterBridgeAdapter.ClaimantService;

                var claimed = service.TryClaimTargeting(sessionId, claimant, out var reason);
                return Task.FromResult(claimed
                    ? McpToolResult.Success($"已认领待决请求（claimant={claimant}）。")
                    : McpToolResult.Failure(McpCodes.PendingTargeting, reason ?? "认领失败。"));
            });
    }

    private static async Task<McpToolResult> SubmitAsync(
        McpService service,
        McpToolCall call,
        CancellationToken ct,
        bool cancel = false)
    {
        var sessionId = ToolArgs.RequireString(call.Arguments, "sessionId");
        var picks = cancel ? Array.Empty<int>() : ToolArgs.OptionalIntList(call.Arguments, "picks");

        var outcome = await service.Executor
            .SubmitAsync(sessionId, picks, cancel, ct, TargeterBridgeAdapter.ClaimantService)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, cancel ? "已提交取消。" : "已提交选择。");
    }
}
