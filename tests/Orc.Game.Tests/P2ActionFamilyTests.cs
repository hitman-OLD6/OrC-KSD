using System.Reflection;
using Orc.Cards;
using Orc.Core;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P2 动作族验收（S6 卡牌移动 / S7 抽牌与加入手牌 / S8 死亡两端）：
/// 逐条钉死本阶段的**允许行为变更**与**不允许行为变更**，避免「半新半旧」的第三条路径。
/// </summary>
public class P2ActionFamilyTests
{
    // ══════════════════════════ S6：卡牌移动族 ══════════════════════════

    [Fact]
    public async Task Effect_Move_Is_Free_And_Consumes_No_Operate_State()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(unit);

        var supportLine = match.Battlefield.GetSupportLine(player);
        var support = supportLine.GetInsertionGaps().At(0);
        var points = player.Points;
        using var recorder = new UpdateRecorder(match.Engine);

        Assert.True(await match.CommandManager.MoveUnitAsync(unit, support));

        // 动作状态：落地（唯一落地口）——落在请求的阵线、占请求的序号（空隙身份随快照失效）。
        Assert.Same(supportLine, unit.GetData<UnitStateData>().Line);
        Assert.Same(unit, supportLine[0]);
        // 行动状态：零消耗（S6 有意变更：效果移动免费）。
        Assert.Equal(points, player.Points);
        Assert.True(unit.GetData<CommandData>().CanMove);
        Assert.True(unit.GetData<CommandData>().CanAttack);
        // 信号：位置变更恰一次；零 unit.acted、零 point.changed。
        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.UnitPositionChanged));
        Assert.DoesNotContain(GameUpdates.UnitActed, recorder.Types);
        Assert.DoesNotContain(GameUpdates.PointChanged, recorder.Types);
    }

    [Fact]
    public async Task Suppressed_Unit_Is_Rejected_As_Command_But_Still_Movable_By_Effect()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        Assert.True(await SuppressRules.ApplyAsync(unit));

        // 压制只落在**指挥资格/候选面**（R16）：指挥移动不可用、原因可辨识。
        var availability = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(availability.Move.CanUse);
        Assert.Equal(CommandBlockReason.Suppressed, availability.Move.BlockReason);

        // 效果移动不受压制阻断（动作本身零压制判定）。
        var destination = match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);
        Assert.True(await match.CommandManager.MoveUnitAsync(unit, destination));
        Assert.Same(destination.Line, unit.GetData<UnitStateData>().Line);
    }

    [Fact]
    public async Task Command_Move_Still_Pays_And_Clears_Operate_Flags()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var points = player.Points;

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);

        Assert.True(result.IsSuccess);
        Assert.Equal(points - unit.Modifiers.GetEffectiveValue(CardStatFields.OperateCost), player.Points);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }


    // ══════════════════════════ S7：抽牌与加入手牌 ══════════════════════════

    [Fact]
    public async Task Draw_Under_Limit_Emits_Drawn_Before_HandAdd_And_Drawn_Precedes_Hand_Insertion()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        var inHandAtDrawn = new List<bool>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDrawn && payload is not null
                && payload[GameUpdates.PayloadCard] is CardBase drawnCard)
            {
                inHandAtDrawn.Add(player.Hand.Contains(drawnCard));
            }

            return Task.CompletedTask;
        });

        var card = await match.PlayerManager.DrawCard(player);

        // 信号顺序与计数（不允许的行为变更）。
        Assert.Equal(new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd }, recorder.Types);
        Assert.Contains(card, player.Hand);

        // 有意变更（P2 登记）：drawn 的观察时点提前到「入列表之前」——载荷已携带 Card 引用。
        Assert.False(Assert.Single(inHandAtDrawn));
    }

    [Fact]
    public async Task Draw_At_Hand_Limit_Burns_Via_Destroy_Without_Discarded()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        while (player.Hand.Count < Player.HandLimit)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        }

        using var recorder = new UpdateRecorder(match.Engine);
        var card = await match.PlayerManager.DrawCard(player);

        Assert.Equal(
            new[] { GameUpdates.CardDrawn, Updates.CardDestroyed, GameUpdates.CardBurned },
            recorder.Types.Where(t => t is GameUpdates.CardDrawn or Updates.CardDestroyed
                or GameUpdates.CardBurned or GameUpdates.CardDiscarded or GameUpdates.CardHandAdd));
        Assert.DoesNotContain(GameUpdates.CardDiscarded, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardHandAdd, recorder.Types);
        Assert.False(card.Life.IsAlive);
        Assert.Equal(Player.HandLimit, player.Hand.Count);
    }

    [Fact]
    public async Task Non_Draw_Add_To_Hand_Emits_Only_HandAdd()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var player = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        var placed = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, player);

        Assert.Equal(CardPlaceStatus.Placed, placed.Status);
        // 非抽牌加入：只有 card.hand.add（drawn 零次——「未经手牌」路径口径保持）。
        Assert.Equal(
            new[] { GameUpdates.CardHandAdd },
            recorder.Types.Where(t => t == GameUpdates.CardDrawn || t == GameUpdates.CardHandAdd));
        Assert.DoesNotContain(GameUpdates.CardDrawn, recorder.Types);
    }


    [Fact]
    public void Burn_No_Longer_Takes_An_EmitDrawn_Switch()
    {
        var method = typeof(HandLimitBurn).GetMethod("BurnAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(), p => p.ParameterType == typeof(bool));
        Assert.Equal(4, method.GetParameters().Length); // engine / player / card / ct
    }

    // ══════════════════════════ S8：死亡两端 ══════════════════════════





    // ---------- 测试桩 ----------

    private sealed class FixedMoveServices : IMoveActionServices
    {
        public Task<bool> LandMoveAsync(UnitCard unit, Orc.Game.Board.FormationGap destination, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
