using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp.Tools;

/// <summary>
/// 执行器结局 → 统一返回外壳 的映射（所有动作类工具共用；保证取消/失败/待决的语义一致）。
/// </summary>
internal static class ToolResults
{
    public static McpToolResult FromOutcome(ActionOutcome outcome, string successMessage)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome.Status)
        {
            case ActionOutcome.StatusCompleted:
                return McpToolResult.Success(successMessage, outcome.Payload);

            case ActionOutcome.StatusCancelled:
                // R5：引擎三态中的"取消"是合法结局——按成功结果处理（与失败严格区分）。
                return McpToolResult.SuccessWith(
                    McpCodes.Cancelled,
                    "动作被取消（引擎三态 Cancelled）——按成功结果处理。",
                    outcome.Payload);

            case ActionOutcome.StatusTargeting:
                return McpToolResult.SuccessWith(
                    McpCodes.Targeting,
                    "动作已进入待决选择（请用 target.submit 选取，或 target.cancel 取消）。",
                    outcome.Payload,
                    PendingDetails(outcome));

            case ActionOutcome.StatusNotReady:
                return McpToolResult.Failure(McpCodes.NotReady, outcome.FailureMessage ?? "对局未就绪。");

            case ActionOutcome.StatusBusy:
                return McpToolResult.Failure(
                    McpCodes.PendingTargeting,
                    outcome.FailureMessage ?? "存在未完成的选择请求。",
                    PendingDetails(outcome));

            default:
                var code = outcome.FailureCode ?? McpCodes.EngineRejected;
                var details = PendingDetails(outcome);
                return McpToolResult.Failure(code, outcome.FailureMessage ?? "动作失败。", details, outcome.Payload);
        }
    }

    private static JsonNode? PendingDetails(ActionOutcome outcome)
    {
        var pending = McpService.PendingJson(outcome.Pending);

        if (pending is null && !outcome.TimedOut)
        {
            return null;
        }

        var details = new JsonObject();
        if (pending is not null)
        {
            details["pending"] = pending;
        }

        if (outcome.TimedOut)
        {
            details["timedOut"] = true;
            details["note"] = "上一轮待决请求因超时被按取消处理（引擎侧结果为 Cancelled）。";
        }

        return details;
    }
}
