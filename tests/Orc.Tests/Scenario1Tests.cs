using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 场景 1『火球/反制/中断』端到端测试（S5 验收点③）：
/// 火球＝主动效果经引擎级共享指令流程（<see cref="OrderFlow"/>）施放 → 伤害结算流程（直接伤害 → 扣血）；
/// 反制＝被动效果（独立反制卡经放置装载链）经既有注入机制接入 OrderFlow 的具名检查位（Counter band），
/// 判定命中 → <see cref="Context.Interrupt"/> → 施放结算（Resolve）被跳过——伤害不发生。
/// 观测面：(i) 目标状态（HP）不变；(ii) 事件流中断留痕（keywords 含 "interrupt"）；
/// (iii) "被反制"信号（反制效果写入的日志）＋链路结构（无施放链子流）；另覆盖正反路径与条件反制未命中。
/// </summary>
public class Scenario1Tests
{
    [Fact] // 正路径（未被反制）：施放 → 直接伤害 → 扣血；完整链节点结构
    public async Task Scenario1_Fireball_Direct_Damage_And_Full_Chain_Structure()
    {
        var engine = new LogicEngine();
        var mage = new Card(engine, "法师");
        mage.AddData(new HealthData { Hp = 10 });
        var goblin = new Card(engine, "哥布林");
        goblin.AddData(new HealthData { Hp = 10 });

        var fireball = new DamageOrder(engine.DamageFlow, "火球");
        mage.AddEffect(fireball);
        await S4TestHelpers.Place(engine, mage);

        // ── 规范路径：经引擎级共享指令流程（承载检查位）施放 ─────────────────
        var stream = await engine.OrderFlow.ExecuteAsync(mage, fireball, goblin, 6);

        // 伤害：6 直落（直接伤害链，无修正）
        Assert.Equal(4, goblin.GetData<HealthData>().Hp);
        Assert.Equal(1, fireball.CastCount);

        // 未被反制：无中断留痕
        Assert.DoesNotContain(engine.RootStream.Entries, e => e.Keywords.Contains("interrupt"));

        // 完整链节点：指令流程流（顶层挂总流）→ 指令施放链流 → 伤害结算流
        Assert.Same(engine.RootStream, stream.Parent);
        var castChain = stream.Children.Single();
        var damageStream = castChain.Children.Single();
        Assert.Same(stream, castChain.Parent);
        Assert.Same(castChain, damageStream.Parent);

        // 伤害结算流内：结算前先于伤害生效（阶段次序）
        var preIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("damage") && e.Keywords.Contains("preapply") && e.Keywords.Contains("哥布林"));
        var applyIdx = S4TestHelpers.IndexOfEntry(
            engine, e => e.Keywords.Contains("damage") && e.Keywords.Contains("apply") && e.Keywords.Contains("哥布林"));
        Assert.True(preIdx >= 0);
        Assert.True(applyIdx > preIdx);
    }

    [Fact] // 反制路径：被反制 → 中断 → 伤害不发生；移除反制效果后恢复（卸载链撤销注入）
    public async Task Scenario1_Counter_Interrupts_Before_Resolve_Damage_Never_Happens()
    {
        var engine = new LogicEngine();
        var mage = new Card(engine, "法师");
        mage.AddData(new HealthData { Hp = 10 });
        var goblin = new Card(engine, "哥布林");
        goblin.AddData(new HealthData { Hp = 10 });

        var fireball = new DamageOrder(engine.DamageFlow, "火球");
        mage.AddEffect(fireball);
        await S4TestHelpers.Place(engine, mage);

        // 反制者：独立反制卡 → 放置 → 装载 → 注入共享施放检查位（完整链，全走公共机制）
        var counterCard = new Card(engine, "反制塔");
        var counter = new OrderCounterEffect(engine);
        counterCard.AddEffect(counter);
        await S4TestHelpers.Place(engine, counterCard);
        Assert.True(counter.IsMounted);
        Assert.Contains("指令反制/dispose", engine.Bus.GetSubscribers(Updates.CardDestroyed)); // disposing 触发器已挂载（装载链完成）

        var stream = await engine.OrderFlow.ExecuteAsync(mage, fireball, goblin, 6);

        // (i) 伤害不发生：目标 HP 不变；指令方无半完成状态
        Assert.Equal(10, goblin.GetData<HealthData>().Hp);
        Assert.Equal(0, fireball.CastCount);
        Assert.Empty(stream.Children); // 施放结算未执行 → 无施放链子流

        // (ii) 中断留痕：事件流中断记录（由反制检查位内的 Interrupt 写入）
        var interrupt = engine.RootStream.Entries.Single(e => e.Keywords.Contains("interrupt"));
        Assert.Contains("执行被中断", interrupt.Message);
        Assert.StartsWith("指令流程", interrupt.Source);
        Assert.Equal(LogLevel.Warning, interrupt.Level);

        // (iii) 失败可查询："被反制"信号（反制效果写入的日志）＋检查位触达记录
        Assert.Equal(1, counter.CounterChecks);
        Assert.Equal(1, counter.CounterHits);
        Assert.Contains(engine.RootStream.Entries, e => e.Keywords.Contains("counter") && e.Keywords.Contains("rejected"));

        // 无残留：移除反制效果（经既有清理链撤销注入）→ 再施放 → 施放照常发生
        counterCard.RemoveEffect(counter);
        Assert.False(counter.IsMounted);
        Assert.DoesNotContain("指令反制", engine.Bus.GetSubscribers(Updates.EffectRemoved));

        await engine.OrderFlow.ExecuteAsync(mage, fireball, goblin, 6);
        Assert.Equal(4, goblin.GetData<HealthData>().Hp); // 6 伤害直落（无修正）
        Assert.Equal(1, fireball.CastCount);
    }

    [Fact] // 条件反制未命中（加分）：检查位被触达但条件不命中 → 施放照常
    public async Task Scenario1_Conditional_Counter_Miss_Allows_Cast()
    {
        var engine = new LogicEngine();
        var mage = new Card(engine, "法师");
        mage.AddData(new HealthData { Hp = 10 });
        var goblin = new Card(engine, "哥布林");
        goblin.AddData(new HealthData { Hp = 10 });

        var fireball = new DamageOrder(engine.DamageFlow, "火球");
        mage.AddEffect(fireball);
        await S4TestHelpers.Place(engine, mage);

        // 条件反制：仅反制「寒冰箭」指令——本次施放「火球」不命中
        var counterCard = new Card(engine, "反制塔");
        var counter = new OrderCounterEffect(engine, onlyOrderName: "寒冰箭");
        counterCard.AddEffect(counter);
        await S4TestHelpers.Place(engine, counterCard);

        await engine.OrderFlow.ExecuteAsync(mage, fireball, goblin, 6);

        Assert.Equal(1, counter.CounterChecks); // 检查位被触达（接线真实成立）
        Assert.Equal(0, counter.CounterHits);   // 条件未命中
        Assert.DoesNotContain(engine.RootStream.Entries, e => e.Keywords.Contains("interrupt"));
        Assert.Equal(4, goblin.GetData<HealthData>().Hp); // 施放照常：6 伤害直落
        Assert.Equal(1, fireball.CastCount);
    }
}

