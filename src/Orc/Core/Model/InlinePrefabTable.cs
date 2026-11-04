namespace Orc.Core.Model;

/// <summary>
/// **`inline` 的对局级展开表**（R39 裁决 9／13／17／S30）：随 <see cref="OrchestrationRuntime"/> 实例存在，
/// 引擎实例丢弃即消失——**不落进程级静态、不跨对局复用**（Constraint 3）。
/// <para>
/// 键是内部派生的缓存键（模板 id ＋ <c>args</c> 规范化哈希），**不是资产 id**：
/// 不进资产命名空间、不产出 sidecar、不进 lock、不进资产库（R39 裁决 1）。
/// 键恒以 <see cref="KeyPrefix"/> 开头——合法资产 id 不得命中本表（<see cref="TryGet"/> 对非该前缀一律 false）。
/// </para>
/// <para>
/// 表里存的是展开结果（形态与 <c>named</c> 完全一致，Constraint 1）。首次被
/// <see cref="OrchestrationRuntime.CreateEntity"/> 解析时，按运行时的 handler 注册面**惰性**构建带自有边的
/// <see cref="PrefabType"/>（结构投影没有边，不能直接拿去执行 handler）——同一键只构建一次。
/// </para>
/// </summary>
public sealed class InlinePrefabTable
{
    /// <summary>缓存键前缀（与资产 id 的合法字符集不重叠：资产局部 id 不允许 <c>/</c>）。</summary>
    public const string KeyPrefix = "inline/";

    private sealed class Entry
    {
        public required InstantiatedPrefab Instance { get; init; }

        public PrefabType? RuntimeType { get; set; }
    }

    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    /// <summary>表内条目数（诊断用；不暴露键——缓存键不进对外面）。</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// 登记一次 <c>inline</c> 实例（同键幂等：模板 ＋ 同参数 ⇒ 同一份展开形态）。
    /// </summary>
    /// <exception cref="ArgumentNullException">instance 为 null。</exception>
    /// <exception cref="ArgumentException">不是 inline 产物，或键不是本表的键形态。</exception>
    public InstantiatedPrefab Add(InstantiatedPrefab instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.CacheKey is null)
        {
            throw new ArgumentException(
                "只有 inline 产物进对局级展开表：named 实例是可解析的资产记录，走 Save 落盘（R39 裁决 13）。",
                nameof(instance));
        }

        if (!instance.CacheKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"inline 缓存键必须以 '{KeyPrefix}' 开头（收到 '{instance.CacheKey}'）："
                + "只有这个前缀的键才允许命中本表，合法资产 id 一律不命中（R39 裁决 17）。",
                nameof(instance));
        }

        if (_byKey.TryGetValue(instance.CacheKey, out var existing))
        {
            return existing.Instance;
        }

        _byKey[instance.CacheKey] = new Entry { Instance = instance };
        return instance;
    }

    /// <summary>
    /// 按缓存键取（键只在运行时内部流转；编辑器不暴露它）。
    /// <para>**只匹配本表的键形态**：非 <see cref="KeyPrefix"/> 前缀的键恒 false——保证合法资产 id 不误命中。</para>
    /// </summary>
    public bool TryGet(string cacheKey, out InstantiatedPrefab? instance)
    {
        if (cacheKey is not null
            && cacheKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && _byKey.TryGetValue(cacheKey, out var entry))
        {
            instance = entry.Instance;
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>
    /// 取（或首次构建）该条目的**可执行**类型：结构投影没有自有边，必须用运行时的 handler 注册面重建一次。
    /// 同一键只构建一次（幂等；不重复展开）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">键不在表内（不可能：调用方先 <see cref="TryGet"/>）。</exception>
    internal PrefabType GetOrCreateRuntimeType(string cacheKey, Func<InstantiatedPrefab, PrefabType> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_byKey.TryGetValue(cacheKey, out var entry))
        {
            throw new KeyNotFoundException($"inline 缓存键 '{cacheKey}' 不在本表内。");
        }

        return entry.RuntimeType ??= factory(entry.Instance);
    }

    /// <summary>清空（对局结束；正常情况下随运行时一起被丢弃）。</summary>
    public void Clear() => _byKey.Clear();
}
