using Orc.Cards;

namespace Orc.Game.Board;

/// <summary>
/// 阵线（有序单位列表模型）：元素＝HQ 与单位，**顺序即位置**；容量限制元素总数（默认 5，HQ 计入）。
/// <para>可插入位置＝N 个元素之间的 N+1 个空隙（含首尾两端），满员＝无空隙；空隙是临时生成的
/// <see cref="FormationGap"/>（快照失效即拒绝），阵线本身不保存任何"空格"。</para>
/// <para>插入**不居中重排**：元素保持插入后的相对顺序，因此 HQ 可以在最左、中间或最右。</para>
/// </summary>
public sealed class BattleLine
{
    private readonly List<Card> _members = new();
    private readonly string _name;
    private long _generation;
    private Card[]? _gapSnapshot;
    private IReadOnlyList<FormationGap> _gaps = Array.Empty<FormationGap>();

    /// <summary>创建阵线。</summary>
    /// <param name="capacity">元素容量（单位＋HQ 总数上限；须为正整数）。</param>
    /// <param name="lineName">线名（用于诊断与空隙命名；缺省＝「战线」）。</param>
    /// <exception cref="ArgumentOutOfRangeException">capacity 非正整数。</exception>
    public BattleLine(int capacity, string? lineName = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "阵线容量须为正整数（≥1）。");
        }

        Capacity = capacity;
        _name = string.IsNullOrWhiteSpace(lineName) ? "战线" : lineName;
    }

    /// <summary>容量（元素数上限；HQ 计入）。</summary>
    public int Capacity { get; }

    /// <summary>线名。</summary>
    public string Name => _name;

    /// <summary>当前元素序列（从左到右；只读快照视图）。</summary>
    public IReadOnlyList<Card> Members => _members;

    /// <summary>元素数（＝<see cref="Members"/> 数量，**不是**容量）。</summary>
    public int Size => _members.Count;

    /// <summary>是否已满（元素数＝容量）。</summary>
    public bool IsFull => _members.Count >= Capacity;

    /// <summary>按位置序号取元素。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 越界（明确错误、不静默）。</exception>
    public Card this[int index]
    {
        get
        {
            if (index < 0 || index >= _members.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"位置序号越界（合法范围：0..{_members.Count - 1}）。");
            }

            return _members[index];
        }
    }

    /// <summary>元素位置序号（不在线上＝-1）。</summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public int IndexOf(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _members.IndexOf(card);
    }

    /// <summary>元素是否在该线上。</summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public bool Contains(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _members.Contains(card);
    }

    /// <summary>
    /// 当前全部可插入空隙（N 个元素 ⇒ N+1 个，含首尾两端；满员＝空）。
    /// 快照不变＝复用同一批实例；占用变化＝旧实例销毁并重建（<see cref="FormationGap.IsCurrent"/> 由此失效）。
    /// </summary>
    public IReadOnlyList<FormationGap> GetInsertionGaps()
    {
        var snapshot = _members.ToArray();
        if (_gapSnapshot is not null && snapshot.SequenceEqual(_gapSnapshot))
        {
            return _gaps;
        }

        foreach (var gap in _gaps)
        {
            gap.Destroy();
        }

        _gapSnapshot = snapshot;
        _generation++;

        if (snapshot.Length >= Capacity)
        {
            return _gaps = Array.Empty<FormationGap>();
        }

        return _gaps = Array.AsReadOnly(Enumerable.Range(0, snapshot.Length + 1)
            .Select(i => new FormationGap(this, _generation, i, snapshot,
                i > 0 ? snapshot[i - 1] : null,
                i < snapshot.Length ? snapshot[i] : null))
            .ToArray());
    }

    internal bool IsCurrent(FormationGap gap)
        => ReferenceEquals(gap.Line, this)
           && gap.Generation == _generation
           && _gaps.Contains(gap)
           && _members.Count < Capacity
           && _members.SequenceEqual(gap.Positions);

    /// <summary>按空隙插入（落地唯一写面；快照失效／已满／序号越界＝false、零变更）。</summary>
    internal bool TryInsert(FormationGap gap, Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!IsCurrent(gap) || _members.Contains(card) || gap.Ordinal > _members.Count)
        {
            return false;
        }

        _members.Insert(gap.Ordinal, card);
        _generation++;
        return true;
    }

    /// <summary>按空隙名次插入（加入战场等"显式序号"路径；序号＝插入后的位置）。</summary>
    internal bool TryInsertAt(int ordinal, Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_members.Contains(card) || _members.Count >= Capacity || ordinal < 0 || ordinal > _members.Count)
        {
            return false;
        }

        _members.Insert(ordinal, card);
        _generation++;
        return true;
    }

    /// <summary>移除元素（不在线上＝false、零变更）。</summary>
    internal bool TryRemove(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!_members.Remove(card))
        {
            return false;
        }

        _generation++;
        return true;
    }

    /// <summary>装配期初始入列（仅对局/战场构造路径使用；不校验空隙）。</summary>
    internal void AppendInitial(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _members.Add(card);
        _generation++;
    }
}
