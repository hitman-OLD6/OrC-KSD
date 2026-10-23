using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// G9（延迟调度与回合相位效果）机制级验收（订阅自管模式）：效果订阅 turn.* 相位事件、
/// 逻辑组件内部维护待办（测试内构造有状态 handler）、到点执行并自清理；
/// 锚点约定＝「回合开始时」→ turn.start.after、「回合结束时」→ turn.end。
/// 四场景：①延迟资源（下个己方回合开始 → 点数 +10）②延迟动作（下个己方回合开始 → 位置恢复〔模拟〕）
/// ③结束响应（回合结束时 → 弃牌类动作〔模拟〕；含 turn.end 时刻保留点数读数）④资源语义修正验证（X3：
/// 结束不清零＋敌方回合保留＋己方开始重设为槽值）。
/// 附：受控加值面（AddPoints）硬性要件、显式注销（取消后不再触发）、敌回合注册 → 即将到来的己方回合边界。
/// 说明：动作作用于真实对局对象、产生真实可观察副作用；不动用效果装载链（X2 范畴）。
/// </summary>
public class TurnPhaseEffectTests
{
    // ---------- 场景①：延迟资源——「下个己方回合开始」到点执行「点数 +10」 ----------

    [Fact]
    public async Task Delayed_Resource_Effect_Executes_On_Next_Own_Turn_Start_And_Self_Cleans()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize(); // 回合 1（先手 A）：槽 1、点数 1
        var playerA = match.Players[0];

        // 注册「下个己方回合开始」监听（订阅自管：turn.start.after ＋ 载荷玩家过滤为己方 ＋ 一次性）。
        var executions = 0;
        var pointsSeenAtExecution = -1;
        IDisposable? subscription = null;
        subscription = match.Engine.Subscribe(async (type, payload, _) =>
        {
            if (type != GameUpdates.TurnStartAfter
                || payload is null
                || !ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                return;
            }

            executions += 1;
            pointsSeenAtExecution = playerA.Points; // 锚点证据：执行时点晚于结算（读到的是本回合结算值）
            await match.ResourceManager.AddPointsAsync(playerA, 10); // 到点执行：点数 +10
            subscription!.Dispose(); // 执行后自清理
        });

        // 负控（硬性）：敌方回合开始不触发——B 回合结算后 A 点数原样保留（X3）。
        await match.EndTurn(); // → 回合 2（B）
        Assert.Equal(0, executions);
        Assert.Equal(1, playerA.Points); // A 的 1 点保留（X3）；未被 +10

        // 到点：下一个己方（A）回合开始 → 恰好执行一次；执行时点＝结算已完成后（turn.start.after 锚点）。
        await match.EndTurn(); // → 回合 3（A）：结算后槽 2＝2 点 → +10
        Assert.Equal(1, executions);
        Assert.Equal(2, pointsSeenAtExecution); // 锚点证据：执行时已读到结算值 2（晚于结算、非提前）
        Assert.Equal(12, playerA.Points); // 结算值 + 10 ＝ 12
        Assert.True(playerA.Points > playerA.PointSlots); // 加值未钳制到槽（槽 2）

