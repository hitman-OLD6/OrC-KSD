using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Manager;

/// <summary>
/// 管理器结果码（**管理器侧唯一来源**）：子进程码表先映射为本表，再由管理器对外返回。
/// <para>
/// 两侧各自有常量源，一致性由映射测试守住（<c>ManagerCodes.Map</c> 只委托、不新造码）。
/// </para>
/// </summary>
public static class ManagerCodes
{
    public const string Ok = "ok";
    public const string InvalidArguments = "invalid-arguments";
    public const string UnknownInstance = "unknown-instance";
    public const string Busy = "busy";
    public const string Internal = "internal";
    public const string Drained = "drained";

    /// <summary>并集中同 id 版本冲突（指名双方与版本）。</summary>
    public const string LockConflict = "lock-conflict";

    /// <summary>缺口清单（点名哪一方缺哪些 <c>{kind,id,version}</c>）。</summary>
    public const string MissingAssets = "missing-assets";

    /// <summary>实例进程起不来（boot 前置不满足）。</summary>
    public const string InstanceBootFailed = "instance-boot-failed";

    /// <summary>
    /// 子进程码 → 管理器码（**不直穿**；两侧码表各自有常量源）。
    /// <para>未登记的子进程码按原样返回（它们是管理器的超集：如 <c>room-rotating</c>、<c>seat-taken</c>）。</para>
    /// </summary>
    public static string Map(string? serverCode) => serverCode switch
    {
        null or "" => Internal,
        ResultCodes.Ok => Ok,
        ResultCodes.InvalidArguments => InvalidArguments,
        ResultCodes.Busy => Busy,
        ResultCodes.Internal => Internal,
        ResultCodes.LockConflict => LockConflict,
        ResultCodes.MissingAssets => MissingAssets,
        ResultCodes.InstanceBootFailed => InstanceBootFailed,
        _ => serverCode,
    };

    /// <summary>是否为"资产判定"类码（管理器专属）。</summary>
    public static bool IsAssetCode(string code)
        => code is LockConflict or MissingAssets;
}

/// <summary>
/// 管理器 → 实例的管理面调用（与服务器进程内的 <c>AdminApi</c> 同名同参；管理器只做转发与码表映射）。
/// </summary>
public sealed record ManagerAdminCall(string Op, JsonObject? Args = null);

/// <summary>管理面回执（管理器视角）。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Code">管理器码（已经过 <see cref="ManagerCodes.Map"/>）。</param>
/// <param name="Message">人读说明。</param>
/// <param name="Payload">结构化载荷。</param>
public sealed record ManagerReply(bool Ok, string Code, string? Message = null, JsonNode? Payload = null)
{
    /// <summary>成功。</summary>
    public static ManagerReply Success(JsonNode? payload = null, string message = "ok")
        => new(true, ManagerCodes.Ok, message, payload);

    /// <summary>失败。</summary>
    public static ManagerReply Fail(string code, string message, JsonNode? payload = null)
        => new(false, code, message, payload);
}

/// <summary>
/// spawn 配置（实例的能力开关与座位形态**在启动时声明**，不通过 CLI 隐式传递）。
/// </summary>
public sealed record SpawnRequest
{
    /// <summary>实例标识（稳定字符串；管理器内唯一）。</summary>
    public required string InstanceId { get; init; }

    /// <summary>卡包目录（管理器资产库解析后的路径）。</summary>
    public IReadOnlyList<string> PackDirectories { get; init; } = [];

    /// <summary>座位数（恒 2；元素＝座位形态）。</summary>
    public IReadOnlyList<SeatSpawn> Seats { get; init; } = [new(), new()];

    /// <summary>服务层权限面（<c>play</c>/<c>sandbox</c>）。</summary>
    public string Mode { get; init; } = "play";

    /// <summary>是否注册调试写类工具（仅 sandbox 有效）。</summary>
    public bool AllowDebugWrites { get; init; }

    /// <summary>沙盒写根（缺省＝运行目录下 sandbox）。</summary>
    public string? SandboxRoot { get; init; }

    /// <summary>业务端口（0＝系统分配）。</summary>
    public int Port { get; init; }

    /// <summary>运行目录（日志/沙盒的唯一落点；缺省＝&lt;root&gt;/instances/&lt;id&gt;）。</summary>
    public string? RunDir { get; init; }

    /// <summary>随机种子（可空）。</summary>
    public int? Seed { get; init; }

    /// <summary>卡组规则档位（strict/lenient/none）。</summary>
    public string DeckRules { get; init; } = "strict";

    /// <summary>对局态断线宽限（秒）。</summary>
    public int DisconnectGraceSeconds { get; init; } = 60;

    /// <summary>实例标签（日志/诊断）。</summary>
    public string? Label { get; init; }

    /// <summary>恢复超时（等待首行就绪报文）。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>座位 spawn 形态（<c>ai</c>/<c>agent</c>；AI 的卡包声明缺省为空集＝并集单位元）。</summary>
public sealed class SeatSpawn
{
    /// <summary><c>ai</c> 或 <c>agent</c>。</summary>
    public string Kind { get; init; } = "agent";

    /// <summary>展示名。</summary>
    public string? DisplayName { get; init; }

    /// <summary>卡包声明（引用形态）。</summary>
    public IReadOnlyList<PackageRef> Packages { get; init; } = [];

    /// <summary>卡组（可空＝开局由本局卡池生成）。</summary>
    public IReadOnlyList<string>? Deck { get; init; }
}

/// <summary>实例清单条目（对外只读快照）。</summary>
public sealed record InstanceInfo(
    string InstanceId,
    int ProcessId,
    int Port,
    string RoomId,
    string Mode,
    string RunDir,
    string? Label,
    IReadOnlyList<string> PackDirectories,
    DateTimeOffset StartedAt,
    bool Running);

/// <summary>
/// 管理器（R1/R2）：**常驻**、并行托管多个服务器实例；实例＝进程（一实例一条独立 stdin/stdout 对）。
/// <para>管理器**不持有**任何 <c>Match</c>/<c>Player</c>/<c>Room</c> 活对象：对实例的读取一律来自通道回执或事件。</para>
/// </summary>
public interface IManager : IAsyncDisposable
{
    /// <summary>本进程的资产库（唯一权威的资产判定来源）。</summary>
    AssetLibrary Assets { get; }

    /// <summary>启动一个实例并等待就绪行（失败＝<c>instance-boot-failed</c>）。</summary>
    Task<ManagerReply> SpawnAsync(SpawnRequest request, CancellationToken ct = default);

    /// <summary>销毁一个实例（进程树回收 ＋ 端口可复用）。</summary>
    Task<ManagerReply> KillAsync(string instanceId, CancellationToken ct = default);

    /// <summary>热更新：销毁旧子进程 → 用新 dll 起新子进程（管理器不重启）。</summary>
    Task<ManagerReply> ReloadAsync(string instanceId, CancellationToken ct = default);

    /// <summary>实例清单。</summary>
    IReadOnlyList<InstanceInfo> List();

    /// <summary>把管理面调用透传给指定实例。</summary>
    Task<ManagerReply> AdminAsync(string instanceId, ManagerAdminCall call, CancellationToken ct = default);
}
