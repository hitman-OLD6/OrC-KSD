namespace Orc.Client;

/// <summary>
/// 客户端连接选项（多人支持 S1 骨架；S2 起由连接实现消费）。
/// </summary>
public sealed class ClientOptions
{
    /// <summary>服务端主机（本地＝127.0.0.1；局域网＝房主回报的地址）。</summary>
    public required string Host { get; init; }

    /// <summary>服务端端口（由服务端 stdout 就绪行或 <c>room.info</c> 取得）。</summary>
    public required int Port { get; init; }

    /// <summary>连接超时（默认 5 秒）。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>心跳间隔（默认 5 秒；断线判定依赖它）。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>心跳超时（默认 15 秒；超时即判定断线）。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
