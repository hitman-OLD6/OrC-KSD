using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 对局级入口工具（回合推进/认输）。实现＝服务层强类型动作 API 的薄封装。
/// </summary>
internal static class MatchTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "match.end_turn",
            "match",
            "结束当前回合（引擎入口 Match.EndTurn；受引擎门禁与相位约束）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            async (call, ct) =>
            {
                var outcome = await service.EndTurnAsync(ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "回合已结束。");
            });

        service.AddTool(
            "match.concede",
            "match",
            "指定玩家认输（引擎入口 Match.Concede）。",
            """
            {
              "type": "object",
              "properties": {
                "owner": { "type": "integer", "description": "玩家索引（0＝玩家A、1＝玩家B）" }
              },
              "required": ["owner"],
              "additionalProperties": false
            }
            """,
            async (call, ct) =>
            {
                var owner = ToolArgs.RequireInt(call.Arguments, "owner");
                var outcome = await service.ConcedeAsync(owner, ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "认输已受理。");
            });
    }
}
