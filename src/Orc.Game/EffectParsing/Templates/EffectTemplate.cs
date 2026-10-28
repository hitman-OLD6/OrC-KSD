using Orc.Core;

namespace Orc.Game.EffectParsing.Templates;

// ─────────────────────────────────────────────────────────────────────────────
// 模板效果（专用格式，转换段 S2）：
//   为模板效果设计的专用预制体格式（**不依赖**实际/原生预制体），与
//   Orc.Cards.EffectSnapshot 的 root **同构子集**（mainTrigger/otherTriggers/modings/injects/events），
//   并**额外**用 slots 表声明"哪些 handler 可被 DSL 填写"（因一个效果可有多个 handler）。
//   槽位以**语义槽位名 + 事件 id 引用**（`mainTrigger.events[e1]`）表达；DSL 只认槽位名。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>模板效果：预制体骨架（同构子集）＋ 可填写槽位声明。</summary>
public sealed class EffectTemplate
{
    /// <summary>模板效果 schema 版本（本版＝1）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>创建模板效果（构造期 fail-fast）。</summary>
    /// <exception cref="ArgumentException">id 空白；slots 含空白名/目标。</exception>
    /// <exception cref="ArgumentNullException">root 或 slots 或其元素为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">schemaVersion ≤ 0。</exception>
    public EffectTemplate(
        int schemaVersion,
        string id,
        IReadOnlyList<EffectTemplateSlot> slots,
        EffectTemplateRoot root,
        ActorFrom actorFrom = ActorFrom.EventCard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(root);

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "模板效果 schema 版本须为正整数。");
        }

        var slotList = new List<EffectTemplateSlot>();
        foreach (var slot in slots)
        {
            ArgumentNullException.ThrowIfNull(slot);
            ArgumentException.ThrowIfNullOrWhiteSpace(slot.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(slot.Target);
            slotList.Add(slot);
        }

        SchemaVersion = schemaVersion;
        Id = id;
        Slots = slotList;
        Root = root;
        ActorFrom = actorFrom;
    }

    /// <summary>施动卡来源（模板显式声明；D-甲）。</summary>
    public ActorFrom ActorFrom { get; }

    /// <summary>schema 版本。</summary>
    public int SchemaVersion { get; }

    /// <summary>模板 id（DSL 的 <c>template</c> 引用它）。</summary>
    public string Id { get; }

    /// <summary>可填写槽位声明（声明序）。</summary>
    public IReadOnlyList<EffectTemplateSlot> Slots { get; }

    /// <summary>预制体骨架（同构子集）。</summary>
    public EffectTemplateRoot Root { get; }
}

/// <summary>槽位声明：语义名 + 事件定位（事件 id 引用，如 <c>mainTrigger.events[e1]</c>）。</summary>
/// <param name="Name">槽位名（DSL 只认它）。</param>
/// <param name="Target">事件定位表达式。</param>
public sealed record EffectTemplateSlot(string Name, string Target);

/// <summary>
/// 施动卡来源（模板显式声明；D-甲）：handler 内 op 作用到的"施动卡"怎么取。
/// <list type="bullet">
///   <item><see cref="EventCard"/>＝载荷卡（<c>view.Card</c>）。部署/主动型正确（载荷卡即宿主）。</item>
///   <item><see cref="EffectHost"/>＝效果宿主卡（经 <c>view.Effect</c> 取 <c>Effect.Host</c>）。监听型需要。</item>
/// </list>
/// </summary>
public enum ActorFrom
{
    /// <summary>载荷卡（<c>view.Card</c>）——默认。</summary>
    EventCard,

    /// <summary>效果宿主卡（<c>view.Effect</c> → <c>Effect.Host</c>）。</summary>
    EffectHost,
}

/// <summary>模板骨架根（对齐 <c>Orc.Cards.EffectPrefab</c>）。</summary>
public sealed class EffectTemplateRoot
{
    /// <summary>创建模板骨架根。</summary>
    /// <exception cref="ArgumentNullException">mainTrigger 为 null；集合含 null。</exception>
    public EffectTemplateRoot(
        EffectTemplateTrigger mainTrigger,
        IEnumerable<EffectTemplateTrigger>? otherTriggers = null,
        IEnumerable<EffectTemplateModing>? modings = null,
        IEnumerable<EffectTemplateInject>? injects = null)
    {
        ArgumentNullException.ThrowIfNull(mainTrigger);

        MainTrigger = mainTrigger;
        OtherTriggers = Copy(otherTriggers, nameof(otherTriggers));
        Modings = Copy(modings, nameof(modings));
        Injects = Copy(injects, nameof(injects));
    }

