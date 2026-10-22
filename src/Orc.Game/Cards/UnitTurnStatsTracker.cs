using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Players;

namespace Orc.Game.Cards;

/// <summary>
/// 单位回合统计订阅器（对局级单实例）：监听更新总线维护各单位 <see cref="ActionRecordData"/>——
/// 行动次数（<c>unit.acted</c>）、移动次数（<c>unit.position.changed</c>，含效果位移/撤退）、
/// 累计受伤（<c>card.damaged</c>）；攻击次数与花费指挥点在指挥收尾（<see cref="RecordAttack"/>）写入；
/// 每己方 <c>turn.start.after</c> 清零。
/// <para>单一订阅者：数据组件保持纯数据（无引擎引用）；与 <c>RefreshActionStates</c>/<c>AdvanceTurnsInPlay</c> 同模式。</para>
/// </summary>
public sealed class UnitTurnStatsTracker
{
    private readonly LogicEngine _engine;
    private readonly Battlefield _battlefield;
    private IDisposable? _subscription;

    /// <summary>创建统计订阅器（未启动——装配方调用 <see cref="Start"/>）。</summary>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public UnitTurnStatsTracker(LogicEngine engine, Battlefield battlefield)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(battlefield);
        _engine = engine;
        _battlefield = battlefield;
    }

    /// <summary>启动订阅（幂等）。</summary>
    public void Start() => _subscription ??= _engine.Subscribe(OnUpdateAsync);

    /// <summary>记录一次指挥攻击（仅指挥收尾调用；效果攻击不计入）。</summary>
    internal static void RecordAttack(UnitCard unit, int cost)
    {
        if (unit.TryGetData<ActionRecordData>(out var record))
        {
            record.RecordAttack(cost);
        }
    }

    private Task OnUpdateAsync(
        string kind, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        switch (kind)
        {
            case GameUpdates.UnitActed:
                if (UnitOf(payload) is { } acted && acted.TryGetData<ActionRecordData>(out var actionRecord))
                {
                    actionRecord.RecordAction();
                }

                break;

            case GameUpdates.UnitPositionChanged:
                if (UnitOf(payload) is { } moved && moved.TryGetData<ActionRecordData>(out var moveRecord))
                {
                    moveRecord.RecordMove();
                }

                break;

            case GameUpdates.CardDamaged:
                if (payload.TryGetValue(GameUpdates.PayloadCard, out var damagedValue)
                    && damagedValue is UnitCard damaged
                    && damaged.TryGetData<ActionRecordData>(out var damageRecord)
                    && payload.TryGetValue(GameUpdates.PayloadAmount, out var amountValue)
                    && amountValue is int amount)
                {
                    damageRecord.RecordDamage(amount);
                }

                break;

            case GameUpdates.TurnStartAfter:
                if (payload.TryGetValue(GameUpdates.PayloadPlayer, out var playerValue)
                    && playerValue is Player player)
                {
                    ResetFor(player);
                }

                break;
        }

        return Task.CompletedTask;
    }

    private static UnitCard? UnitOf(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue(GameUpdates.PayloadUnit, out var unitValue) && unitValue is UnitCard unit)
        {
            return unit;
        }

        if (payload.TryGetValue(GameUpdates.PayloadCard, out var cardValue) && cardValue is UnitCard card)
        {
            return card;
        }

        return null;
    }

    private void ResetFor(Player player)
    {
        foreach (var line in new[]
                 {
                     _battlefield.PlayerASupportLine, _battlefield.FrontLine, _battlefield.PlayerBSupportLine,
                 })
        {
            for (var i = 0; i < line.Size; i++)
            {
                if (line[i] is UnitCard unit
                    && ReferenceEquals(unit.Owner, player)
                    && unit.TryGetData<ActionRecordData>(out var record))
                {
                    record.Reset();
                }
            }
        }
    }
}
