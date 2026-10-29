using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 死亡处理端的**成员拆分**（R1i band 修订 2026-10-29，用户裁决 A）：
/// 旧的单成员 <c>death.handle</c> 横跨 <c>Veto</c>（免死守卫）与 <c>Apply</c>（死亡链）两段，
/// band 模型下"一个成员落一段"，故拆为 <c>death.veto</c> ＋ <c>death.handle</c>。
/// <para>
/// 本类钉住拆分后**唯一需要新机制的地方**：<c>ctx.Stop()</c> 的作用域仍是"当前成员"（S2a），
/// 所以"免死守卫命中 ⇒ 死亡链不执行"不能靠 Stop 自己跨成员——由触发端转发读
/// <see cref="MemberExecutionResult.Stopped"/> 完成。用例直接构造"只改写 ShouldDie、不 Stop"的改写边，
/// 于是唯一能拦住死亡链的就是**守卫的 Stop ＋ 跨成员判定**（若拆分丢了这条语义，单位会被杀掉，用例必红）。
/// </para>
/// </summary>
public sealed class DeathVetoSplitTests
{
    [Fact]
    public async Task 免死守卫命中_死亡链不执行_单位未被消灭()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 0);

        // 改写位（Rewrite 段，早于 Veto 段）：只把 ShouldDie 置 false，**不调用 Stop**。
        // 因此拦住死亡链的只能是 death.veto 的守卫（Veto 段）＋ 触发端的跨成员判定。
        using var rewrite = match.Engine.AttachMemberEdge(
            EngineProcesses.DeathVeto,
            "测试免死改写",
            match,
            MemberEdges.For<DeathHandleView>((view, _, _) =>
            {
                view.ShouldDie = false;
                return Task.CompletedTask;
            }),
            band: BandSchemes.Rewrite);

        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        Assert.Equal(ActionOutcomeKind.Blocked, result.Targets[0].Outcome);
        Assert.Equal(ActionFailureKind.DeathPrevented, result.Targets[0].Reason);
        Assert.Equal(0, result.Targets[0].AppliedAmount);
        Assert.False(unit.GetData<UnitStateData>().IsDestroyed, "免死命中后死亡链不得执行");
    }

    [Fact]
    public async Task 无改写时_守卫放行_死亡链照常落地()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, player, CommandTestKit.InfantryId, 0);

        var result = await match.CommandManager.DeathAction.ExecuteAsync(unit);

        Assert.Equal(ActionOutcomeKind.Applied, result.Targets[0].Outcome);
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task 两个成员各有一条自有边_分属Veto与Apply段()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var engine = match.EngineRuntime.Engine;

        var veto = engine.FindMember(EngineProcesses.DeathVeto);
        var handle = engine.FindMember(EngineProcesses.DeathHandle);
        Assert.NotNull(veto);
        Assert.NotNull(handle);
        Assert.Equal(BandSchemes.Veto, veto!.Band);
        Assert.Equal(BandSchemes.Apply, handle!.Band);

        // 拆分前"守卫 → 死亡链"是同一成员的两条自有边；拆分后每个成员各一条（动作实现在构造期接入）。
        Assert.Single(engine.AttachedOwnedEdges(EngineProcesses.DeathVeto));
        Assert.Single(engine.AttachedOwnedEdges(EngineProcesses.DeathHandle));
    }
}
