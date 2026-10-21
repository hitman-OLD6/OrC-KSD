using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 卡牌事件视图（S4 框架内部载体）：移除类更新（effect.removed / card.destroyed）与放置更新（card.placed）的通用装载视图。
/// 全可选（可从任意载荷绑定）；属性为载荷对象的弱类型引用（Card＝卡牌对象、Effect＝效果对象）。
/// </summary>
[ContextView]
public class CardEventView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    [Optional]
    [Read]
    public virtual object? Effect { get; set; }

    /// <summary>
    /// 效果宿主卡（E1 加性可选面）：本触发器所属**效果实例的宿主卡**——由触发器执行链在
    /// 所有者是带宿主的效果时注入（载荷不含宿主信号，如 <c>card.died</c>，亦可用于取"施动卡"）；
    /// 未注入＝null（非效果承载的触发器）。
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

/// <summary>S4 更新与流程载荷键名约定（card.placed / effect.removed / card.destroyed；攻击/伤害结算流程视图属性同名）。</summary>
public static class PayloadKeys
{
    /// <summary>卡牌载荷键："Card"（值＝<see cref="Card"/> 对象引用；销毁类更新携带——对象引用而非 Ref，销毁后仍可读）。</summary>
    public const string Card = "Card";

    /// <summary>效果载荷键："Effect"（值＝<see cref="Effect"/> 对象引用；effect.removed 携带）。</summary>
    public const string Effect = "Effect";

    /// <summary>来源载荷键："Source"（攻击/伤害结算流程视图属性名）。</summary>
    public const string Source = "Source";

    /// <summary>目标载荷键："Target"。</summary>
    public const string Target = "Target";

    /// <summary>数量载荷键："Amount"。</summary>
    public const string Amount = "Amount";

    /// <summary>指令载荷键："Order"（值＝<see cref="ICastAction"/> 指令接线面；S5 单指令形态，保留常量供旧数据键读取）。</summary>
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
/// 卡牌装载链（S4；框架模板骨架）：放置驱动装载（幂等）与销毁收敛点。
/// 装载链＝【挂 disposing → 挂主触发器 → OnMount → 装载完成动作（装载管线扩展点；如游戏层托管登记）】——
/// 任何装载入口（加载时点/放置驱动/Add 即时/词条内嵌）统一经此、复装自动重建（W3-A3）。
/// 销毁唯一入口＝disposing 触发器（E-D1；由 RemoveEffect / effect.removed 兜底 / card.destroyed 默认兜底共同抵达），
/// 内部幂等——重复调用收敛为无操作，行为终态一致（进入即停用、注入撤销、disposing 事件链、最终 teardown、容器移除）；
/// 成功事务的实际移除命中恰发射一次 effect.removed（静默路径除外——未成功事务回滚与「源头即信号」的更新驱动）。
/// </summary>
internal static class CardLoadout
{
    /// <summary>
    /// 装载一个被动效果：主触发器挂载至总线 → 执行 OnMount（注入）→ 执行「装载完成动作」
    /// （装载管线扩展点，如游戏层托管登记——任何装载入口统一生效、复装自动重建；可缺省）。
    /// 幂等（已装载＝跳过）。失败（OnMount 抛 / 装载完成动作抛）：记录进事件流（source 含效果标识与钩子名/动作名）、
    /// 回滚装载（撤销注入、卸载主触发器、清除卸载清理登记残留——效果视为未生效、「不登记残留」）、不阻断其它效果与宿主。
    /// </summary>
    /// <returns>true＝本次完成装载；false＝跳过（已装载）或失败（已记录并回滚）。</returns>
    /// <param name="mountMainTriggers">false＝只挂 disposing、跳过主触发器（宿主自持调用面——反制卡效果装配）；默认 true。</param>
    internal static bool MountEffect(Card card, Effect effect, bool mountMainTriggers = true)
    {
        effect.ValidateForUse();
        effect.MountDisposal(card.Engine.Bus); // 主动/被动统一：disposing 触发器先挂（幂等）

        if (effect.IsMounted)
        {
            return false; // 幂等：重复放置/重复 Add 不重复装载
        }

        try
        {
            effect.ExecuteMount(card.Engine.Bus, mountMainTriggers);
        }
        catch (Exception ex)
        {
            effect.RollbackMount();
            CardsLog.Write(
                card.Engine,
                $"{effect.Name}/OnMount",
                ex.Message,
                LogLevel.Error,
                new[] { "loadout", "error", $"exception:{ex.GetType().Name}", effect.Name });
            return false;
        }

        try
        {
            card.Engine.RunCardMountCompletedActions(card, effect);
        }
        catch (Exception ex)
        {
            // 「装载成功」的定义包含「自动动作完成」——动作异常归入装载失败口径：
            // 记录＋回滚为未生效＋不登记残留（回滚清除本次装载期间的全部卸载清理登记）。
            effect.RollbackMount();
            CardsLog.Write(
                card.Engine,
                $"{effect.Name}/MountCompleted",
                ex.Message,
                LogLevel.Error,
                new[] { "loadout", "error", $"exception:{ex.GetType().Name}", effect.Name });
            return false;
        }

        CardsLog.Write(
            card.Engine,
            "loadout",
            $"效果 '{effect.Name}' 已装载（注入完成）。",
            LogLevel.Info,
            new[] { "loadout", "mount", card.Name, effect.Name });
        return true;
    }

