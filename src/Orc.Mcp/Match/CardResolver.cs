using Orc.Cards;
using Orc.Game.Board;
using Orc.Game.Players;

namespace Orc.Mcp.Match;

/// <summary>
/// 引用键 → 引擎对象 的解析 helper（服务层内部共用）。
/// 引用键＝引擎托管引用表的 key（实体的 `Entity.Id` 的 N 格式；无 Id 对象由表分配）。
/// </summary>
internal static class CardResolver
{
    /// <summary>解析为卡牌（任意具体类型）。</summary>
    public static bool TryResolveCard(Orc.Game.Match match, string reference, out Card? card, out string error)
    {
        card = null;
        error = string.Empty;

        if (!match.Engine.References.TryResolve(reference, out var value))
        {
            error = $"引用键 '{reference}' 不在本对局的引擎引用表中。";
            return false;
        }

        if (value is not Card resolved)
        {
            error = $"引用键 '{reference}' 指向的对象不是卡牌（{value?.GetType().Name}）。";
            return false;
        }

        card = resolved;
        return true;
    }

    /// <summary>解析并做具体类型判定（如 <c>UnitCard</c>/<c>OrderCard</c>/<c>CounterCard</c>）。</summary>
    public static bool TryResolveCard<T>(Orc.Game.Match match, string reference, out T? card, out string error)
        where T : class
    {
        card = null;

        if (!TryResolveCard(match, reference, out var resolved, out error))
        {
            return false;
        }

        if (resolved is not T typed)
        {
            error = $"引用键 '{reference}' 指向的卡牌类型不符（实际 {resolved!.GetType().Name}，期望 {typeof(T).Name}）。";
            return false;
        }

        card = typed;
        return true;
    }

    /// <summary>解析为玩家（接受玩家索引串或玩家引用键）。</summary>
    public static bool TryResolvePlayer(Orc.Game.Match match, string reference, out Player? player, out string error)
    {
        player = null;
        error = string.Empty;

        foreach (var candidate in match.Players)
        {
            if (string.Equals(candidate.ReferenceKey, reference, StringComparison.Ordinal))
            {
                player = candidate;
                return true;
            }
        }

        error = $"引用键 '{reference}' 不匹配任何玩家（用 players[].ref 或索引 0/1）。";
        return false;
    }

    /// <summary>按玩家索引取玩家。</summary>
    public static bool TryResolvePlayer(Orc.Game.Match match, int ownerIndex, out Player? player, out string error)
    {
        player = null;
        error = string.Empty;

        if (ownerIndex is < 0 or > 1)
        {
            error = "owner 须为 0 或 1。";
            return false;
        }

        player = match.Players[ownerIndex];
        return true;
    }

    /// <summary>按玩家索引 ＋ 槽位序号解析支援线槽位。</summary>
    public static bool TryResolveSupportSlot(Orc.Game.Match match, int ownerIndex, int slotIndex, out Slot? slot, out string error)
    {
        slot = null;
        error = string.Empty;

        if (!TryResolvePlayer(match, ownerIndex, out var player, out error))
        {
            return false;
        }

        var line = match.Battlefield.GetSupportLine(player!);
        if (slotIndex < 0 || slotIndex >= line.Count)
        {
            error = $"槽位序号越界（玩家 {ownerIndex} 支援线容量 {line.Count}，请求 {slotIndex}）。";
            return false;
        }

        slot = line[slotIndex];
        return true;
    }

}
