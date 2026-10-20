using Orc.Core;

namespace Orc.Packages;

/// <summary>一次加载调用的结果状态（结构化；加载器**从不抛错**——问题一律经状态＋报告返回）。</summary>
public enum AssetLoadStatus
{
    /// <summary>首次装载成功（无旧列表）。</summary>
    Loaded = 0,

    /// <summary>重载成功（已从首个变更层起增量重装）。</summary>
    Reloaded = 1,

    /// <summary>列表与缓存逐层一致，未发生装载。</summary>
    Unchanged = 2,

    /// <summary>被重载门禁拒绝（缓存与装载面均未改动）。</summary>
    Rejected = 3,

    /// <summary>装载失败（**全有或全无**：已回滚，缓存仍是旧列表）。</summary>
    Failed = 4,

    /// <summary>列表计算失败（lock 冲突／环依赖等结构性非法），未装载。</summary>
    Invalid = 5,
}

/// <summary>
/// 单域的**增量重载差量**（D13）：从 <see cref="FirstChangedLayer"/> 起重装其后全部资产。
/// </summary>
/// <param name="Domain">域。</param>
/// <param name="FirstChangedLayer">首个变更层（-1＝该域无变更）。</param>
/// <param name="Reloaded">需要（重）装载的条目（层序）。</param>
/// <param name="Retired">需要摘除的旧条目（变化层起的旧段 ＋ 新列表中已不存在的条目）。</param>
public sealed record AssetReloadDelta(
    AssetDomain Domain,
    int FirstChangedLayer,
    IReadOnlyList<LoadListItem> Reloaded,
    IReadOnlyList<LoadListItem> Retired)
{
    /// <summary>该域是否有变更。</summary>
    public bool Changed => FirstChangedLayer >= 0;
}

/// <summary>
/// 一次加载调用的完整结果（结构化）：状态 ＋ 新列表 ＋ 增量差量 ＋ 问题报告 ＋ 失败原因。
/// </summary>
/// <param name="Status">状态。</param>
/// <param name="FirstChangedLayer">首个变更层（两域取最小，正式优先；-1＝无变更）。</param>
/// <param name="Plan">计算出的加载计划（<see cref="AssetLoadStatus.Failed"/> 时为 null——缓存未被替换）。</param>
/// <param name="Deltas">两域差量（正式、测试各一条）。</param>
/// <param name="Issues">计算期问题报告（资产级问题不整段失败）。</param>
/// <param name="Failure">装载失败原因（非 Failed＝null）。</param>
public sealed record AssetLoadResult(
    AssetLoadStatus Status,
    int FirstChangedLayer,
    AssetLoadPlan? Plan,
    IReadOnlyList<AssetReloadDelta> Deltas,
    IReadOnlyList<AssetLoadIssue> Issues,
    string? Failure)
{
    /// <summary>是否成功（含「无变更」）。</summary>
    public bool Ok => Status is AssetLoadStatus.Loaded or AssetLoadStatus.Reloaded or AssetLoadStatus.Unchanged;

    /// <summary>本次（重）装载的全部条目（层序）。</summary>
    public IReadOnlyList<LoadListItem> Reloaded => Deltas.SelectMany(delta => delta.Reloaded).ToArray();

    /// <summary>本次摘除的全部旧条目。</summary>
    public IReadOnlyList<LoadListItem> Retired => Deltas.SelectMany(delta => delta.Retired).ToArray();
}

/// <summary>
/// **装载目标**（S5 的装载动作缝；S6 的卡牌加载器实现它）：接收加载列表条目并落地，
/// 失败以异常报告——加载器负责回滚并保持缓存不变（**全有或全无**，D19）。
/// </summary>
public interface IAssetLoadSink
{
    /// <summary>装载一批条目（层序；内含资产随宿主同层）。失败＝抛异常。</summary>
    /// <param name="items">条目（层序）。</param>
    void Apply(IReadOnlyList<LoadListItem> items);

