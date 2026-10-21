using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Mcp.Tools;

/// <summary>
/// 房间/座位工具面（多人支持 S3）：一律转调注入的 <see cref="IRoomControl"/>，
/// 不直接触碰引擎或对局对象；未注入＝整组不登记。
/// </summary>
internal static class RoomTools
{
    private const string EmptySchema = """
        {"type":"object","properties":{},"additionalProperties":false}
        """;

    private const string SeatSchema = """
        {
          "type": "object",
          "properties": { "seat": { "type": "integer", "minimum": 0, "maximum": 1 } },
          "required": ["seat"],
          "additionalProperties": false
        }
        """;

    private const string SeatWithTokenSchema = """
        {
          "type": "object",
          "properties": {
            "seat": { "type": "integer", "minimum": 0, "maximum": 1 },
            "seatToken": { "type": "string" }
          },
          "required": ["seat"],
          "additionalProperties": false
        }
        """;

    private const string ClaimSchema = """
        {
          "type": "object",
          "properties": {
            "seat": { "type": "integer", "minimum": 0, "maximum": 1 },
            "name": { "type": "string" },
            "deck": { "type": "array", "items": { "type": "string" } },
            "deckConfig": {
              "type": "object",
              "properties": {
                "mainFaction": { "type": "string" },
                "allyFaction": { "type": "string" }
              },
              "additionalProperties": false
            },
            "seatToken": { "type": "string" },
            "kind": { "type": "string", "enum": ["local", "remote"] }
          },
          "required": ["seat"],
          "additionalProperties": false
        }
        """;

    private const string StartSchema = """
        {
          "type": "object",
          "properties": { "seatToken": { "type": "string" } },
          "additionalProperties": false
        }
        """;

    private const string ResumeSchema = """
        {
          "type": "object",
          "properties": { "seatToken": { "type": "string" } },
          "required": ["seatToken"],
          "additionalProperties": false
        }
        """;

    /// <summary>登记房间工具组（未注入房间控制面时整组跳过）。</summary>
    public static void Register(McpService service)
    {
        if (service.RoomControl is not { } room)
        {
            return;
        }

        service.AddTool(
            "room.info",
            "room",
            "读取房间快照（状态、座位、控制器、可连接地址）。",
            EmptySchema,
            (_, _) => Task.FromResult(McpToolResult.Success("房间快照已读取。", details: RoomSnapshotJson.ToNode(room.Snapshot()))),
            readOnly: true);

        service.AddTool(
            "room.claim",
            "room",
            "声明座位（携带卡组与构筑配置）；成功返回座位令牌。",
            ClaimSchema,
            (call, _) => Task.FromResult(room.Claim(ReadClaim(call.Arguments))));

        service.AddTool(
            "room.release",
            "room",
            "释放座位（房主或持有座位令牌者）。",
            SeatWithTokenSchema,
            (call, _) => Task.FromResult(room.Release(new RoomSeatRelease(
                ToolArgs.RequireInt(call.Arguments, "seat"),
                ToolArgs.OptionalString(call.Arguments, "seatToken")))));

        service.AddTool(
            "room.add_ai",
            "room",
            "在空座生成 AI 控制器（缺省卡组由服务端生成）。",
            ClaimSchema,
            (call, _) => Task.FromResult(room.AddAi(new RoomAiSeat(
                ToolArgs.RequireInt(call.Arguments, "seat"),
                ToolArgs.OptionalString(call.Arguments, "name") ?? "AI",
                ToolArgs.OptionalStringList(call.Arguments, "deck") is { Count: > 0 } deck ? deck : null,
                ReadDeckConfig(call.Arguments)))));

        service.AddTool(
            "room.start",
            "room",
            "开局（房主或持有座位令牌者）；成功后进入对局态。",
            StartSchema,
            (call, ct) => room.StartAsync(ToolArgs.OptionalString(call.Arguments, "seatToken"), ct));

        service.AddTool(
            "room.kick",
            "room",
            "强制释放座位（房主）。",
            SeatWithTokenSchema,
            (call, _) => Task.FromResult(room.Kick(
                ToolArgs.RequireInt(call.Arguments, "seat"),
                ToolArgs.OptionalString(call.Arguments, "seatToken"))));

        service.AddTool(
            "room.resume",
            "room",
            "凭座位令牌重连并重新绑定控制器。",
            ResumeSchema,
            (call, _) => Task.FromResult(room.Resume(ToolArgs.RequireString(call.Arguments, "seatToken"))));
    }

    private static RoomSeatClaim ReadClaim(JsonElement args)
        => new(
            ToolArgs.RequireInt(args, "seat"),
            ToolArgs.OptionalString(args, "name") ?? "玩家",
            ToolArgs.OptionalStringList(args, "deck"),
            ReadDeckConfig(args),
            ToolArgs.OptionalString(args, "seatToken"),
            ToolArgs.OptionalString(args, "kind") ?? ControllerKinds.Remote);

    private static RoomDeckConfig? ReadDeckConfig(JsonElement args)
    {
        if (ToolArgs.OptionalObject(args, "deckConfig") is not { } config)
        {
            return null;
        }

        return new RoomDeckConfig(
            ToolArgs.OptionalString(config, "mainFaction"),
            ToolArgs.OptionalString(config, "allyFaction"));
    }
}
