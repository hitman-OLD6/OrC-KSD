using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orc.Protocol;

/// <summary>
/// 协议序列化设置（服务端与客户端共用同一份）。
/// <para>
/// 线上一律**自描述 JSON**：不依赖 DTO 类型名或二进制布局；空值不写，减小帧体。
/// </para>
/// </summary>
public static class ProtocolJson
{
    /// <summary>默认序列化选项（camelCase、忽略 null、不缩进）。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
