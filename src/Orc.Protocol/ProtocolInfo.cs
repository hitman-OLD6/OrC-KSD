namespace Orc.Protocol;

/// <summary>
/// 协议标识与 JSON-RPC 常量（多人支持 S1 骨架；S2 起填充信封与错误码）。
/// <para>
/// <see cref="ProtocolVersion"/> 是**线协议**版本；对局契约版本由服务端从
/// <c>ContractSchemaVersion</c> 取值并在 <c>initialize</c> 响应中回报，不在本层硬编码。
/// </para>
/// </summary>
public static class ProtocolInfo
{
    /// <summary>线协议版本（NDJSON over TCP，JSON-RPC 2.0 外形）。</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>JSON-RPC 版本字面量。</summary>
    public const string JsonRpcVersion = "2.0";
}
