using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Targeting;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收①②（单位）：预打出（合法性/交互成功/取消；拒绝不发起请求、不进入交互、不发更新）、
/// 出链（验证/部署/单位化/扣费；仅部署扣费）、复验失败（零副作用＋留痕）、直接驱动防御（占槽/重复单位化）、
/// 未加载卡 fail-fast。候选断言＝被选中的阵型空隙；结果断言＝引擎重排后的实际占用槽。
/// </summary>
public class PlayChainUnitTests
{
    [Fact]
    public async Task Unit_PrePlay_Rejected_When_Not_Enough_Points()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0]; // 回合 1 先手（点数 1）
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player, PlayChainTestKit.UnitCostlyId); // 花费 5
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.BeginUnitPrePlayAsync(unit);

        // 点数不足＝开始拒绝：不发起 targeter 请求、不进入交互、不发任何更新、卡留手。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PrePlayPointShortage, result.FailureReason);
        Assert.Empty(bridge.CollectCalls);
        Assert.Empty(bridge.Begins);
        Assert.Empty(recorder.Updates);
        Assert.Contains(unit, player.Hand);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
        Assert.Equal(1, player.Points);
    }

    [Fact]
    public async Task Unit_PrePlay_Rejected_When_No_Available_Slots()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player); // 花费 1（点数足够）
        var line = match.Battlefield.PlayerASupportLine;

        // 占满支援线（容量＝5 元素：HQ ＋ 4 个单位）——满员＝无插入点（位置模型里「无空位」就是这条）。
        while (!line.IsFull)
        {
            // 夹具（v4）：填位卡同样要经正式装配路径（加入经 `host#join` 成员，未加载卡＝fail-fast）。
            var filler = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
                match, player, PlayChainTestKit.UnitCheapId, toHand: false);
            Assert.Equal(
                PlayResultStatus.Success,
                (await match.PlayManager.JoinUnitAsync(filler, line.GetInsertionGaps().At(1))).Status);
        }

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.PlayManager.BeginUnitPrePlayAsync(unit);

        // 无可用空槽＝预打出不可开始（原因可区分于点数不足）：不进入交互、不发任何更新。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PrePlayNoAvailableSlots, result.FailureReason);
        Assert.Empty(bridge.Begins);
        Assert.Empty(recorder.Updates);
        Assert.Contains(unit, player.Hand);
        Assert.Equal(1, player.Points);
    }

    [Fact]
    public async Task Unit_PrePlay_Confirm_Deploys_Through_Interaction()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;
        bridge.CollectScript = PlayChainTestKit.AllSlotsCandidatesScript(match); // 前端提交超集（全槽位）；后端收敛
        using var recorder = new UpdateRecorder(match.Engine);

        var task = match.PlayManager.BeginUnitPrePlayAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 候选＝己方支援阵型插入空隙（初始＝HQ 两侧）；单选空隙。
        Assert.Equal(line.GetInsertionGaps().Select(gap => gap.Ref), description.AllowedTargets);
        Assert.Equal(SelectorNames.Slot, description.Slots[0].Name);
        Assert.Equal(1, description.Slots[0].Min);
        Assert.Equal(1, description.Slots[0].Max);

        // 确认（选中左侧阵型空隙）→ 自动衔接打出链。
        Assert.True(responder.Complete(description.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, line.GetInsertionGaps().At(0).Ref)));
        var result = await task;

        Assert.Equal(PlayResultStatus.Success, result.Status);
        // 部署链：单位化（组件挂载：位置＝阵线、已毁＝false、类型空列表、三实时值＝对战组件值）＋落位。
        var state = unit.GetData<UnitStateData>();
        Assert.Same(line, state.Line); // 落在选中空隙所在的阵线（空隙身份随快照失效，按线＋序号核对）
        Assert.Equal(0, line.IndexOf(unit));
        Assert.False(state.IsDestroyed);
        Assert.Empty(state.UnitTypes);
        Assert.Equal(1, state.OperateCost);
        Assert.Equal(2, state.Attack);
        Assert.Equal(3, state.Defense);
        var command = unit.GetData<CommandData>();
        Assert.False(command.CanMove);
        Assert.False(command.CanAttack);
        Assert.Same(unit, line[0]);
        // 收尾：仅部署扣费（恰一次）＋离手。
        Assert.Equal(0, player.Points);
        Assert.DoesNotContain(unit, player.Hand);
        Assert.NotEmpty(recorder.Updates);
    }

    [Fact]
    public async Task Unit_PrePlay_Cancel_Leaves_Zero_Side_Effects()
    {
        var bridge = new MockTargeterBridge();
        var match = PlayChainTestKit.CreatePlayMatch(bridge);
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;
        bridge.CollectScript = PlayChainTestKit.AllSlotsCandidatesScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var task = match.PlayManager.BeginUnitPrePlayAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 拖回取消（交互取消路径）→ 终止预打出。
        Assert.True(responder.Cancel(description.RequestId));
        var result = await task;

        // 取消后可观测要求：不发任何游戏更新、卡仍在手牌、无费用扣除、无槽位/组件变化。
        Assert.Equal(PlayResultStatus.Cancelled, result.Status);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.Targeting);
        Assert.Empty(recorder.Updates);
        Assert.Contains(unit, player.Hand);
        Assert.Equal(1, player.Points);
        Assert.Single(line.Members); // 位置未变：支援线仍只有 HQ
        Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    [Fact]
    public async Task Unit_Play_Direct_Drive_Rejects_Occupied_Slot()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var front = match.Battlefield.FrontLine;

        // 新位置模型下插入点没有「被占用」形态：占掉该位置＝阵线快照变化 ⇒ 先前取得的空隙失效；
        // 直接驱动时目标不可用＝拒绝（原语义），失败分类按入口既有契约（部署门户＝FormationGapUnavailable）。
        var occupied = front.GetInsertionGaps().At(0);
        // 夹具（v4）：占位卡经正式装配路径（加入经 `host#join` 成员，未加载卡＝fail-fast）。
        var occupant = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(
            match, player, PlayChainTestKit.UnitCheapId, toHand: false);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(occupant, occupied)).Status);
        Assert.False(occupied.IsCurrent);

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayUnitAsync(unit, occupied);

        // 防御性前置（直接驱动）：目标不可用＝拒绝、零副作用。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.FormationGapUnavailable, result.FailureReason);
        Assert.Empty(recorder.Updates);
        Assert.Contains(unit, player.Hand);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
        Assert.Equal(1, player.Points);
    }

    [Fact]
    public async Task Unit_Play_Direct_Drive_Rejects_Already_Unitized()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;

        var first = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, first.Status);

        using var recorder = new UpdateRecorder(match.Engine);
        // 放回手牌：让「手牌门禁」通过，才能直接驱动到「已单位化」这条拒绝（否则先被 CardNotInHand 拦下）。
        player.Hand.Add(unit);
        var again = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(0)); // 目标＝当前有效插入点（重复单位化先于落位判定）

        // 已单位化（重复部署被拒绝）：目标槽未动、无新更新。
        Assert.Equal(PlayResultStatus.Failed, again.Status);
        Assert.Equal(PlayFailureReason.UnitAlreadyUnitized, again.FailureReason);
        Assert.Equal(2, line.Size); // 位置未动：HQ ＋ 首次部署的单位
        Assert.Empty(recorder.Updates);
    }

    [Fact]
    public async Task Unit_Play_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var first = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var second = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;

        // 第一张正常部署（点数 1 → 0）
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.PlayUnitAsync(first, line.GetInsertionGaps().At(1))).Status);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.PlayManager.PlayUnitAsync(second, line.GetInsertionGaps().At(0)); // 目标＝当前有效插入点

        // 复验失败（打出段；原因与预打出阶段拒绝在阶段上可区分）：仅本次取消语义＋留痕；
        // 零副作用——不部署、不扣费、不发更新、不离手。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.PlayVerificationRejected, result.FailureReason);
        Assert.Equal(2, line.Size); // 位置未动：HQ ＋ 首次部署的单位
        Assert.False(second.TryGetData<UnitStateData>(out _));
        Assert.Empty(recorder.Updates);
        Assert.Contains(second, player.Hand);
        Assert.Equal(0, player.Points);

        // 留痕（**v4 等价观测面**）：原断言＝旧触发链把 `validation:rejected` 写进引擎总流；
        // 该关键词的唯一写入方（旧触发器/验证包装）随 v4 退场，总流里不再有这一条。
        // v4 的对应面＝守卫边把拒绝写进**成员执行的数据面**（`MemberGuardKeys.Rejected`／`Reason`）并 Stop 本级，
        // 入口据此映射出上面的 PlayVerificationRejected。此处按入口同一路径再执行一次 `host#play`
        // （纯验证成员：守卫拒绝即停、零副作用），直接读该标记——与旧留痕同义（拒绝可辨识、可程序化确认）。
        var probe = new Context(new Dictionary<string, object?>
        {
            [GameUpdates.PayloadCard] = second,
            [GameUpdates.PayloadPlayer] = player,
        });
        await second.InvokeMemberAsync(CardProcesses.Play, probe);
        Assert.True(probe.TryGet(MemberGuardKeys.Rejected, out var guardRejected) && guardRejected is true);
    }

    [Fact]
    public async Task Unit_Play_Requires_Loaded_Owner_FailFast()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var raw = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId); // 未加载（无归属）

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayManager.PlayUnitAsync(raw, match.Battlefield.FrontLine.GetInsertionGaps().At(0)));
    }

}
