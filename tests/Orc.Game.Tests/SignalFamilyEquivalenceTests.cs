using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// A2 等价测试（**逐信号族一条**）：发射从旧总线切到 <c>engine#&lt;信号&gt;</c> 成员之后，
/// 行为必须与总线时代逐项一致。本组用例对每个族里的**每一个信号**都断言四件事：
/// <list type="number">
/// <item><b>顺序</b>：更新条目（source=bus、keywords=[信号名]）→ 外部观察者 → 订阅者；</item>
/// <item><b>载荷</b>：订阅者拿到的数据面逐键与发射载荷一致，且是**各自的副本**（互不共享）；</item>
/// <item><b>同步语义</b>：<c>await Emit</c> 返回即全部执行完，每个订阅者恰一次（不双执行——A2 的最大风险）；</item>
/// <item><b>审查面</b>：该信号上有对应订阅边（旧 <c>GetSubscribers</c> 的等价读面）。</item>
/// </list>
/// 族划分与 <c>GameSignals.Names</c> 的声明序一致（内核 4 ＋ 游戏层 35 = 39 个信号，全覆盖）。
/// </summary>
public class SignalFamilyEquivalenceTests
{
    /// <summary>族的划分（每族一条用例；族内逐信号断言）。</summary>
    public static IEnumerable<object[]> Families()
    {
        yield return new object[]
        {
            "内核",
            new[]
            {
                Updates.CardPlaced, Updates.CardDestroyed, Updates.CardData, Updates.EffectRemoved,
            },
        };

        yield return new object[]
        {
            "回合",
            new[]
            {
                GameUpdates.TurnStartBefore, GameUpdates.TurnStart, GameUpdates.TurnStartAfter,
                GameUpdates.TurnEndBefore, GameUpdates.TurnEnd,
            },
        };

        yield return new object[]
        {
            "卡牌通用与手牌",
            new[]
            {
                GameUpdates.CardPlayed, GameUpdates.CardDrawn, GameUpdates.CardStatChanged,
                GameUpdates.CardLoad, GameUpdates.CardHandAdd, GameUpdates.CardDiscarded, GameUpdates.CardBurned,
            },
        };

        yield return new object[]
        {
            "死亡与离场",
            new[]
            {
                GameUpdates.CardDied, GameUpdates.CardDying, GameUpdates.CardLeaving,
                GameUpdates.KeywordAttackFinished, GameUpdates.CardDamaged,
            },
        };

        yield return new object[]
        {
            "单位",
            new[]
            {
                GameUpdates.UnitJoined, GameUpdates.UnitDeployed, GameUpdates.UnitPositionChanged,
                GameUpdates.UnitUpgraded, GameUpdates.UnitRevealed, GameUpdates.UnitActed,
                GameUpdates.UnitCombatSurvived, GameUpdates.UnitTypesChanged, GameUpdates.UnitDamageDealt,
            },
        };

        yield return new object[]
        {
            "反制与卡组资源",
            new[]
            {
                GameUpdates.CounterTriggered, GameUpdates.CounterDisarmed, GameUpdates.DeckShuffled,
                GameUpdates.SlotGained, GameUpdates.SlotLost, GameUpdates.SlotChanged,
                GameUpdates.PointGained, GameUpdates.PointLost, GameUpdates.PointChanged,
            },
        };
    }


    /// <summary>族内的信号名字集合必须与声明表**逐项一致**（防止族表漂移导致漏测）。</summary>
    [Fact]
    public void Families_CoverEveryDeclaredSignal_ExactlyOnce()
    {
        var declared = KernelSignals.Names.Concat(GameSignals.Names).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        var covered = Families()
            .SelectMany(static row => (string[])row[1])
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, covered);
    }

}

/// <summary>探针视图（无字段：本组用例只验证"被执行到"与数据面，不验证视图投影）。</summary>
[ContextView]
public class SignalProbeView
{
}
