using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Board;

/// <summary>
/// 阵线插入空隙（有序列表模型）：身份属于**某一次线快照**，不是物理空格。
/// <para><see cref="Ordinal"/>＝插入后该元素所处的位置序号（0＝最左、N＝最右）；落地后旧快照即失效。</para>
/// </summary>
public sealed class FormationGap : Entity
{
    internal FormationGap(BattleLine line, long generation, int ordinal, Card[] positions,
        Card? left, Card? right) : base($"Formation gap {ordinal}")
    {
        Line = line;
        Generation = generation;
        Ordinal = ordinal;
        Positions = positions;
        Left = left;
        Right = right;
    }

    /// <summary>所属阵线。</summary>
    public BattleLine Line { get; }

    /// <summary>插入后该元素的位置序号（0＝最左、元素数＝最右）。</summary>
    public int Ordinal { get; }

    /// <summary>插入点左邻（首端＝null）。</summary>
    public Card? Left { get; }

    /// <summary>插入点右邻（末端＝null）。</summary>
    public Card? Right { get; }

    /// <summary>是否仍是当前快照的有效空隙（占用变化、满员、被销毁＝false）。</summary>
    public bool IsCurrent => Life.IsAlive && Line.IsCurrent(this);

    internal long Generation { get; }

    internal Card[] Positions { get; }
}
