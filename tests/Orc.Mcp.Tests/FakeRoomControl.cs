using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Mcp.Tests;

/// <summary>房间控制面的测试替身（不触碰引擎；仅用于工具面/白名单护栏）。</summary>
internal sealed class FakeRoomControl : IRoomControl
{
    public RoomSnapshot Snapshot() => new(
        "test-room",
        RoomStates.Lobby,
        [
            new RoomSeatSnapshot(0, ControllerKinds.Local, "房主", true),
            new RoomSeatSnapshot(1, ControllerKinds.Remote, null, false),
        ],
        "loaded",
        null,
        []);

    public McpToolResult Claim(RoomSeatClaim claim) => Ok("已声明座位。", "claim");

    public McpToolResult Release(RoomSeatRelease release) => Ok("已释放座位。", "release");

    public McpToolResult AddAi(RoomAiSeat ai) => Ok("已生成 AI 控制器。", "ai");

    public Task<McpToolResult> StartAsync(string? seatToken, CancellationToken ct = default)
        => Task.FromResult(Ok("已开局。", "start"));

    public McpToolResult Kick(int seat, string? seatToken) => Ok("已释放座位。", "kick");

    public McpToolResult Resume(string seatToken) => Ok("已重新绑定。", "resume");

    private static McpToolResult Ok(string message, string kind)
        => McpToolResult.Success(message, details: new JsonObject { ["kind"] = kind });
}
