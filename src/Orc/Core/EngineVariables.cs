namespace Orc.Core;

/// <summary>
/// 引擎共享变量表（引擎实例级＝对局级）：跨效果、跨卡牌的 key→object 数据袋。
/// <para>
/// 用途：一张卡的效果写入、另一张卡的效果读取（典型："造成等同于所有友方已摧毁单位数量的伤害"）。
/// 与 <see cref="EngineReferences"/> 并列、互不干扰：本表的 key 是**自由字符串**（非 GUID），
/// 值是**任意对象**（引用共享），不参与引用解析、不进快照、不发事件流。
/// </para>
/// <para>
/// 语义：
/// ① key 空白/null＝拒绝；值 null＝拒绝（避免"有键无值"的二义性）；
/// ② 同 key 同运行时类型写入＝覆盖（后写生效）；同 key **不同**运行时类型写入＝拒绝（不静默覆盖）；
/// ③ 读取缺失或类型不符：<see cref="TryGet{T}"/> 返回 false（不抛），<see cref="Get{T}"/> 抛明确异常；
/// ④ 不参与任何事务/回滚——写入即生效，效果装载失败或销毁都不撤销已写入的值；
/// ⑤ 生命周期＝引擎实例：随引擎实例丢弃而丢弃，无进程级残留。
/// </para>
/// <para>
/// 线程模型：与引擎一致的单线程语义（不加锁；调用方负责串行化）。
/// 多引擎实例各自独立（每实例一张表；无全局单例）。
/// </para>
/// </summary>
public sealed class EngineVariables
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    /// <summary>已写入条目数。</summary>
    public int Count => _values.Count;

    /// <summary>全部已写入 key（只读视图；写入序不保证）。</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    /// <summary>是否存在该 key（空白/null＝false）。</summary>
    public bool Contains(string key)
        => !string.IsNullOrWhiteSpace(key) && _values.ContainsKey(key);

    /// <summary>
    /// 写入变量（同 key 同类型＝覆盖；同 key 不同类型＝拒绝）。
    /// </summary>
    /// <exception cref="ArgumentException">key 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">value 为 null。</exception>
    /// <exception cref="InvalidOperationException">该 key 已以**另一运行时类型**写入。</exception>
    public void Set(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (_values.TryGetValue(key, out var existing) && existing.GetType() != value.GetType())
        {
            throw new InvalidOperationException(
                $"共享变量 '{key}' 已以类型 '{existing.GetType().Name}' 写入（不同类型写入被拒绝——不静默覆盖）。");
        }

        _values[key] = value;
    }

    /// <summary>按 key 读取并做类型判定（缺失/类型不符＝false，不抛错）。</summary>
    public bool TryGet<T>(string key, out T value)
    {
        if (!string.IsNullOrWhiteSpace(key)
            && _values.TryGetValue(key, out var found)
            && found is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>按 key 读取（缺失或类型不符＝明确异常）。</summary>
    /// <exception cref="ArgumentException">key 为 null/空白。</exception>
    /// <exception cref="KeyNotFoundException">该 key 未写入。</exception>
    /// <exception cref="InvalidCastException">值的实际类型不能按 <typeparamref name="T"/> 读取。</exception>
    public T Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_values.TryGetValue(key, out var found))
        {
            throw new KeyNotFoundException($"共享变量 '{key}' 不存在（未写入）。");
        }

        if (found is not T typed)
        {
            throw new InvalidCastException(
                $"共享变量 '{key}' 的实际类型为 '{found.GetType().Name}'，不能按 '{typeof(T).Name}' 读取。");
        }

        return typed;
    }

    /// <summary>
    /// 读取；缺失时创建 <typeparamref name="T"/> 实例并写入（同一 key 后续调用返回同一实例）。
    /// 仅限引用类型（值类型没有共享实例语义，避免误用副本）。
    /// </summary>
    /// <exception cref="ArgumentException">key 为 null/空白。</exception>
    /// <exception cref="InvalidCastException">该 key 已存在但不是 <typeparamref name="T"/>。</exception>
    public T GetOrCreate<T>(string key)
        where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_values.TryGetValue(key, out var found))
        {
            if (found is not T typed)
            {
                throw new InvalidCastException(
                    $"共享变量 '{key}' 的实际类型为 '{found.GetType().Name}'，不能按 '{typeof(T).Name}' 读取。");
            }

            return typed;
        }

        var created = new T();
        _values.Add(key, created);
        return created;
    }

    /// <summary>移除变量（幂等：不存在/空白 key＝false，不抛错）。</summary>
    public bool Remove(string key)
        => !string.IsNullOrWhiteSpace(key) && _values.Remove(key);

    /// <summary>清空全部变量（沙盒调试/复用引擎实例时手工使用；不参与回滚）。</summary>
    public void Clear() => _values.Clear();
}
