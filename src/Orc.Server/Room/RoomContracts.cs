using Orc.Mcp.Contracts;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>房间内部状态（与协议字符串一一对应）。</summary>
public enum RoomState
{
    Lobby,
    Starting,
    InMatch,
    Ended,
}

/// <summary>开局请求（房间 → 对局装配的唯一入口）。</summary>
public sealed record RoomStartRequest(
    string RoomId,
    IReadOnlyList<RoomSeatStart> Seats,
    string? PackFingerprint);

/// <summary>开局座位输入（卡组与构筑配置已在房间态校验过）。</summary>
public sealed record RoomSeatStart(
    int Seat,
    string DisplayName,
    IReadOnlyList<string> Deck,
    RoomDeckConfig? DeckConfig,
    string ControllerKind);

/// <summary>对局装配面（S7 由 `McpService.CreateMatchAsync` 实现；房间不直接依赖引擎）。</summary>
public interface IMatchLauncher
{
    Task<McpToolResult> StartAsync(RoomStartRequest request, CancellationToken ct = default);
}

/// <summary>
/// 卡组合法性校验面（S7 由 `Orc.Game.Decks.DeckRules` 实现）。
/// 返回 <c>null</c>＝通过；否则为失败结果（错误码 + 可定位信息）。
/// </summary>
public interface IDeckValidator
{
    McpToolResult? Validate(IReadOnlyList<string> deck, RoomDeckConfig? config);
}
