using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// E-D1 效果 disposing 触发器：显式销毁、幂等/重入、异常终态、卡销毁兜底、误伤隔离、复装复位、状态面。
/// </summary>
public class EffectDisposalTests
{
    private sealed class DisposalProbeEffect : PassiveEffect
    {
        public int DisposalRuns;

        public DisposalProbeEffect(string name = "销毁探针")
            : base(name)
        {
        }

        protected override void OnUnmount() => DisposalRuns++;
    }

    [Fact]
    public async Task RemoveEffect_Destroys_Completely_And_Emits_Once()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        var effect = new DisposalProbeEffect();
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);

        var removed = 0;
        engine.Subscribe((updateType, _, _) =>
        {
            if (updateType == Updates.EffectRemoved)
            {
                removed++;
            }

            return Task.CompletedTask;
        });

        Assert.Equal(DisposalState.Mounted, effect.DisposalState);

        card.RemoveEffect(effect);

        Assert.Equal(DisposalState.Disposed, effect.DisposalState);
        Assert.True(effect.IsDisposed);
        Assert.Empty(card.Effects);
        Assert.Equal(1, effect.DisposalRuns);
        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task Repeated_Remove_Does_Not_Repeat_Disposal_Or_Emit()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        var effect = new DisposalProbeEffect();
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);

        var removed = 0;
        engine.Subscribe((updateType, _, _) =>
        {
            if (updateType == Updates.EffectRemoved)
            {
                removed++;
            }

            return Task.CompletedTask;
        });

        card.RemoveEffect(effect);
        card.RemoveEffect(effect);

        Assert.Equal(1, effect.DisposalRuns);
        Assert.Equal(1, removed);
        Assert.Equal(DisposalState.Disposed, effect.DisposalState);
    }

    [Fact]
    public async Task Disposal_Logic_Exception_Still_Removes_And_Emits()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        var effect = new FailingUnmountEffect();
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);

        var removed = 0;
        engine.Subscribe((updateType, _, _) =>
        {
            if (updateType == Updates.EffectRemoved)
            {
                removed++;
            }

            return Task.CompletedTask;
        });

        card.RemoveEffect(effect); // 不抛（隔离记录）

        Assert.True(effect.IsDisposed);
        Assert.Empty(card.Effects);
        Assert.Equal(1, removed);
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Level == LogLevel.Error && e.Source == "卸载炸弹/dispose/效果销毁");
    }

    [Fact]
    public async Task Card_Destroyed_Is_Fallback_Disposal()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        var effect = new DisposalProbeEffect();
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);

        Assert.Equal(DisposalState.Mounted, effect.DisposalState);

        await engine.DestroyCard(card);

        Assert.Equal(DisposalState.Disposed, effect.DisposalState);
        Assert.Equal(1, effect.DisposalRuns);
        Assert.Empty(card.Effects);
    }

    [Fact]
    public async Task Destroying_Another_Card_Does_Not_Dispose_This_Effect()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var effectA = new DisposalProbeEffect("甲的销毁探针");
        var effectB = new DisposalProbeEffect("乙的销毁探针");
        a.AddEffect(effectA);
        b.AddEffect(effectB);
        await S4TestHelpers.Place(engine, a);
        await S4TestHelpers.Place(engine, b);

        await engine.DestroyCard(b);

        Assert.Equal(DisposalState.Disposed, effectB.DisposalState);
        Assert.Equal(DisposalState.Mounted, effectA.DisposalState); // 守卫过滤：不误伤其它卡的效果
        Assert.Equal(0, effectA.DisposalRuns);
        Assert.Contains(effectA, a.Effects);
    }

    [Fact]
    public async Task Reattach_Resets_Disposal_State()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        var effect = new DisposalProbeEffect();
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);

        card.RemoveEffect(effect);
        Assert.Equal(DisposalState.Disposed, effect.DisposalState);

        card.AddEffect(effect); // 复装＝新生命周期
        Assert.Equal(DisposalState.Mounted, effect.DisposalState);
        Assert.True(effect.IsMounted);
        Assert.Equal(1, effect.DisposalRuns);
    }
}
