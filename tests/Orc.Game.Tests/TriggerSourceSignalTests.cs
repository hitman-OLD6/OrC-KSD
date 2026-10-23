using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// R9 验收（游戏层信号面与两面分工）：
/// ① <c>card.discarded</c> 载荷与视图同时携带 <c>Source</c>（显式施动方 / 缺省 null）；
/// ② 监听者 handler 内 <c>TriggerCard</c> ＝监听者自身宿主（≠ <c>Source</c>）——R6e 双向（敌方弃置触发、己方弃置不触发）；
/// ③ 回合五连恒携带 <c>Source = null</c>（键存在、值为 null）。
/// </summary>
public class TriggerSourceSignalTests
{
    [Fact]
    public async Task Discarded_Carries_Source_On_Payload_And_View()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var enemyCard = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var victim = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var listener = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var seen = MountDiscardProbe(match, listener);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        await match.PlayerManager.DiscardCardAsync(playerA, victim, source: enemyCard);

        // 载荷面：恒写 Source 键（值为施动方卡）
        var payload = recorder.PayloadOf(GameUpdates.CardDiscarded);
        Assert.NotNull(payload);
        Assert.True(payload!.ContainsKey(GameUpdates.PayloadSource));
        Assert.Same(enemyCard, payload[GameUpdates.PayloadSource]);

        // 视图面：Source＝事件引发者；TriggerCard＝监听者自身宿主（≠ Source）
        var pair = Assert.Single(seen);
        Assert.Same(enemyCard, pair.Source);
        Assert.Same(enemyCard, pair.ContextSource); // view.Source 与 ctx.Get("Source") 同值
        Assert.Same(listener, pair.TriggerCard);

        // 缺省调用：Source 键仍在、值为 null（不猜测、不回退）
        var victim2 = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        recorder.Clear();
        await match.PlayerManager.DiscardCardAsync(playerA, victim2);

        var payload2 = recorder.PayloadOf(GameUpdates.CardDiscarded);
        Assert.NotNull(payload2);
        Assert.True(payload2!.ContainsKey(GameUpdates.PayloadSource));
        Assert.Null(payload2[GameUpdates.PayloadSource]);
        Assert.Null(seen[1].Source);
        Assert.Null(seen[1].ContextSource);
    }

    [Fact]
    public async Task Discarded_By_Enemy_Triggers_Discarded_By_Self_Does_Not()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var listener = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyCard = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var seen = MountDiscardProbe(match, listener);

        var byEnemy = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        await match.PlayerManager.DiscardCardAsync(playerA, byEnemy, source: enemyCard);

        var bySelf = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        await match.PlayerManager.DiscardCardAsync(playerA, bySelf, source: listener);

        Assert.Equal(2, seen.Count);
        Assert.Same(enemyCard, seen[0].Source); // 敌方弃置——Source 判敌方、TriggerCard 仍是自身
        Assert.Same(listener, seen[0].TriggerCard);
        Assert.Same(listener, seen[1].Source);  // 己方弃置——Source＝自身
        Assert.Same(listener, seen[1].TriggerCard);
    }

    [Fact]
    public async Task TurnSignals_Always_Carry_Null_Source()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        using var recorder = new UpdateRecorder(match.Engine);
        await match.Initialize();
        recorder.Clear();

        await match.EndTurn();

        var turnUpdates = recorder.Updates
            .Where(item => item.Type.StartsWith("turn.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(5, turnUpdates.Count);
        foreach (var update in turnUpdates)
        {
            Assert.NotNull(update.Payload);
            Assert.True(update.Payload!.ContainsKey(GameUpdates.PayloadSource));
            Assert.Null(update.Payload[GameUpdates.PayloadSource]);
        }
    }

    [Fact]
    public async Task Unit_Deployed_Carries_Unit_As_Source()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await match.ResourceManager.AddPointsAsync(playerA, 3);
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Success, result.Status);
        var payload = recorder.PayloadOf(GameUpdates.UnitDeployed);
        Assert.NotNull(payload);
        Assert.Same(unit, payload![GameUpdates.PayloadUnit]);
        Assert.Same(unit, payload[GameUpdates.PayloadSource]); // 卡自身内置链注册 ⇒ Source＝本卡
    }

    [Fact]
    public async Task Card_Played_Carries_Played_Card_As_Source()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await match.ResourceManager.AddPointsAsync(playerA, 3);
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Success, result.Status);
        var payload = recorder.PayloadOf(GameUpdates.CardPlayed);
        Assert.NotNull(payload);
        Assert.Same(unit, payload![GameUpdates.PayloadCard]);
        Assert.Same(unit, payload[GameUpdates.PayloadSource]); // 被打出的卡即引发者
    }

    [Fact]
    public async Task Counter_Triggered_Carries_Counter_Card_As_Source()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var counter = await PlayChainTestKit.InstantiateLoadedAsync<CounterCard>(
            match, player, PlayChainTestKit.CounterCheapId);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var activation = await match.PlayManager.UseCounterAsync(counter);

        Assert.Equal(PlayResultStatus.Success, activation.Status);
        var payload = recorder.PayloadOf(GameUpdates.CounterTriggered);
        Assert.NotNull(payload);
        Assert.Same(counter, payload![GameUpdates.PayloadCard]);
        Assert.Same(counter, payload[GameUpdates.PayloadSource]);
    }

    [Fact]
    public async Task Card_Died_Carries_Killer_As_Source()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var killer = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var victim = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 0);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var runtime = EffectRuntime.ResolveFor(killer);
        Assert.NotNull(runtime);
        await runtime!.DamageAsync(victim, 99, killer);

        var payload = recorder.PayloadOf(GameUpdates.CardDied);
        Assert.NotNull(payload);
        Assert.Same(victim, payload![GameUpdates.PayloadCard]);
        Assert.Same(killer, payload[GameUpdates.PayloadKiller]);
        Assert.Same(killer, payload[GameUpdates.PayloadSource]); // 死亡信号的引发者＝击杀者
    }

    // ---------- 监听探针 ----------

    private static List<(Card? Source, Card? TriggerCard, object? ContextSource)> MountDiscardProbe(
        Match match, Card listener)
    {
        var seen = new List<(Card?, Card?, object?)>();
        var trigger = new Trigger<CardEventView>(
            "R9弃置监听",
            TriggerKind.Passive,
            hooks: new[] { GameUpdates.CardDiscarded });
        trigger.Register("来源判定", (view, ctx, _) =>
        {
            seen.Add((view.Source, view.TriggerCard, ctx.TryGet(PayloadKeys.Source, out var raw) ? raw : null));
            return Task.CompletedTask;
        }, registrant: listener);
        match.Engine.Bus.Mount(trigger);
        return seen;
    }
}