    /// <summary>撤销一批条目（回滚或摘除旧段）。</summary>
    /// <param name="items">条目。</param>
    void Revert(IReadOnlyList<LoadListItem> items);
}

/// <summary>
/// **资产加载器**（R7/D10/D13/D19/D22；宿主级、列表驱动）：
/// lock 集合 → 注册面取版本 → 拓扑分层（<see cref="LoadListPlanner"/>）→ **缓存加载列表** →
/// 与旧列表按层比对、从**首个变更层**起增量重载其后全部资产。
/// <para>
/// 语义要点：
/// <list type="bullet">
///   <item>依赖检测／拓扑排序／装载顺序**在一次计算中完成**；装载期不做任何依赖查找。</item>
///   <item>正式域与测试域各一张列表、**先正式后测试**；两域差量各自独立计算。</item>
///   <item>重载门禁＝判定器 <see cref="ReloadGateJudicator.Name"/>（结构化返回、不抛错）；仅**重载**过门禁，首次装载不过。</item>
///   <item>装载失败**全有或全无**：撤销新装条目、恢复旧段，缓存保持旧列表。</item>
///   <item>单线程（装配期／重载期）；不承诺并发安全。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AssetLoader
{
    private readonly AssetRegistry _registry;
    private readonly IAssetLoadSink? _sink;
    private readonly JudicatorRegistry? _judicators;
    private AssetLoadPlan? _current;

    /// <summary>构造加载器。</summary>
    /// <param name="registry">资产注册表（注册面；本类型只读查询）。</param>
    /// <param name="sink">装载目标（S6 卡牌加载器；null＝只计算与缓存，不落地）。</param>
    /// <param name="judicators">重载门禁所在的判定器注册表（null＝无门禁；未注册 <c>reloadGate</c> 亦视为无门禁）。</param>
    /// <exception cref="ArgumentNullException">registry 为 null。</exception>
    public AssetLoader(AssetRegistry registry, IAssetLoadSink? sink = null, JudicatorRegistry? judicators = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _sink = sink;
        _judicators = judicators;
    }

    /// <summary>当前缓存的加载计划（未装载＝null）。</summary>
    public AssetLoadPlan? Current => _current;

    /// <summary>当前缓存的加载列表（某域；未装载＝null）。</summary>
    public LoadList? CurrentList(AssetDomain domain) => _current?.For(domain);

    /// <summary>
    /// 按 lock 集合装载／重载（结构化返回，不抛错）。
    /// </summary>
    /// <param name="locks">参与合并的 lock（跨域绝不互相合并）。</param>
    /// <exception cref="ArgumentNullException">locks 为 null。</exception>
    public AssetLoadResult Load(IEnumerable<AssetLock> locks)
    {
        ArgumentNullException.ThrowIfNull(locks);

        var plan = LoadListPlanner.Plan(_registry, locks);
        var deltas = BuildDeltas(plan);
        var firstChanged = FirstChanged(deltas);

        if (!plan.IsValid)
        {
            return new AssetLoadResult(AssetLoadStatus.Invalid, firstChanged, plan, deltas, plan.Issues, null);
        }

        if (firstChanged < 0)
        {
            _current = plan;
            return new AssetLoadResult(AssetLoadStatus.Unchanged, -1, plan, deltas, plan.Issues, null);
        }

        var reloaded = deltas.SelectMany(delta => delta.Reloaded).ToArray();
        var retired = deltas.SelectMany(delta => delta.Retired).ToArray();
        var isReload = _current is not null;

        if (isReload)
        {
            var verdict = EvaluateGate(deltas, firstChanged, reloaded, retired);
            if (!verdict.Allowed)
            {
                return new AssetLoadResult(
                    AssetLoadStatus.Rejected, firstChanged, plan, deltas, plan.Issues, verdict.Reason);
            }
        }

        var failure = Apply(reloaded, retired);
        if (failure is not null)
        {
            return new AssetLoadResult(AssetLoadStatus.Failed, firstChanged, null, deltas, plan.Issues, failure);
        }

        _current = plan;
        return new AssetLoadResult(
            isReload ? AssetLoadStatus.Reloaded : AssetLoadStatus.Loaded,
            firstChanged,
            plan,
            deltas,
            plan.Issues,
            null);
    }

    /// <summary>执行装载（失败即回滚；返回失败原因，成功＝null）。</summary>
    private string? Apply(IReadOnlyList<LoadListItem> reloaded, IReadOnlyList<LoadListItem> retired)
    {
        try
        {
            _sink?.Revert(retired); // 先摘除变化层起的旧段与已移除条目
            _sink?.Apply(reloaded);
            return null;
        }
        catch (Exception ex)
        {
            var failures = new List<string> { ex.Message };
            try
            {
                _sink?.Revert(reloaded);
            }
            catch (Exception rollback)
            {
                failures.Add($"回滚新装段失败：{rollback.Message}");
            }

            try
            {
                if (retired.Count > 0)
                {
                    _sink?.Apply(retired);
                }
            }
            catch (Exception rollback)
            {
                failures.Add($"恢复旧段失败：{rollback.Message}");
            }

            return string.Join("；", failures);
        }
    }

    /// <summary>门禁判定（结构化：被拒／异常均转为拒绝，不向外抛错）。</summary>
    private ReloadVerdict EvaluateGate(
        IReadOnlyList<AssetReloadDelta> deltas,
        int firstChanged,
        IReadOnlyList<LoadListItem> reloaded,
        IReadOnlyList<LoadListItem> retired)
    {
        if (_judicators is null)
        {
            return ReloadVerdict.Allow;
        }

        JudicatorRegistration handle;
        try
        {
            handle = _judicators.Resolve(ReloadGateJudicator.Name);
        }
        catch (KeyNotFoundException)
        {
            return ReloadVerdict.Allow; // 未注册门禁＝该宿主不设门禁
        }

        var domain = deltas.First(delta => delta.Changed).Domain;
        var request = new ReloadRequest(domain, firstChanged, reloaded, retired);

        try
        {
            var result = handle.Invoke(new object[] { request });
            return result is { Length: > 0 } && result[0] is ReloadVerdict verdict ? verdict : ReloadVerdict.Allow;
        }
        catch (Exception ex)
        {
            return ReloadVerdict.Reject($"重载门禁异常：{ex.Message}");
        }
    }

    private IReadOnlyList<AssetReloadDelta> BuildDeltas(AssetLoadPlan plan) =>
    [
        Delta(AssetDomain.Formal, _current?.Formal, plan.Formal),
        Delta(AssetDomain.Test, _current?.Test, plan.Test),
    ];

    /// <summary>单域差量：首个变更层起其后的新条目 ＋ 需摘除的旧条目。</summary>
    private static AssetReloadDelta Delta(AssetDomain domain, LoadList? previous, LoadList next)
    {
        var first = next.FirstChangedLayer(previous);
        if (first < 0)
        {
            return new AssetReloadDelta(domain, -1, [], []);
        }

        var reloaded = next.FromLayer(first);
        if (previous is null)
        {
            return new AssetReloadDelta(domain, first, reloaded, []);
        }

        var kept = next.Items.Select(item => item.AssetGuid).ToHashSet();
        var retired = new List<LoadListItem>(previous.FromLayer(first));
        retired.AddRange(previous.Items.Where(item => !kept.Contains(item.AssetGuid)));

        var seen = new HashSet<Guid>();
        return new AssetReloadDelta(
            domain, first, reloaded, retired.Where(item => seen.Add(item.AssetGuid)).ToArray());
    }

    private static int FirstChanged(IReadOnlyList<AssetReloadDelta> deltas)
        => deltas.Where(delta => delta.Changed)
            .Select(delta => delta.FirstChangedLayer)
            .DefaultIfEmpty(-1)
            .Min();
}
