using Orc.Core;
using Orc.Core.Model;

namespace Orc.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 效果快照 DTO（原 `Prefabs.cs` 的数据面）：
//   事件预制体（handler 声明；来源＝csx 内联文本 / 程序集注册键）→
//   触发器预制体（触发器定义：稳定键 / Kind / hooks / 事件集合）→
//   效果预制体（主触发器 + 其它触发器预制体 + wrapper 声明）。
// 效果快照（EffectSnapshot）= schemaVersion + 效果预制体（嵌套三层，自包含）。
// **只承载数据**：装载计划（EffectLoadPlan／EffectLoadStep／EffectLoadStepKind）随旧装载链一并删除——
// 装配面改由 `LegacySnapshotComponentBinder`（旧快照 → v4 组件）与 `SnapshotStore`（快照登记）承担。
// 来源承载（S-C11）：内置预制体走程序集键；动态/社区预制体走 csx 内联文本。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 事件预制体（S-C5；三层之最内层）：具名、可参数化的 handler 逻辑声明。
/// 来源**二选一**（恰一种）：<see cref="CsxSource"/>（内联 csx 文本，动态/社区）或
/// <see cref="AssemblyKey"/>（程序集显式注册键，内置）。入口名默认 <c>HandleAsync</c>。
/// </summary>
public sealed class EventPrefab
{
    /// <summary>创建事件预制体（来源二选一，构造期 fail-fast）。</summary>
    /// <exception cref="ArgumentException">id/entryName 空白；来源不是"恰一种"；来源值为空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException">version ≤ 0。</exception>
    public EventPrefab(
        string id,
        string entryName = "HandleAsync",
        string? csxSource = null,
        string? assemblyKey = null,
        IEnumerable<string>? downstream = null,
        int version = 1,
        string? band = null,
        IEnumerable<string>? seams = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (csxSource is null && assemblyKey is null)
        {
            throw new ArgumentException("事件预制体必须提供一种来源（csxSource 或 assemblyKey）。", nameof(csxSource));
        }

        if (csxSource is not null && assemblyKey is not null)
        {
            throw new ArgumentException("事件预制体来源必须恰一种（csxSource 与 assemblyKey 不可同给）。", nameof(csxSource));
        }

        if (csxSource is not null && string.IsNullOrWhiteSpace(csxSource))
        {
            throw new ArgumentException("csxSource 不能为空白。", nameof(csxSource));
        }

        if (assemblyKey is not null && string.IsNullOrWhiteSpace(assemblyKey))
        {
            throw new ArgumentException("assemblyKey 不能为空白。", nameof(assemblyKey));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "预制体版本须为正整数。");
        }

