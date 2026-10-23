using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W3-3 G11「HQ 实体化」四场景验收（设计定稿：《实现文档》S4；机制基础：W2 系列链/修饰/管线/效果装载）：
/// ①实体装载 a–h：双方各一 HQ 实体（槽 0 占位者＝HQ 实体、Player 不再作为占位者）／Player 持 HQ 引用
///   （转发读面恒可用、不依赖入槽）／邻位·守护·拦截回归（既有测试随改承载）／装载面可挂可卸（最小回路：
///   挂→生效→卸→还原含数值还原）／终局迁移回归（更新流冻结＋只读可用——见 MatchOutcomeTests 随改）／
///   攻击基础路径（血量正确＋数值变化信号可订阅）／Player.HqHealth 转发读面／目标承载（实体引用＋无槽引用形态）。
/// ②数值+监听：挂 +X→血量 20+X；订阅者收到信号（方向可辨）；Player.HqHealth 同值；监听注销无残留。
/// ③管线改写：受 3 伤扣 2（挂钩改写）；受 1 伤钳 0 不变＋无变化零发射、不终局判定；卸载后行为复原。
/// ④管线介入：5 血受 7 伤→介入→存活 4 不终局；不挂→0→终局；非致命不介入；单次结算内不递归；
///   介入卸载后行为复原。
/// 另：直调 HQ 数值路径（不经攻击流程）致归零→终局——「统一响应」与「判定由 HQ 承接（不内联）」的直接证据。
/// </summary>
public class HqEntityTests
{
    // ---------- 场景①：实体装载 ----------

    [Fact]
    public async Task Scene1_Hq_Entities_Place_On_The_Middle_Slot_And_Player_Holds_Reference()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var lineA = match.Battlefield.PlayerASupportLine;
        var lineB = match.Battlefield.PlayerBSupportLine;

        // a) 双方各一 HQ 实体；居中槽占位者＝HQ 实体（Player 不再作为占位者）
        Assert.NotNull(playerA.Hq);
        Assert.NotNull(playerB.Hq);
        Assert.Same(playerA.Hq, lineA[0]);
        Assert.Same(playerB.Hq, lineB[0]);
        Assert.IsNotType<Player>(lineA[0]); // 否定：占位者不再是 Player
        Assert.NotSame(playerA, lineA[0]);

        // b) Player 持 HQ 引用（双向互持）；布局语义锚点（占位槽引用）就绪
        Assert.Same(playerA, playerA.Hq.Owner);
        Assert.Same(lineA[0], playerA.Hq.Line);
        Assert.Same(lineB[0], playerB.Hq.Line);

        // g) Player.HqHealth 转发读面可用（只读、初值 20＝不依赖入槽的读面口径；实体读面同值）
        Assert.Equal(Player.InitialHqHealth, playerA.HqHealth);
        Assert.Equal(Player.InitialHqHealth, playerA.Hq.Health);
        Assert.Equal(Player.InitialHqHealth, playerB.HqHealth);

