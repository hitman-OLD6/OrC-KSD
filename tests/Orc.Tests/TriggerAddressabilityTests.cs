using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 补全性佐证（X4「全部事件 handler 注册项可寻址」硬性要求）：全 src 构造期装配点的句柄获取路径 ＋ moding 替换生效。
/// 覆盖：①卡牌放置处理器（engine.CardPlacedTrigger——懒创建）；②卡牌清理处理器（engine.CardCleanupTrigger——懒创建）；
/// ③被动效果 disposing 触发器（Effect.DisposingTrigger——protected，子类/装配方面）；④主动效果施放事件（ActiveEffect.CastTrigger——构造期 castEvents 项）。
/// 驱动方式：①经总线（card.placed）；②③直接执行目标触发器（隔离总线上的其它链、聚焦本事件替换）；④经 CastAsync。
/// 约定：moding 逻辑与 handler 本体一致＝单一处理单元（记录）；禁止流程编排——本文件全部用例均以单一处理单元形态示范。
/// </summary>
public class TriggerAddressabilityTests
{
    // ---------- ① 卡牌放置处理器（懒创建；经引擎公开面寻址） ----------

    [Fact]
    public async Task Card_Placed_Trigger_Handle_Is_Addressable_And_Moding_Replaces_Placed_Handling()
    {
        var engine = new LogicEngine();
        Assert.Null(engine.CardPlacedTrigger); // 懒创建：未创建卡牌前无实例

        var card = new Card(engine, "甲"); // 首次创建卡牌 → 装载链处理器装配
        var trigger = engine.CardPlacedTrigger;
        Assert.NotNull(trigger);

        var handle = trigger!.InitialRegistrations[0]; // 构造期装配项句柄（「放置处理」）
        Assert.Equal("放置处理", handle.Name);

        var trace = new List<string>();
        var m = trigger.RegisterReplaceWrapper(handle, (view, ctx, ct) =>
        {
            trace.Add("moding-放置");
            return Task.CompletedTask;
        });
        Assert.NotNull(m);

        await S4TestHelpers.Place(engine, card); // 总线驱动（card.placed → 放置处理器）

        Assert.Equal(new[] { "moding-放置" }, trace); // 替换生效
        Assert.False(card.IsPlaced);                  // 原「放置处理」未执行（放置初始化未发生）
    }

    // ---------- ② 卡牌清理处理器（懒创建；经引擎公开面寻址） ----------

    [Fact]
    public async Task Card_Cleanup_Trigger_Handle_Is_Addressable_And_Moding_Replaces_Cleanup_Handling()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        card.AddData(new HealthData { Hp = 10 });
        var effect = new RecordingPassiveEffect("记录者", "r", new List<string>());
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);
        Assert.True(effect.IsMounted);

        var trigger = engine.CardCleanupTrigger;
        Assert.NotNull(trigger);

        var handle = trigger!.InitialRegistrations[0]; // 「清理处理」
        Assert.Equal("清理处理", handle.Name);

        var trace = new List<string>();
        var m = trigger.RegisterReplaceWrapper(handle, (view, ctx, ct) =>
        {
            trace.Add("moding-清理");
            return Task.CompletedTask;
        });
        Assert.NotNull(m);

        await trigger.InvokeAsync(engine, new Dictionary<string, object?> // 直接执行（隔离其它链）
        {
            [PayloadKeys.Card] = card,
            [PayloadKeys.Effect] = effect,
        });

        Assert.Equal(new[] { "moding-清理" }, trace); // 替换生效
        Assert.True(effect.IsMounted);                // 原清理未执行（未卸载）
        Assert.Single(card.Effects);                  // 效果仍在列表
    }

    // ---------- ③ 被动效果生命周期清理（protected 面——子类/装配方） ----------

    private sealed class ModedPassiveEffect : PassiveEffect
    {
        public ModedPassiveEffect(List<string> trace)
            : base("替换清理")
        {
            // 装配期（子类构造）：经基类受保护触发器面 + 构造期项句柄注册 moding（替换「disposing 兜底驱动」——
            // 新架构下销毁入口是 ExecuteDispose，直接 Invoke 触发器不再等价于"执行完整清理"；本用例只验证可寻址性）。
            DisposingTrigger.RegisterReplaceWrapper(DisposingTrigger.InitialRegistrations[1], (view, ctx, ct) =>
            {
                trace.Add("moding-生命周期");
                return Task.CompletedTask;
            });
        }

        /// <summary>测试驱动：直接执行 disposing 触发器（隔离总线上的其它链、聚焦本事件替换）。</summary>
        public Task InvokeLifecycleAsync(LogicEngine engine, Card card)
            => DisposingTrigger.InvokeAsync(engine, new Dictionary<string, object?>
            {
                [PayloadKeys.Card] = card,
                [PayloadKeys.Effect] = this,
            });
    }

    [Fact]
    public async Task Passive_Effect_Lifecycle_Trigger_Is_Addressable_For_Subclasses()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "甲");
        card.AddData(new HealthData { Hp = 10 });

        var trace = new List<string>();
        var effect = new ModedPassiveEffect(trace);
        card.AddEffect(effect);
        await S4TestHelpers.Place(engine, card);
        Assert.True(effect.IsMounted);

        await effect.InvokeLifecycleAsync(engine, card);

        Assert.Equal(new[] { "moding-生命周期" }, trace); // 替换生效
        Assert.True(effect.IsMounted);                   // 原生命周期清理模板未执行（未卸载）
        Assert.Single(card.Effects);                     // 效果仍在列表
    }

    // ---------- ④ 主动效果施放事件（构造期 castEvents 通道） ----------

    private sealed class ModedCastEffect : ActiveEffect<CounterView>
    {
        public ModedCastEffect(List<string> trace)
            : base("替换施放", castEvents: new[]
            {
                new TriggerEvent<CounterView>("施放", (view, ctx, ct) =>
                {
                    trace.Add("原施放");
                    return Task.CompletedTask;
                }),
            })
        {
            // 构造期 castEvents 项句柄：经 CastTrigger.InitialRegistrations 供给——子类 moding 替换
            CastTrigger.RegisterReplaceWrapper(CastTrigger.InitialRegistrations[0], (view, ctx, ct) =>
            {
                trace.Add("moding-施放");
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task Active_Effect_Cast_Events_Are_Addressable_For_Subclasses()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var effect = new ModedCastEffect(trace);

        await effect.CastAsync(engine);

        Assert.Equal(new[] { "moding-施放" }, trace); // 构造期施放事件被替换（原施放无痕）
    }
}