        Id = id;
        EntryName = entryName;
        CsxSource = csxSource;
        AssemblyKey = assemblyKey;
        Downstream = ValidateDownstream(downstream);
        Seams = ValidateSeams(seams);
        Version = version;
        Band = NormalizeBand(band);
    }

    /// <summary>
    /// 事件所属区段（E-D1 加性）：<c>null</c>／<c>"default"</c>＝普通区段（按优先级排序，可用于"前置销毁"）；
    /// <c>"disposing"</c>＝保留槽（恒在全部普通事件之后）。大小写敏感、ordinal。
    /// </summary>
    public string? Band { get; }

    private static string? NormalizeBand(string? band)
    {
        if (string.IsNullOrWhiteSpace(band) || string.Equals(band, "default", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(band, "disposing", StringComparison.Ordinal))
        {
            return "disposing";
        }

        throw new ArgumentException(
            $"事件区段 '{band}' 不受支持（本版仅接受 \"default\" 与 \"disposing\"）。", nameof(band));
    }

    /// <summary>预制体标识（注册/引用键）。</summary>
    public string Id { get; }

    /// <summary>入口名（csx 侧要被解析的函数名；默认 <c>HandleAsync</c>）。</summary>
    public string EntryName { get; }

    /// <summary>csx 内联源码（动态/社区来源；null＝程序集来源）。</summary>
    public string? CsxSource { get; }

    /// <summary>程序集显式注册键（内置来源；null＝csx 来源）。</summary>
    public string? AssemblyKey { get; }

    /// <summary>声明的下游触发器稳定键（审查链取边；空＝未声明）。</summary>
    public IReadOnlyList<string> Downstream { get; }

    /// <summary>声明的动作缝名（空＝无动作缝）；同名缝在 handler 条目内唯一。</summary>
    public IReadOnlyList<string> Seams { get; }

    /// <summary>版本（引用形态 <c>{id, version}</c> 的组成；不匹配＝结构化失败）。</summary>
    public int Version { get; }

    /// <summary>是否为内联（csx）来源。</summary>
    public bool IsInline => CsxSource is not null;

    private static string[] ValidateDownstream(IEnumerable<string>? downstream)
    {
        if (downstream is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var key in downstream)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("downstream 不能含 null/空白项。", nameof(downstream));
            }

            if (list.Contains(key))
            {
                throw new ArgumentException($"downstream 含重复项 '{key}'。", nameof(downstream));
            }

            list.Add(key);
        }

        return list.ToArray();
    }

    private static string[] ValidateSeams(IEnumerable<string>? seams)
    {
        if (seams is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var seam in seams)
        {
            if (string.IsNullOrWhiteSpace(seam))
            {
                throw new ArgumentException("seams 不能含 null/空白项。", nameof(seams));
            }

            if (list.Contains(seam))
            {
                throw new ArgumentException($"seams 含重复项 '{seam}'。", nameof(seams));
            }

            list.Add(seam);
        }

        return list.ToArray();
    }
}

/// <summary>
/// 触发器预制体（S-C5；三层之中层）：触发器定义声明（稳定键 / 主动被动 / hooks / band 方案名 / 事件集合）。
/// <see cref="StableKey"/> 即定义级身份来源。
/// <para>
/// 批次 7（§27）：主动／被动枚举随触发器类型退役，这里只留一个**布尔**——"要不要挂到信号上"
/// 是这个 DTO 唯一用到"种类"的地方（被动＝声明了 hooks、经信号挂载）。
/// </para>
/// </summary>
public sealed class TriggerPrefab
{
    /// <summary>创建触发器预制体（构造期 fail-fast；主动触发器不得声明 hooks）。</summary>
    /// <exception cref="ArgumentException">id/stableKey 空白；主动 + 非空 hooks。</exception>
    /// <exception cref="ArgumentNullException">events 含 null 元素。</exception>
    /// <exception cref="ArgumentOutOfRangeException">version ≤ 0。</exception>
    public TriggerPrefab(
        string id,
        string stableKey,
        bool isPassive = false,
        IEnumerable<string>? hooks = null,
        IEnumerable<EventPrefab>? events = null,
        int version = 1,
        string viewTypeName = "Orc.Cards.CardEventView")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewTypeName);

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "预制体版本须为正整数。");
        }

        var hookList = new List<string>();
        if (hooks is not null)
        {
            foreach (var hook in hooks)
            {
                if (string.IsNullOrWhiteSpace(hook))
                {
                    throw new ArgumentException("hooks 不能含 null/空白项。", nameof(hooks));
                }

                if (hookList.Contains(hook))
                {
                    throw new ArgumentException($"hooks 含重复项 '{hook}'。", nameof(hooks));
                }

                hookList.Add(hook);
            }
        }

        if (hookList.Count > 0 && !isPassive)
        {
            throw new ArgumentException(
                "主动触发器预制体不能声明 hooks（矛盾声明：主动触发器不参与挂载）。", nameof(hooks));
        }

        var eventList = new List<EventPrefab>();
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
        Version = version;
        ViewTypeName = viewTypeName;
    }

    /// <summary>预制体标识。</summary>
    public string Id { get; }

    /// <summary>触发器稳定键（定义级身份）。</summary>
    public string StableKey { get; }

    /// <summary>是否被动（被动＝可挂到信号上；主动＝只被直接调用）。</summary>
    public bool IsPassive { get; }

    /// <summary>hook 名（声明顺序；空＝未声明）。</summary>
    public IReadOnlyList<string> Hooks { get; }

    /// <summary>事件预制体集合（声明序＝注册序）。</summary>
    public IReadOnlyList<EventPrefab> Events { get; }

    /// <summary>版本。</summary>
    public int Version { get; }

    /// <summary>视图类型名（程序集限定名或全名；动态实例化时解析为 <see cref="Type"/>；默认 CardEventView）。</summary>
    public string ViewTypeName { get; }
}