    /// <summary>
    /// 销毁入口（唯一；E-D1）：对「确属该卡」的效果调用 <see cref="Effect.ExecuteDispose"/>——
    /// 进入即停用 → disposing 事件链 → 最终 teardown → 容器移除 → 恰发一次 effect.removed。
    /// 归属校验：效果不在该卡列表且宿主引用不指向该卡 → 无操作返回 false（防跨卡误清；幂等）。
    /// </summary>
    /// <returns>true＝执行了销毁；false＝无操作（幂等/非本卡效果）。</returns>
    internal static bool DisposeEffect(
        Card card, Effect effect, DisposalSource source = DisposalSource.Early, bool emitRemoved = true)
    {
        var contains = card.ContainsEffect(effect);
        var hostMatch = ReferenceEquals(effect.HostOrNull, card);
        if (!contains && !hostMatch)
        {
            return false; // 非本卡效果/已清理：幂等无操作
        }

        return effect.ExecuteDispose(card, emitRemoved, source);
    }
}

/// <summary>
/// 卡牌装载链处理器（S4；引擎级、懒挂载共享实例）：响应三个驱动更新，完成装载/清理编排。
/// 「卡牌放置处理器」（card.placed）：单位初始化（数据/状态就绪）先、效果注入后（Effects 列表序、幂等）；
/// 「卡牌清理处理器」（effect.removed / card.destroyed）：卡牌登记维护＋旧信号幂等兜底（效果销毁由各 disposing 触发器承载）。
/// 处理器仅响应载荷定位的卡牌/效果；对非相关载荷幂等无操作。
/// </summary>
internal sealed class CardLoadoutProcessor
{
    private readonly Trigger<CardEventView> _placedHandler;
    private readonly Trigger<CardEventView> _cleanupHandler;

    /// <summary>「放置处理」触发器（内置事件：放置处理；供 moding（逻辑替换）等经句柄寻址）。</summary>
    internal Trigger<CardEventView> PlacedTrigger => _placedHandler;

    /// <summary>「清理处理」触发器（内置事件：清理处理；供 moding（逻辑替换）等经句柄寻址）。</summary>
    internal Trigger<CardEventView> CleanupTrigger => _cleanupHandler;

    internal CardLoadoutProcessor(LogicEngine engine)
    {
        _placedHandler = new Trigger<CardEventView>(
            "卡牌放置处理器",
            TriggerKind.Passive,
            events: new[]
            {
                new TriggerEvent<CardEventView>("放置处理", OnCardPlaced),
            },
            hooks: new[] { Updates.CardPlaced });

        _cleanupHandler = new Trigger<CardEventView>(
            "卡牌清理处理器",
            TriggerKind.Passive,
            events: new[]
            {
                new TriggerEvent<CardEventView>("清理处理", OnCleanup),
            },
            hooks: new[] { Updates.EffectRemoved, Updates.CardDestroyed });

        engine.Bus.Mount(_placedHandler);
        engine.Bus.Mount(_cleanupHandler);
    }

    /// <summary>放置处理：初始化（幂等；先）→ 逐效果装载（Effects 列表序；被动且未装载者）；重复放置＝幂等（不重复装载/不重复 OnMount）。</summary>
    private Task OnCardPlaced(CardEventView view, Context ctx, CancellationToken ct)
    {
        var card = view.Card as Card;
        if (card is null)
        {
            return Task.CompletedTask; // 载荷无卡牌：无操作（防误用；总线隔离已保证不抛）
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

        var mountedNow = 0;
        foreach (var effect in card.EffectsSnapshot())
        {
            if (effect.Kind != TriggerKind.Passive)
            {
                continue; // 主动效果不装载（等待施放）
            }

            if (CardLoadout.MountEffect(card, effect))
            {
                mountedNow++;
            }
        }

        if (wasPlaced && mountedNow == 0)
        {
            CardsLog.Write(
                card.Engine,
                "loadout",
                $"卡牌 '{card.Name}' 重复放置（幂等：不重复装载）。",
                LogLevel.Debug,
                new[] { "loadout", "skip", card.Name });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 清理处理（E-D1 收敛）：
    /// ①effect.removed（定位单个效果）：旧路径/外部直接发射的幂等兜底——已进入 Disposing/Disposed 时静默跳过，
    ///   仍 Mounted 时调用 disposing（源头即信号：不重复发射）；未装载残留＝仅容器面移除。
    /// ②card.destroyed：只维护卡牌登记（<see cref="LogicEngine.UnregisterCard"/>）；
    ///   各效果的销毁由各自 disposing 触发器的默认兜底 hook 自行响应（销毁单一路径，不再由处理器遍历）。
    /// </summary>
    private Task OnCleanup(CardEventView view, Context ctx, CancellationToken ct)
    {
        var effect = view.Effect as Effect;
        if (effect is not null)
        {
            var card = view.Card as Card;
            if (card is null)
            {
                return Task.CompletedTask; // 载荷契约：effect.removed 携带卡牌＋效果；无卡牌＝无操作
            }

            if (effect.DisposalState is DisposalState.Disposing or DisposalState.Disposed)
            {
                return Task.CompletedTask; // 正常销毁的完成通知：静默跳过（防自指/重复留痕）
            }

            CardLoadout.DisposeEffect(card, effect, DisposalSource.Signal, emitRemoved: false);
            return Task.CompletedTask;
        }

        var destroyed = view.Card as Card;
        if (destroyed is null)
        {
            return Task.CompletedTask;
        }

        // S5：销毁清理响应时移除卡牌登记（一步式与两步（杀+发更新）路径均收敛于此；幂等）。
        // 效果销毁交给各效果的 disposing 触发器（默认卡销毁兜底 hook）。
        destroyed.Engine.UnregisterCard(destroyed);
        return Task.CompletedTask;
    }
}
