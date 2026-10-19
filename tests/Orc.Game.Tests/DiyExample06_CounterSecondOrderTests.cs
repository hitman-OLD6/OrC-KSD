using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 6（R6f + G-01）证据：**敌方使用第二张指令时将其反制**。
/// 代码侧：指令经 <c>engine.OrderFlow.ExecuteAsync</c> 施放（Counter → Resolve → Finalize）；
/// 反制效果注入 <see cref="OrderFlowBands.Counter"/>，命中第二张时 <c>ctx.Interrupt()</c> 使 Resolve 不执行。
/// 边界（P4 起）：原生 <c>OrderCard</c> 打出链同样经 OrderFlow；本示例用裸卡直接驱动 flow 以隔离指令数据。
/// </summary>
public class DiyExample06_CounterSecondOrderTests
{
    [Fact]
    public async Task 第二张指令_被反制_首张正常结算()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 反制方：一个在场单位承载反制效果（真实卡可为反制卡/单位）。
        var counterHost = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var counter = new SecondOrderCounterEffect(match.Engine);
        counterHost.AddEffect(counter);
        Assert.True(counter.IsMounted);

        var caster = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.Equal(5, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        var order = new DamageOrder();

        // 第一张：正常结算（5 → 3）。
        await match.Engine.OrderFlow.ExecuteAsync(caster, order, target, 2);
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(1, counter.Seen);

        // 第二张：Counter 段命中 → Interrupt → Resolve 不执行（防御保持 3）。
        await match.Engine.OrderFlow.ExecuteAsync(caster, order, target, 2);
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        Assert.Equal(2, counter.Seen);
    }

    /// <summary>施放视图：只声明本指令需要的键（全部可选，便于 OrderFlow 只传三键）。</summary>
    [ContextView]
    public class DamageOrderView
    {
        [Optional]
        [Read]
        public virtual Card? Source { get; set; }

        [Optional]
        [Read]
        public virtual Card? Target { get; set; }

        [Optional]
        [Read]
        public virtual int Amount { get; set; }
    }

    /// <summary>测试指令：施放时对 Target 造成 Amount 伤害（ICastAction 由 ActiveEffect 承载）。</summary>
    private sealed class DamageOrder : ActiveEffect<DamageOrderView>
    {
        public DamageOrder()
            : base("测试伤害指令", castEvents: new[] { new TriggerEvent<DamageOrderView>("施放结算", CastAsync) })
        {
        }

        private static async Task CastAsync(DamageOrderView view, Context ctx, CancellationToken ct)
        {
            if (view.Source is not { } source || view.Target is not { } target)
            {
                return;
            }

            if (EffectRuntime.ResolveFor(source) is not { } runtime)
            {
                return;
            }

            await runtime.DamageAsync(target, view.Amount, source, ct);
        }
    }

    /// <summary>反制效果：每次进入 Counter 段计数；第二张起中断整条指令链。</summary>
    private sealed class SecondOrderCounterEffect : PassiveEffect
    {
        private readonly LogicEngine _engine;

        public SecondOrderCounterEffect(LogicEngine engine)
            : base("第二张指令反制")
        {
            _engine = engine;
        }

        public int Seen { get; private set; }

        protected override void OnMount()
            => Inject(_engine.OrderFlow.Trigger, "反制检查", OrderFlowBands.Counter, OnCounter);

        private Task OnCounter(OrderFlowView view, Context ctx, CancellationToken ct)
        {
            Seen++;
            if (Seen >= 2)
            {
                ctx.Interrupt(); // 链级中断：Resolve 段不执行
            }

            return Task.CompletedTask;
        }
    }
}
