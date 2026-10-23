using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S10 功能点④验收（在场回合数——单位字段/组件、回合事件驱动计数）：
/// ④1 计数语义（入场即第 1 回合；己方回合正式开始〔turn.start〕递增、对方回合不递增；示例口径：
/// 全局回合 5 部署→回合 7＝2→回合 9＝3）；④2 读取面（读取当前在场回合数——「是否第 N 回合」由值比较派生）；
/// ④3 边界断言（死亡停止〔值保持〕；转换重置；加入路径同等；未入场不适用、不抛错）；
/// ④4 边界核验（静默变更——不新增信号、不发更新）。
/// </summary>
public class UnitTurnsInPlayTests
{
    // ---------- ④1 计数语义（示例口径：全局回合 5 部署→7＝2→9＝3） ----------

    [Fact]
    public async Task Deploy_At_Turn_Five_Advances_On_Own_Turn_Starts_Only()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 推进到全局回合 5（A）：回合 1(A)→2(B)→3(A)→4(B)→5(A)〔每回合完整开始序列已完结〕
        for (var i = 0; i < 4; i++)
        {
            await match.EndTurn();
        }

        Assert.Equal(5, match.TurnNumber);

        // 玩家A 于全局回合 5 部署单位 X（加入路径入场——与部署路径同等：入场完成点初始化）
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);

        // 入场当回合＝第 1 回合（「不存在入场后为 0/未定义状态」）
        Assert.Equal(1, unit.TurnsInPlay);

        // 全局回合 6（B）：对方回合不递增（单方步进）
        await match.EndTurn();
        Assert.Equal(6, match.TurnNumber);
        Assert.Equal(1, unit.TurnsInPlay);

        // 全局回合 7（A）：己方回正式开始递增 → 第 2 回合
        await match.EndTurn();
        Assert.Equal(7, match.TurnNumber);
        Assert.Equal(2, unit.TurnsInPlay);

        // 全局回合 8（B）：不递增
        await match.EndTurn();
        Assert.Equal(2, unit.TurnsInPlay);

        // 全局回合 9（A）：→ 第 3 回合（「是否处于在场第 N 回合」＝读取值比较派生）
        await match.EndTurn();
        Assert.Equal(9, match.TurnNumber);
        Assert.Equal(3, unit.TurnsInPlay);
        Assert.True(unit.TurnsInPlay == 3); // 派生查询形态示例（值比较——不单独立面）
    }

    [Fact]
    public async Task Real_Deployment_Path_Initializes_Turns_In_Play_To_One()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0]; // 回合 1：点数 1
        var unit = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);

        Assert.Null(unit.TurnsInPlay); // 手牌中＝未入场（不适用）

        // 真实部署链（预打出 → 交互 → 部署 → 单位化）
        bridge.CollectScript = PlayChainTestKit.AllSlotsCandidatesScript(match);
        var task = match.PlayManager.BeginUnitPrePlayAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(
                TargeterTestKit.PrimarySlot(description), match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0).Ref)));
        var deployed = await task;

        Assert.Equal(PlayResultStatus.Success, deployed.Status);
        Assert.Equal(1, unit.TurnsInPlay); // 部署即第 1 回合（部署路径与加入路径同等）
    }

    // ---------- ④3 边界断言（死亡停止 / 转换重置 / 未入场不适用） ----------

    [Fact]
    public async Task Death_Stops_Advance_And_Value_Remains_Readable()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1); // 防 2
        await match.EndTurn(); // 回合 2（B）
        await match.EndTurn(); // 回合 3（A）→ 递增
        Assert.Equal(2, unit.TurnsInPlay);

        // 致死（经伤害门户——防御归零统一死亡衔接）
        await unit.ApplyDefenseDamageAsync(2);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);

        // 死亡后停止递增（值保持最后值、可读）
        Assert.Equal(2, unit.TurnsInPlay);

        // 再跨两个回合（含一个己方回合）：不再递增
        await match.EndTurn(); // 回合 4（B）
        await match.EndTurn(); // 回合 5（A）
        Assert.Equal(2, unit.TurnsInPlay);
    }

    [Fact]
    public async Task Transform_Rebuilt_Instance_Resets_Count()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await match.EndTurn(); // 回合 2（B）
        await match.EndTurn(); // 回合 3（A）→ 2
        Assert.Equal(2, unit.TurnsInPlay);
        var originalSlot = unit.GetData<UnitStateData>().Line!;

        // S9 转换组合＝离场＋销毁＋按定义重建（加入路径）＋放回原位置
        await match.CommandManager.LeaveBattlefieldAsync(unit);
        await match.Engine.DestroyCard(unit);
        var result = await match.CardService.CreateAndPlaceToSupportLineAsync(CommandTestKit.InfantryId, playerA, originalSlot.GetInsertionGaps().At(0));

        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        var rebuilt = (UnitCard)result.Card!;
        Assert.Equal(1, rebuilt.TurnsInPlay); // 转换重建的新实例＝计数重置（静态重建、无原状态携带）
        Assert.Same(originalSlot, rebuilt.GetData<UnitStateData>().Line); // 放回原位置
    }

    [Fact]
    public async Task Not_On_Field_Reads_Null_Without_Throwing()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 手牌中（已加载、未入场）：不适用——null、不抛错（沿用降级先例）
        var inHand = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        Assert.Null(inHand.TurnsInPlay);

        // 已加载、未放入任何区域：同样不适用
        var loaded = await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId);
        Assert.Null(loaded.TurnsInPlay);

        // 独立构造（脱离对局）：不适用
        var standalone = new UnitCard(match.Engine, match.CardLibrary.Get(CommandTestKit.InfantryId));
        Assert.Null(standalone.TurnsInPlay);
    }

    // ---------- ④4 边界核验（静默变更——不新增信号、不发更新） ----------

    [Fact]
    public async Task Advance_Is_Silent_No_Extra_Updates()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        using var recorder = new UpdateRecorder(match.Engine);

        // 回合 2（B——对方回合）：TurnsInPlay 递增不发生；序列＝既有 9 条
        // （end.before/end/start.before/start/slot.changed〔E1-25 结算〕/point.changed〔点数设为〕/drawn/hand.add/start.after）
        await match.EndTurn();
        Assert.Equal(
            new[]
            {
                GameUpdates.TurnEndBefore, GameUpdates.TurnEnd,
                GameUpdates.TurnStartBefore, GameUpdates.TurnStart,
                GameUpdates.SlotChanged,
                GameUpdates.PointChanged,
                GameUpdates.CardDrawn, GameUpdates.CardHandAdd,
                GameUpdates.TurnStartAfter,
            },
            recorder.Types);
        Assert.Equal(1, unit.TurnsInPlay);

        // 回合 3（A——己方回合）：TurnsInPlay 递增发生且静默——序列同样仅既有 9 条（零额外信号；不新增信号面）
        recorder.Clear();
        await match.EndTurn();
        Assert.Equal(
            new[]
            {
                GameUpdates.TurnEndBefore, GameUpdates.TurnEnd,
                GameUpdates.TurnStartBefore, GameUpdates.TurnStart,
                GameUpdates.SlotChanged,
                GameUpdates.PointChanged,
                GameUpdates.CardDrawn, GameUpdates.CardHandAdd,
                GameUpdates.TurnStartAfter,
            },
            recorder.Types);
        Assert.Equal(2, unit.TurnsInPlay); // 递增已发生（静默数据变更）
    }
}