/// <summary>
/// wrapper 声明（S-C5；随效果快照往返）：以目标事件预制体 id 锚定替换对象，
/// 实例化时按声明序重建 wrapper 栈（保留运行期注销、回退基础逻辑的语义）。
/// </summary>
/// <param name="TargetEventId">目标事件预制体 id（须在本效果预制体的触发器内）。</param>
/// <param name="Handler">替换逻辑预制体。</param>
public sealed record WrapperPrefab(string Phase, string TargetEventId, string? SeamName, EventPrefab Handler);

/// <summary>
/// 注入声明（S5／P7＝a；<see cref="EffectPrefab"/> 级，与 <see cref="WrapperPrefab"/> 并列）：
/// 把本效果内某个事件（<see cref="EventId"/> 指向的 handler）注册进**宿主具名触发器**（<see cref="TargetTriggerName"/>）——
/// 装载时由装载链框架侧执行、卸载自动撤销。目标触发器由宿主卡按名解析（未命中＝隔离记录）。
/// <see cref="BandName"/> 为空＝默认区段（默认 band 方案；P8a）；非空＝按名解析专门枚举成员（解析失败＝隔离记录）。
/// </summary>
/// <param name="TargetTriggerName">宿主具名触发器名（经宿主卡按名解析）。</param>
/// <param name="BandName">区段名（空＝默认区段）。</param>
/// <param name="EventId">本效果内的事件 id（handler 来源）。</param>
/// <param name="Priority">区段内优先级。</param>
public sealed record InjectPrefab(string TargetTriggerName, string? BandName, string EventId, int Priority = 0);

/// <summary>
/// 判定器包装器声明（效果自注册）：装载时把本效果内某事件（<see cref="Handler"/>）的 **csx 同步**逻辑
/// 注册为指定判定器的包装器（<see cref="Phase"/>＝before／after／replace；挂默认域），随效果卸载自动注销。
/// <see cref="EventPrefab.CsxSource"/> 必填（同步契约 <c>ISyncScriptEvaluator</c>）；约定运行时 args 末尾追加宿主卡。
/// </summary>
/// <param name="JudicatorName">判定器名（须已注册）。</param>
/// <param name="Phase">包装器相位（before／after／replace）。</param>
/// <param name="Handler">同步逻辑预制体（csx）。</param>
public sealed record JudicatorWrapperPrefab(string JudicatorName, string Phase, EventPrefab Handler);

