using Orc.Core;
using Orc.Game.Cards;
using Orc.Output;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W2a G3 修饰机制核心——链与更新检测管线机制级测试（验收点②链全量重跑确定性、③缓存比较与集中触发、④检测接口输出本体）。
/// 补充用例：单列 (i) 零负担（无修饰卡三口径）、(ii) 未就绪操作不得静默、(iii) 固定点检验（稳定后不再触发）。
/// </summary>
public class StatUpdatePipelineTests
{
    // ---------- 场景②：链全量重跑确定性（含基准变化随动——显式请求统一入口） ----------

    [Fact]
    public async Task Scenario_Full_Rerun_Is_Deterministic_And_Follows_Base_Changes()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);

        // 全量重跑＝从基准出发、按挂载序确定变换：5 + 2 + 3 = 10
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, new object()));
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 3, new object()));
        Assert.Equal(10, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 确定性：同一输入序列重复跑链 → 输出不变、零发射
        recorder.Clear();
        await unit.Modifiers.RequestRerunAsync();
        Assert.Equal(10, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.Empty(recorder.Updates);

        // 链起点变化（W2b：受控变更——防御损伤经门户〔变更→跑链→有变更集中触发〕）→ 全量重跑 → 有效值随动（新起点 + 修饰）
        recorder.Clear();
        await unit.ApplyDefenseDamageAsync(2);
        Assert.Equal(2, unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // (4 − 2) + 0（防御无修饰）
        Assert.Single(ModifierTestKit.StatChangedUpdates(recorder)); // E1-33：另有一条 card.damaged
        Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDamaged);

        // 再次稳定：零发射（显式请求对稳定态＝幂等）
        recorder.Clear();
        await unit.Modifiers.RequestRerunAsync();
        Assert.Empty(recorder.Updates);
    }

    // ---------- 场景③：缓存比较与集中触发（有变更才发、无变更止） ----------

    [Fact]
    public async Task Scenario_Cache_Comparison_And_Concentrated_Trigger()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);
        var source = new object();

        // +0 修饰：链应用结果＝基准 → 无变化 → 零发射；读取＝基准
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 0, source));
        Assert.Empty(recorder.Updates);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // +2：有变化 → 恰一次集中触发（单条更新、含目标卡与字段集合）
        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, source));
        var update = Assert.Single(recorder.Updates);
        Assert.Equal(GameUpdates.CardStatChanged, update.Type);
        Assert.Same(unit, update.Payload![GameUpdates.PayloadCard]);
        Assert.Equal(new[] { CardStatFields.Attack }, ModifierTestKit.ChangedFieldsOf(update.Payload));

        // 固定点：稳定后再请求 → 零发射、状态不变（缓存不替换、无副作用）
        recorder.Clear();
        await unit.Modifiers.RequestRerunAsync();
        Assert.Empty(recorder.Updates);
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 撤销：净变化 → 集中触发；再请求 → 零发射
        recorder.Clear();
        await unit.Modifiers.RemoveBySourceAsync(source);
        Assert.Single(recorder.Updates);
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        recorder.Clear();
        await unit.Modifiers.RequestRerunAsync();
        Assert.Empty(recorder.Updates);
    }

    // ---------- 场景④：检测接口输出本体（(i)-(iv) 最低断言域） ----------

    [Fact]
    public void Scenario_Detection_Interface_Output_Body()
    {
        // 以合规实现（固定基准检测器）直接验证接口本体
        var detector = new FixedBaseDetector(("字段甲", 10), ("字段乙", 20));
        var values = new Dictionary<string, int>(StringComparer.Ordinal) { ["字段甲"] = 12, ["字段乙"] = 20 };

        // (i) 同态重复生成稳定（确定性）
        var snapshot1 = detector.GenerateSnapshot(values);
        var snapshot2 = detector.GenerateSnapshot(values);
        Assert.Equal(snapshot1["字段甲"], snapshot2["字段甲"]);
        Assert.Equal(snapshot1["字段乙"], snapshot2["字段乙"]);

        // (ii) 无变化时比较＝无变更＋空差异
        var same = detector.Compare(snapshot2, snapshot1);
        Assert.False(same.HasChanges);
        Assert.Empty(same.ChangedFields);

        // (iii) 有变化时＝有变更＋对应字段差异（方向：旧→新）
        var grown = detector.GenerateSnapshot(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["字段甲"] = 15, ["字段乙"] = 20 });
        var diff = detector.Compare(grown, snapshot1);
        Assert.True(diff.HasChanges);
        Assert.Equal(new[] { "字段甲" }, diff.ChangedFields);

        // (iv) 生成/比较无副作用（不影响后续轮——入参未被修改、结果可重复）
        Assert.Equal(12, values["字段甲"]);
        var repeat = detector.Compare(detector.GenerateSnapshot(values), grown);
        Assert.True(repeat.HasChanges);
        Assert.Equal(new[] { "字段甲" }, repeat.ChangedFields);
        Assert.Equal(new[] { "字段甲" }, detector.Compare(grown, snapshot1).ChangedFields);
    }

    // ---------- 单列 (i)：零负担（无修饰卡三口径） ----------

    [Fact]
    public async Task Zero_Burden_For_Unmodified_Cards()
    {
        var engine = new LogicEngine();
        using var recorder = new UpdateRecorder(engine);
        var unit = ModifierTestKit.CreateReadyUnit(engine);

        // ① 不发任何更新（含 card.stat.changed）
        Assert.Empty(recorder.Types);
        // ② 查询返回空集合
        Assert.Empty(unit.Modifiers.All);
        // ③ 读取＝基准值（纯读）；修饰器容器不纳入快照导出（不破坏既有快照输出）
        Assert.Equal(5, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.DoesNotContain("Modifier", SnapshotJson.Serialize(unit));

        // 显式空跑：就绪域存在、无修饰 → 零发射
        await unit.Modifiers.RequestRerunAsync();
        Assert.Empty(recorder.Types);
    }

    // ---------- 单列 (ii)：未就绪操作不得静默（挂载/跑链/读取——明确错误） ----------

    [Fact]
    public async Task Unready_Operations_Fail_Fast_Instead_Of_Silent()
    {
        var engine = new LogicEngine();
        var raw = ModifierTestKit.CreateBareUnit(engine); // 未单位化（无 UnitStateData）
        using var recorder = new UpdateRecorder(engine);
        var source = new object();

        // 变更类操作（挂载）：基准来源未就绪 → 明确错误（fail-fast、不静默）
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => raw.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, source)));
        // 计算动作（跑链）：W3-2 随改——部署费域全类别常驻就绪（「无就绪组件」形态不再可达）：
        // 跑链经部署费域完成、无修饰零发射；未就绪字段（Attack）的变更/读取仍明确错误（不静默）。
        await raw.Modifiers.RequestRerunAsync();
        // 读取：未就绪 → 明确错误（不静默返回错值）
        Assert.Throws<InvalidOperationException>(() => raw.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 指令卡：无单位数据组件 → 同字段域挂载同样明确错误；未知字段域亦明确错误
        var command = ModifierTestKit.CreateOrderCard(engine);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, new object())));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.Modifiers.AddModifierAsync(new AddModifier("NoSuchField", 2, new object())));

        // 全程零发射（未就绪不得静默——更不得发幽灵更新）
        Assert.Empty(recorder.Types);
    }

    // ---------- 单列 (iii)：固定点检验（稳定后不再触发） ----------

    [Fact]
    public async Task Fixed_Point_Reruns_Are_Silent()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        using var recorder = new UpdateRecorder(engine);

        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, new object()));
        Assert.Single(recorder.Updates); // 首轮有变

        recorder.Clear();
        for (var i = 0; i < 3; i++)
        {
            await unit.Modifiers.RequestRerunAsync(); // 稳定后连续跑链 → 后续零发射、状态不变
        }

        Assert.Empty(recorder.Updates);
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
    }

    // ---------- 顺序契约：先落定（快照/有效值）、后发射（订阅回调内读到新值；发射＝管线末步） ----------

    [Fact]
    public async Task Emission_Order_Contract_State_Lands_Before_Trigger()
    {
        var engine = new LogicEngine();
        var unit = ModifierTestKit.CreateReadyUnit(engine, attack: 5);
        var valueSeenInCallback = -1;
        using var subscription = engine.Subscribe((type, payload, ct) =>
        {
            if (type == GameUpdates.CardStatChanged)
            {
                valueSeenInCallback = unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);
            }

            return Task.CompletedTask;
        });

        await unit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, new object()));

        // 发射时效应已落定：回调内读到的是新有效值（7），且读取为纯读
        Assert.Equal(7, valueSeenInCallback);
        Assert.Equal(7, unit.Modifiers.GetEffectiveValue(CardStatFields.Attack));
    }
}
