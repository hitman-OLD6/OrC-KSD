using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Board;

/// <summary>
/// 已落地的位置（阵线 ＋ 位置序号）：用于信号载荷与"已成事实"的读取面。
/// <para>与 <see cref="FormationGap"/> 的区别：空隙是**请求落位**（快照会失效）；本类型是**已经站定**的位置。</para>
/// <para>序号是快照值——线内容变化后应重新取（读"当前位置"一律经 <c>line.IndexOf(card)</c>）。</para>
/// <para>继承 <see cref="Entity"/>：位置作为触发数据载荷/引用集成员时沿用既有 <c>Ref&lt;Entity&gt;</c> 通路。</para>
/// </summary>
public sealed class BoardPosition : Entity
{
    /// <summary>创建位置。</summary>
    /// <exception cref="ArgumentNullException">line 为 null。</exception>
    public BoardPosition(BattleLine line, int index) : base(NameOf(line, index))
    {
        ArgumentNullException.ThrowIfNull(line);
        Line = line;
        Index = index;
    }

    /// <summary>所在阵线。</summary>
    public BattleLine Line { get; }

    /// <summary>位置序号（0＝最左）。</summary>
    public int Index { get; }

    /// <summary>该位置上的元素（越界/空位＝null）。</summary>
    public Card? Occupant => Index >= 0 && Index < Line.Size ? Line[Index] : null;

    /// <summary>线名＋序号（诊断用）。</summary>
    public override string ToString() => $"{Line.Name}[{Index}]";

    private static string NameOf(BattleLine line, int index)
    {
        ArgumentNullException.ThrowIfNull(line);
        return $"{line.Name}[{index}]";
    }
}
