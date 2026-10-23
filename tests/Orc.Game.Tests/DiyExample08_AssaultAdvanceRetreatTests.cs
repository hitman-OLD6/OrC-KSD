using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 8（R6h + D4）证据：指令「突袭」——加入支援阵线 → 随机与敌方单位战斗 → 存活则推进（前线无敌人）
/// 并对敌方 HQ 3 伤害；否则撤退；阵亡则终止（无移动/无伤害）。
/// 代码侧：<c>PlayManager.JoinUnitAsync</c>（加入路径）＋ <c>EffectRuntime.BattleAsync</c>（R10 快照＋同时结算）＋
/// <c>MoveAsync("frontline")</c> / <c>CommandManager.MoveUnitAsync(unit, slot)</c>（撤退；S6 起效果移动免费）。
/// 缺口 G-02 已由 R10 关闭：战斗不再需要两向 <c>DamageAsync</c> 逼近。
/// </summary>
public class DiyExample08_AssaultAdvanceRetreatTests
{
    [Fact]
    public async Task 突袭_BattleAsync_存活且前线无敌人_推进并打总部3()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        // 敌方目标在支援线（前线为空 → 满足推进条件）；轰炸机防御 2，被我方 2 攻击毁。
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.BomberId, 1);
        Assert.Empty(match.Battlefield.FrontLine.Members);

        // 推进走 MoveAsync（S6 效果移动入口：不扣指挥点、不动行动位、不受压制阻断；落地经底层移动动作）。
        CommandTestKit.Activate(unit);

        var hqBefore = playerB.Hq.Health;
        var outcome = await AssaultFlow.RunAsync(match, unit);

        Assert.Equal(AssaultOutcome.Advanced, outcome);
        Assert.Contains(match.Battlefield.FrontLine.Members, slot => ReferenceEquals(slot, unit));
        Assert.Equal(hqBefore - 3, playerB.Hq.Health);
    }

    [Fact]
    public async Task 突袭_BattleAsync_存活但前线有敌人_撤退到己方支援线()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        // 目标：敌方步兵（防御 5，吃 2 伤存活并反击 2）；前线另有一个敌方单位 → 推进条件不满足。
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);

        var hqBefore = playerB.Hq.Health;
        var outcome = await AssaultFlow.RunAsync(match, unit);

        Assert.Equal(AssaultOutcome.Retreated, outcome);
        var position = unit.GetData<UnitStateData>().Line;
        Assert.NotNull(position);
        Assert.Same(match.Battlefield.GetSupportLine(playerA), position); // 撤退到我方支援线
        Assert.Contains(match.Battlefield.GetSupportLine(playerA).Members, slot => ReferenceEquals(slot, unit));
        Assert.Equal(hqBefore, playerB.Hq.Health); // 撤退分支不造成总部伤害

        // 反击已发生：我方防御 5 → 3（战斗不是单向伤害）。
        Assert.Equal(3, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    [Fact]
    public async Task 突袭_BattleAsync_阵亡_不推进不撤退不打总部()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1); // 反击 2 致死的目标
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);

        // 先把我方防御压到 1：战斗反击 2 → 阵亡。
        await unit.SetRemainingDefenseAsync(1);

        var hqBefore = playerB.Hq.Health;
        var outcome = await AssaultFlow.RunAsync(match, unit);

        Assert.Equal(AssaultOutcome.Died, outcome);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
        Assert.Null(unit.GetData<UnitStateData>().Line);
        Assert.Equal(hqBefore, playerB.Hq.Health);
    }

    // ==================== 示例流程（可复制到卡效果） ====================

    internal enum AssaultOutcome
    {
        NoTarget,
        Died,
        Advanced,
        Retreated,
    }

    private static class AssaultFlow
    {
        internal static async Task<AssaultOutcome> RunAsync(Match match, UnitCard unit, CancellationToken ct = default)
        {
            if (EffectRuntime.ResolveFor(unit) is not { } runtime)
            {
                return AssaultOutcome.NoTarget;
            }

            // ① 随机一个敌方单位（无候选＝不执行；卡面未定义该分支）。
            var picked = await runtime.SelectAsync(unit, new EffectSelector("random", "enemy", null, null, null, 1));
            if (picked.Count == 0)
            {
                return AssaultOutcome.NoTarget;
            }

            var target = picked[0];

            // ② 强制战斗：R10 的 BattleAsync——快照双方攻击力后同时结算（先死者不影响反向输出）。
            //    无射程/守护/伏击改写/反击资格判定；不扣行动费、不清行动位、不发攻击类信号。
            await runtime.BattleAsync(unit, target, source: unit, ct);

            // ③ 阵亡分支：终止（不移动、不伤害）。
            if (unit.GetData<UnitStateData>().IsDestroyed)
            {
                return AssaultOutcome.Died;
            }

            var owner = unit.Owner!;
            var enemy = match.Players.First(player => !ReferenceEquals(player, owner));

            // ④ 推进条件＝前线无存活敌方单位。
            var enemyOnFront = match.Battlefield.FrontLine.Members.Any(slot =>
                slot is CardBase occupant
                && ReferenceEquals(occupant.Owner, enemy)
                && !occupant.GetData<UnitStateData>().IsDestroyed);

            if (!enemyOnFront)
            {
                await runtime.MoveAsync(unit, "frontline", ct, unit);
                await runtime.DamageAsync(enemy.Hq, 3, unit, ct);
                return AssaultOutcome.Advanced;
            }

            // ⑤ 否则撤退：移回己方支援线空槽（不是回手牌）。S6：指定槽效果入口（零资源消耗）。
            var destination = match.Battlefield.GetSupportLine(owner).GetInsertionGaps().At(0);
            await match.CommandManager.MoveUnitAsync(unit, destination, ct);
            return AssaultOutcome.Retreated;
        }
    }
}
