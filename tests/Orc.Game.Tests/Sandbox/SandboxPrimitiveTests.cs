using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Judicators;
using Orc.Game.Sandbox;
using Xunit;

namespace Orc.Game.Tests.Sandbox;

/// <summary>
/// 沙盒原语验收（引擎级）：S2 授权门禁、S3 结果载体/硬门槛/审计、S4 六原语的逐项语义。
/// 断言口径：只经 <c>Match.Sandbox</c> 公开入口 ＋ 引擎更新流/事件流，不碰触发器内部。
/// </summary>
public sealed class SandboxPrimitiveTests
{
    // ═══════════════════════ S2：授权门禁 ═══════════════════════

    [Fact]
    public async Task Unauthorized_AllPrimitives_Rejected_StateUnchanged()
    {
        var match = await SandboxTestKit.CreateMatchAsync(authorized: false);
        var player = match.Players[0];
        var seed = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        await match.CardService.PlaceToHandAsync(seed, player);

        var sandbox = match.Sandbox;
        Assert.False(sandbox.IsAuthorized);

        var before = SandboxTestKit.Snapshot(match);

        var results = new[]
        {
            await sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "hand"),
            await sandbox.UnitizeAsync(seed, "support", 0),
            await sandbox.MoveAsync(seed, "frontline", 0),
            await sandbox.RemoveAsync(seed),
            await sandbox.SetPointsAsync(player, 5),
            await sandbox.DrawAsync(player, 1),
        };

        foreach (var result in results)
        {
            Assert.False(result.Ok);
            Assert.Equal(SandboxFailure.NotAuthorized, result.Reason);
        }