    /// <summary>主触发器。</summary>
    public EffectTemplateTrigger MainTrigger { get; }

    /// <summary>其它触发器（声明序）。</summary>
    public IReadOnlyList<EffectTemplateTrigger> OtherTriggers { get; }

    /// <summary>moding 声明（声明序）。</summary>
    public IReadOnlyList<EffectTemplateModing> Modings { get; }

    /// <summary>注入声明（声明序）。</summary>
    public IReadOnlyList<EffectTemplateInject> Injects { get; }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? items, string name) where T : class
    {
        var list = new List<T>();
        if (items is null)
        {
            return list;
        }

        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentNullException(name, "集合不能包含 null 元素。");
            }

            list.Add(item);
        }

        return list;
    }
}

/// <summary>模板触发器（对齐 <c>Orc.Cards.TriggerPrefab</c>，事件不含槽位内容）。</summary>
public sealed class EffectTemplateTrigger
{
    /// <summary>创建模板触发器（构造期 fail-fast）。</summary>
    /// <exception cref="ArgumentException">id/stableKey 空白；主动触发器声明了 hooks。</exception>
    public EffectTemplateTrigger(
        string id,
        string stableKey,
        bool isPassive = false,
        IEnumerable<string>? hooks = null,
        IEnumerable<EffectTemplateEvent>? events = null,
        string viewTypeName = "Orc.Cards.CardEventView")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewTypeName);

        var hookList = new List<string>();
        if (hooks is not null)
        {
            foreach (var hook in hooks)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(hook);
                if (!hookList.Contains(hook))
                {
                    hookList.Add(hook);
                }
            }
        }

        if (hookList.Count > 0 && !isPassive)
        {
            throw new ArgumentException("主动触发器预制体不能声明 hooks。", nameof(hooks));
        }

        var eventList = new List<EffectTemplateEvent>();
        if (events is not null)
        {
            foreach (var item in events)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(events), "事件集合不能包含 null 元素。");
                }

                eventList.Add(item);
            }
        }

        Id = id;
        StableKey = stableKey;
        IsPassive = isPassive;
        Hooks = hookList;
        Events = eventList;
        ViewTypeName = viewTypeName;
    }

    /// <summary>触发器 id。</summary>
    public string Id { get; }

    /// <summary>稳定键。</summary>
    public string StableKey { get; }

    /// <summary>是否被动（被动＝可挂到信号上）。</summary>
    public bool IsPassive { get; }

    /// <summary>hook 名（声明序）。</summary>
    public IReadOnlyList<string> Hooks { get; }

    /// <summary>事件集合（声明序）。</summary>
    public IReadOnlyList<EffectTemplateEvent> Events { get; }

    /// <summary>视图类型名。</summary>
    public string ViewTypeName { get; }
}

/// <summary>
/// 模板事件：<see cref="Csx"/> 为**固定** handler 源码（非槽位事件必须有）；
/// 被槽位指认的事件**不得**已有 <see cref="Csx"/>（其内容由 DSL 渲染填入）。
/// </summary>
public sealed class EffectTemplateEvent
{
    /// <summary>创建模板事件。</summary>
    /// <exception cref="ArgumentException">id/entry 空白。</exception>
    public EffectTemplateEvent(string id, string entry = "HandleAsync", string? csx = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        Id = id;
        Entry = entry;
        Csx = csx;
    }

    /// <summary>事件 id。</summary>
    public string Id { get; }

    /// <summary>入口名。</summary>
    public string Entry { get; }

    /// <summary>固定 csx 源码（可空＝待槽位填写）。</summary>
    public string? Csx { get; }
}

/// <summary>模板 moding 声明（对齐 <c>Orc.Cards.ModingPrefab</c>）。</summary>
public sealed record EffectTemplateModing(string TargetEventId, EffectTemplateEvent Replacement);

/// <summary>模板注入声明（对齐 <c>Orc.Cards.InjectPrefab</c>）。</summary>
public sealed record EffectTemplateInject(string TargetTriggerName, string? BandName, string EventId, int Priority = 0);
