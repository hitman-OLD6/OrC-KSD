using Orc.Game.Board;

namespace Orc.Game.Tests;

/// <summary>
/// 测试侧的空隙取位：越界时**钳制到最后一个可插入位置**。
/// <para>旧槽位模型每条线恒有 5 个槽；有序列表模型的插入点数随占用变化（N 元素 ⇒ N+1 个），</para>
/// <para>测试里"取第 3 个位置"在新模型下可能越界——钳制到末尾保持"尽量靠后"的意图。</para>
/// </summary>
internal static class TestGapListExtensions
{
    /// <summary>按序号取插入空隙（越界钳制到末尾；空线取唯一插入点）。</summary>
    public static FormationGap At(this IReadOnlyList<FormationGap> gaps, int index)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        if (gaps.Count == 0)
        {
            throw new InvalidOperationException("目标线已满：没有可插入位置。");
        }

        return gaps[Math.Min(Math.Max(index, 0), gaps.Count - 1)];
    }
}
