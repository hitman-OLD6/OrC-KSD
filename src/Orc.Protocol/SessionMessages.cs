using System.Text.Json.Nodes;

namespace Orc.Protocol;

/// <summary>角色标识（连接可申请的角色；与 seat 绑定与否共同决定权限）。</summary>
public static class SessionRoles
{
    /// <summary>引擎输入端：绑 seat＝玩家面；不绑 seat＝调试面。</summary>
    public const string Controller = "controller";

    /// <summary>引擎输出端：绑 seat＝脱敏视角；不绑 seat＝全知。</summary>
    public const string Viewer = "viewer";

    /// <summary>调试面（不绑 seat；仍须满足回环来源判据）。</summary>
    public const string Debug = "debug";
}

/// <summary>连接的角色态（协议可见的受控字符串；与 <c>session.attach</c> 的 <c>state</c> 一一对应）。</summary>
public static class SessionStates
{
    /// <summary>已握手、未申请角色：只有 initialize/ping/session.attach/room.info。</summary>
    public const string Connected = "connected";

    /// <summary>已申请、未获批准：只能 ping/session.detach/room.info，零推送。</summary>
    public const string Pending = "pending";

    /// <summary>玩家面（绑 seat 的 controller）。</summary>
    public const string Admitted = "admitted";

    /// <summary>绑 seat 的 viewer（脱敏）。</summary>
    public const string Viewer = "viewer";

    /// <summary>不绑 seat 的 viewer（全知）。</summary>
    public const string Omniscient = "omniscient";

    /// <summary>调试面（不绑 seat 的 controller）。</summary>
    public const string Debug = "debug";

    /// <summary>许可被撤销（无任何能力；可退回 connected 重新申请）。</summary>
    public const string Revoked = "revoked";
}

/// <summary>卡包引用（声明形态：<c>{packageId,version,guid}</c>；不传活对象、不传路径）。</summary>
public sealed record PackageRef(string PackageId, string? Version = null, string? Guid = null)
{
    /// <summary>转 JSON（与声明报文同形）。</summary>
    public JsonObject ToJson() => new()
    {
        ["packageId"] = PackageId,
        ["version"] = Version,
        ["guid"] = Guid,
    };

    /// <summary>从 JSON 读取（缺 <c>packageId</c> ＝ null）。</summary>
    public static PackageRef? From(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["packageId"]?.GetValue<string>() is not { Length: > 0 } id)
        {
            return null;
        }

        return new PackageRef(
            id,
            obj["version"]?.GetValue<string>(),
            obj["guid"]?.GetValue<string>());
    }
}

/// <summary>构筑配置（主国/盟国；可空＝不校验配对）。</summary>
public sealed record DeckConfigPayload(string? MainFaction = null, string? AllyFaction = null);

/// <summary>
/// <c>session.attach</c> 参数：角色 ＋ 座位 ＋ 卡组 ＋ 卡包声明**一条报文带齐**。
/// <para>
/// 不新开"提交卡包"方法——管理员批准时必须同时看到"要哪个 seat、什么卡组、要哪些包"，
/// 否则得批两次，且必然出现"批了座位却发现包不对"。
/// </para>
/// </summary>
public sealed record SessionAttachParams
{
    /// <summary>申请的角色集合（<see cref="SessionRoles"/>）。</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>申请绑定的座位（0/1）；调试面与全知 viewer 不填。</summary>
    public int? Seat { get; init; }

    /// <summary>展示名（可空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// 手写卡组（卡 id 序列）。与 <see cref="DeckId"/> **互斥**；两源都空＝座位没有卡组。
    /// <para>语义收紧：**空数组＝没给**（<c>deck: []</c> 与不传等效）。</para>
    /// </summary>
    public IReadOnlyList<string>? Deck { get; init; }

    /// <summary>
    /// 卡组资产标识（应用层 ID／包内裸局部 ID／展示名）。给了它就用该卡组，不再看 <see cref="Deck"/>。
    /// <para>解析失败（不存在／未入 lock／重名歧义）＝<c>DeckInvalid</c>，歧义时说明文本含候选。</para>
    /// </summary>
    public string? DeckId { get; init; }

    /// <summary>构筑配置（可空）。</summary>
    public DeckConfigPayload? DeckConfig { get; init; }

    /// <summary>卡包声明（引用形态；缺省＝空集）。</summary>
    public IReadOnlyList<PackageRef>? Packages { get; init; }

    /// <summary>重连令牌（凭令牌恢复原角色与 seat 绑定；跳过 <c>pending</c>）。</summary>
    public string? Token { get; init; }
}

/// <summary><c>session.attach</c> 的应答（**立即返回**，不为等批准挂起）。</summary>
public sealed record SessionAttachResult
{
    /// <summary>连接标识（与 <c>initialize</c> 返回的一致）。</summary>
    public required string ConnectionId { get; init; }

    /// <summary>当前态（<see cref="SessionStates"/>）。</summary>
    public required string State { get; init; }

    /// <summary>结果码（<see cref="ResultCodes"/>；成功＝<c>ok</c>，拒绝＝可读的受控码）。</summary>
    public string Code { get; init; } = ResultCodes.Ok;

    /// <summary>已绑座位（未绑＝null）。</summary>
    public int? Seat { get; init; }

    /// <summary>已授予的角色（调试面＝立即生效；申请中＝空）。</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>说明（人读）。</summary>
    public string? Message { get; init; }
}

/// <summary><c>notify/admission</c> 载荷（批准/拒绝/撤销；令牌经**该连接自己的**批准响应下发）。</summary>
public sealed record AdmissionPayload
{
    /// <summary><c>admitted</c> / <c>rejected</c> / <c>revoked</c>。</summary>
    public required string Decision { get; init; }

    /// <summary>授予/收回的角色集合。</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>绑定的座位（未绑＝null）。</summary>
    public int? Seat { get; init; }

    /// <summary>座位令牌（仅 <c>admitted</c> 且绑 seat 时下发；凭它重连恢复）。</summary>
    public string? SeatToken { get; init; }

    /// <summary>说明（人读；如 <c>admin-revoked</c>）。</summary>
    public string? Reason { get; init; }
}