        // 邻位候选回归：HQ 计入被占位（支援线邻位＝槽 1）
        Assert.Equal(new[] { 0, 1 }, lineA.GetInsertionGaps().Select(s => s.Ordinal));
        Assert.Equal(new[] { 0, 1 }, lineB.GetInsertionGaps().Select(s => s.Ordinal));
    }

    [Fact]
    public async Task Scene1_Target_Carrier_Is_Hq_Entity_Reference_Without_Slot_Form()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var artillery = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.ArtilleryId, 1); // 任意线组
        CommandTestKit.Activate(artillery);

        var report = match.CommandManager.GetCommandAvailability(artillery);

        // h) 正向：候选出现的 HQ 承载＝HQ 实体引用（hq.Ref）；炮兵任意线组合含 HQ
        Assert.Contains(playerB.Hq.Ref, report.Attack.Candidates);

        // h) 否定（显式）：不存在「槽引用形态的 HQ 目标」产出（不留双承载的必要证据）
        Assert.DoesNotContain(match.Battlefield.PlayerBSupportLine.GetInsertionGaps().At(0).Ref, report.Attack.Candidates);
        Assert.DoesNotContain(match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0).Ref, report.Attack.Candidates); // 己方 HQ 槽亦不产出
    }

    [Fact]
    public async Task Scene1_LoadingFace_Mount_Effect_Unmount_And_Restore_Minimal_Circuit()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var hq = playerB.Hq;
        var source = new object(); // 施加方标识（按来源撤销）

        // d) 最小回路（代表性装载物＝修饰器）：挂载 → 生效 → 卸载（按来源撤销）→ 还原（含数值还原）
        await hq.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.HqHealth, 3, source));
        Assert.Equal(23, hq.Health);
        Assert.Equal(23, playerB.HqHealth);

        await hq.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(Player.InitialHqHealth, hq.Health); // 数值还原（卸载后回到挂载前值）
        Assert.Equal(Player.InitialHqHealth, playerB.HqHealth);

        // 装载面就位核对：修饰容器（挂/卸/按来源撤销）＋效果容器面（AddEffect/Effects）＋引擎订阅面（场景②）
        Assert.Empty(hq.Modifiers.All);
        Assert.Empty(hq.Effects);
    }

    [Fact]
    public async Task Scene1_Attack_Base_Path_Applies_Damage_Through_Pipeline_With_Signal()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var tank = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.TankId, 0); // 攻 3（前线→敌支援线 HQ 相邻）
        CommandTestKit.Activate(tank);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await CommandTestKit.RunCommandAsync(match, bridge, tank, playerB.Hq.Ref);

        // f) 双断言——①血量数值正确（20−3=17）；②该次伤害产生可订阅、可辨识的数值变化信号（经管线痕迹）
        Assert.Equal(CommandResultStatus.Success, result.Status);
        Assert.Equal(17, playerB.HqHealth);
        Assert.Equal(17, playerB.Hq.Health);
        Assert.Equal(MatchState.InProgress, match.State); // 不归零 → 不终局

        var statUpdate = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardStatChanged);
        Assert.Equal(GameUpdates.CardStatChanged, statUpdate.Type);
        Assert.Same(playerB.Hq, statUpdate.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { CardStatFields.HqHealth }, ModifierTestKit.ChangedFieldsOf(statUpdate.Payload));
    }

    [Fact]
    public async Task Scene1_Direct_Damage_Path_Zero_Ends_Match_As_Hq_Side_Uniform_Response()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var hq = playerB.Hq;

        // 统一响应：非攻击来源（直调 HQ 数值路径）致归零 → 同一判定终局；胜者＝HQ 归零方之对手；
        // 判定由 HQ 侧承接（不内联于攻击流程——本调用不经过任何攻击/指挥流程）。
        await hq.ApplyDamageAsync(25);

        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
        Assert.Equal(0, playerB.HqHealth);

        // 终局后只读可用：继续读面稳定（终局不改变只读查询语义）
        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
    }

    [Fact]
    public async Task Scene1_Zero_Does_Not_Run_Death_Or_Destroy_Semantics_And_Keeps_Loading_Face()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var hq = playerB.Hq;

        // 归零前挂载物：修饰器（+3）＋介入挂钩（0 调整——仅观察保留性）
        await hq.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.HqHealth, 3, new object()));
        hq.AddLethalIntervention(new HqLethalIntervention(new object(), _ => 0));
        using var recorder = new UpdateRecorder(match.Engine);

        await hq.ApplyDamageAsync(25); // 23-25 → 钳制表现 0 → 终局

        // 终局成立（统一响应）
        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);

        // 不入死亡/销毁链（Q2 语义）：不发 card.died / card.destroyed；不置「已毁」、不清槽位、不注销效果/修饰
        Assert.DoesNotContain(GameUpdates.CardDied, recorder.Types);
        Assert.DoesNotContain(Orc.Core.Updates.CardDestroyed, recorder.Types);
        Assert.Same(hq, match.Battlefield.PlayerBSupportLine.GetInsertionGaps().At(0)); // 占位保留（居中槽）
        Assert.Single(hq.Modifiers.All); // 修饰器保留（不因归零清理）
        Assert.IsType<AddModifier>(hq.Modifiers.All[0]);
        Assert.Single(hq.LethalInterventions); // 挂钩保留（挂载物冻结保持）

        // 终局后只读可用（血量/引用保持可读且稳定）
        Assert.Equal(0, hq.Health);
        Assert.Equal(0, playerB.HqHealth);
        Assert.Same(playerB, hq.Owner);
    }

    // ---------- 场景②：数值 + 监听 ----------

    [Fact]
    public async Task Scene2_Gain_Defense_Changes_Health_And_Listener_Sees_Direction()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var hq = playerB.Hq;
        var source = new object();

        // 订阅者（测试内构造）：记录「HQ 血量域变化」信号——自读当前有效值＋自记旧值辨方向
        var signals = new List<(int ValueAtEvent, int Direction)>();
        var lastSeen = hq.Health;
        using var subscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.CardStatChanged
                && ReferenceEquals(payload?[GameUpdates.PayloadCard], hq))
            {
                var now = hq.Health;
                signals.Add((now, Math.Sign(now - lastSeen)));
                lastSeen = now;
            }

            return Task.CompletedTask;
        });

        // 挂 +2（「获得 +2 防御力」＝血量 +2）
        await hq.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.HqHealth, 2, source));

        // 血量 20+2=22；Player.HqHealth 同值；订阅者收到信号且方向＝增加
        Assert.Equal(22, hq.Health);
        Assert.Equal(22, playerB.HqHealth);
        var gain = Assert.Single(signals);
        Assert.Equal(22, gain.ValueAtEvent);
        Assert.Equal(1, gain.Direction);

        // 撤销（对称回收）：20；方向＝减少
        await hq.Modifiers.RemoveBySourceAsync(source);
        Assert.Equal(Player.InitialHqHealth, hq.Health);
        Assert.Equal(Player.InitialHqHealth, playerB.HqHealth);
        Assert.Equal(2, signals.Count);
        Assert.Equal(20, signals[1].ValueAtEvent);
        Assert.Equal(-1, signals[1].Direction);

        // 监听注销无残留：Dispose 后数值再变化，订阅者不再收到任何信号
        subscription.Dispose();
        await hq.ApplyDamageAsync(1);
        Assert.Equal(19, hq.Health);
        Assert.Equal(2, signals.Count); // 无残留（注销后零新增）
    }

    // ---------- 场景③：管线改写 ----------

    [Fact]
    public async Task Scene3_Damage_Rewriter_MinusOne_Reduces_Incoming_Damage_And_Unmount_Restores()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var hq = match.Players[1].Hq;
        var source = new object();
        var rewriter = new HqDamageRewriter(source, amount => amount - 1); // 「受伤 -1」＝伤害性扣减量改写

        hq.AddDamageRewriter(rewriter);
        Assert.Contains(rewriter, hq.DamageRewriters);

        // 受 3 伤 → 改写后 2（扣 2）：20-2=18
        await hq.ApplyDamageAsync(3);
        Assert.Equal(18, hq.Health);
        Assert.Equal(MatchState.InProgress, match.State);

        // 卸后行为复原：卸载挂钩 → 同样 3 伤按原量扣减 → 18-3=15
        Assert.True(hq.RemoveDamageRewriter(rewriter));
        Assert.Empty(hq.DamageRewriters);
        await hq.ApplyDamageAsync(3);
        Assert.Equal(15, hq.Health);
    }

    [Fact]
    public async Task Scene3_Rewrite_Clamped_To_Zero_Emits_Nothing_And_Keeps_Health()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var hq = match.Players[1].Hq;
        hq.AddDamageRewriter(new HqDamageRewriter(new object(), amount => amount - 1));
        using var recorder = new UpdateRecorder(match.Engine);

        // 受 1 伤 → 改写至 0（钳制为 0，不产生治疗）：血量不变、无变化零发射、不触发终局判定
        await hq.ApplyDamageAsync(1);

        Assert.Equal(Player.InitialHqHealth, hq.Health);
        Assert.Empty(recorder.Updates); // 零发射（P1「改变才传播」）
        Assert.Equal(MatchState.InProgress, match.State); // 不判定
    }

    // ---------- 场景④：管线介入 ----------

    [Fact]
    public async Task Scene4_Lethal_Intervention_PlusSix_Saves_Hq_At_Five_Health()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var hq = match.Players[1].Hq;
        var intervention = new HqLethalIntervention(new object(), _ => 6); // 「致命前 +6 防」

        await hq.ApplyDamageAsync(15); // 预置 5 血
        Assert.Equal(5, hq.Health);

        hq.AddLethalIntervention(intervention);
        await hq.ApplyDamageAsync(7); // 将致命（5-7≤0）→ 介入 +6 → 5+6-7=4

        Assert.Equal(4, hq.Health);
        Assert.Equal(MatchState.InProgress, match.State); // 存活、不终局
    }

    [Fact]
    public async Task Scene4_Without_Intervention_Same_Course_Ends_Match()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var hq = match.Players[1].Hq;

        await hq.ApplyDamageAsync(15); // 5 血
        await hq.ApplyDamageAsync(7);  // 5-7 → 钳 0 → 终局

        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
    }

    [Fact]
    public async Task Scene4_NonLethal_Damage_Does_Not_Trigger_Intervention()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var hq = match.Players[1].Hq;
        hq.AddLethalIntervention(new HqLethalIntervention(new object(), _ => 6));

        await hq.ApplyDamageAsync(15); // 5 血
        await hq.ApplyDamageAsync(3);  // 非致命 → 不介入、正常结算

        Assert.Equal(2, hq.Health); // 5-3=2（而非 5+6-3=8——防「每次受伤都 +6」的错误实现）
        Assert.Equal(MatchState.InProgress, match.State);
    }

    [Fact]
    public async Task Scene4_Intervention_Does_Not_Recurse_Within_Single_Settlement()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var hq = match.Players[1].Hq;
        hq.AddLethalIntervention(new HqLethalIntervention(new object(), _ => 6));

        await hq.ApplyDamageAsync(15); // 5 血
        await hq.ApplyDamageAsync(20); // 介入一次（5+6-20=-9）——介入后仍将致命 → 不再介入 → 照常终局

        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State); // 单次结算一轮、不递归（递归将无限救援、破坏终局语义）
        Assert.Same(playerA, match.Winner);
    }

    [Fact]
    public async Task Scene4_Intervention_Unmount_Restores_Lethal_Outcome()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var hq = match.Players[1].Hq;
        var intervention = new HqLethalIntervention(new object(), _ => 6);
        hq.AddLethalIntervention(intervention);

        await hq.ApplyDamageAsync(15); // 5 血
        await hq.ApplyDamageAsync(7);  // 介入救援 → 4（活）
        Assert.Equal(4, hq.Health);

        // 卸后行为复原：卸载介入 → 再受致命伤害 → 照常终局
        Assert.True(hq.RemoveLethalIntervention(intervention));
        Assert.Empty(hq.LethalInterventions);
        await hq.ApplyDamageAsync(7); // 4-7 → 0 → 终局
        Assert.Equal(0, hq.Health);
        Assert.Equal(MatchState.Ended, match.State);
        Assert.Same(playerA, match.Winner);
    }
}
