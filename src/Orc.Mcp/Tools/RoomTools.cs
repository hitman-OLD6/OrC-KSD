using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Mcp.Tools;

/// <summary>
/// 房间工具面（R12）：**只有 <c>room.info</c>**。
/// <para>
/// 管理动作（开局/终止/绑定/撤销/卡池重装）属于管理面，**不注册进工具表**——
/// 因此这里没有 <c>room.claim</c>/<c>room.start</c>/<c>room.kick</c> 之类的方法（座位申请走
/// <c>session.attach</c>，批准走管理 API）。
/// </para>
/// <para>
/// <c>room.info</c> 的 <see cref="ToolRole.None"/> 是**显式例外**：<c>Connected</c> 与 <c>Pending</c> 都可调——
/// 未申请座位的人必须先能看到"哪些座位空着"，否则无法决定申请哪个。
/// </para>
/// </summary>
internal static class RoomTools
{
    private const string EmptySchema = """
        {"type":"object","properties":{},"additionalProperties":false}
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
            "读取房间快照（座位状态与可连接地址）。只能看到自己的卡组与卡包声明；对手的永远不可见。",
            EmptySchema,
            (_, _) => Task.FromResult(McpToolResult.Success(
                "房间快照已读取。",
                details: RoomSnapshotJson.ToNode(room.Snapshot(service.Caller.ToRoomView())))),
            readOnly: true,
            requiredRole: ToolRole.None);
    }
}
