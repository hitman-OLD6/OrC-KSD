using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收⑦（更新发射）：部署＝card.played ×1 ＋ card.placed ×1（W3-A3 加线）＋ unit.deployed ×1（不发 unit.joined；
/// played 先于 placed、placed 先于 deployed；deployed 位于单位化完成之后——发射时点状态探针）；
/// 加入＝card.placed ×1（W3-A3 加线）＋ unit.joined ×1（不发 card.played / unit.deployed）；
/// 指令＝card.played ×1（不发 unit 类）。载荷键沿用既有常量。
/// </summary>
public class PlayChainUpdateTests
{
    [Fact]
    public async Task Deploy_Emits_CardPlayed_Then_UnitDeployed_Once_Without_Joined()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;

        // 发射时点探针：unit.deployed 到达时，单位化（组件挂载＋落位）是否已完成。
        var deployedProbe = new List<(bool HasComponents, bool Occupied)>();
        using var probe = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.UnitDeployed)
            {
                var deployedUnit = (UnitCard)payload![GameUpdates.PayloadUnit]!;
                var position = (BoardPosition)payload[GameUpdates.PayloadPosition]!;
                deployedProbe.Add((
                    deployedUnit.TryGetData<UnitStateData>(out _),
                    position.Line.Contains(deployedUnit) && position.Index == position.Line.IndexOf(deployedUnit)));
            }

            return Task.CompletedTask;
        });
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Success, result.Status);
        // 组合与互斥：played ×1 ＋ card.placed ×1（W3-A3 加线）＋ deployed ×1 ＋ 末条 point.changed（收尾扣部署费；E1-25 后续）；
        // 不发 joined；顺序 played → placed → deployed → point.changed。
        Assert.Equal(
            new[]
            {
                GameUpdates.CardPlayed, Updates.CardPlaced, GameUpdates.UnitDeployed, GameUpdates.PointChanged,
            },
            recorder.Types);
        Assert.DoesNotContain(GameUpdates.UnitJoined, recorder.Types);
        // card.placed 载荷 {Card}（W3-A3：单位化完成后、完成信号之前）
        var placedPayload = recorder.Updates[1].Payload!;
        Assert.Same(unit, placedPayload[GameUpdates.PayloadCard]);
        // 载荷 {Unit, Position}
        var deployedPayload = recorder.Updates[2].Payload!;
        Assert.Same(unit, deployedPayload[GameUpdates.PayloadUnit]);
        Assert.Equal(1, ((BoardPosition)deployedPayload[GameUpdates.PayloadPosition]!).Index);
        // 时点：位于单位化完成（组件挂载＋落位）之后
        var state = Assert.Single(deployedProbe);
        Assert.True(state.HasComponents);
        Assert.True(state.Occupied);
    }

    [Fact]
    public async Task Join_Emits_UnitJoined_Once_Without_Played_Or_Deployed()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        // 夹具（v4）：加入经卡实体成员 `host#join`，卡须经正式装配路径加载（未加载卡＝fail-fast）。
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, match.Players[0], PlayChainTestKit.UnitCheapId, toHand: false);
        var front = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.JoinUnitAsync(unit, front);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        // 加入路径：card.placed ×1（W3-A3 加线：放置驱动信号——单位化成功后、完成信号之前）＋ unit.joined ×1；
        // 不发 card.played、不发 unit.deployed。
        Assert.Equal(new[] { Updates.CardPlaced, GameUpdates.UnitJoined }, recorder.Types);
        var placedPayload = recorder.Updates[0].Payload!;
        Assert.Same(unit, placedPayload[GameUpdates.PayloadCard]);
        var joinedPayload = recorder.Updates[1].Payload!;
        Assert.Same(unit, joinedPayload[GameUpdates.PayloadUnit]);
        var joinedPosition = Assert.IsType<BoardPosition>(joinedPayload[GameUpdates.PayloadPosition]);
        Assert.Same(match.Battlefield.FrontLine, joinedPosition.Line); // 落位＝请求的空隙所在阵线（按线＋序号核对）
        Assert.Equal(0, joinedPosition.Index);
    }

    [Fact]
    public async Task Command_Emits_CardPlayed_Once_Without_Unit_Updates()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var command = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, PlayChainTestKit.CommandCheapId);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        // 指令＝card.played ×1 ＋ 末条 point.changed（收尾扣部署费；E1-25 后续）；不发 unit 类更新。
        Assert.Equal(new[] { GameUpdates.CardPlayed, GameUpdates.PointChanged }, recorder.Types);
        Assert.DoesNotContain(GameUpdates.UnitJoined, recorder.Types);
        Assert.DoesNotContain(GameUpdates.UnitDeployed, recorder.Types);
    }
}
