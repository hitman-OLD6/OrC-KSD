using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 玩家动作面工具（与引擎公开入口 1:1；**两模式共有**——沙盒模式与 player 模式都可用）。
/// <para>
/// 实现＝服务层强类型动作 API（<see cref="Hosting.IMcpActions"/>）的薄封装：
/// 两条入口（MCP 工具 / 宿主 C# 调用）共用同一执行器、门禁与 pending 状态机。
/// </para>
/// </summary>
internal static class PlayerActionTools
{
    private const string RefSchema = """
        { "type": "object", "properties": { "ref": { "type": "string", "description": "卡牌/单位引用键（N 格式 GUID）" } }, "required": ["ref"], "additionalProperties": false }
        """;

    private const string OwnerSchema = """
        { "type": "object", "properties": { "owner": { "type": "integer", "description": "玩家索引（0＝玩家A、1＝玩家B）" } }, "required": ["owner"], "additionalProperties": false }
        """;

    private const string PlaceSchema = """
        {
          "type": "object",
          "properties": {
            "ref": { "type": "string", "description": "手牌中卡的引用键" },
            "owner": { "type": "integer", "description": "玩家索引（0/1）" },
            "slot": { "type": "integer", "description": "支援线槽位序号（0 起）" }
          },
          "required": ["ref", "owner", "slot"],
          "additionalProperties": false
        }
        """;

    public static void Register(McpService service)
    {
        // ---------- 换牌 ----------
        service.AddTool(
            "mulligan.begin",
            "mulligan",
            "开始换牌（进入换牌交互；可能需要选牌——届时返回 targeting，用 target.submit 应答）。",
            OwnerSchema,
            async (call, ct) =>
            {
                var outcome = await service.MulliganBeginAsync(ToolArgs.RequireInt(call.Arguments, "owner"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "换牌已开始。");
            });

        service.AddTool(
            "mulligan.done",
            "mulligan",
            "确认换牌完成（双方确认后进入进行态）。",
            OwnerSchema,
            async (call, ct) =>
            {
                var outcome = await service.MulliganDoneAsync(ToolArgs.RequireInt(call.Arguments, "owner"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "换牌已确认。");
            });

        // ---------- 出牌 ----------
        service.AddTool(
            "play.unit_pre",
            "play",
            "单位预打出（进入部署选位交互；返回 targeting 时用 target.submit 应答）。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.PlayUnitPreAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "单位已预打出。");
            });

        service.AddTool(
            "play.unit",
            "play",
            "把单位卡打出到指定支援线槽位。",
            PlaceSchema,
            async (call, ct) =>
            {
                var outcome = await service.PlayUnitAsync(
                    ToolArgs.RequireString(call.Arguments, "ref"),
                    ToolArgs.RequireInt(call.Arguments, "owner"),
                    ToolArgs.RequireInt(call.Arguments, "slot"),
                    ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "单位已打出。");
            });

        service.AddTool(
            "play.join",
            "play",
            "单位加入（加入己方已在场编队；不扣费、不走部署词条）。",
            PlaceSchema,
            async (call, ct) =>
            {
                var outcome = await service.JoinUnitAsync(
                    ToolArgs.RequireString(call.Arguments, "ref"),
                    ToolArgs.RequireInt(call.Arguments, "owner"),
                    ToolArgs.RequireInt(call.Arguments, "slot"),
                    ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "单位已加入。");
            });

        service.AddTool(
            "play.command_pre",
            "play",
            "指令预打出（进入选目标等交互）。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.PlayCommandPreAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "指令已预打出。");
            });

        service.AddTool(
            "play.command",
            "play",
            "打出指令卡（兼容入口；与 play.command_pre 同一实现）。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.PlayCommandAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "指令已打出。");
            });

        service.AddTool(
            "play.counter",
            "play",
            "使用反制卡。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.UseCounterAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "反制已使用。");
            });

        // ---------- 指挥 ----------
        RegisterCommand(service, "command.begin", "发起指挥（移动/攻击的统一入口）。");
        RegisterCommand(service, "command.move", "发起移动。");
        RegisterCommand(service, "command.attack", "发起攻击。");
        RegisterCommand(service, "command.leave", "离开战场。");

        service.AddTool(
            "command.availability",
            "command",
            "查询单位指挥可用性（纯读；UI 高亮与 agent 决策用）。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.GetCommandAvailabilityAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "指挥可用性已读取。");
            });

        service.AddTool(
            "play.can_play",
            "play",
            "查询卡牌当前是否可打出（纯读；受门禁/费用/相位约束）。",
            RefSchema,
            async (call, ct) =>
            {
                var outcome = await service.CanPlayAsync(ToolArgs.RequireString(call.Arguments, "ref"), ct).ConfigureAwait(false);
                return ToolResults.FromOutcome(outcome, "可打出性已读取。");
            });
    }

    private static void RegisterCommand(McpService service, string name, string description)
        => service.AddTool(
            name,
            "command",
            description,
            RefSchema,
            async (call, ct) =>
            {
                var reference = ToolArgs.RequireString(call.Arguments, "ref");
                var outcome = name switch
                {
                    "command.begin" => await service.BeginCommandAsync(reference, ct).ConfigureAwait(false),
                    "command.move" => await service.BeginMoveAsync(reference, ct).ConfigureAwait(false),
                    "command.attack" => await service.BeginAttackAsync(reference, ct).ConfigureAwait(false),
                    _ => await service.LeaveBattlefieldAsync(reference, ct).ConfigureAwait(false),
                };

                return ToolResults.FromOutcome(outcome, "指挥动作已执行。");
            });
}
