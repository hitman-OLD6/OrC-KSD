using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// R9 验收（内核）：触发者来源透传——卡/效果注册项改写帧值、非卡注册项继承、
/// 同帧多事件后者胜、顶层空且不残留、入口载荷初值、审计面（Registrant/SourceCard）、
/// 宿主销毁后降级、错形态必抛（入口载荷 / 视图 getter）。
/// </summary>
public class TriggerSourceTests
{
    [Fact]
    public async Task CardRegistered_Handler_Overrides_TriggerCard()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "引发者");
        var trigger = new Trigger<SourceProbeView>("探针");
        Card? fromView = null;
        Card? fromContext = null;
        trigger.Register(
            "卡注册",
            (view, ctx, _) =>
            {
                fromView = view.TriggerCard;
                fromContext = ctx.TriggerCard;
                return Task.CompletedTask;
            },
            registrant: card);

        var stream = await trigger.InvokeAsync(engine);

        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome);
        Assert.Same(card, fromView);
        Assert.Same(card, fromContext); // 视图面与 ctx 读取面同值（同一帧）
    }

    [Fact]
    public async Task NonCardRegistered_Handler_Inherits_TriggerCard()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "载荷引发者");
        var trigger = new Trigger<SourceProbeView>("探针");
        Card? seen = null;
        trigger.Register(
            "非卡注册",
            (view, _, _) =>
            {
                seen = view.TriggerCard;
                return Task.CompletedTask;
            },
            registrant: new object()); // 非卡非效果 ⇒ 归因降级 null ⇒ 继承入口初值

        await trigger.InvokeAsync(
            engine, new Dictionary<string, object?> { [TriggerPayloadKeys.TriggerCard] = card });

        Assert.Same(card, seen);
    }

    [Fact]
    public async Task LastCardRegistered_Handler_Wins_On_Shared_Trigger()
    {
        var engine = new LogicEngine();
        var first = new Card(engine, "甲");
        var second = new Card(engine, "乙");
        var trigger = new Trigger<SourceProbeView>("共享");
        var seen = new List<Card?>();
        trigger.Register("甲注册", (view, _, _) => { seen.Add(view.TriggerCard); return Task.CompletedTask; }, registrant: first);
        trigger.Register("乙注册", (view, _, _) => { seen.Add(view.TriggerCard); return Task.CompletedTask; }, registrant: second);

        await trigger.InvokeAsync(engine);

        Assert.Equal(2, seen.Count);
        Assert.Same(first, seen[0]);
        Assert.Same(second, seen[1]); // 链上最后者胜
    }

    [Fact]
    public async Task TopLevel_Player_Action_Leaves_TriggerCard_Null_And_Does_Not_Leak()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "前次引发者");
        var trigger = new Trigger<SourceProbeView>("探针");
        var seen = new List<Card?>();
        trigger.Register("读取", (view, _, _) => { seen.Add(view.TriggerCard); return Task.CompletedTask; });

        // 第一轮：效果引发（入口初值＝卡）
        await trigger.InvokeAsync(
            engine, new Dictionary<string, object?> { [TriggerPayloadKeys.TriggerCard] = card });
        // 第二轮：顶层玩家操作（不携带）——不得残留上一轮值
        await trigger.InvokeAsync(engine);

        Assert.Same(card, seen[0]);
        Assert.Null(seen[1]);
    }

    [Fact]
    public async Task Explicit_Payload_TriggerCard_Seeds_Initial_Value()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "入口初值");
        var child = new Trigger<SourceProbeView>("子");
        Card? childSeen = null;
        child.Register("子读取", (view, _, _) => { childSeen = view.TriggerCard; return Task.CompletedTask; });

        var parent = new Trigger<SourceProbeView>("父");
        parent.Register(
            "嵌套调用",
            async (_, _, ct) => await child.InvokeAsync(engine, ct: ct));

        await parent.InvokeAsync(
            engine, new Dictionary<string, object?> { [TriggerPayloadKeys.TriggerCard] = card });

        Assert.Same(card, childSeen); // 嵌套无初值时继承父帧
    }

    [Fact]
    public async Task Audit_Exposes_Registrant_And_SourceCard()
    {
        var engine = new LogicEngine();
        var host = new Card(engine, "效果宿主");
        var effect = new AuditProbeEffect();
        host.AddEffect(effect);

        var cardTrigger = new Trigger<SourceProbeView>("卡触发器", registrant: host);
        cardTrigger.Register("卡注册", (_, _, _) => Task.CompletedTask);
        var cardInfo = Assert.Single(cardTrigger.Events);
        Assert.Same(host, cardInfo.Registrant);
        Assert.Same(host, cardInfo.SourceCard);

        var effectTrigger = new Trigger<SourceProbeView>("效果触发器", registrant: effect);
        effectTrigger.Register("效果注册", (_, _, _) => Task.CompletedTask);
        var effectInfo = Assert.Single(effectTrigger.Events);
        Assert.Same(effect, effectInfo.Registrant);
        Assert.Same(host, effectInfo.SourceCard); // 效果 ⇒ 宿主卡

        var anonymous = new Trigger<SourceProbeView>("无主触发器");
        anonymous.Register("无主", (_, _, _) => Task.CompletedTask);
        var anonymousInfo = Assert.Single(anonymous.Events);
        Assert.Null(anonymousInfo.Registrant);
        Assert.Null(anonymousInfo.SourceCard); // 非卡非效果 ⇒ null
    }

    [Fact]
    public async Task SourceCard_Degrades_To_Null_After_Host_Destroyed()
    {
        var engine = new LogicEngine();
        var host = new Card(engine, "将销毁宿主");
        var effect = new AuditProbeEffect();
        host.AddEffect(effect);
        var trigger = new Trigger<SourceProbeView>("效果触发器", registrant: effect);
        trigger.Register("注册", (_, _, _) => Task.CompletedTask);

        Assert.Same(host, Assert.Single(trigger.Events).SourceCard);

        await engine.DestroyCard(host);

        Assert.Null(Assert.Single(trigger.Events).SourceCard); // 降级不抛
    }

    [Fact]
    public async Task Payload_With_Ref_Under_TriggerCard_Throws()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "错形态");
        var trigger = new Trigger<SourceProbeView>("探针");
        trigger.Register("不应执行", (_, _, _) => Task.CompletedTask);

        var data = new Dictionary<string, object?> { [TriggerPayloadKeys.TriggerCard] = card.Ref };

        var ex = await Assert.ThrowsAsync<InvalidCastException>(() => trigger.InvokeAsync(engine, data));
        Assert.Contains("SourceNormalizer", ex.Message);
    }

    [Fact]
    public async Task View_With_Mismatched_Data_Throws()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "数据形态与声明不符");
        var trigger = new Trigger<MismatchedTriggerCardView>("错形态视图");
        InvalidCastException? caught = null;
        trigger.Register(
            "读取",
            (view, _, _) =>
            {
                try
                {
                    _ = view.TriggerCard; // 声明 Ref<Entity>?，数据面为 Card ⇒ Unbox_Any 必抛
                }
                catch (InvalidCastException ex)
                {
                    caught = ex;
                }

                return Task.CompletedTask;
            });

        await trigger.InvokeAsync(
            engine, new Dictionary<string, object?> { [TriggerPayloadKeys.TriggerCard] = card });

        Assert.NotNull(caught);
    }

    // ---------- 探针视图/效果 ----------

    /// <summary>来源探针视图（R9 两面之一；全可选）。</summary>
    [ContextView]
    public class SourceProbeView
    {
        [Optional]
        [Read]
        public virtual Card? TriggerCard { get; set; }
    }

    /// <summary>故意声明为 Ref 的错形态视图（数据面写 Card 时读取必抛）。</summary>
    [ContextView]
    public class MismatchedTriggerCardView
    {
        [Optional]
        [Read]
        public virtual Ref<Entity>? TriggerCard { get; set; }
    }

    /// <summary>最小被动效果（无业务主触发器；仅用于宿主生命周期与归因面）。</summary>
    private sealed class AuditProbeEffect : PassiveEffect
    {
        public AuditProbeEffect()
            : base("审计探针")
        {
        }
    }
}
