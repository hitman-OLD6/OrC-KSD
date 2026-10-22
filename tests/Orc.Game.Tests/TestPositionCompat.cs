using Orc.Cards;
using Orc.Game.Board;

namespace Orc.Game.Tests;

/// <summary>
/// 有序列表模型下的位置读法适配（**仅测试侧**）：
/// 元素（<see cref="Card"/>）恒占其位置；空隙（<see cref="FormationGap"/>）"空"当且仅当插入点落在末尾之外。
/// </summary>
internal static class TestPositionCompat
{
    /// <summary>元素恒占用自身位置（旧断言「槽为空」对元素不成立）。</summary>
    public static bool IsEmpty(this Card card)
    {
        _ = card;
        return false;
    }

    /// <summary>空隙是否位于线末尾（仅末端插入点"为空"）。</summary>
    public static bool IsEmpty(this FormationGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return gap.Ordinal >= gap.Line.Size;
    }
}
