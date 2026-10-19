using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2a G3 修饰机制核心——修饰器体系机制级测试（验收点①加值/叠加/按来源撤销 及补充用例）。
/// 覆盖：场景①三层断言（加值/叠加/按来源撤销）；单列用例——重入拒绝（(iv)）、期限到期自注销经真实订阅通路（(v)，含 +0 静默与
/// 任意相位/使用处过滤）、跨卡冲突/归属边界（(vi)）、批量原子失败回滚（(vii)）、非单位卡承载（(viii)）、组语义（(ix)）。
/// 机制测试口径：独立构造卡＋最小状态（不依赖对局装载路径）；发射经引擎总线真实通路。
/// </summary>
public class ModifierSystemTests
{
    // ---------- 场景①：加值 / 叠加 / 按来源撤销（三层断言） ----------

    [Fact]
    public async Task Scenario_Add_Stack_And_RemoveBySource_ThreeLayers()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);
        var sourceA = new object();
        var sourceB = new object();

        // 层 1：单来源加值 +2 → 有效值＝基准+2；恰一次集中触发（载荷＝目标卡＋变化字段）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, sourceA));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var update = Assert.Single(recorder.Updates);
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Same(unit, update.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 层 2：叠加（两来源 +2 / +3 → +5）；再触发一次（净变化）
        recorder.Clear();
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, sourceB));
        Assert.Equal(10, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Single(recorder.Updates);

        // 层 3：按来源撤销（撤 A 留 B → 剩 +3 贡献）；再触发一次（净变化）
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(sourceA);
        Assert.Equal(8, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Single(recorder.Updates);
        Assert.Single(unit.Modifiers.All);
        Assert.Same(sourceB, unit.Modifiers.All[0].Source);
        Assert.Equal(CardStatFields.Attack, unit.Modifiers.All[0].Field);

        // 撤销无修饰的来源＝幂等无操作（零发射）
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(sourceA);
        Assert.Empty(recorder.Updates);
    }

    // ---------- 单列 (iv)：重入场景（含所定义的重入策略行为——拒绝并明确错误） ----------

    [Fact]
    public async Task Reentrant_State_Changes_During_Run_Are_Rejected_With_Clear_Error()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine);

        // 探针修饰器：链节执行窗内尝试重入（重跑 / 挂载）——策略＝拒绝并明确错误（同步拒绝、可观测）
        var probe = new ReentrancyProbeModifier(CardStatFields.Attack, new object());
        await unit.Modifiers.AddModifierAsync(probe); // 挂载 → 自动衔接（跑链）→ 链节内触发重入探测

        Assert.True(probe.RerunRejectedSync);
        Assert.True(probe.AddRejectedSync);

        // 任何路径结束后不留中间态：机制可再次请求、状态一致（后续操作正常）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, new object()));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Equal(2, unit.Modifiers.All.Count);
    }

    // ---------- 单列 (v)：期限到期自注销（经订阅机制真实通路） ----------

    [Fact]
    public async Task Expiry_Self_Unmounts_Through_Real_Subscription_Path()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);

        // 期限修饰器：到期相位＝turn.end；挂载即生效（+2）→ 恰一次集中触发
        await unit.Modifiers.AddModifierAsync(
            new AddModifier(CardStatFields.Attack, 2, new object(), expiry: new ModifierExpiry(GameUpdates.TurnEnd)));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Single(recorder.Updates);

        // 无关相位不触发（订阅过滤正确——不误撤）
        recorder.Clear();
        await engine.Emit(GameUpdates.TurnStartAfter);
        Assert.Single(unit.Modifiers.All);
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder)); // 无 card.stat.changed（相位发射自身除外）

        // 相位到达：总线发射 → 订阅回调 → 自注销（统一注销路径）→ 自动衔接一轮 → 有变更才发
        await engine.Emit(GameUpdates.TurnEnd);
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder)); // 恰一条集中触发
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 自清理：再次同相位＝无残留订阅、无再触发
        recorder.Clear();
        await engine.Emit(GameUpdates.TurnEnd);
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder));
    }

    [Fact]
    public async Task Expiry_With_Zero_Net_Change_Is_Silent()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);

        // +0 期限修饰器：挂载轮无净变化（零发射）→ 到期自注销仍无净变化（零发射）
        await unit.Modifiers.AddModifierAsync(
            new AddModifier(CardStatFields.Attack, 0, new object(), expiry: new ModifierExpiry(GameUpdates.TurnEnd)));
        Assert.Empty(recorder.Updates);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        await engine.Emit(GameUpdates.TurnEnd);
        Assert.Empty(unit.Modifiers.All);
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder)); // 到期自注销仍无净变化 → 零发射
    }

    [Fact]
    public async Task Expiry_Supports_Arbitrary_Phase_And_Usage_Site_Filter()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);
        var marker = new object();

        // 任意相位标识（机制不设白名单）；过滤器＝使用处注入的载荷判定（机制不内建归属语义）
        await unit.Modifiers.AddModifierAsync(new AddModifier(
            CardStatFields.Attack, 2, new object(),
            expiry: new ModifierExpiry(
                "phase.custom",
                payload => payload is not null && ReferenceEquals(payload["Marker"], marker))));
        recorder.Clear();

        // 过滤未命中：不撤（保持挂载）
        await engine.Emit("phase.custom", new Dictionary<string, object?> { ["Marker"] = new object() });
        Assert.Single(unit.Modifiers.All);
        Assert.Empty(ModifierTestKit.StatChangedUpdates(recorder));

        // 命中：自注销（有变更 → 集中触发）；有效值回归基准
        await engine.Emit("phase.custom", new Dictionary<string, object?> { ["Marker"] = marker });
        Assert.Empty(unit.Modifiers.All);
        var update = Assert.Single(ModifierTestKit.StatChangedUpdates(recorder));
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
    }

    // ---------- 单列 (vi)：跨卡冲突 / 归属边界 ----------

    [Fact]
    public async Task Instance_Belongs_To_Single_Card_And_Idempotent_On_Same_Card()
    {
        var engine = new LogicEngine();
        var unitA = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        var unitB = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);

        var modifier = new AddModifier(CardStatFields.Attack, 2, new object());
        await unitA.Modifiers.AddModifierAsync(modifier);
        Assert.Single(unitA.Modifiers.All);
        Assert.Equal(7, unitA.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 已挂载（未注销）实例挂同一卡＝幂等（不重复贡献、零发射）
        recorder.Clear();
        await unitA.Modifiers.AddModifierAsync(modifier);
        Assert.Single(unitA.Modifiers.All);
        Assert.Equal(7, unitA.Modifiers.GetEffectiveValue(CardStatFields.Attack)); // 未变 +4
        Assert.Empty(recorder.Updates);

        // 跨卡：已挂载实例挂另一张卡＝明确错误（fail-fast、不静默）
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitB.Modifiers.AddModifierAsync(modifier));
        Assert.Empty(unitB.Modifiers.All);

        // 撤后重挂不限卡：A 撤 → 挂 B 成功（注销已清空全部挂载态、无残留）
        await unitA.Modifiers.RemoveModifierAsync(modifier);
        Assert.Empty(unitA.Modifiers.All);
        recorder.Clear();
        await unitB.Modifiers.AddModifierAsync(modifier);
        Assert.Single(unitB.Modifiers.All);
        Assert.Equal(7, unitB.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Single(recorder.Updates);
    }

    // ---------- 单列 (vii)：批量原子失败回滚（挂载侧） ----------

    [Fact]
    public async Task Batch_Add_Is_Atomic_And_Rolls_Back_On_Failure()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);
        var ok = new RecordingModifier(CardStatFields.Attack, new object(), transform: (card, current) => current + 2);
        var failing = new FailingModifier(CardStatFields.Defense, new object());

        // 失败注入：批内第二条挂载钩子抛 → 整体回滚（无部分条目、不自动衔接、原异常上抛）
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unit.Modifiers.AddModifiersAsync(new Modifier[] { ok, failing }));
        Assert.Equal(FailingModifier.FailureMessage, ex.Message);

        Assert.Empty(unit.Modifiers.All); // 无部分条目
        Assert.Equal(1, ok.MountCalls);
        Assert.Equal(1, ok.UnmountCalls); // 已成功挂载条目经完整注销路径退回（自托管清理被执行）
        Assert.Empty(recorder.Updates); // 未衔接（零发射——不留半更新）
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack)); // 有效值＝基准（无缓存污染）

        // 机制可继续使用（回滚后无残留、可再次挂载）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, new object()));
        Assert.Equal(8, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Single(recorder.Updates);
    }

    // ---------- 单列 (viii)：非单位卡承载（证明机制不硬编码「单位卡」） ----------

    [Fact]
    public async Task Non_Unit_Cards_Support_The_Modifier_Mechanism()
    {
        var engine = new LogicEngine();
        var command = ModifierTestKit.CreateOrderCard(engine);
        using var recorder = new UpdateRecorder(engine);
        const string powerField = "CommandPower";
        var source = new object();

        // 任何卡类可注册检测组件（名单扩展注册）——此处以固定基准注入指令卡数据域
        command.Modifiers.RegisterDetector(new FixedBaseDetector((powerField, 10)));

        // 挂载 / 查询 / 跑链 / 读取可用
        await command.Modifiers.AddModifierAsync(new AddModifier(powerField, 5, source));
        Assert.Single(command.Modifiers.All);
        Assert.Equal(powerField, command.Modifiers.All[0].Field);
        Assert.Equal(15, command.Modifiers.GetEffectiveValue(powerField));
        var update = Assert.Single(recorder.Updates);
        Assert.Same(command, update.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { powerField }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 显式跑链稳定（固定点）
        recorder.Clear();
        await command.Modifiers.RequestRerunAsync();
        Assert.Empty(recorder.Updates);
        Assert.Equal(15, command.Modifiers.GetEffectiveValue(powerField));

        // 撤销（按来源）→ 回基准 + 集中触发
        await command.Modifiers.RemoveBySourceAsync(source);
        Assert.Empty(command.Modifiers.All);
        Assert.Equal(10, command.Modifiers.GetEffectiveValue(powerField));
        Assert.Single(recorder.Updates);
    }

    // ---------- 单列 (ix)：组语义（同源多条批量注册 → 按来源全撤；批量＝一次衔接） ----------

    [Fact]
    public async Task Same_Source_Group_Registration_And_Mass_Revoke()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5, defense: 4);
        using var recorder = new UpdateRecorder(engine);
        var source = new object();

        // 同源多条批量注册：恰一次衔接（单条更新、携带本轮全部变化字段）
        await unit.Modifiers.AddModifiersAsync(new Modifier[]
        {
            new AddModifier(CardStatFields.Attack, 2, source),
            new AddModifier(CardStatFields.Defense, 3, source),
        });
        var update = Assert.Single(recorder.Updates);
        Assert.Equal(
            new[] { CardStatFields.Attack, CardStatFields.Defense },
            ModifierTestKit.ChangedFieldsOf(update.Payload));
        Assert.Equal(2, unit.Modifiers.All.Count);
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        // 按来源全撤（组语义）：恰一次衔接；有效值回归基准
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(source);
        var revoke = Assert.Single(recorder.Updates);
        Assert.Equal(
            new[] { CardStatFields.Attack, CardStatFields.Defense },
            ModifierTestKit.ChangedFieldsOf(revoke.Payload));
        Assert.Empty(unit.Modifiers.All);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Equal(4, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }
}
