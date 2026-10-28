namespace Orc.Cards;

/// <summary>
/// 效果快照登记处（原 <c>PrefabManager</c>，v4-only 后只剩**注册面**）。
/// <para>
/// 两条注册面：①效果快照（<see cref="RegisterPrefab"/>，键＝快照 id）；②程序集来源处理器
/// （<see cref="RegisterHandler"/>，键＝assemblyKey）。装载（目录扫描/校验/解析）随旧装载链一并删除——
/// 快照的装配面是 <c>LegacySnapshotComponentBinder</c>（旧快照 → v4 组件 → 组件实体）。
/// </para>
/// <para>单线程语义（与引擎一致）；注册即配置，重复注册一律拒绝。</para>
/// </summary>
public sealed class SnapshotStore
{
    private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectSnapshot> _prefabs = new(StringComparer.Ordinal);

    /// <summary>已注册处理器键（登记序快照）。</summary>
    public IReadOnlyList<string> HandlerKeys => _handlers.Keys.ToArray();

    /// <summary>已注册效果快照 id（登记序快照）。</summary>
    public IReadOnlyList<string> PrefabIds => _prefabs.Keys.ToArray();

    /// <summary>注册程序集来源处理器（显式；注册即配置）。</summary>
    /// <exception cref="ArgumentException">assemblyKey 为空白。</exception>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一键重复注册（被拒绝）。</exception>
    public void RegisterHandler(string assemblyKey, Delegate handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyKey);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(assemblyKey, handler))
        {
            throw new InvalidOperationException($"处理器键 '{assemblyKey}' 已注册（重复注册被拒绝——注册即配置）。");
        }
    }

    /// <summary>按键取处理器（未注册＝false）。</summary>
    public bool TryGetHandler(string assemblyKey, out Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(assemblyKey);
        return _handlers.TryGetValue(assemblyKey, out handler!);
    }

    /// <summary>注册效果快照（预制体 id 为键；重复＝拒绝）。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一 id 重复注册（被拒绝）。</exception>
    public void RegisterPrefab(EffectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_prefabs.TryAdd(snapshot.Root.Id, snapshot))
        {
            throw new InvalidOperationException($"效果预制体 '{snapshot.Root.Id}' 已注册（重复注册被拒绝）。");
        }
    }

    /// <summary>按 id 取效果快照（未注册＝false）。</summary>
    public bool TryGetPrefab(string id, out EffectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _prefabs.TryGetValue(id, out snapshot!);
    }
}
