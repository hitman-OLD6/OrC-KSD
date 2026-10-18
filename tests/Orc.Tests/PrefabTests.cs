using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S-C5 预制体模型与效果快照/装载计划验收：来源二选一、主动禁 hooks、hooks 并集、计划步骤与顺序。
/// </summary>
public class PrefabTests
{
    private static EventPrefab CsxEvent(string id, params string[] downstream) =>
        new(id, csxSource: $"Task {id}(Context ctx) => Task.CompletedTask;", downstream: downstream);

    private static EventPrefab AsmEvent(string id) => new(id, assemblyKey: $"prefab.events.{id}");

    [Fact]
    public void 事件预制体_必须恰一种来源()
    {
        Assert.Throws<ArgumentException>(() => new EventPrefab("e")); // 都未给
        Assert.Throws<ArgumentException>(() => new EventPrefab("e", csxSource: "x", assemblyKey: "y")); // 都给了
    }

    [Fact]
    public void 事件预制体_来源为空白_拒绝()
    {
        Assert.Throws<ArgumentException>(() => new EventPrefab("e", csxSource: "   "));
        Assert.Throws<ArgumentException>(() => new EventPrefab("e", assemblyKey: "   "));
    }

    [Fact]
    public void 事件预制体_id与入口_空白_拒绝()
    {
        Assert.Throws<ArgumentException>(() => new EventPrefab("  ", assemblyKey: "k"));
        Assert.Throws<ArgumentException>(() => new EventPrefab("e", entryName: " ", assemblyKey: "k"));
    }

    [Fact]
    public void 事件预制体_内联标记与下游声明()
    {
        var inline = CsxEvent("e", "unit.deploy");
        Assert.True(inline.IsInline);
        Assert.Equal("HandleAsync", inline.EntryName);
        Assert.Equal(new[] { "unit.deploy" }, inline.Downstream);

        var asm = AsmEvent("e2");
        Assert.False(asm.IsInline);
        Assert.Equal("prefab.events.e2", asm.AssemblyKey);
    }

    [Fact]
    public void 触发器预制体_主动声明hooks_拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            new TriggerPrefab("t", "unit.play", TriggerKind.Active, hooks: new[] { "card.placed" }));

        var passive = new TriggerPrefab("t", "effect.lifecycle", TriggerKind.Passive, hooks: new[] { "card.placed" });
        Assert.Equal(new[] { "card.placed" }, passive.Hooks);
    }

    [Fact]
    public void 触发器预制体_事件集合含null_拒绝()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TriggerPrefab("t", "k", events: new EventPrefab[] { CsxEvent("e"), null! }));
    }

    [Fact]
    public void 效果预制体_挂载hook为被动hooks并集且去重保序()
    {
        var main = new TriggerPrefab("m", "effect.lifecycle", TriggerKind.Passive, hooks: new[] { "effect.removed", "card.destroyed" });
        var other = new TriggerPrefab("o", "watch.trigger", TriggerKind.Passive, hooks: new[] { "card.destroyed", "turn.end" });

        var effect = new EffectPrefab("prefab.mine", main, otherTriggers: new[] { other });

        Assert.Equal(new[] { "effect.removed", "card.destroyed", "turn.end" }, effect.MountedHooks);
    }

    [Fact]
    public void 效果快照_被动_装载计划顺序为挂载_注册_替换()
    {
        var main = new TriggerPrefab(
            "m", "effect.lifecycle", TriggerKind.Passive,
            hooks: new[] { "effect.removed", "card.destroyed" },
            events: new[] { CsxEvent("onRemove") });
        var other = new TriggerPrefab(
            "o", "watch.trigger", TriggerKind.Passive,
            hooks: new[] { "turn.end" },
            events: new[] { CsxEvent("onTurnEnd", "effect.lifecycle") });
        var wrapper = new WrapperPrefab("replace", "onRemove", null, CsxEvent("onRemoveOverride"));

        var snapshot = new EffectSnapshot(new EffectPrefab("p", main, new[] { other }, new[] { wrapper }));
        var plan = snapshot.BuildLoadPlan();

        Assert.Equal(EffectSnapshot.SchemaVersion, snapshot.Version);
        Assert.Equal(
            new[]
            {
                EffectLoadStepKind.MountMainTrigger,
                EffectLoadStepKind.RegisterEvent,
                EffectLoadStepKind.MountTrigger,
                EffectLoadStepKind.RegisterEvent,
                EffectLoadStepKind.ApplyWrapper,
                EffectLoadStepKind.Dispose,
            },
            plan.Steps.Select(s => s.Kind));

        Assert.Equal("effect.lifecycle", plan.Steps[0].Target);
        Assert.Contains("effect.removed", plan.Steps[0].Detail);
        Assert.Contains("csx:HandleAsync", plan.Steps[1].Detail);
        Assert.Contains(plan.Steps, s => s.Detail.Contains("onRemoveOverride"));
    }

    [Fact]
    public void 效果快照_主动_无挂载步骤()
    {
        var main = new TriggerPrefab(
            "m", "order.fireball", TriggerKind.Active,
            events: new[] { AsmEvent("cast") });

        var plan = new EffectSnapshot(new EffectPrefab("p", main)).BuildLoadPlan();

        Assert.DoesNotContain(plan.Steps, s => s.Kind == EffectLoadStepKind.MountMainTrigger);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(EffectLoadStepKind.RegisterEvent, plan.Steps[0].Kind);
        Assert.Contains("asm:prefab.events.cast", plan.Steps[0].Detail);
        Assert.Equal(EffectLoadStepKind.Dispose, plan.Steps[1].Kind); // E-D1：disposing 触发器默认必选
    }

    [Fact]
    public void 效果快照_主动触发的下游边_可指向主动触发器()
    {
        var watcher = new TriggerPrefab("w", "watch.trigger", TriggerKind.Passive, hooks: new[] { "turn.end" },
            events: new[] { CsxEvent("onTurnEnd", "order.fireball") });
        var fireball = new TriggerPrefab("f", "order.fireball", TriggerKind.Active, events: new[] { AsmEvent("cast") });

        var effect = new EffectPrefab("p", watcher, otherTriggers: new[] { fireball });

        Assert.Equal(new[] { "order.fireball" }, effect.MainTrigger.Events[0].Downstream);
        Assert.Equal(TriggerKind.Active, effect.OtherTriggers[0].Kind);
    }

    [Fact]
    public void 效果快照_版本非法_拒绝()
    {
        var main = new TriggerPrefab("m", "k", TriggerKind.Active);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventPrefab("e", assemblyKey: "k", version: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectSnapshot(new EffectPrefab("p", main), schemaVersion: 0));
    }

    [Fact]
    public void moding预制体_目标id空白_拒绝()
    {
        var main = new TriggerPrefab("m", "k", TriggerKind.Active, events: new[] { AsmEvent("cast") });
        Assert.Throws<ArgumentException>(() =>
            new EffectPrefab("p", main, wrappers: new[] { new WrapperPrefab("replace", "  ", null, AsmEvent("r")) }));
    }
}
