using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Server.Admin;

/// <summary>
/// 管理面操作名（**单一来源**；不注册进工具表，业务端口上的 <c>tools/list</c> 不含任何一项）。
/// </summary>
public static class AdminOps
{
    /// <summary>连接清单（纯读）。</summary>
    public const string ConnectionsList = "connections.list";

    /// <summary>批准接入（许可 ＋ 绑定；令牌经该连接自己的批准响应下发）。</summary>
    public const string ConnectionAdmit = "conn.admit";

    /// <summary>拒绝申请（连接退回 <c>Connected</c>）。</summary>
    public const string ConnectionReject = "conn.reject";

    /// <summary>撤销已许可连接（立即生效，不给宽限）。</summary>
    public const string ConnectionRevoke = "conn.revoke";

    /// <summary>把连接绑定到 seat（与 <see cref="ConnectionAdmit"/> 是同一件事的两半）。</summary>
    public const string SeatBind = "seat.bind";

    /// <summary>解绑（收回座位绑定）。</summary>
    public const string SeatUnbind = "seat.unbind";

    /// <summary>开局（按本局加载计划装配卡池后建局）。</summary>
    public const string MatchStart = "match.start";

    /// <summary>终止对局（无胜者；幂等）。</summary>
    public const string RoomAbort = "room.abort";

    /// <summary>重置沙盒包（删除并重建脚手架）。</summary>
    public const string SandboxReset = "sandbox.reset";

    /// <summary>实例信息（纯读）。</summary>
    public const string InstanceInfo = "instance.info";

    /// <summary>全部管理 op 名（守卫测试：业务端口不得路由这些名字）。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ConnectionsList,
        ConnectionAdmit,
        ConnectionReject,
        ConnectionRevoke,
        SeatBind,
        SeatUnbind,
        MatchStart,
        RoomAbort,
        SandboxReset,
        InstanceInfo,
    };
}

/// <summary>管理面结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Code">结果码（<see cref="ResultCodes"/>）。</param>
/// <param name="Message">人读说明。</param>
/// <param name="Payload">结构化载荷。</param>
public sealed record AdminOutcome(bool Ok, string Code, string? Message = null, JsonNode? Payload = null)
{
    /// <summary>成功。</summary>
    public static AdminOutcome Success(JsonNode? payload = null, string message = "ok")
        => new(true, ResultCodes.Ok, message, payload);

    /// <summary>失败。</summary>
    public static AdminOutcome Fail(string code, string message, JsonNode? payload = null)
        => new(false, code, message, payload);
}
