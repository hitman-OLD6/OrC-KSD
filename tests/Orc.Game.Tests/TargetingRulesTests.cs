using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>「指向」通用接入点验收：指挥攻击与效果选定各触发一次；候选收集不触发。</summary>
public sealed class TargetingRulesTests
{
    [Fact]
    public async Task 指挥攻击_对目标触发一次指向()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], CommandTestKit.InfantryId, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var hits = new List<string>();
        var enemyHq = match.Players[1].Hq;
        enemyHq.TargetingTrigger.Register("观测", (view, _, _) =>
        {
            lock (hits)
            {
                hits.Add(view.Kind as string ?? "");
            }

            return Task.CompletedTask;
        });

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, enemyHq.Ref);

        Assert.True(result.IsSuccess);
        Assert.Equal(TargetingRules.KindCommand, Assert.Single(hits));
    }

    [Fact]
    public async Task 候选收集_不触发指向()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], CommandTestKit.InfantryId, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        CommandTestKit.Activate(unit);
        await CommandTestKit.PrepareUnitAsync(
            match, match.Players[1], CommandTestKit.InfantryId, match.Battlefield.PlayerBSupportLine.GetInsertionGaps()[1]);

        var hits = 0;
        var enemy = CommandTestKit.AllUnitsOf(match)
            .Single(u => ReferenceEquals(u.Owner, match.Players[1]));
        enemy.TargetingTrigger.Register("观测", (_, _, _) =>
        {
            Interlocked.Increment(ref hits);
            return Task.CompletedTask;
        });

        // 候选面（可用性/候选收集）只是“列出可选项”，不构成指向。
        var availability = match.CommandManager.GetCommandAvailability(unit);
        Assert.NotEmpty(availability.Attack.Candidates);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task 效果选定_对每个最终目标各触发一次指向()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var viewer = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], CommandTestKit.InfantryId, match.Battlefield.FrontLine.GetInsertionGaps()[0]);
        await CommandTestKit.PrepareUnitAsync(
            match, match.Players[1], CommandTestKit.InfantryId, match.Battlefield.PlayerBSupportLine.GetInsertionGaps()[1]);
        await CommandTestKit.PrepareUnitAsync(
            match, match.Players[1], CommandTestKit.InfantryId, match.Battlefield.PlayerBSupportLine.GetInsertionGaps()[0]);

        var hits = 0;
        foreach (var enemy in CommandTestKit.AllUnitsOf(match).Where(u => ReferenceEquals(u.Owner, match.Players[1])))
        {
            enemy.TargetingTrigger.Register("观测", (_, _, _) =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            });
        }

        var selected = await match.EffectRuntime.SelectAsync(
            viewer, new EffectSelector("all", Side: "enemy"));

        Assert.Equal(2, selected.Count);
        Assert.Equal(2, hits);
    }
}
