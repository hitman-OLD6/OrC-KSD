using Orc.Game.Board;

namespace Orc.Game.Tests;

/// <summary>
/// 测试侧的空隙取位：按序号取当前快照中的插入点（0 起；N 个元素共 N+1 个）。
/// <para>与 <see cref="BattleLine"/> 索引器同口径——越界＝明确错误、不静默（fail-fast），不做任何钳制。</para>
/// </summary>
internal static class TestGapListExtensions
{
    /// <summary>按序号取插入空隙（越界抛；线已满＝无插入点，同样抛）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">index 越界，或目标线已满（无插入点）。</exception>
    public static FormationGap At(this IReadOnlyList<FormationGap> gaps, int index)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        if (index < 0 || index >= gaps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                gaps.Count == 0
                    ? "目标线已满：没有可插入位置。"
                    : $"插入点序号越界（合法范围：0..{gaps.Count - 1}）。");
        }

        return gaps[index];
    }
}
