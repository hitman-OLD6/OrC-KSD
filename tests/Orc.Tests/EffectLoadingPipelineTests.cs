using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// W3-A3 运行时装载修复——内核侧验收：
/// ①装载管线（装载完成动作自动执行/失败回滚不登记残留/复装自动重建托管登记）；
/// ②放置驱动兜底装载（未装载者重试；「初始化先、装载后」顺序契约保持）；
/// ③effect.removed 发射规则（实际移除命中恰一次〔先清理后发射〕；幂等无操作不发射；销毁批量逐效果发；源头即信号不重复发；静默路径不发）；
/// ④Add 即装载（三入口幂等由既有 EffectLifecycleTests 覆盖，本文件补充管线细节）。
/// </summary>
public class EffectLoadingPipelineTests
{
    // ---------- ① 装载管线（装载完成动作） ----------

    [Fact]
    public async Task Remount_Re_Registers_Managed_Cleanup_For_Second_Unmount()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var cleanups = new List<string>();
        engine.RegisterCardMountCompletedAction((_, effect) => effect.AddTeardownRegistration(() => cleanups.Add(effect.Name)));

        var effect = new RecordingPassiveEffect("效果甲", "r1", new List<string>());
        card.AddEffect(effect); // 授予→装载（通用装载路径自动执行托管登记）
        Assert.True(effect.IsMounted);
        Assert.Empty(cleanups); // 未卸载：清理未执行

        card.RemoveEffect(effect); // 移除→卸载→托管清理执行（第 1 次）
        Assert.Equal(new[] { "效果甲" }, cleanups);

        card.AddEffect(effect); // 复装→重新装载（装载完成动作自动重建登记）
        Assert.True(effect.IsMounted);

