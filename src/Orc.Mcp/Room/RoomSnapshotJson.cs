using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Orc.Mcp.Room;

/// <summary>
/// 房间快照的**唯一序列化来源**：<c>room.info</c> 的 details 与 <c>notify/room</c> 的载荷共用。
/// <para>两处各写一份是最容易漂移的地方（字段增删只改一处即漏），故集中在这里。</para>
/// </summary>
public static class RoomSnapshotJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>快照 → 协议载荷（camelCase；null 字段省略）。</summary>
    public static JsonNode? ToNode(RoomSnapshot snapshot)
        => JsonSerializer.SerializeToNode(snapshot, Json);
}
