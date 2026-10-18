using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收 A（场景 2『攻击时回血』主锚）与 F（销毁）：
/// 放置→注入生效（初始化先/注入后信号）→攻击→回血（伤害结算先于回血；攻击/伤害分离）→多轮持续→移除清理→不再回血→重装再次生效；
/// 一步式销毁（杀＋card.destroyed）后卡牌失效且全部效果清理完成；低层两步等价。
/// </summary>
public class Scenario2Tests
{
    [Fact]
    public async Task Scenario2_Attack_Heal_End_To_End()
    {
        var engine = new LogicEngine();
        var attacker = new Card(engine, "战士");
        attacker.AddData(new HealthData { Hp = 10 });
        var defender = new Card(engine, "哥布林");
        defender.AddData(new HealthData { Hp = 10 });
        var heal = new AttackHealEffect(engine, 5);
        attacker.AddEffect(heal);

        // ── ① 添加即装载（W3-A3）→ 放置驱动幂等 ────────────────────────
        Assert.True(heal.IsMounted); // 效果生效（注入完成——未放置也装载）
        Assert.Equal(1, heal.MountCount);
        Assert.False(heal.SawPlacedAtMount); // 装载时初始化尚未发生（未上场语义下放效果三策略）

        await S4TestHelpers.Place(engine, attacker);
        await S4TestHelpers.Place(engine, defender);

        Assert.True(heal.IsMounted);
        Assert.Contains("攻击时回血/dispose", engine.Bus.GetSubscribers(Updates.CardDestroyed)); // disposing 触发器挂载（销毁兜底就位）
        Assert.Equal(1, heal.MountCount); // 放置驱动幂等：不重复装载/不重复 OnMount

        var initIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("loadout") && e.Keywords.Contains("init") && e.Keywords.Contains("战士"));
        var mountIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("loadout") && e.Keywords.Contains("mount") && e.Keywords.Contains("攻击时回血"));
        Assert.True(initIdx >= 0 && mountIdx >= 0);
        Assert.True(mountIdx < initIdx); // 事件流条目次序（W3-A3 新时序）：添加即装载先于放置初始化（放置驱动幂等跳过）

        // ── ② 攻击 →（经伤害结算）→ 攻击者回血；伤害结算先于回血 ────────
        var attackStream = await engine.AttackFlow.ExecuteAsync(attacker, defender, 3);

        Assert.Equal(7, defender.GetData<HealthData>().Hp);  // 承受方受伤
        Assert.Equal(15, attacker.GetData<HealthData>().Hp); // 发起方回血
        Assert.Equal(7, heal.TargetHpAtHealTime);            // 回血执行时伤害已生效（强制信号）

        // 攻击/伤害分离：伤害结算流程在攻击执行下作为独立子流运行
        var damageStream = attackStream.Children.Single();
        Assert.Contains(damageStream.Entries, e => e.Keywords.Contains("damage") && e.Keywords.Contains("apply"));

        // 留痕次序：伤害生效条目先于回血条目
        var damageIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("damage") && e.Keywords.Contains("apply") && e.Keywords.Contains("哥布林"));
        var healIdx = S4TestHelpers.IndexOfEntry(engine, e => e.Keywords.Contains("test-heal"));
        Assert.True(damageIdx >= 0 && healIdx >= 0 && damageIdx < healIdx);

        // ── ③ 多轮攻击持续回血 ─────────────────────────────────────────
        await engine.AttackFlow.ExecuteAsync(attacker, defender, 3);
        Assert.Equal(4, defender.GetData<HealthData>().Hp);
        Assert.Equal(20, attacker.GetData<HealthData>().Hp);
        Assert.Equal(2, heal.HealCount);

        // ── ④ 移除 → 清理（撤销注入/总线卸载/专属清理；容器读面）────────
        attacker.RemoveEffect(heal);

        Assert.DoesNotContain(heal, attacker.Effects); // 容器读面与运行态一致
        Assert.False(heal.IsMounted);
        Assert.True(heal.ExclusiveCleanupRan);          // 作者专属清理（OnUnmount）
        Assert.True(heal.InjectionRefCleared);
        Assert.DoesNotContain("攻击时回血", engine.Bus.GetSubscribers(Updates.EffectRemoved)); // 总线卸载
        Assert.DoesNotContain("攻击时回血/dispose", engine.Bus.GetSubscribers(Updates.CardDestroyed));
        Assert.Contains(
            engine.RootStream.Entries,
            e => e.Keywords.Contains("loadout") && e.Keywords.Contains("cleanup") && e.Keywords.Contains("攻击时回血"));

        // ── ⑤ 清理后再攻击 → 不再回血、不再进入 handler ─────────────────
        await engine.AttackFlow.ExecuteAsync(attacker, defender, 3);
        Assert.Equal(1, defender.GetData<HealthData>().Hp);
        Assert.Equal(20, attacker.GetData<HealthData>().Hp); // 不回血
        Assert.Equal(2, heal.HealCount);

        // ── ⑥ 重装（移除后重新 Add）→ 再次生效（且非双倍：撤销彻底）───
        attacker.AddEffect(heal);
        Assert.True(heal.IsMounted);
        Assert.Equal(2, heal.MountCount);

        await engine.AttackFlow.ExecuteAsync(attacker, defender, 3);
        Assert.Equal(25, attacker.GetData<HealthData>().Hp); // 恰回血一次（旧注册已撤销、未残留双倍）
        Assert.Equal(3, heal.HealCount);

        attacker.RemoveEffect(heal);
        await engine.AttackFlow.ExecuteAsync(attacker, defender, 3);
        Assert.Equal(25, attacker.GetData<HealthData>().Hp); // 再次清理后不再回血
    }

    [Fact]
    public async Task Destroy_Card_Cleans_All_Effects_And_Invalidates_Card()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var heal = new AttackHealEffect(engine, 5);
        var rec = new RecordingPassiveEffect("记录", "r", new List<string>());
        card.AddEffect(heal);
        card.AddEffect(rec);
        await S4TestHelpers.Place(engine, card);

        await engine.DestroyCard(card); // 一步式：杀（Destroy 语义）＋ card.destroyed 驱动清理

        // 卡牌失效（Life/Ref 语义）
        Assert.False(card.Life.IsAlive);
        Assert.False(card.Ref.IsAlive);
        Assert.Throws<StaleReferenceException>(() => card.Ref.Value);

        // 清理完成：该卡全部效果
        Assert.False(heal.IsMounted);
        Assert.False(rec.IsMounted);
        Assert.Empty(card.Effects);
        Assert.Equal(1, heal.UnmountCount);
        Assert.DoesNotContain("攻击时回血", engine.Bus.GetSubscribers(Updates.EffectRemoved));

        // 清理后再攻击 → 不再回血
        var target = new Card(engine, "木桩");
        target.AddData(new HealthData { Hp = 100 });
        await engine.AttackFlow.ExecuteAsync(card, target, 3);
        Assert.Equal(0, heal.HealCount);
    }

    [Fact]
    public async Task Destroy_Two_Step_Composition_Is_Equivalent_And_Destroy_Alone_Does_Not_Unload()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        var heal = new AttackHealEffect(engine, 5);
        card.AddEffect(heal);
        await S4TestHelpers.Place(engine, card);

        card.Destroy(); // 低层两步之一：只杀（S3 约束不变）
        Assert.False(card.Life.IsAlive);
        Assert.True(heal.IsMounted); // Destroy 本身不触发卸载

        await engine.Emit(Updates.CardDestroyed, new Dictionary<string, object?> { [PayloadKeys.Card] = card }); // 两步之二：发更新驱动清理

        Assert.False(heal.IsMounted); // 与一步式等价：清理完成
        Assert.Empty(card.Effects);
        Assert.Equal(1, heal.UnmountCount);
    }
}
