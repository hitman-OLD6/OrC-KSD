using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 补全性佐证（X4「全部事件 handler 注册项可寻址」硬性要求）：CommandManager 两个内置检查触发器的句柄获取路径 ＋ moding 替换生效。
/// ①防御归零检查（DefenseDepletionTrigger——被动；挂载更新总线、响应 card.stat.changed）——替换后：防御归零单位不再进入统一死亡衔接；
/// ②HQ 归零检查（HqZeroTrigger——同构）——替换后：HQ 归零不再触发终局响应。
/// 约定：moding 逻辑与 handler 本体一致＝单一处理单元（记录）；禁止流程编排——本文件全部用例均以单一处理单元形态示范。
/// </summary>
public class CommandTriggerAddressabilityTests
{
    [Fact]
    public async Task Defense_Depletion_Trigger_Handle_Is_Addressable_And_Moding_Replaces_Death_Handoff()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 0);

        // 句柄获取路径：公开触发器属性 → 构造期装配项句柄（「防御归零检查」）
        var trigger = match.CommandManager.DefenseDepletionTrigger;
        var handle = trigger.InitialRegistrations[0];
        Assert.Equal("防御归零检查", handle.Name);

        var trace = new List<string>();
        var m = trigger.RegisterReplaceWrapper(handle, (view, ctx, ct) =>
        {
            trace.Add("moding-防御归零");
            return Task.CompletedTask;
        });
        Assert.NotNull(m);

        // 门户伤害致死（防御 2 → 0）：card.stat.changed → 检查触发器（总线驱动；替换生效）
        await target.ApplyDefenseDamageAsync(999);

        Assert.Equal(new[] { "moding-防御归零" }, trace); // 替换生效
        var state = target.GetData<UnitStateData>();
        Assert.False(state.IsDestroyed);                        // 默认死亡衔接未执行（原逻辑被替换）
        Assert.Contains(target, match.Battlefield.FrontLine.Members);   // 位置未释放（单位仍在线上）
    }

    [Fact]
    public async Task Hq_Zero_Trigger_Handle_Is_Addressable_And_Moding_Replaces_Terminal_Response()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var hq = playerB.Hq;

        // 句柄获取路径：公开触发器属性 → 构造期装配项句柄（「HQ 归零检查」）
        var trigger = match.CommandManager.HqZeroTrigger;
        var handle = trigger.InitialRegistrations[0];
        Assert.Equal("HQ 归零检查", handle.Name);

        var trace = new List<string>();
        var m = trigger.RegisterReplaceWrapper(handle, (view, ctx, ct) =>
        {
            trace.Add("moding-HQ归零");
            return Task.CompletedTask;
        });
        Assert.NotNull(m);

        await hq.ApplyDamageAsync(25); // 20-25 → 0：card.stat.changed → 检查触发器（总线驱动；替换生效）

        Assert.Equal(new[] { "moding-HQ归零" }, trace);   // 替换生效
        Assert.Equal(0, hq.Health);                       // 数值归零照旧（数据面不经替换）
        Assert.Equal(MatchState.InProgress, match.State); // 默认终局响应未执行（被替换）
        Assert.Null(match.Winner);
    }
}