        // 自清理：后续己方回合不再触发（重设覆盖残留后仅剩结算值）。
        await match.EndTurn(); // → 回合 4（B）
        await match.EndTurn(); // → 回合 5（A）：结算后 3 点（覆盖 12）
        Assert.Equal(1, executions); // 不再触发
        Assert.Equal(3, playerA.Points); // 仅结算值——若未清理将再叠加 +10
    }

    // ---------- 场景②：延迟动作——「下个己方回合开始」到点执行「位置恢复」（模拟） ----------

    [Fact]
    public async Task Delayed_Action_Executes_On_Next_Own_Turn_Start_And_Self_Cleans()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var originalSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1);
        var displacedSlot = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(3);

        // 模拟「被效果临时移位」：单位从原槽移到另一空槽（真实对象、真实槽位变化）。
        originalSlot.Line?.TryRemove(unit);
        displacedSlot.Line.TryInsertAt(displacedSlot.Ordinal, unit);
        unit.GetData<UnitStateData>().Line = displacedSlot.Line;
        Assert.Same(unit, displacedSlot);

        // 注册「下个己方回合开始」监听：到点执行「位置恢复」动作（模拟）＋执行后自清理。
        var executions = 0;
        IDisposable? subscription = null;
        subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type != GameUpdates.TurnStartAfter
                || payload is null
                || !ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                return Task.CompletedTask;
            }

            executions += 1;
            displacedSlot.Line?.TryRemove(unit);
            originalSlot.Line.TryInsertAt(originalSlot.Ordinal, unit);
            unit.GetData<UnitStateData>().Line = originalSlot.Line;
            subscription!.Dispose();
            return Task.CompletedTask;
        });

        // 负控（硬性）：敌方回合不触发——单位仍在移位处。
        await match.EndTurn(); // → 回合 2（B）
        Assert.Equal(0, executions);
        Assert.Same(unit, displacedSlot);
        Assert.NotEqual(originalSlot.Ordinal, originalSlot.Line!.IndexOf(unit));

        // 到点：己方回合开始 → 恢复执行一次（真实位置副作用）。
        await match.EndTurn(); // → 回合 3（A）
        Assert.Equal(1, executions);
        Assert.Same(unit, originalSlot);
        Assert.Equal(originalSlot.Ordinal, originalSlot.Line!.IndexOf(unit));
        Assert.Same(originalSlot, unit.GetData<UnitStateData>().Line);

        // 自清理：再移位后，后续己方回合不再触发（单位保持移位处——未被第二次恢复）。
        await match.EndTurn(); // → 回合 4（B）
        originalSlot.Line?.TryRemove(unit);
        displacedSlot.Line.TryInsertAt(displacedSlot.Ordinal, unit);
        unit.GetData<UnitStateData>().Line = displacedSlot.Line;
        await match.EndTurn(); // → 回合 5（A）
        Assert.Equal(1, executions);
        Assert.Same(unit, displacedSlot); // 未被恢复——handler 已自清理
        Assert.NotEqual(originalSlot.Ordinal, originalSlot.Line!.IndexOf(unit));
    }

    // ---------- 场景③：结束响应——「回合结束时」到点执行「弃牌」（模拟消灭/弃牌类） ----------

    [Fact]
    public async Task End_Of_Turn_Effect_Executes_Action_With_Anchor_And_Self_Cleans()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize(); // 回合 1（先手 A）：1 点、手牌 4
        var playerA = match.Players[0];

        // 注册「回合结束时」监听（turn.end ＋ 结束方过滤）：到点执行「弃牌」动作（模拟消灭/弃牌类）＋自清理。
        var executions = 0;
        var pointsSeenAtTurnEnd = -1;
        Orc.Cards.Card? discardedCard = null;
        IDisposable? subscription = null;
        subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type != GameUpdates.TurnEnd
                || payload is null
                || !ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                return Task.CompletedTask;
            }

            executions += 1;
            pointsSeenAtTurnEnd = playerA.Points; // 锚点证据：turn.end 时刻读到保留点数（X3：未清零）
            discardedCard = playerA.Hand[0];
            playerA.Hand.Remove(discardedCard!); // 到点执行：弃牌（真实手牌副作用）
            subscription!.Dispose();
            return Task.CompletedTask;
        });

        await match.EndTurn(); // ① 结束 A 回合 1 → 回合 2（B）开始：turn.end{A,1} 触发弃牌

        Assert.Equal(1, executions);
        Assert.Equal(1, pointsSeenAtTurnEnd); // turn.end 时刻读到保留点数 1（X3：未清零）
        Assert.Equal(3, playerA.Hand.Count); // 4 - 1：弃牌真实副作用
        Assert.DoesNotContain(discardedCard!, playerA.Hand);

        // 自清理：后续己方回合结束（回合 3）不再触发（executions 不增、手牌不再减）。
        await match.EndTurn(); // ② 结束 B 回合 2 → 回合 3（A）开始：A 照抽 1（3 → 4）
        Assert.Equal(4, playerA.Hand.Count);
        await match.EndTurn(); // ③ 结束 A 回合 3 → 回合 4（B）开始：不再触发（若未清理将再弃 1、executions 变 2）
        Assert.Equal(1, executions); // 自清理：后续己方回合结束不再触发
        Assert.Equal(4, playerA.Hand.Count); // 未再弃牌（若未清理将 3）
    }

    // ---------- 场景④：资源语义修正验证（X3）——结束不清零＋敌方回合保留＋己方开始重设 ----------

    [Fact]
    public async Task Resource_Semantics_Retain_At_Turn_End_And_Reset_To_Slots_On_Own_Turn_Start()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 制造「残留点数」（模拟"获得 N 点"类效果）：回合 1 的 1 点 → +5 → 6 点（超槽 1）。
        await match.ResourceManager.AddPointsAsync(playerA, 5);
        Assert.Equal(6, playerA.Points);

        // 「结束时读残留点数」探测（turn.end 时刻读数——X3：未清零）。
        var pointsSeenAtTurnEnd = -1;
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.TurnEnd
                && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                pointsSeenAtTurnEnd = playerA.Points;
            }

            return Task.CompletedTask;
        });

        // ①结束不清零：A 回合 1 结束 → turn.end 时刻读到残留 6；EndTurn 返回后 A 仍 6（X3 关键修正）。
        await match.EndTurn(); // → 回合 2（B）
        Assert.Equal(6, pointsSeenAtTurnEnd); // turn.end 时刻读残留点数（不清零）
        Assert.Equal(6, playerA.Points); // 回合结束不清零——点数保留

        // ②敌方回合保留：B 回合（2）内 A 仍 6；B 正常结算（槽 1）。
        Assert.Same(playerB, match.CurrentPlayer);
        Assert.Equal(6, playerA.Points);
        Assert.Equal(1, playerB.Points);

        // ③己方开始重设为槽值：A 回合 3 结算＝槽 +1（2）→ 点数＝槽值（设为 2——覆盖残留 6，非累积）。
        await match.EndTurn(); // → 回合 3（A）
        Assert.Equal(2, playerA.PointSlots);
        Assert.Equal(2, playerA.Points); // 重设覆盖（累积语义将 > 2）
    }

    // ---------- 附：受控加值面硬性要件 ----------

    [Fact]
    public async Task AddPoints_Rejects_Null_Player_And_Non_Positive_Amount()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var playerA = match.Players[0]; // 回合 1：1 点

        // 硬性要件①：player null 拒绝（fail-fast）。
        await Assert.ThrowsAsync<ArgumentNullException>(() => match.ResourceManager.AddPointsAsync(null!, 1));

        // 硬性要件②：amount ≤ 0 拒绝（"加值"严格为正）。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => match.ResourceManager.AddPointsAsync(playerA, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => match.ResourceManager.AddPointsAsync(playerA, -5));

        // 拒绝＝零副作用：点数原样。
        Assert.Equal(1, playerA.Points);

        // 正控：合法加值落地（受控写入、不钳制到槽）。
        await match.ResourceManager.AddPointsAsync(playerA, 2);
        Assert.Equal(3, playerA.Points);
        Assert.True(playerA.Points > playerA.PointSlots);

        // 防御性溢出防护（实现裁量）：加值后超出 int 上限＝拒绝、不产生溢出写。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => match.ResourceManager.AddPointsAsync(playerA, int.MaxValue));
        Assert.Equal(3, playerA.Points);
    }

    // ---------- 附：显式注销（订阅取消后不再触发；正控＝重新注册可触发） ----------

    [Fact]
    public async Task Explicitly_Cancelled_Subscription_Does_Not_Fire()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var playerA = match.Players[0];

        // 注册 → 外部提前取消（自撤销面）：注销后不再触发。
        var fired = 0;
        var subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.TurnEnd
                && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                fired += 1;
            }

            return Task.CompletedTask;
        });
        subscription.Dispose();

        await match.EndTurn(); // 回合 1（A）结束——若未注销将触发
        Assert.Equal(0, fired);

        // 正控：重新注册后同相位可触发（机制面本身有效）。
        using var reRegistered = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.TurnEnd
                && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                fired += 1;
            }

            return Task.CompletedTask;
        });

        await match.EndTurn(); // → 回合 2（B）结束：结束方非 A → 不触发
        Assert.Equal(0, fired);
        await match.EndTurn(); // → 回合 3（A）结束：触发
        Assert.Equal(1, fired);
    }

    // ---------- 附：边界——注册于敌方回合 → 目标为即将到来的己方回合开始 ----------

    [Fact]
    public async Task Delayed_Effect_Registered_During_Enemy_Turn_Targets_Upcoming_Own_Turn()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var playerA = match.Players[0];

        // 注册于敌方回合（B 回合 2）→ 目标为即将到来的己方回合开始（回合 3）——"自注册时刻起算的下一个"。
        await match.EndTurn(); // → 回合 2（B）
        Assert.Same(match.Players[1], match.CurrentPlayer);

        var executions = 0;
        IDisposable? subscription = null;
        subscription = match.Engine.Subscribe(async (type, payload, _) =>
        {
            if (type != GameUpdates.TurnStartAfter
                || payload is null
                || !ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                return;
            }

            executions += 1;
            await match.ResourceManager.AddPointsAsync(playerA, 10); // 到点执行：点数 +10
            subscription!.Dispose();
        });

        await match.EndTurn(); // → 回合 3（A）：即将到来的己方回合开始 → 触发
        Assert.Equal(1, executions);
        Assert.Equal(12, playerA.Points); // 结算值 2 + 10
    }
}
