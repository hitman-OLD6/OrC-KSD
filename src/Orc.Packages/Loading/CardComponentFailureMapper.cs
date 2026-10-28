using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// 游戏层失败事实 → 报告回填入参的**唯一转换点**。
/// <para>
/// 落点理由（依赖方向 `Orc.Packages → Orc.Game`）：转换要同时看见游戏层的
/// <see cref="CardComponentFailureData"/> 与本侧的报告模型，只有这一侧成立；
/// 宿主只做「读 sink 参数 → 调本方法 → 交给 <see cref="MatchCardAssembly.RecordComponentFailures"/>」。
/// </para>
/// </summary>
public static class CardComponentFailureMapper
{
    /// <summary>逐条转换（顺序不变、不合并——合并由报告侧按 GUID 做）。</summary>
    /// <exception cref="ArgumentNullException">failures 为 null 或含 null 项。</exception>
    public static IReadOnlyList<CardComponentFailure> ToReportFailures(
        IReadOnlyList<CardComponentFailureData> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var converted = new CardComponentFailure[failures.Count];
        for (var i = 0; i < failures.Count; i++)
        {
            var failure = failures[i] ?? throw new ArgumentNullException(
                nameof(failures), "失败清单含 null 项（调用方构造错误）。");

            converted[i] = new CardComponentFailure(
                failure.AppId,
                failure.AssetGuid,
                failure.Issues ?? Array.Empty<CardFieldIssue>());
        }

        return converted;
    }
}
