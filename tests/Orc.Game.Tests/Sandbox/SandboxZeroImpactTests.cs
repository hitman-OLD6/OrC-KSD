using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests.Sandbox;

/// <summary>
/// R7 零影响（引擎级）：①授权但零调用 vs 未授权对局——事件流段数、摘要逐项一致；
/// ②授权实例的事件流段数在「不调用原语」时不因装配而增长。
/// </summary>
public sealed class SandboxZeroImpactTests
{
    [Fact]
    public async Task ZeroImpact_AuthorizedNoCalls_SameSegmentsAndSummary()
    {
        var authorized = await SandboxTestKit.CreateMatchAsync(authorized: true, seed: 20261021);
        var plain = await SandboxTestKit.CreateMatchAsync(authorized: false, seed: 20261021);

        var authorizedSegments = authorized.Engine.TakeSegments().Count;
        var plainSegments = plain.Engine.TakeSegments().Count;
        Assert.Equal(plainSegments, authorizedSegments);

        Assert.Equal(SandboxTestKit.Snapshot(plain), SandboxTestKit.Snapshot(authorized));

        // 事件流条目数（含初始化链）逐条一致。
        Assert.Equal(plain.Engine.RootStream.Entries.Count, authorized.Engine.RootStream.Entries.Count);

        // 摘要（引擎规模计数）一致。
        Assert.Equal(plain.Engine.Orchestration.Count, authorized.Engine.Orchestration.Count);
        Assert.Equal(plain.Engine.Cards.Count, authorized.Engine.Cards.Count);
        Assert.Equal(plain.Engine.Prefabs.PrefabIds.Count, authorized.Engine.Prefabs.PrefabIds.Count);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ZeroImpact_NoCalls_ProducesNoSandboxAuditTrace()
    {
        var authorized = await SandboxTestKit.CreateMatchAsync(authorized: true);

        // 授权但零调用 ⇒ 事件流里不存在任何 sandbox-primitive 留痕（装配不留痕）。
        Assert.DoesNotContain(
            authorized.Engine.RootStream.Entries,
            entry => entry.Keywords.Contains("sandbox-primitive"));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ZeroImpact_ReservedTriggersNotInOrchestration()
    {
        var match = await SandboxTestKit.CreateMatchAsync(authorized: true);

        // 保留触发器不进入编排面（不参与挂载、不被按名解析、不影响既有计数器）。
        foreach (var (name, trigger) in match.Sandbox.ReservedTriggers)
        {
            Assert.DoesNotContain(name, match.Engine.Hooks.Names);
            Assert.False(trigger.IsSealed == false, "保留触发器应封闭。");
        }

        await Task.CompletedTask;
    }
}
