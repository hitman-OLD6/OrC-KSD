using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收③④（部署词条与加入路径）：部署词条效果按注册序触发（A→B→C 可观测）、
/// 无效果＝空转（条件触发语义、部署不因此失败）、单个效果异常隔离（记录并继续、不阻断部署链）、
/// 触发上下文（被部署单位＋目标槽位）；加入路径（不扣费/不走部署词条/共用单位化/仅发 unit.joined）、
/// 加入拒绝（槽位非空/重复单位化）。
/// S6 随改：A4 部署逻辑路径废弃——部署效果改由**效果 + 注入「部署词条触发器」**表达
/// （<see cref="DeployLogicProbeEffect"/> 测试效果经 <c>Effect.Inject</c> 接入；卸载自动撤销）。
/// 原「空 handler 条目」语义随 A4 退场（P8b：该概念不再存在）。
/// </summary>
public class PlayChainDeploymentTests
{

    [Fact]
    public async Task Deploy_Without_Effects_Still_Unitizes_And_Charges()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;

        // 无部署效果＝空转、仍继续单位化与扣费（部署不因此失败）。
        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Same(unit, line[1]); // 落在插入点 1＝支援线 HQ 之后
        Assert.Equal(0, player.Points);
    }



    [Fact]
    public async Task Join_Rejects_Occupied_Slot()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 夹具（v4）：加入路径经卡实体成员 `host#join` 执行——卡必须走正式装配路径（LoadAsync）
        // 才带编排实体；`new`／未加载的独立构造卡在新模型下是 fail-fast（旧模型静默空转）。
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, player, PlayChainTestKit.UnitCheapId, toHand: false);
        var front = match.Battlefield.FrontLine;

        // 新位置模型下插入点没有「被占用」形态：占用该位置＝阵线快照变化 ⇒ 先前取得的空隙失效。
        // 「同一空隙再用一次」即本用例的原语义（目标已不可用），失败分类按 R2 走 PositionStale（过期须重取插入点）。
        var occupied = front.GetInsertionGaps().At(0);
        var occupant = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, player, PlayChainTestKit.UnitCheapId, toHand: false);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(occupant, occupied)).Status);
        Assert.False(occupied.IsCurrent);

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.JoinUnitAsync(unit, occupied);

        // 目标不可用＝拒绝、可观测；零副作用。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PositionStale, result.FailureReason);
        Assert.Empty(recorder.Updates);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    [Fact]
    public async Task Join_Rejects_Already_Unitized()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        // 夹具（v4）：同 Join_Rejects_Occupied_Slot——加入经 `host#join` 成员，卡须先正式装配。
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, player, PlayChainTestKit.UnitCheapId, toHand: false);
        var front = match.Battlefield.FrontLine;

        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(unit, front.GetInsertionGaps().At(0))).Status);
        using var recorder = new UpdateRecorder(match.Engine);
        var again = await match.PlayManager.JoinUnitAsync(unit, front.GetInsertionGaps().At(1));

        Assert.Equal(PlayResultStatus.Failed, again.Status);
        Assert.Equal(PlayFailureReason.UnitAlreadyUnitized, again.FailureReason);
        Assert.Single(front.Members); // 位置未变：只有首次加入的单位
        Assert.Empty(recorder.Updates);
    }
}

