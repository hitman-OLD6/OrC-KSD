using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// K0·B12 爆牌统一（Kb 爆牌独立化与术语更名）——两路语义并排对照回归（统一改造的专属锚）：
/// 同一满手条件下分别走两路（DrawCard 路径 / PlaceToHand 路径），显式断言：
/// ① 差异面＝仅 card.drawn（DrawCard 路径「算被抽到」恰一次；PlaceToHand 路径「未经手牌」零次）；
/// ② 共同面＝销毁（含 card.destroyed）先于 card.burned、card.burned 恰一次、card.discarded 零次（爆牌不走弃牌路线——
///    反向锁定）、card.hand.add 零次、手牌全程保持 HandLimit（无瞬时超额）、被爆卡终态（已销毁、离手、离场/离引擎登记）。
/// 另：未满边界对照——两路在「上限−1」时均按正常路径处置（无销毁/爆牌副作用）。
/// 附：card.burned 监听消费——独立信号经既有触发器 hooks 机制挂载可达（「当有牌被爆时」类表达可承载）。
/// </summary>
public class HandLimitBurnUnificationTests
{
    /// <summary>手牌填充至指定张数（经既有「实例化＋加载＋放入手牌」测试面）。</summary>
    private static async Task FillHandToAsync(Match match, Player player, int count)
    {
        while (player.Hand.Count < count)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        }
    }

    [Fact]
    public async Task Two_Burn_Paths_At_Full_Hand_Parallel_Contrast_Only_Drawn_Differs()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await FillHandToAsync(match, playerA, Player.HandLimit);
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 全程观测：drawn 信号时点读手牌数（证明「无瞬时第 10 张」——手牌全程保持上限）
        var handCountsAtDraw = new List<int>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDrawn && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                handCountsAtDraw.Add(playerA.Hand.Count);
            }

            return Task.CompletedTask;
        });

        // ══ 段 ①：DrawCard 路径（「算被抽到」）——drawn → 销毁 → burned ══
        var drawn = await match.PlayerManager.DrawCard(playerA);

        var drawPathSignals = recorder.Types.ToList();
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);
        Assert.Equal(Player.HandLimit, Assert.Single(handCountsAtDraw)); // drawn 恰一次观察点、值＝上限
        Assert.Equal(1, drawPathSignals.Count(t => t == GameUpdates.CardDrawn));
        Assert.Equal(1, drawPathSignals.Count(t => t == Updates.CardDestroyed));
        Assert.Equal(1, drawPathSignals.Count(t => t == GameUpdates.CardBurned));
        Assert.Equal(0, drawPathSignals.Count(t => t == GameUpdates.CardDiscarded)); // 反向：爆牌不发弃置信号
        Assert.Equal(0, drawPathSignals.Count(t => t == GameUpdates.CardHandAdd));
        var drawPathBurned = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardBurned);
        Assert.Same(drawn, drawPathBurned.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, drawPathBurned.Payload![GameUpdates.PayloadPlayer]);
        // 被爆卡终态：已销毁、离手、离卡组、离引擎登记
        Assert.False(drawn.Life.IsAlive);
        Assert.DoesNotContain(drawn, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(drawn));
        Assert.DoesNotContain(drawn, match.Engine.Cards);

        // ══ 段 ②：PlaceToHand 路径（「未经手牌」）——销毁 → burned（不发 drawn） ══
        recorder.Clear();
        var placed = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA);

        var placePathSignals = recorder.Types.ToList();
        Assert.Equal(CardPlaceStatus.Burned, placed.Status);
        Assert.True(placed.IsSuccess);
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);
        var burned = Assert.IsType<UnitCard>(placed.Card);
        Assert.Equal(0, placePathSignals.Count(t => t == GameUpdates.CardDrawn));
        Assert.Equal(1, placePathSignals.Count(t => t == Updates.CardDestroyed));
        Assert.Equal(1, placePathSignals.Count(t => t == GameUpdates.CardBurned));
        Assert.Equal(0, placePathSignals.Count(t => t == GameUpdates.CardDiscarded)); // 反向：爆牌不发弃置信号
        Assert.Equal(0, placePathSignals.Count(t => t == GameUpdates.CardHandAdd));
        var placePathBurned = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardBurned);
        Assert.Same(burned, placePathBurned.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, placePathBurned.Payload![GameUpdates.PayloadPlayer]);
        Assert.False(burned.Life.IsAlive);
        Assert.DoesNotContain(burned, playerA.Hand);
        Assert.DoesNotContain(burned, match.Engine.Cards);

        // ══ 并排对照：关键信号序列——差异面＝仅 card.drawn；其余（销毁先于 burned、hand.add 零次、不发 discarded）一致 ══
        static List<string> KeyBurnSignals(IReadOnlyList<string> types) => types
            .Where(t => t == GameUpdates.CardDrawn
                || t == Updates.CardDestroyed
                || t == GameUpdates.CardBurned
                || t == GameUpdates.CardDiscarded // 保留过滤：若出现 discarded 将破坏下方预期序列（反向锁定）
                || t == GameUpdates.CardHandAdd)
            .ToList();

        Assert.Equal(
            new[] { GameUpdates.CardDrawn, Updates.CardDestroyed, GameUpdates.CardBurned },
            KeyBurnSignals(drawPathSignals)); // ① 算被抽到：drawn 在销毁前；burned 收尾、不发 discarded
        Assert.Equal(
            new[] { Updates.CardDestroyed, GameUpdates.CardBurned },
            KeyBurnSignals(placePathSignals)); // ② 未经手牌：无 drawn、销毁先于 burned、不发 discarded
    }

    [Fact]
    public async Task Under_Limit_Both_Paths_Place_Normally_Without_Burn_Side_Effects()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        await FillHandToAsync(match, playerA, Player.HandLimit - 1);
        await FillHandToAsync(match, playerB, Player.HandLimit - 1);

        using var recorder = new UpdateRecorder(match.Engine);

        // 段 ①：DrawCard@「上限−1」＝正常入手（drawn → hand.add；零销毁/爆牌）
        recorder.Clear();
        var drawn = await match.PlayerManager.DrawCard(playerA);
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);
        Assert.Contains(drawn, playerA.Hand);
        Assert.True(drawn.Life.IsAlive);
        Assert.Equal(new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd }, recorder.Types);

        // 段 ②：PlaceToHand@「上限−1」＝正常放置（hand.add 恰一次；drawn/销毁/爆牌零次）
        recorder.Clear();
        var placed = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerB);
        Assert.Equal(CardPlaceStatus.Placed, placed.Status);
        Assert.Equal(Player.HandLimit, playerB.Hand.Count);
        var placedCard = Assert.IsType<UnitCard>(placed.Card);
        Assert.Contains(placedCard, playerB.Hand);
        Assert.True(placedCard.Life.IsAlive);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardBurned));
        var handAdd = recorder.PayloadOf(GameUpdates.CardHandAdd)!;
        Assert.Same(placedCard, handAdd[GameUpdates.PayloadCard]);
        Assert.Same(playerB, handAdd[GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task Burned_Signal_Is_Consumable_Via_Existing_Hooks_Mechanism()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await FillHandToAsync(match, playerA, Player.HandLimit);

        // 消费层：自写被动触发器挂总线（hooks＝card.burned——独立信号经既有触发器 hooks 机制挂载）
        var watcher = new BurnedWatchHandler(match.Engine, playerA);
        watcher.Attach();

        var drawn = await match.PlayerManager.DrawCard(playerA);

        // 「当有牌被爆时」可承载：收到恰一次；载荷含被爆卡（与 drawn 返回的同一实例）与归属玩家
        Assert.Equal(1, watcher.SeenEvents);
        Assert.Same(drawn, Assert.Single(watcher.Burned));
    }
}

