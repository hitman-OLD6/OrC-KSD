using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2C 验收①（指挥系统）：移动（仅推进：支援线→前线＋扣行动费＋两 bool 外层更新）；取消＝零副作用；
/// 发起拒绝全集（非己方回合 / 归属无效 / 已死亡 / 两动作均不可用——含费用不足动作级置黑）；
/// 「两 bool 只在外层更新」的可观测窗口（更新发射时 bool 未更新、费未扣）；内层触发器直接执行不写 bool/不扣费；
/// 可用性查询纯函数语义；发起拒绝不进入交互（无 targeter 请求）。
/// </summary>
public class CommandSystemTests
{
    [Fact]
    public async Task Move_Advances_To_Front_Line_Deducts_Cost_And_Updates_Flags()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var support = match.Battlefield.PlayerASupportLine;
        var front = match.Battlefield.FrontLine;

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, front.GetInsertionGaps().At(0).Ref);

        // 成功：旧槽移除 → 放入目标空槽（原槽变为空槽）；扣 1 点行动费；两 bool 外层更新（步兵：二选一全清）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(1, support.Size);
        Assert.Same(unit, front[0]);
        Assert.Same(front, unit.GetData<UnitStateData>().Line);
        Assert.Equal(0, player.Points);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task Move_Emits_PositionChanged_Once_With_Frozen_Payload()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);
        var oldSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        var newSlot = match.Battlefield.FrontLine.GetInsertionGaps().At(0);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, newSlot.Ref);

        // unit.position.changed 恰一次（移动专属）＋ 收尾扣行动费的 point.changed（E1-25 后续）；
        // 载荷 {Unit, OldPosition, NewPosition}——均槽位引用。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(
            new[] { GameUpdates.UnitPositionChanged, GameUpdates.PointChanged, GameUpdates.UnitActed }, // E1-33：行动后
            recorder.Types);
        var payload = recorder.Updates[0].Payload!;
        Assert.Same(unit, payload[GameUpdates.PayloadUnit]);
        Assert.Same(oldSlot.Line, ((BoardPosition)payload[GameUpdates.PayloadOldPosition]!).Line);
        Assert.Same(newSlot.Line, ((BoardPosition)payload[GameUpdates.PayloadNewPosition]!).Line);
    }

    [Fact]
    public async Task Move_Observation_Window_Shows_Flags_And_Points_Not_Yet_Updated()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 更新回调内的中间态探针：槽位已变更，但「两 bool 未更新、行动费未扣」（只在外层更新的预期观测窗口）。
        var probes = new List<(bool CanMove, bool CanAttack, int Points)>();
        using var probe = match.Engine.Subscribe((type, _, _) =>
        {
            if (type == GameUpdates.UnitPositionChanged)
            {
                var command = unit.GetData<CommandData>();
                probes.Add((command.CanMove, command.CanAttack, player.Points));
            }

            return Task.CompletedTask;
        });

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);

        Assert.Equal(CommandResultStatus.Success, result.Status);
        var state = Assert.Single(probes);
        Assert.True(state.CanMove);   // 发射时点：bool 尚未更新
        Assert.True(state.CanAttack); // 同上
        Assert.Equal(1, state.Points); // 发射时点：行动费尚未扣
        // 流程返回后：外层收尾已完成。
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.Equal(0, player.Points);
    }

    [Fact]
    public async Task Move_By_Tank_Clears_Only_CanMove()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.TankId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Battlefield.FrontLine.GetInsertionGaps().At(0).Ref);

        // 坦克双动：移动仅清 CanMove；CanAttack 保留（两动作独立清位）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.False(unit.GetData<CommandData>().CanMove);
        Assert.True(unit.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task Move_Cancel_Leaves_Zero_Side_Effects()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);
        var support = match.Battlefield.PlayerASupportLine;

        var result = await CommandTestKit.CancelCommandAsync(match, bridge, unit);

        // 取消（拖回）＝零副作用：不发更新、不扣费、不清位、槽位不变。
        Assert.Equal(CommandResultStatus.Cancelled, result.Status);
        Assert.Null(result.FailureReason);
        Assert.Same(unit, support[1]);
        Assert.Equal(1, player.Points);
        Assert.True(unit.GetData<CommandData>().CanMove);
        Assert.True(unit.GetData<CommandData>().CanAttack);
        Assert.Empty(recorder.Updates);
        Assert.Single(bridge.Begins); // 一次拖拽＝一次请求、一次终局
        // 新契约无候选收集（Q19＝a）：收集记录恒空。
    }

    [Fact]
    public async Task Move_Is_Blocked_For_Front_Line_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 仅推进：前线单位无移动候选（动作级不可用——NoCandidates、候选空）。
        var report = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(report.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, report.Move.BlockReason);
        Assert.Empty(report.Move.Candidates);

        // 对照：移动缺位不改变攻击面——前线单位对敌方 HQ 的攻击资格正常（前线→敌 HQ 允许）。
        Assert.True(report.Attack.CanUse);
        Assert.Contains(match.Players[1].Hq.Ref, report.Attack.Candidates); // W3-3：HQ 目标＝实体引用
    }

    [Fact]
    public async Task Begin_Rejects_When_No_Action_Available_By_Candidates()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        // 两动作均不可用（候选均空）：支援线单位（攻击无可达目标；敌无场上单位、HQ 不可达）
        // ＋前线占满（移动无空槽——占位用己方单位，不作为攻击目标）。
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        for (var i = 0; i < match.Battlefield.FrontLineCapacity; i++)
        {
            await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, i);
        }

        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var report = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(report.Move.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, report.Move.BlockReason);
        Assert.False(report.Attack.CanUse);
        Assert.Equal(CommandBlockReason.NoCandidates, report.Attack.BlockReason);

        // 发起拒绝：不进入交互、零副作用（无 targeter 请求）。
        var result = await match.CommandManager.BeginCommandAsync(unit);
        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NoActionAvailable, result.FailureReason);
        Assert.Empty(bridge.Begins);
        Assert.Empty(bridge.CollectCalls);
    }

    [Fact]
    public async Task Begin_Rejects_NonOwnerTurn_Without_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        await match.EndTurn(); // 切到玩家 B 回合
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var report = match.CommandManager.GetCommandAvailability(unit);
        Assert.Equal(CommandBlockReason.NonOwnerTurn, report.IneligibleReason);

        var result = await match.CommandManager.BeginCommandAsync(unit);

        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NonOwnerTurn, result.FailureReason);
        Assert.Empty(bridge.Begins); // 发起拒绝：不进入交互、零副作用
    }

    [Fact]
    public async Task Begin_Rejects_Owner_Missing()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        // 加入路径不要求归属：构造无主已单位化单位（Owner＝null）。
        // 夹具（v4）：加入经卡实体成员 `host#join`，实体只由 `LoadAsync(owner)` 建立（加载必带归属），
        // 因此「无归属」样本＝加载后清空归属（Owner 为私有 setter——夹具经反射还原本用例的样本口径；
        // 生产面不允许无主加载，故不为此加宽松路径）。
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        await unit.LoadAsync(match.Players[0]);
        typeof(CardBase).GetProperty(nameof(CardBase.Owner))!.SetValue(unit, null);
        Assert.Null(unit.Owner);
        var joinResult = await match.PlayManager.JoinUnitAsync(unit, match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1)); // 插入点 1＝紧随 HQ（HQ 位置不变）
        Assert.Equal(PlayResultStatus.Success, joinResult.Status);
        CommandTestKit.Activate(unit);

        var result = await match.CommandManager.BeginCommandAsync(unit);

        // 无归属单位不可被指挥（Owner=null → 指挥发起校验拒绝）。
        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.OwnerInvalid, result.FailureReason);
        Assert.Empty(bridge.Begins);
    }

    [Fact]
    public async Task Begin_Rejects_Dead_Unit()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // A 的脆皮（攻 1 / 防 2）攻击 B 的巨兽（攻 6 / 防 7）→ 被反击致死。
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        var beast = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BeastId, 0);
        CommandTestKit.Activate(weak);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var killResult = await CommandTestKit.RunCommandAsync(match, bridge, weak, beast.Ref);
        Assert.Equal(CommandResultStatus.Success, killResult.Status);
        Assert.True(weak.GetData<UnitStateData>().IsDestroyed);

        var result = await match.CommandManager.BeginCommandAsync(weak);

        // 已死亡＝发起拒绝（尸体不可被指挥）。
        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.UnitDead, result.FailureReason);
    }

    [Fact]
    public async Task Begin_Rejects_When_Both_Actions_Unavailable_By_Flags()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        // 常态初值：部署/加入时 false/false（本回合不可行动）。

        var result = await match.CommandManager.BeginCommandAsync(unit);

        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NoActionAvailable, result.FailureReason);
        Assert.Empty(bridge.Begins);
    }

    [Fact]
    public async Task Begin_Rejects_Point_Shortage_As_Action_Level_Block()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0]; // 1 点
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.CostlyId, 1); // 行动费 2
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 费用不足＝动作级拒绝（整体置黑；非逐目标筛选）——两动作均不可用 → 发起拒绝。
        var report = match.CommandManager.GetCommandAvailability(unit);
        Assert.False(report.Move.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, report.Move.BlockReason);
        Assert.False(report.Attack.CanUse);
        Assert.Equal(CommandBlockReason.PointShortage, report.Attack.BlockReason);

        var result = await match.CommandManager.BeginCommandAsync(unit);

        Assert.Equal(CommandResultStatus.Failed, result.Status);
        Assert.Equal(CommandFailureReason.NoActionAvailable, result.FailureReason);
        Assert.Empty(bridge.Begins);
        Assert.Equal(1, player.Points); // 零副作用
    }


    [Fact]
    public async Task Availability_Query_Is_Pure_And_Shares_Source_With_Flow()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        using var recorder = new UpdateRecorder(match.Engine);

        // 纯查询：无副作用、不发更新、不启动交互、不改变任何状态（可被 UI 预览高频调用）。
        var first = match.CommandManager.GetCommandAvailability(unit);
        var second = match.CommandManager.GetCommandAvailability(unit);

        Assert.True(first.AnyActionAvailable);
        Assert.Equal(second.Move.Candidates, first.Move.Candidates); // 与流程内部同源（稳定）
        Assert.Empty(recorder.Updates);
        Assert.Empty(bridge.Begins);
        Assert.Equal(1, player.Points);
        Assert.True(unit.GetData<CommandData>().CanMove);
    }

    [Fact]
    public async Task Move_Candidates_Are_All_Front_Line_Empty_Slots()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        // 移动候选＝前线任意空槽（不被邻位动态规则约束）；已占槽不在。
        // 适配〔后置项 C〕：占位单位改为己方（无敌人占线——推进前置满足；保持原覆盖语义）。
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.InfantryId, 0); // 占一个前线槽（己方）
        CommandTestKit.Activate(unit);
        var front = match.Battlefield.FrontLine;

        var report = match.CommandManager.GetCommandAvailability(unit);

        Assert.True(report.Move.CanUse);
        // 候选＝前线**全部插入空隙**（有序列表模型：候选是"落位请求"，不是已占槽；
        // 占用不会移除空隙，只有满员才没有空隙）。
        var expected = front.GetInsertionGaps().Select(gap => gap.Ref).ToArray();
        Assert.Equal(expected, report.Move.Candidates);
    }

    [Fact]
    public async Task Interaction_Candidates_Are_Union_And_Off_Candidates_Are_Rejected_Until_Valid()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyOnFront = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var enemyOnSupport = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var front = match.Battlefield.FrontLine;

        var task = match.CommandManager.BeginCommandAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 候选＝可用动作的并集：移动（推进前置不满足〔敌占前线〕＝整类剔除、不进入候选面——后置项 C 适配）
        // ＋攻击（步兵视角＝仅敌前线单位；敌支援线不计、HQ（支援线）不可达）。
        var expected = new[] { enemyOnFront.Ref };
        Assert.Equal(expected, description.AllowedTargets);
        Assert.DoesNotContain(enemyOnSupport.Ref, description.AllowedTargets);
        Assert.DoesNotContain(playerB.Hq.Ref, description.AllowedTargets); // W3-3：HQ 目标＝实体引用（步兵不可达）
        Assert.DoesNotContain(front.GetInsertionGaps().At(0).Ref, description.AllowedTargets); // 移动剔除（前线空槽不作为移动候选）

        // 无效目标点击＝选择器终局 Failed → targeter 内部重入（同一选择器）。
        var rejected = responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargetSlot.DefaultName, enemyOnSupport.Ref));
        Assert.True(rejected);

        // 纠正后提交合法目标（敌方前线单位）→ 确认 → 攻击链执行（非法后异步重入）。
        var (reentryDescription, reentryResponder) = await bridge.WaitForNextBeginAsync();
        var accepted = reentryResponder.Complete(
            reentryDescription.RequestId,
            TargeterTestKit.Selection(TargetSlot.DefaultName, enemyOnFront.Ref));
        Assert.True(accepted);
        var result = await task;
        Assert.Equal(CommandResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task Attack_Unit_Resolves_Mutual_Damage_And_Finalizes_Flags()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);

        // 互伤（同时结算、以互扣前实时值为基准）：目标 5-2=3、攻击者 5-2=3；收尾：扣费＋两 bool（步兵二选一全清）。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(3, target.GetData<UnitStateData>().Defense);
        Assert.Equal(3, attacker.GetData<UnitStateData>().Defense);
        Assert.False(attacker.GetData<CommandData>().CanAttack);
        Assert.False(attacker.GetData<CommandData>().CanMove);
        Assert.Equal(0, playerA.Points);
    }

    [Fact]
    public async Task Attack_Hq_Deducts_Health_Without_Counterattack()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var attacker = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var enemyHq = playerB.Hq; // W3-3：HQ 目标＝实体引用

        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, enemyHq.Ref);

        // HQ 简路：伤害＝攻击者实时攻击力（W3-3：经 HQ 数值路径与管线）；HQ 不反击（攻击者不受伤害）；收尾照常。
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(Player.InitialHqHealth - 2, playerB.HqHealth);
        Assert.Equal(Player.InitialHqHealth - 2, enemyHq.Health); // 实体读面与 Player 转发读面同值（20-2=18）
        Assert.Equal(5, attacker.GetData<UnitStateData>().Defense);
        Assert.False(attacker.GetData<CommandData>().CanAttack);
        Assert.Equal(0, playerA.Points);
    }

    [Fact]
    public async Task Ununitized_Card_Fails_Fast_On_Command()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        await unit.LoadAsync(player); // 未单位化（未加入战场）

        // 装配性错误：未单位化卡不能驱动指挥（fail-fast 明确异常）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.CommandManager.BeginCommandAsync(unit));
        Assert.Throws<InvalidOperationException>(() => match.CommandManager.GetCommandAvailability(unit));
    }
}
