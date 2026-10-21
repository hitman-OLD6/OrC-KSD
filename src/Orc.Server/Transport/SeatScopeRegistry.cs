using System.Collections.Frozen;

namespace Orc.Server.Transport;

/// <summary>
/// 工具 ↔ 会话座位的授权作用域（授权层骨架）。
/// <para>
/// 四类把"是否可写入座位""是否校验 owner""是否只要求已绑定"三件事正交化；
/// 语义确实特异的工具（<c>observe.view</c> / <c>target.*</c>）走专门分支，但仍算"已登记"。
/// </para>
/// </summary>
internal enum SeatScope
{
    /// <summary>唯一可写入 <c>session.Seat</c> 的工具（成功后绑定）。</summary>
    Bind,

    /// <summary>参数带 <c>owner</c>，且必须等于会话座位。</summary>
    OwnerScoped,

    /// <summary>无 owner 参数，引擎自行判当前方；门禁只要求已绑定。</summary>
    SeatRequired,

    /// <summary>房间/诊断/只读：不看座位，且严禁绑定座位（鉴权靠工具自身令牌）。</summary>
    Agnostic,
}

/// <summary>
/// 座位作用域白名单（集中表 ＋ 默认拒绝）。
/// <para>
/// **新工具必须显式登记**——未登记的工具在 <see cref="ServerHost"/> 门禁处直接
/// <c>unauthorized</c>（不解析参数、不入队、不碰引擎）。两个守卫测试
/// （"宣告即须登记"／"表内无死条目"）把这条纪律钉死。
/// </para>
/// </summary>
internal static class SeatScopeRegistry
{
    /// <summary>四类归属表（未列出＝默认拒绝）。</summary>
    public static readonly FrozenDictionary<string, SeatScope> ToolScopes =
        new Dictionary<string, SeatScope>(StringComparer.Ordinal)
        {
            // ---- Bind：session.Seat 的唯一写入点 ----
            ["room.claim"] = SeatScope.Bind,
            ["room.resume"] = SeatScope.Bind,

            // ---- OwnerScoped：owner 必须等于会话座位 ----
            ["match.concede"] = SeatScope.OwnerScoped,
            ["mulligan.begin"] = SeatScope.OwnerScoped,
            ["mulligan.done"] = SeatScope.OwnerScoped,
            ["play.unit"] = SeatScope.OwnerScoped,
            ["play.join"] = SeatScope.OwnerScoped,

            // ---- SeatRequired：仅要求已绑定 ----
            ["match.end_turn"] = SeatScope.SeatRequired,
            ["play.unit_pre"] = SeatScope.SeatRequired,
            ["play.command_pre"] = SeatScope.SeatRequired,
            ["play.command"] = SeatScope.SeatRequired,
            ["play.counter"] = SeatScope.SeatRequired,
            ["command.begin"] = SeatScope.SeatRequired,
            ["command.move"] = SeatScope.SeatRequired,
            ["command.attack"] = SeatScope.SeatRequired,
            ["command.leave"] = SeatScope.SeatRequired,

            // ---- Agnostic：房间/诊断/只读 ----
            ["room.info"] = SeatScope.Agnostic,
            ["room.release"] = SeatScope.Agnostic,
            ["room.kick"] = SeatScope.Agnostic,
            ["room.add_ai"] = SeatScope.Agnostic,
            ["room.start"] = SeatScope.Agnostic,
            ["service.info"] = SeatScope.Agnostic,
            ["service.tools"] = SeatScope.Agnostic,
            ["docs.list"] = SeatScope.Agnostic,
            ["docs.read"] = SeatScope.Agnostic,
            ["play.can_play"] = SeatScope.Agnostic,
            ["command.availability"] = SeatScope.Agnostic,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>语义无法用四类表达的专门分支（仍算"已登记"）。</summary>
    public static readonly FrozenSet<string> SpecialCasedTools =
        new[] { "observe.view", "target.submit", "target.cancel", "target.claim" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>已登记的工具名（四类 ∪ 专门分支；供反向守卫测试使用）。</summary>
    public static IReadOnlyCollection<string> RegisteredNames
        => ToolScopes.Keys.Concat(SpecialCasedTools).ToArray();

    public static bool IsClassified(string name)
        => ToolScopes.ContainsKey(name) || SpecialCasedTools.Contains(name);

    /// <summary>取作用域（专门分支/未登记返回 <c>null</c>，由 <see cref="IsClassified"/> 区分）。</summary>
    public static SeatScope? ScopeOf(string name)
        => ToolScopes.TryGetValue(name, out var scope) ? scope : null;

    /// <summary>作用域的可读名（诊断/测试用；未登记＝null，专门分支＝<c>"special"</c>）。</summary>
    public static string? ScopeName(string name)
        => ToolScopes.TryGetValue(name, out var scope)
            ? scope switch
            {
                SeatScope.Bind => "bind",
                SeatScope.OwnerScoped => "owner-scoped",
                SeatScope.SeatRequired => "seat-required",
                _ => "agnostic",
            }
            : SpecialCasedTools.Contains(name) ? "special" : null;
}

/// <summary>
/// 座位相关策略判定（与传输层解耦，便于单测）。
/// </summary>
public static class SeatScopePolicy
{
    /// <summary>
    /// 房主（<c>kind:"local"</c>）的来源约束：**仅回环**（严格 <see cref="System.Net.IPAddress.IsLoopback"/>）。
    /// <para>本机网卡地址（如 <c>192.168.x.x</c>）**不**视为回环——否则同机其它进程可冒用房主身份。</para>
    /// </summary>
    public static bool IsTrustedLocal(System.Net.IPAddress? address)
        => address is not null && System.Net.IPAddress.IsLoopback(address);
}