        card.RemoveEffect(effect); // 二次卸载→托管清理再次执行（第 2 次——复装重建生效）
        Assert.Equal(new[] { "效果甲", "效果甲" }, cleanups);
        Assert.False(effect.IsMounted);
    }

    [Fact]
    public async Task Mount_Completed_Action_Failure_Falls_Back_To_Load_Failure_Without_Registration_Residue()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var fired = 0;
        var failFirst = true;
        engine.RegisterCardMountCompletedAction((_, effect) =>
        {
            effect.AddTeardownRegistration(() => fired++);
            if (failFirst)
            {
                failFirst = false;
                throw new InvalidOperationException("托管动作首次失败");
            }
        });

        var effect = new RecordingPassiveEffect("效果乙", "r2", new List<string>());
        card.AddEffect(effect); // 装载→动作失败→回滚（「装载成功」含自动动作完成；不登记残留）
        Assert.False(effect.IsMounted); // 视为未生效
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Level == LogLevel.Error && e.Source == "效果乙/MountCompleted"); // 失败记录可定位（source 含效果标识与动作名）

        card.MountPassiveEffects(); // 兜底重试→动作成功（登记）
        Assert.True(effect.IsMounted);

        card.RemoveEffect(effect); // 卸载→托管清理恰执行一次（失败轮次的登记残留已清除）
        Assert.Equal(1, fired);
    }

    // ---------- ② 放置驱动兜底装载 ----------

    [Fact]
    public async Task Place_Driven_Fallback_Load_Mounts_After_Init()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var flaky = new FlakyMountEffect();
        card.AddEffect(flaky); // 首次尝试失败（回滚为未生效）
        Assert.False(flaky.IsMounted);
        Assert.Equal(1, flaky.Attempts);

        await S4TestHelpers.Place(engine, card); // 放置驱动兜底重试：初始化先 → 装载后（成功）
        Assert.True(card.IsPlaced);
        Assert.True(flaky.IsMounted);
        Assert.True(flaky.SawPlacedAtMount); // 放置处理内「初始化先、装载后」顺序契约保持
        Assert.Equal(2, flaky.Attempts);

        var initIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("loadout") && e.Keywords.Contains("init") && e.Keywords.Contains("战士"));
        var mountIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("loadout") && e.Keywords.Contains("mount") && e.Keywords.Contains("摇摆效果"));
        Assert.True(initIdx >= 0 && mountIdx >= 0 && initIdx < mountIdx); // 该次兜底装载位于初始化之后
    }

    // ---------- ③ effect.removed 发射规则 ----------

    [Fact]
    public async Task Effect_Removed_Update_Is_Emitted_On_Actual_Removal_Only()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var effect = new RecordingPassiveEffect("效果丙", "r3", new List<string>());
        card.AddEffect(effect);

        var emitted = new List<(object? Card, object? Effect)>();
        using var sub = engine.Subscribe((type, payload, _) =>
        {
            if (type == Updates.EffectRemoved && payload is not null)
            {
                emitted.Add((payload[PayloadKeys.Card], payload[PayloadKeys.Effect]));
            }

            return Task.CompletedTask;
        });

        card.RemoveEffect(effect); // 实际移除命中：恰发射一次（先清理〔落定〕、后发射）
        var record = Assert.Single(emitted);
        Assert.Same(card, record.Card);
        Assert.Same(effect, record.Effect);
        Assert.False(effect.IsMounted); // 发射时清理已落定
        Assert.Empty(card.Effects);

        card.RemoveEffect(effect); // 重复移除（幂等无操作）：不发射
        Assert.Single(emitted);

        card.RemoveEffect(new RecordingPassiveEffect("从未添加", "x", new List<string>())); // 不存在：不发射
        Assert.Single(emitted);
    }

    [Fact]
    public async Task Effect_Removed_Update_Driven_Cleanup_Does_Not_Emit_Again()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var effect = new RecordingPassiveEffect("效果丁", "r4", new List<string>());
        card.AddEffect(effect);

        var emittedCount = 0;
        using var sub = engine.Subscribe((type, _, _) =>
        {
            if (type == Updates.EffectRemoved)
            {
                emittedCount++;
            }

            return Task.CompletedTask;
        });

        await S4TestHelpers.RemoveViaUpdate(engine, card, effect); // 外部注入信号（源头即信号）

        Assert.Equal(1, emittedCount); // 恰一条（外部注入本身）；清理不重复发射（防循环）
        Assert.Empty(card.Effects);
        Assert.False(effect.IsMounted);
    }

    [Fact]
    public async Task Destroy_Card_Emits_Effect_Removed_Per_Effect_After_Cleanup()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var first = new RecordingPassiveEffect("效果一", "d1", new List<string>());
        var second = new RecordingPassiveEffect("效果二", "d2", new List<string>());
        card.AddEffect(first);
        card.AddEffect(second);

        var observed = new List<string>();
        using var sub = engine.Subscribe((type, payload, _) =>
        {
            if (type == Updates.EffectRemoved && payload is not null)
            {
                var effect = (Effect)payload[PayloadKeys.Effect]!;
                observed.Add(effect.Name);
                Assert.False(effect.IsMounted);        // 先清理（落定）后发射
                Assert.DoesNotContain(effect, card.Effects);
            }

            return Task.CompletedTask;
        });

        await engine.DestroyCard(card); // 销毁驱动批量清理：逐效果实际移除命中各恰发射一次

        Assert.Equal(2, observed.Count);
        Assert.Contains("效果一", observed);
        Assert.Contains("效果二", observed);
    }

    [Fact]
    public async Task RemoveEffectSilently_Does_Not_Emit_Update()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var effect = new RecordingPassiveEffect("效果戊", "r5", new List<string>());
        card.AddEffect(effect);

        var emittedCount = 0;
        using var sub = engine.Subscribe((type, _, _) =>
        {
            if (type == Updates.EffectRemoved)
            {
                emittedCount++;
            }

            return Task.CompletedTask;
        });

        card.RemoveEffectSilently(effect); // 静默清理路径（回滚专用）：行为与 RemoveEffect 一致但不发射

        Assert.Equal(0, emittedCount);
        Assert.Empty(card.Effects);
        Assert.False(effect.IsMounted); // 静默销毁：已装载卸载链照常完成（不发射）
    }
}
