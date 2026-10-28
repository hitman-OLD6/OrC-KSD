using Orc.Core;
using Orc.Core.Model;

namespace Orc.Cards;

/// <summary>
/// 卡牌事件视图（S4 框架内部载体）：移除类更新（effect.removed / card.destroyed）与放置更新（card.placed）的通用装载视图。
/// 全可选（可从任意载荷绑定）；属性为载荷对象的弱类型引用（Card＝卡牌对象）。
/// </summary>
[ContextView]
public class CardEventView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    /// <summary>
    /// 效果载荷（历史键）：<c>effect.removed</c> 曾携带效果对象引用。
    /// <para>
    /// v4-only 后卡上不再有 <c>Effect</c> 实例，本属性只作为**旧资产视图的兼容读位**保留（恒为 null）——
    /// 组件卸载通知走 <see cref="PayloadKeys.Component"/>／<see cref="PayloadKeys.ComponentInstance"/>。
    /// </para>
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Effect { get; set; }

    /// <summary>
    /// 效果宿主卡（E1 加性可选面）：本触发器所属**效果实例的宿主卡**——由触发器执行链注入
    /// （载荷不含宿主信号，如 <c>card.died</c>，亦可用于取"施动卡"）；未注入＝null（非效果承载的触发器）。
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Host { get; set; }

    /// <summary>
    /// 事件单位（E1-34 加性可选面）：<c>unit.*</c> 系列信号（<c>unit.joined</c>/<c>unit.deployed</c>/<c>unit.position.changed</c>）
    /// 的载荷键是 <c>Unit</c> 而**非** <c>Card</c>；本属性让视图侧也能取到"事件卡"（归属过滤等按卡语义比较用）。
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Unit { get; set; }

    /// <summary>
    /// 事件玩家（E1-39 加性可选面）：载荷**只有玩家、没有事件卡**的信号（如 <c>slot.gained</c>/<c>slot.lost</c>）
    /// 的载荷键是 <c>Player</c>；本属性让视图侧也能取到"事件归属玩家"（归属过滤的玩家面比较用）。
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Player { get; set; }

    /// <summary>
    /// 击杀者（E1-47 加性可选面）：<c>card.died</c> 的载荷键 <c>Killer</c>（可为 null）；
    /// 供"本单位消灭…时"类监听按**归属**过滤（自指守卫读本面）。
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Killer { get; set; }

    /// <summary>
    /// 事件引发者（R9 加性可选面）：信号载荷 <c>Source</c>（<see cref="PayloadKeys.Source"/>）的引发卡；
    /// 只读、可空（无引发者＝null）。与 <see cref="TriggerCard"/>（当前 handler 的触发者）分工不同：
    /// 「敌方弃牌」读本面判敌方。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? Source { get; set; }

    /// <summary>
    /// 当前 handler 的触发者卡牌（R9 加性可选面）：执行链在进入事件前写入的帧值
    /// （卡/效果注册项＝该卡/效果宿主卡；链上最后者胜），无触发者＝null。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }
}

/// <summary>S4 更新与流程载荷键名约定（card.placed / effect.removed / card.destroyed）。</summary>
public static class PayloadKeys
{
    /// <summary>卡牌载荷键："Card"（值＝<see cref="Card"/> 对象引用；销毁类更新携带——对象引用而非 Ref，销毁后仍可读）。</summary>
    public const string Card = "Card";

    /// <summary>效果载荷键："Effect"（历史键；v4-only 后不再携带，保留常量供旧资产读位对齐）。</summary>
    public const string Effect = "Effect";

    /// <summary>
    /// 组件载荷键："Component"（值＝组件预制体 id 字符串；<c>effect.removed</c> 的**组件卸载形态**携带）。
    /// <para>
    /// 泛化口径（b4）：<c>effect.removed</c> 是"某个可卸载物完成了卸载"的完成通知——v4 形态是编排实体上的组件实例（本键），
    /// 旧形态（<see cref="Effect"/> 对象引用）已随卡上效果容器删除。
    /// </para>
    /// </summary>
    public const string Component = "Component";

