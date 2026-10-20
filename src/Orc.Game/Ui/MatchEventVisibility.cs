using Orc.Cards;
using Orc.Core;
using Orc.Game.Players;
using Orc.Output;

namespace Orc.Game.Ui;

/// <summary>
/// 事件载荷可见性实现（多人支持 S5）：以「对手手牌」为唯一隐藏边界。
/// <para>
/// 规则：
/// ① 条目级——payload 中出现非观察者的 <see cref="Player"/>（如 <c>card.drawn</c> 的 player）⇒ 整条实体隐藏；
///    （抽牌瞬间卡已离开卡组、尚未进手牌，容器归属判定覆盖不到，必须靠条目级。）
/// ② 实体级兜底——卡实体属于对手手牌 ⇒ 隐藏。
/// ③ 其余（战场单位、HQ、效果、已公开区域）⇒ 可见。
/// </para>
/// <para>本类型是可见性判定的**唯一实现**；投影走结构性省略，不另立规则。</para>
/// </summary>
public sealed class MatchEventVisibility : IEventVisibility
{
    private readonly Match _match;

    public MatchEventVisibility(Match match)
    {
        _match = match ?? throw new ArgumentNullException(nameof(match));
    }

    /// <inheritdoc />
    public bool IsEntryHidden(IReadOnlyDictionary<string, object?> payload, object? viewer)
    {
        if (viewer is not Player viewerPlayer)
        {
            return false;
        }

        foreach (var value in payload.Values)
        {
            if (value is Player owner && owner.Index != viewerPlayer.Index)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsEntityVisible(object? entity, object? viewer)
    {
        if (viewer is not Player viewerPlayer)
        {
            return true;
        }

        if (Unwrap(entity) is not Card card)
        {
            return true;
        }

        var players = _match.Players;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player.Index == viewerPlayer.Index)
            {
                continue;
            }

            var hand = player.Hand;
            for (var j = 0; j < hand.Count; j++)
            {
                if (ReferenceEquals(hand[j], card))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static object? Unwrap(object? value) => value switch
    {
        Entity entity => entity,
        Ref<Entity> { IsAlive: true } reference => reference.Value,
        _ => null,
    };
}
