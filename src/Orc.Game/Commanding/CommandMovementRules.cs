using Orc.Game.Board;
using Orc.Game.Cards;

namespace Orc.Game.Commanding;

/// <summary>Normal movement destinations, shared by availability and execution revalidation.
/// 位置＝已落地位置（<see cref="BoardPosition"/>）；候选＝目标线的插入空隙（有序列表模型）。</summary>
internal static class CommandMovementRules
{
    public static bool IsRetreat(Battlefield board, UnitCard unit, BoardPosition? source)
        => source is not null && ReferenceEquals(source.Line, board.FrontLine)
            && KeywordProgramRuntime.Resolve(unit, "move.retreat", 0) != 0;

    public static bool CanStart(Battlefield board, UnitCard unit, BoardPosition? source)
        => unit.Owner is { } owner && source is not null
            && (ReferenceEquals(source.Line, board.GetSupportLine(owner)) || IsRetreat(board, unit, source));

    public static IReadOnlyList<FormationGap> Destinations(Battlefield board, UnitCard unit, BoardPosition? source)
        => (IsRetreat(board, unit, source) ? board.GetSupportLine(unit.Owner!) : board.FrontLine).GetInsertionGaps();
}