/// <summary>
/// 指令反制效果（场景 1 fixture）：被动效果——OnMount 中把「反制检查」handler 经 Inject 注入
/// 引擎级共享指令流程（<see cref="OrderFlow"/>）的 Counter band（具名检查位）；
/// 判定命中 → 记录「被反制」信号 + <see cref="Context.Interrupt"/> → 施放结算被跳过（伤害不发生）。
/// 反制条件（仅对特定指令）由 handler 内部判定——装配不预绑具体指令引用，施放信息经执行时视图获得。
/// </summary>
public sealed class OrderCounterEffect : PassiveEffect
{
    private readonly LogicEngine _engine;
    private readonly string? _onlyOrderName;

    /// <summary>检查位被触达次数（接线探针）。</summary>
    public int CounterChecks;

    /// <summary>命中次数（实际发起反制）。</summary>
    public int CounterHits;

    public OrderCounterEffect(LogicEngine engine, string? onlyOrderName = null, string name = "指令反制")
        : base(name)
    {
        _engine = engine;
        _onlyOrderName = onlyOrderName;
    }

    protected override void OnMount()
    {
        Inject(_engine.OrderFlow.Trigger, "反制检查", OrderFlowBands.Counter, OnCounterCheck);
    }

    private Task OnCounterCheck(OrderFlowView view, Context ctx, CancellationToken ct)
    {
        CounterChecks++;

        if (_onlyOrderName is not null && view.Order.Name != _onlyOrderName)
        {
            return Task.CompletedTask; // 条件未命中（反制哪些指令＝handler 内部自由）
        }

        CounterHits++;
        _engine.RootStream.WriteLog(
            "测试效果/指令反制",
            $"反制 '{view.Order.Name}'（目标 '{view.Target.Name}'）：施放被拒绝。",
            LogLevel.Warning,
            new[] { "counter", "rejected", view.Order.Name, view.Target.Name });
        ctx.Interrupt();
        return Task.CompletedTask;
    }
}
