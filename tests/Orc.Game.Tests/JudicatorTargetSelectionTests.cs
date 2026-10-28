using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// J3 示范②（判定器包装 Targeter）：候选名单由判定器 handler 产出（D9）；
/// 调用方经注册面按名解析 → 包装为选择规则 → 接入筛选链。
/// ① 默认态（名单＝在场单位）；② 禁止步兵（After 包装剔除）；③ 允许 HQ（After 包装加入）。
/// </summary>
public class JudicatorTargetSelectionTests
{
    // ---------- ① 完整流程·默认态 ----------

    [Fact]
    public async Task Full_Selection_Flow_Default_Rule_Filters_Through_Judicator()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateTargetingMatch(bridge);
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var infantryA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var tankA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        var infantryB = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        bridge.CollectScript = CommandTestKit.AllRefsScript(match); // 候选超集（槽位＋单位＋双方 HQ）

        var rule = new JudicatorSelectionRule(
            match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility),
            playerA.Hq,
            new EffectSelector("all"));
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());

        var task = targeter.Targeting();
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 默认规则＝在场单位可选：允许集＝全部单位；HQ/槽位候选被剔除
        Assert.Equal(new[] { infantryA.Ref, tankA.Ref, infantryB.Ref }, description.AllowedTargets.ToArray());

        Assert.True(responder.Complete(
            description.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), infantryA.Ref)));
        var result = await task;
        Assert.Equal(TargeterStatus.Ok, result.Status);
        Assert.Same(infantryA.Ref, result.Outcome!.Single);
    }

    // ---------- ② 改写（禁止步兵类可选）＋提交被拒＋注销回退 ----------

    [Fact]
    public async Task Forbid_Infantry_Wrapper_Removes_Class_And_Falls_Back()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateTargetingMatch(bridge);
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var infantryA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var tankA = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        var infantryB = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var registration = match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility);
        var rule = new JudicatorSelectionRule(registration, playerA.Hq, new EffectSelector("all"));
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());

        // 态 1（默认）：允许集含步兵类
        var task1 = targeter.Targeting();
        var (d1, r1) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(new[] { infantryA.Ref, tankA.Ref, infantryB.Ref }, d1.AllowedTargets.ToArray());
        Assert.True(r1.Cancel(d1.RequestId));
        Assert.Equal(TargeterStatus.Cancelled, (await task1).Status);

        // 态 2（改写：禁止步兵类可选——After 包装剔除名单项）
        var moding = match.Judicators.RegisterAfterWrapper(
            registration,
            (_, result) => result is { Length: 1 } && result[0] is IReadOnlyList<Ref<Entity>> list
                ? new object[]
                {
                    list.Where(reference => reference.Value is not UnitCard unit
                        || !unit.GetData<UnitStateData>().UnitTypes.Contains(UnitType.Infantry)).ToArray(),
                }
                : result,
            playerIndex: playerA.Index);
        Assert.NotNull(moding);

        var task2 = targeter.Targeting();
        var (d2, r2) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(new[] { tankA.Ref }, d2.AllowedTargets.ToArray()); // 步兵类从允许集消失

        // 提交该类＝被拒（不在名单）＝选择器终局 Failed → 内部重入（同一选择器）
        Assert.True(r2.Complete(
            d2.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(d2), infantryA.Ref)));
        Assert.False(task2.IsCompleted);

        var (reentry, reentryResponder) = await bridge.WaitForNextBeginAsync();
        Assert.True(reentryResponder.Complete(
            reentry.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(reentry), tankA.Ref)));
        var result2 = await task2;
        Assert.Equal(TargeterStatus.Ok, result2.Status);
        Assert.Same(tankA.Ref, result2.Outcome!.Single);

        // 态 3（注销回退）：经同一路径恢复默认允许集
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        var task3 = targeter.Targeting();
        var (d3, r3) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(new[] { infantryA.Ref, tankA.Ref, infantryB.Ref }, d3.AllowedTargets.ToArray());
        Assert.True(r3.Cancel(d3.RequestId));
        Assert.Equal(TargeterStatus.Cancelled, (await task3).Status);
    }

    // ---------- ③ 改写（允许通常不可选〔HQ〕）＋注销回退 ----------

    [Fact]
    public async Task Allow_Wrapper_Enables_Usually_Ineligible_Class_And_Falls_Back()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateTargetingMatch(bridge);
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var registration = match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility);
        var rule = new JudicatorSelectionRule(registration, playerA.Hq, new EffectSelector("all"));
        var targeter = match.TargeterManager.CreateTargeter(filter: rule.AsFilter());

        // 态 1（默认）：HQ 通常不可选（允许集不含 HQ 引用）
        var task1 = targeter.Targeting();
        var (d1, r1) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(playerA.Hq.Ref, d1.AllowedTargets);
        Assert.DoesNotContain(playerB.Hq.Ref, d1.AllowedTargets);
        Assert.True(r1.Cancel(d1.RequestId));
        await task1;

        // 态 2（改写：允许 HQ 类可选——After 包装并入名单）
        var moding = match.Judicators.RegisterAfterWrapper(
            registration,
            (_, result) => result is { Length: 1 } && result[0] is IReadOnlyList<Ref<Entity>> list
                ? new object[] { list.Concat(new[] { playerA.Hq.Ref, playerB.Hq.Ref }).ToArray() }
                : result,
            playerIndex: playerA.Index);
        Assert.NotNull(moding);

        var task2 = targeter.Targeting();
        var (d2, r2) = await bridge.WaitForNextBeginAsync();
        Assert.Contains(playerA.Hq.Ref, d2.AllowedTargets);
        Assert.Contains(playerB.Hq.Ref, d2.AllowedTargets);

        Assert.True(r2.Complete(
            d2.RequestId,
            TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(d2), playerA.Hq.Ref)));
        var result2 = await task2;
        Assert.Equal(TargeterStatus.Ok, result2.Status);
        Assert.Same(playerA.Hq.Ref, result2.Outcome!.Single);

        // 态 3（注销回退）：HQ 恢复不可选
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        var task3 = targeter.Targeting();
        var (d3, r3) = await bridge.WaitForNextBeginAsync();
        Assert.DoesNotContain(playerA.Hq.Ref, d3.AllowedTargets);
        Assert.DoesNotContain(playerB.Hq.Ref, d3.AllowedTargets);
        Assert.True(r3.Cancel(d3.RequestId));
        await task3;
    }

    // ---------- ④ 可选性声明：包装对手域、剔除被保护目标、撤销回退 ----------

    [Fact]
    public async Task TargetingDeclaration_Removes_Protected_Target_From_Opponent_Domain()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateTargetingMatch(bridge);
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var protectedUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var otherUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        var enemyUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        var declaration = new TargetingDeclaration(
            match.Judicators,
            protectedUnit,
            candidate => ReferenceEquals(candidate, protectedUnit),
            () => true,
            "any");

        declaration.Attach(playerB.Index); // 注入对手域（B 的指令视角）
        try
        {
            var rule = new JudicatorSelectionRule(
                match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility),
                enemyUnit,
                new EffectSelector("all"));

            var candidates = rule.Candidates();
            Assert.DoesNotContain(protectedUnit.Ref, candidates);
            Assert.Contains(otherUnit.Ref, candidates);
            Assert.Contains(enemyUnit.Ref, candidates);
        }
        finally
        {
            declaration.Detach();
        }

        var restored = new JudicatorSelectionRule(
            match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility),
            enemyUnit,
            new EffectSelector("all")).Candidates();
        Assert.Contains(protectedUnit.Ref, restored);
    }

    // ---------- ⑤ 交互式选靶：生产入口受可选性声明约束 ----------

    [Fact]
    public async Task BeginTargetSelection_Excludes_Protected_Target_Through_Opponent_Domain()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateTargetingMatch(bridge);
        await match.Initialize();

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var protectedUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var otherUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.TankId, 2);
        var enemyUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);

        var declaration = new TargetingDeclaration(
            match.Judicators,
            protectedUnit,
            candidate => ReferenceEquals(candidate, protectedUnit),
            () => true,
            "any");
        declaration.Attach(playerB.Index);

        try
        {
            var task = match.EffectRuntime.SelectAsync(enemyUnit, new EffectSelector("one"));
            var (description, responder) = await bridge.WaitForNextBeginAsync();

            Assert.DoesNotContain(protectedUnit.Ref, description.AllowedTargets);
            Assert.Contains(otherUnit.Ref, description.AllowedTargets);

            Assert.True(responder.Complete(
                description.RequestId,
                TargeterTestKit.Selection(TargeterTestKit.PrimarySlot(description), otherUnit.Ref)));
            var result = await task;
            Assert.Single(result);
            Assert.Same(otherUnit, result[0]);
        }
        finally
        {
            declaration.Detach();
        }
    }

    // ---------- ⑥ DSL 动态声明：targeting.forbid 动作 ----------

    [Fact]
    public async Task Dsl_TargetingForbid_Removes_Protected_Unit_From_Opponent_Domain()
    {
        var keywordId = "program." + Guid.NewGuid().ToString("N");
        ProgramKeywordComponent.Register(keywordId, new KeywordProgram
        {
            Handlers =
            [
                new KeywordEventHandler
                {
                    Event = "unit.joined",
                    Subject = "self",
                    Actions = [new KeywordProgramAction { Op = "targeting.forbid", Target = "self", Key = "any" }],
                },
            ],
        });

        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge, extraDefinitions:
        [
            new CardDefinitionEntry(keywordId, new CardDefinition(
                "禁指单位", 1, 0, 2, 5,
                unitTypes: [UnitType.Infantry],
                keywords: [new KeywordDeclaration(keywordId)],
                faction: Faction.Germany,
                rarity: Rarity.Standard)),
        ]);
        await match.Initialize();

        var protectedUnit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], keywordId, 1);
        var plainUnit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.TankId, 2);
        var enemyUnit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[1], CommandTestKit.InfantryId, 1);

        var rule = new JudicatorSelectionRule(
            match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility),
            enemyUnit,
            new EffectSelector("all"));

        var candidates = rule.Candidates();
        Assert.DoesNotContain(protectedUnit.Ref, candidates);
        Assert.Contains(plainUnit.Ref, candidates);
    }

    // ---------- 测试辅助 ----------

    /// <summary>创建示范②对局（标准指挥测试定义集；目标候选判定器经生产内置注册段）。</summary>
    private static Match CreateTargetingMatch(MockTargeterBridge bridge)
        => new(
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions(),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            targeterBridge: bridge,
            packageActivator: TestHost.Activator);
}