    /// <summary>组件实例载荷键："ComponentInstance"（值＝组件实体名；<c>effect.removed</c> 的组件卸载形态携带，诊断用）。</summary>
    public const string ComponentInstance = "ComponentInstance";

    /// <summary>宿主载荷键："Host"（值＝组件宿主的承载对象〔卡牌等〕；组件卸载形态携带——回答"谁掉了组件"）。</summary>
    public const string Host = "Host";

    /// <summary>来源载荷键："Source"（攻击/伤害结算流程视图属性名）。</summary>
    public const string Source = "Source";

    /// <summary>目标载荷键："Target"。</summary>
    public const string Target = "Target";

    /// <summary>数量载荷键："Amount"。</summary>
    public const string Amount = "Amount";

    /// <summary>指令载荷键："Order"（值＝<see cref="ICastAction"/> 指令接线面；S5 单指令形态）。</summary>
    public const string Order = "Order";

    /// <summary>指令集载荷键："Orders"（值＝<see cref="ICastAction"/> 列表，登记序；P4 指令流程携带）。</summary>
    public const string Orders = "Orders";
}

/// <summary>装载链留痕与错误记录工具（内部）：写「当前执行者流（无则总流）」，与会话内留痕一致冒泡。</summary>
internal static class CardsLog
{
    internal static void Write(
        LogicEngine engine, string source, string message, LogLevel level, IReadOnlyList<string> keywords)
    {
        var stream = ExecutionFrame.Current?.Stream ?? engine.RootStream;
        stream.WriteLog(source, message, level, keywords);
    }
}

/// <summary>
/// 卡牌装载链处理器（S4；引擎级、懒挂载共享实例）：响应两个驱动更新，完成放置初始化与卡牌登记维护。
/// 「卡牌放置处理器」（card.placed）：数据/状态就绪标记；
/// 「卡牌清理处理器」（card.destroyed）：卡牌登记维护。
/// 两者都是**引擎信号成员上的边**（订阅体＝成员 handler），不再是独立触发器实例。
/// 处理器仅响应载荷定位的卡牌；对非相关载荷幂等无操作。
/// </summary>
internal sealed class CardLoadoutProcessor
{
    internal CardLoadoutProcessor(LogicEngine engine)
    {
        // 两条订阅＝引擎信号成员（engine#card.placed／engine#card.destroyed）上的两条边；
        // 边体是强类型处理器（视图 CardEventView 从同一份载荷绑定）。
        engine.SubscribeMember(
            Updates.CardPlaced,
            "卡牌放置处理器",
            this,
            MemberEdges.For<CardEventView>(OnCardPlaced));

        engine.SubscribeMember(
            Updates.CardDestroyed,
            "卡牌清理处理器",
            this,
            MemberEdges.For<CardEventView>(OnCleanup));
    }

    /// <summary>放置处理：置放置标记（幂等；重复放置不重复留痕）。</summary>
    private Task OnCardPlaced(CardEventView view, Context ctx, CancellationToken ct)
    {
        var card = view.Card as Card;
        if (card is null)
        {
            return Task.CompletedTask; // 载荷无卡牌：无操作（防误用；订阅体异常隔离已保证不抛）
        }

        var wasPlaced = card.IsPlaced;
        card.MarkPlaced();
        if (!wasPlaced)
        {
            CardsLog.Write(
                card.Engine,
                "loadout",
                $"卡牌 '{card.Name}' 放置初始化完成（数据/状态就绪）。",
                LogLevel.Info,
                new[] { "loadout", "init", card.Name });
        }

        return Task.CompletedTask;
    }

    /// <summary>清理处理：card.destroyed 时移除卡牌登记（幂等——一步式与两步〔杀+发更新〕路径均收敛于此）。</summary>
    private Task OnCleanup(CardEventView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is Card destroyed)
        {
            destroyed.Engine.UnregisterCard(destroyed);
        }

        return Task.CompletedTask;
    }
}