        Assert.Equal(before, SandboxTestKit.Snapshot(match));
    }

    [Fact]
    public async Task Authorized_PlayPathDoesNotAuthorize()
    {
        // play/默认装配路径（未显式授权）＝未授权：授权由装配方在构造期决定、缺省 false。
        var match = await SandboxTestKit.CreateMatchAsync(options: new MatchOptions
        {
            SkipMulligan = true,
            OpeningHandSizeFirstPlayer = 0,
            OpeningHandSizeSecondPlayer = 0,
        });

        Assert.False(match.Sandbox.IsAuthorized);
        var rejected = await match.Sandbox.SetPointsAsync(match.Players[0], 3);
        Assert.Equal(SandboxFailure.NotAuthorized, rejected.Reason);
    }

    // ═══════════════════════ S3：硬门槛 / 校验层 / 审计 ═══════════════════════

    [Fact]
    public async Task HardGateTouched_ReturnsReason_AndSnapshotUnchanged()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var opponent = match.Players[1];

        // 占位者：对手支援线 0 号槽。
        var occupant = await match.CardService.CreateAsync(SandboxTestKit.UnitId, opponent);
        await match.CardService.PlaceToSupportLineAsync(
            (UnitCard)occupant, opponent, match.Battlefield.GetSupportLine(opponent).GetInsertionGaps()[0]);

        var before = SandboxTestKit.Snapshot(match);
        var blocked = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "frontline", 0);

        // 前线 0 号槽空 → 允许；改用对手支援线已占位槽验证硬门槛（同一实例不可重复落位）。
        Assert.True(blocked.Ok, $"前线空槽应允许（原因：{blocked.Reason}）。");

        var beforeSecond = SandboxTestKit.Snapshot(match);
        var occupied = await match.Sandbox.UnitizeAsync(occupant, "support", 0);
        Assert.False(occupied.Ok);
        Assert.Equal(SandboxFailure.UnitAlreadyUnitized, occupied.Reason);
        Assert.Equal(beforeSecond, SandboxTestKit.Snapshot(match));

        // 槽位被占（把新单位放到已占位槽）——硬门槛之一。
        var second = await match.CardService.CreateAsync(SandboxTestKit.UnitId, opponent);
        var target = match.Battlefield.GetSupportLine(opponent).GetInsertionGaps()[1];
        var third = await match.CardService.CreateAsync(SandboxTestKit.UnitId, opponent);
        await match.CardService.PlaceToSupportLineAsync((UnitCard)third, opponent, target);

        var snapshot = SandboxTestKit.Snapshot(match);
        var rejected = await match.Sandbox.UnitizeAsync(second, "support", 1);
        Assert.False(rejected.Ok);
        Assert.Equal(SandboxFailure.TargetSlotOccupied, rejected.Reason);
        Assert.Equal(snapshot, SandboxTestKit.Snapshot(match));
        Assert.True(before.Length > 0);
    }

    [Fact]
    public async Task ValidationSkipped_Succeeds_WhenPointsTurnAndHandWouldFail()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[1]; // 非当前行动方
        var currentPlayer = match.CurrentPlayer;
        Assert.NotSame(currentPlayer, player);

        var pointsBefore = player.Points;
        Assert.Equal(0, pointsBefore); // 无点数（正常动作链会因费用不足被拒）

        var card = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        Assert.DoesNotContain(card, player.Hand); // 卡不在手牌（正常动作链会因「不在手牌」被拒）

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.UnitizeAsync(card, "support", 0);

        Assert.True(result.Ok, $"非己方回合／卡不在手牌／零点数均应被跳过（原因：{result.Reason}）。");
        Assert.Contains(Updates.CardPlaced, updates);
        Assert.Contains(GameUpdates.UnitJoined, updates);
        Assert.Equal(pointsBefore, player.Points); // 不扣费
    }

    [Fact]
    public async Task NoAuditSegment_WhenPrimitiveProducesNoSignal()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];

        match.Engine.TakeSegments(); // 清空初始化遗留段

        var result = await match.Sandbox.SetPointsAsync(player, player.Points);
        Assert.True(result.Ok);
        Assert.Equal(0, result.Delta);
        Assert.Equal(player.Points, result.After);

        Assert.Empty(match.Engine.TakeSegments()); // 零信号 ⇒ 零段
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Source == "sandbox.points" && entry.Keywords.Contains("sandbox-primitive"));
    }

    [Fact]
    public async Task AuditTrace_PerCall_WrittenToStream_NoBusUpdate()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];

        var beforeCount = match.Engine.RootStream.Entries
            .Count(entry => entry.Keywords.Contains("sandbox-primitive"));

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.SetPointsAsync(player, player.Points);
        Assert.True(result.Ok);

        var afterCount = match.Engine.RootStream.Entries
            .Count(entry => entry.Keywords.Contains("sandbox-primitive"));

        Assert.Equal(beforeCount + 1, afterCount); // 每次调用恰一条
        Assert.Empty(updates); // 留痕不进总线
    }

    // ═══════════════════════ S4：instantiate ═══════════════════════

    [Fact]
    public async Task Instantiate_ToHand_EmitsHandAdd()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var updates = SandboxTestKit.TrackUpdates(match);

        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "hand");

        Assert.True(result.Ok, result.Reason);
        Assert.NotNull(result.Ref);
        Assert.Contains(GameUpdates.CardHandAdd, updates);
        Assert.False(result.Burned);

        var card = Assert.IsAssignableFrom<CardBase>(SandboxTestKit.Resolve(match, result.Ref!));
        Assert.Contains(card, player.Hand);
    }

    [Fact]
    public async Task Instantiate_ToHandAtLimit_Burns()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        for (var i = 0; i < Player.HandLimit; i++)
        {
            await match.CardService.CreateAndPlaceToHandAsync(SandboxTestKit.UnitId, player);
        }

        Assert.Equal(Player.HandLimit, player.Hand.Count);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "hand");

        Assert.True(result.Ok); // 爆牌＝成功语义
        Assert.True(result.Burned);
        Assert.Contains(GameUpdates.CardBurned, updates);
        Assert.DoesNotContain(GameUpdates.CardHandAdd, updates);
        Assert.Equal(Player.HandLimit, player.Hand.Count);
    }

    [Fact]
    public async Task Instantiate_ToDeckTop_NoSignal()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var deckBefore = player.Deck.Count;

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "deck_top");

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(deckBefore + 1, player.Deck.Count);
        Assert.DoesNotContain(GameUpdates.CardHandAdd, updates);
        Assert.DoesNotContain(Updates.CardPlaced, updates);
        Assert.DoesNotContain(GameUpdates.UnitJoined, updates);

        var top = player.Deck.PeekInstance(0);
        Assert.Equal(result.Ref, top.Id.ToString("N"));
    }

    [Fact]
    public async Task Instantiate_ToSupportSlot_ExactSlot()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var updates = SandboxTestKit.TrackUpdates(match);

        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "support", 4);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal("support", result.Line);
        Assert.Equal(4, result.Slot);
        Assert.Contains(Updates.CardPlaced, updates);
        Assert.Contains(GameUpdates.UnitJoined, updates);

        var slot = match.Battlefield.GetSupportLine(player).GetInsertionGaps()[4];
        Assert.NotNull(slot);
        Assert.Equal(result.Ref, ((FormationGap)slot!).Id.ToString("N"));
    }

    [Fact]
    public async Task Instantiate_ToFrontlineSlot_ExactSlot()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var updates = SandboxTestKit.TrackUpdates(match);

        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.UnitId, player, "frontline", 1);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal("frontline", result.Line);
        Assert.Equal(1, result.Slot);
        Assert.Contains(Updates.CardPlaced, updates);
        Assert.Contains(GameUpdates.UnitJoined, updates);

        var slot = match.Battlefield.FrontLine.GetInsertionGaps()[1];
        var unit = Assert.IsType<UnitCard>(slot);
        Assert.Equal(result.Ref, unit.Id.ToString("N"));
        Assert.Same(slot.Line, unit.GetData<UnitStateData>().Line);
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Instantiate_UnknownDefinition_Rejected()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var before = SandboxTestKit.Snapshot(match);

        var result = await match.Sandbox.InstantiateAsync("u_not_registered", player, "hand");

        Assert.False(result.Ok);
        Assert.Equal(SandboxFailure.DefinitionUnknown, result.Reason);
        Assert.Equal(before, SandboxTestKit.Snapshot(match));
    }

    [Fact]
    public async Task Instantiate_NonUnitToLine_Rejected()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];

        var before = SandboxTestKit.Snapshot(match);
        var result = await match.Sandbox.InstantiateAsync(SandboxTestKit.OrderId, player, "support", 3);

        Assert.False(result.Ok);
        Assert.Equal(SandboxFailure.NotAUnitCard, result.Reason);
        Assert.Equal(before, SandboxTestKit.Snapshot(match)); // 已创建实例被回收，无残留
    }

    // ═══════════════════════ S4：unitize ═══════════════════════

    [Fact]
    public async Task Unitize_FromHandCard_EmitsPlacedAndJoined()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var card = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        await match.CardService.PlaceToHandAsync(card, player);
        Assert.Contains(card, player.Hand);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.UnitizeAsync(card, "support", 4);

        Assert.True(result.Ok, result.Reason);
        Assert.Contains(Updates.CardPlaced, updates);
        Assert.Contains(GameUpdates.UnitJoined, updates);
        Assert.DoesNotContain(card, player.Hand); // 不得「既在手牌又在场」
        Assert.Same(card, match.Battlefield.GetSupportLine(player).GetInsertionGaps()[4]);
    }

    [Fact]
    public async Task Unitize_ToFrontlineSlot_Allowed()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var card = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);

        var result = await match.Sandbox.UnitizeAsync(card, "frontline", 3);

        Assert.True(result.Ok, result.Reason);
        var slot = match.Battlefield.FrontLine.GetInsertionGaps()[3];
        var unit = Assert.IsType<UnitCard>(slot);
        var state = unit.GetData<UnitStateData>();
        Assert.Same(slot, state.Line);
        Assert.Equal(3, state.Line!.IndexOf(unit));
        Assert.False(state.IsDestroyed);
    }

    [Fact]
    public async Task Unitize_OpponentSlot_Rejected()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var opponent = match.Players[1];

        // 对手在自己的支援线 0 号槽放一个单位。
        var enemy = await match.CardService.CreateAsync(SandboxTestKit.UnitId, opponent);
        var enemyResult = await match.Sandbox.UnitizeAsync(enemy, "support", 0);
        Assert.True(enemyResult.Ok, enemyResult.Reason);

        // 我方的 support[0] 解析到「我方线」——对手槽位不可被跨线写入（线归属由前置检查承担）。
        var mine = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        var result = await match.Sandbox.UnitizeAsync(mine, "support", 0);

        Assert.True(result.Ok, result.Reason);
        Assert.Same(enemy, match.Battlefield.GetSupportLine(opponent).GetInsertionGaps()[0]);
        Assert.Same(mine, match.Battlefield.GetSupportLine(player).GetInsertionGaps()[0]);
    }

    [Fact]
    public async Task Unitize_MissingSlot_ReturnsReason()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var card = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        var before = SandboxTestKit.Snapshot(match);

        var outOfRange = await match.Sandbox.UnitizeAsync(card, "support", 99);
        Assert.False(outOfRange.Ok);
        Assert.Equal(SandboxFailure.SlotOutOfRange, outOfRange.Reason);

        var badLine = await match.Sandbox.UnitizeAsync(card, "nowhere", 0);
        Assert.False(badLine.Ok);
        Assert.Equal(SandboxFailure.InvalidArguments, badLine.Reason);

        Assert.Equal(before, SandboxTestKit.Snapshot(match));
    }

    // ═══════════════════════ S4：move ═══════════════════════

    [Fact]
    public async Task Move_SupportToFrontline_Allowed()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, SandboxTestKit.UnitId, 0);
        var origin = unit.GetData<UnitStateData>().Line!;

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.MoveAsync(unit, "frontline", 2);

        Assert.True(result.Ok, result.Reason);
        Assert.Contains(GameUpdates.UnitPositionChanged, updates);
        Assert.Equal(0, origin.Size);
        Assert.Same(unit, match.Battlefield.FrontLine.GetInsertionGaps()[2]);
        Assert.Same(match.Battlefield.FrontLine.GetInsertionGaps()[2], unit.GetData<UnitStateData>().Line);
    }

    [Fact]
    public async Task Move_OccupiedTarget_Rejected()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var mover = await CommandTestKit.PrepareOnSupportAsync(match, player, SandboxTestKit.UnitId, 0);
        var blocker = await CommandTestKit.PrepareOnSupportAsync(match, player, SandboxTestKit.UnitId, 1);
        var blockedSlot = blocker.GetData<UnitStateData>().Line!;

        var before = SandboxTestKit.Snapshot(match);
        var result = await match.Sandbox.MoveAsync(mover, "support", blockedSlot.Size);

        Assert.False(result.Ok);
        Assert.Equal(SandboxFailure.TargetSlotOccupied, result.Reason);
        Assert.Equal(before, SandboxTestKit.Snapshot(match));
    }

    // ═══════════════════════ S4：remove ═══════════════════════

    [Fact]
    public async Task Remove_HandCard_Discards()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var card = await match.CardService.CreateAsync(SandboxTestKit.UnitId, player);
        await match.CardService.PlaceToHandAsync(card, player);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.RemoveAsync(card);

        Assert.True(result.Ok, result.Reason);
        Assert.DoesNotContain(card, player.Hand);
        Assert.Contains("card.destroyed", updates);
        Assert.Contains(GameUpdates.CardDiscarded, updates);
    }

    [Fact]
    public async Task Remove_OnFieldUnit_DeathChain()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, SandboxTestKit.UnitId, 0);
        var slot = unit.GetData<UnitStateData>().Line!;

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.RemoveAsync(unit);

        Assert.True(result.Ok, result.Reason);
        Assert.Contains(GameUpdates.CardDying, updates); // 死亡链（不是一步式销毁）
        Assert.Contains(GameUpdates.CardDied, updates);
        Assert.Equal(0, slot.Size);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Remove_OtherEntity_Destroyed()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var order = await match.CardService.CreateAsync(SandboxTestKit.OrderId, player);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.RemoveAsync(order);

        Assert.True(result.Ok, result.Reason);
        Assert.Contains("card.destroyed", updates);
        Assert.DoesNotContain(GameUpdates.CardDiscarded, updates);
        Assert.DoesNotContain(GameUpdates.CardDying, updates);
        Assert.False(order.Life.IsAlive);
    }

    // ═══════════════════════ S4：draw ═══════════════════════

    [Fact]
    public async Task Draw_N_Cards_Sequence()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var deckBefore = player.Deck.Count;

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.DrawAsync(player, 2);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(2, result.Drawn.Count);
        Assert.Empty(result.BurnedRefs);
        Assert.Equal(deckBefore - 2, player.Deck.Count);
        Assert.Equal(2, updates.Count(u => u == GameUpdates.CardDrawn));
        Assert.Equal(2, updates.Count(u => u == GameUpdates.CardHandAdd));
    }

    [Fact]
    public async Task Draw_AtHandLimit_Burns()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        for (var i = 0; i < Player.HandLimit; i++)
        {
            await match.CardService.CreateAndPlaceToHandAsync(SandboxTestKit.UnitId, player);
        }

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.DrawAsync(player, 1);

        Assert.True(result.Ok);
        Assert.True(result.Burned);
        Assert.Single(result.BurnedRefs);
        Assert.Empty(result.Drawn);
        Assert.Contains(GameUpdates.CardBurned, updates);
        Assert.Equal(Player.HandLimit, player.Hand.Count);
    }

    // ═══════════════════════ S4：set_points ═══════════════════════

    [Fact]
    public async Task SetPoints_SetHigher_EmitsGainedThenChanged()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var start = player.Points;

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.SetPointsAsync(player, 5);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { GameUpdates.PointGained, GameUpdates.PointChanged }, updates);
        Assert.Equal(5, player.Points);
        Assert.Equal(5, result.After);
        Assert.Equal(5 - start, result.Delta);
    }

    [Fact]
    public async Task SetPoints_SetLower_EmitsLostThenChanged()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        await match.Sandbox.SetPointsAsync(player, 8);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.SetPointsAsync(player, 3);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(new[] { GameUpdates.PointLost, GameUpdates.PointChanged }, updates);
        Assert.Equal(3, player.Points);
        Assert.Equal(3, result.After);
        Assert.Equal(-5, result.Delta);
        Assert.Equal(8, result.Before);
    }

    [Fact]
    public async Task SetPoints_SameValue_NoSignal_ReturnsDeltaZero()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        await match.Sandbox.SetPointsAsync(player, 4);

        var updates = SandboxTestKit.TrackUpdates(match);
        var result = await match.Sandbox.SetPointsAsync(player, 4);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Delta);
        Assert.Equal(4, result.After);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task SetPoints_ExceedsSlots_SucceedsWithWarning()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];
        var target = player.PointSlots + 8; // 超出当前槽数（不拒绝、不截断）

        var result = await match.Sandbox.SetPointsAsync(player, target);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(target, player.Points);
        Assert.Equal(SandboxFailure.WarningPointsExceedSlots, result.Warning);
    }

    [Fact]
    public async Task SetPoints_WithGainJudicator_AppliesWrappedAmount()
    {
        var match = await SandboxTestKit.CreateMatchAsync();
        var player = match.Players[0];

        // 词条/资产侧的「获得点数 +N」改写（效果侧真实语义）：请求 5 实际 +7。
        var entry = match.Judicators.Resolve(JudicatorNames.PointGain);
        match.Judicators.RegisterReplaceWrapper(entry, args => new object?[] { (int)args![1]! + 2 });

        var result = await match.Sandbox.SetPointsAsync(player, 5);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(7, result.Actual);
        Assert.Equal(7, player.Points);
        Assert.Equal(SandboxFailure.WarningPointsAdjusted, result.Warning); // 不静默
    }
}