// ==================== 监听消费辅助：card.burned 自写监听 handler（私有状态闭环） ====================

/// <summary>
/// card.burned 监听 handler（「当有牌被爆时」）：自建被动触发器挂总线（hooks＝card.burned）；
/// 归属过滤（指定玩家）→ 私有状态记录被爆卡——证明独立信号经既有触发器 hooks 机制挂载可达。
/// </summary>
internal sealed class BurnedWatchHandler
{
    private readonly LogicEngine _engine;
    private readonly Player _watchedPlayer;
    private readonly string _watchName;
    private readonly List<Card> _burned = new();
    private IDisposable? _watch;

    public BurnedWatchHandler(LogicEngine engine, Player watchedPlayer)
    {
        _engine = engine;
        _watchedPlayer = watchedPlayer;
        _watchName = $"{watchedPlayer.Index}/爆牌监听";
    }

    /// <summary>收到的 card.burned 事件总数（过滤前；真实源联动证据）。</summary>
    public int SeenEvents { get; private set; }

    /// <summary>被爆卡记录（归属过滤后、发生序）。</summary>
    public IReadOnlyList<Card> Burned => _burned;

    public void Attach()
        => _watch ??= _engine.Watch<BurnedWatchView>(GameUpdates.CardBurned, _watchName, this, OnBurnedAsync);

    private Task OnBurnedAsync(BurnedWatchView view, Context ctx, CancellationToken ct)
    {
        SeenEvents += 1;

        if (view.Player is not Player player || !ReferenceEquals(player, _watchedPlayer))
        {
            return Task.CompletedTask; // 归属过滤：仅累计指定玩家的爆牌
        }

        if (view.Card is not Card card)
        {
            return Task.CompletedTask;
        }

        _burned.Add(card);
        return Task.CompletedTask;
    }
}

/// <summary>card.burned 监听视图（载荷：{ Card, Player }——被爆卡实例＋爆牌时所归属的玩家）。</summary>
[ContextView]
public class BurnedWatchView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    [Optional]
    [Read]
    public virtual object? Player { get; set; }
}