/// <summary>
/// 效果预制体（S-C5；三层之外层）：效果定义声明＝主触发器 + 其它触发器预制体 + wrapper 声明。
/// <see cref="MountedHooks"/> 为计算属性：主触发器与其它被动触发器的 hooks 并集（＝"需挂载的 HookID"）。
/// </summary>
public sealed class EffectPrefab
{
    /// <summary>创建效果预制体（构造期 fail-fast）。</summary>
    /// <exception cref="ArgumentNullException">mainTrigger 为 null；集合含 null 元素。</exception>
    /// <exception cref="ArgumentException">id 空白；wrapper 目标 id 空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException">version ≤ 0。</exception>
    public EffectPrefab(
        string id,
        TriggerPrefab mainTrigger,
        IEnumerable<TriggerPrefab>? otherTriggers = null,
        IEnumerable<WrapperPrefab>? wrappers = null,
        int version = 1,
        IEnumerable<InjectPrefab>? injects = null,
        TriggerPrefab? disposingTrigger = null,
        bool oneShot = false,
        IEnumerable<JudicatorWrapperPrefab>? judicatorWrappers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mainTrigger);

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "预制体版本须为正整数。");
        }

        var others = new List<TriggerPrefab>();
        if (otherTriggers is not null)
        {
            foreach (var item in otherTriggers)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(otherTriggers), "其它触发器集合不能包含 null 元素。");
                }

                others.Add(item);
            }
        }

        var wrapperList = new List<WrapperPrefab>();
        if (wrappers is not null)
        {
            foreach (var item in wrappers)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(wrappers), "wrapper 集合不能包含 null 元素。");
                }

                if (string.IsNullOrWhiteSpace(item.TargetEventId))
                {
                    throw new ArgumentException("wrapper 目标事件 id 不能为空白。", nameof(wrappers));
                }

                if (item.Handler is null)
                {
                    throw new ArgumentException("wrapper 逻辑预制体不能为 null。", nameof(wrappers));
                }

                var phase = (item.Phase ?? string.Empty).Trim().ToLowerInvariant();
                if (phase is not ("before" or "after" or "replace"))
                {
                    throw new ArgumentException($"wrapper phase '{item.Phase}' 非法（须为 before／after／replace）。", nameof(wrappers));
                }

                if (phase != "before" && !string.IsNullOrWhiteSpace(item.SeamName))
                {
                    throw new ArgumentException("只有前置包装器可以挂到动作缝（after/replace 不接受 seam）。", nameof(wrappers));
                }

                if (phase == "before" && item.SeamName is not null && string.IsNullOrWhiteSpace(item.SeamName))
                {
                    throw new ArgumentException("动作缝名不能为空白（未挂缝请传 null）。", nameof(wrappers));
                }

                wrapperList.Add(item with { Phase = phase });
            }
        }

        var injectList = new List<InjectPrefab>();
        if (injects is not null)
        {
            foreach (var item in injects)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(injects), "注入声明集合不能包含 null 元素。");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(item.TargetTriggerName);
                ArgumentException.ThrowIfNullOrWhiteSpace(item.EventId);
                injectList.Add(item);
            }
        }

        var judicatorWrapperList = new List<JudicatorWrapperPrefab>();
        if (judicatorWrappers is not null)
        {
            foreach (var item in judicatorWrappers)
            {
                if (item is null)
                {
                    throw new ArgumentNullException(nameof(judicatorWrappers), "judicatorWrappers 不能包含 null 元素。");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(item.JudicatorName);
                if (item.Handler is null)
                {
                    throw new ArgumentException("judicatorWrapper 逻辑预制体不能为 null。", nameof(judicatorWrappers));
                }

                var phase = (item.Phase ?? string.Empty).Trim().ToLowerInvariant();
                if (phase is not ("before" or "after" or "replace"))
                {
                    throw new ArgumentException(
                        $"judicatorWrapper phase '{item.Phase}' 非法（须为 before／after／replace）。", nameof(judicatorWrappers));
                }

                if (string.IsNullOrWhiteSpace(item.Handler.CsxSource))
                {
                    throw new ArgumentException(
                        "judicatorWrapper 逻辑须为 csx（同步脚本契约）；assemblyKey 形态不支持。", nameof(judicatorWrappers));
                }

                judicatorWrapperList.Add(item with { Phase = phase });
            }
        }

        Id = id;
        MainTrigger = mainTrigger;
        OtherTriggers = others;
        Wrappers = wrapperList;
        Injects = injectList;
        JudicatorWrappers = judicatorWrapperList;
        Version = version;
        OneShot = oneShot;
        DisposingTrigger = NormalizeDisposingTrigger(id, disposingTrigger);

        var hooks = new List<string>(mainTrigger.Hooks);
        foreach (var other in others)
        {
            foreach (var hook in other.Hooks)
            {
                if (!hooks.Contains(hook))
                {
                    hooks.Add(hook);
                }
            }
        }

        MountedHooks = hooks;
    }

    /// <summary>预制体标识。</summary>
    public string Id { get; }

    /// <summary>主触发器预制体。</summary>
    public TriggerPrefab MainTrigger { get; }

    /// <summary>其它触发器预制体（声明序）。</summary>
    public IReadOnlyList<TriggerPrefab> OtherTriggers { get; }

    /// <summary>wrapper 声明（声明序＝栈序）。</summary>
    public IReadOnlyList<WrapperPrefab> Wrappers { get; }

    /// <summary>注入声明（S5；声明序）。</summary>
    public IReadOnlyList<InjectPrefab> Injects { get; }

    /// <summary>判定器包装器声明（效果自注册；声明序）。</summary>
    public IReadOnlyList<JudicatorWrapperPrefab> JudicatorWrappers { get; }

    /// <summary>"需挂载的 HookID"（计算属性：主触发器与其它被动触发器 hooks 的并集，去重保序）。</summary>
    public IReadOnlyList<string> MountedHooks { get; }

    /// <summary>版本。</summary>
    public int Version { get; }

    /// <summary>
    /// disposing 触发器定义（E-D1；与 <see cref="MainTrigger"/> 并列、**默认必选**）：
    /// <c>hooks</c>＝兜底时机（缺省/空时归一化为 <c>card.destroyed</c>，不可移除；<c>effect.removed</c> 被拒绝），
    /// <c>events</c>＝作者销毁逻辑（普通注册，执行在框架默认 OnUnmount 之前）。
    /// </summary>
    public TriggerPrefab DisposingTrigger { get; }

    /// <summary>是否为一次性效果（触发一次后自动销毁；仅在有业务主触发器时成立）。</summary>
    public bool OneShot { get; }

    private static TriggerPrefab NormalizeDisposingTrigger(string effectId, TriggerPrefab? disposing)
    {
        if (disposing is null)
        {
            return new TriggerPrefab(
                $"{effectId}.disposing",
                $"{effectId}/dispose",
                isPassive: true,
                hooks: new[] { Updates.CardDestroyed },
                events: Array.Empty<EventPrefab>(),
                viewTypeName: nameof(CardEventView));
        }

        if (!disposing.IsPassive)
        {
            throw new ArgumentException(
                $"效果 '{effectId}' 的 disposing 触发器必须是 Passive（销毁兜底需要挂载）。", nameof(disposing));
        }

        if (!string.IsNullOrWhiteSpace(disposing.ViewTypeName)
            && !string.Equals(disposing.ViewTypeName, nameof(CardEventView), StringComparison.Ordinal)
            && !string.Equals(disposing.ViewTypeName, typeof(CardEventView).FullName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"效果 '{effectId}' 的 disposing 触发器视图类型必须是 {nameof(CardEventView)}（本版固定）。", nameof(disposing));
        }

        var hooks = ComponentDisposalPolicy.NormalizeSignals(disposing.Hooks, effectId);

        return new TriggerPrefab(
            disposing.Id,
            disposing.StableKey,
            isPassive: true,
            hooks,
            disposing.Events,
            disposing.Version,
            nameof(CardEventView));
    }
}

/// <summary>
/// 效果快照（S-C5/S-C10）：schemaVersion + 效果预制体（嵌套三层、自包含）。
/// 反序列化产出**有序装载计划**（<see cref="BuildLoadPlan"/>）——计划可独立审查/重放。
/// </summary>
public sealed class EffectSnapshot
{
    /// <summary>效果快照 schema 版本（本版＝3——E-D1 新增 disposingTrigger/oneShot/band；读入接受 2 并按默认补齐，写出恒为 3）。</summary>
    public const int SchemaVersion = 3;

    /// <summary>创建效果快照。</summary>
    /// <exception cref="ArgumentNullException">root 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">schemaVersion ≤ 0。</exception>
    public EffectSnapshot(EffectPrefab root, int schemaVersion = SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "schema 版本须为正整数。");
        }

        Root = root;
        Version = schemaVersion;
    }

    /// <summary>schema 版本。</summary>
    public int Version { get; }

    /// <summary>根效果预制体。</summary>
    public EffectPrefab Root { get; }
}
