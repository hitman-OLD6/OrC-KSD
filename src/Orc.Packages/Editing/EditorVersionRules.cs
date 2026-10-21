using Orc.Core;

namespace Orc.Packages.Editing;

/// <summary>
/// 编辑器版本规则（R17/D53 的唯一实现）：
/// <list type="bullet">
///   <item>新建资产从 <c>1.0.0</c> 起（R12）；</item>
///   <item>默认自动递增＝**尾段 +1**（保留书写段数）；</item>
///   <item>手写版本须**严格大于**旧版（允许增段；降级与补 0 后相等皆拒绝）；</item>
///   <item>测试版本＝正式版**追加测试段**（<c>1.2.3</c> → <c>1.2.3.1</c>）；</item>
///   <item>转正＝**去测试段并递增**（<c>1.2.3.5</c> → <c>1.2.4</c>）。</item>
/// </list>
/// 版本比较一律走 <see cref="AssetVersion"/> 的规范形语义，**禁止字符串比对**。
/// </summary>
public static class EditorVersionRules
{
    /// <summary>新建资产的初始版本。</summary>
    public static AssetVersion Initial => new(1, 0, 0);

    /// <summary>默认自动递增（尾段 +1；<c>1.2</c>→<c>1.3</c>、<c>1.0.0</c>→<c>1.0.1</c>）。</summary>
    /// <param name="current">当前版本。</param>
    /// <exception cref="AssetEditorException">尾段已达上限（<see cref="int.MaxValue"/>）。</exception>
    public static AssetVersion Next(AssetVersion current)
    {
        try
        {
            return current.Increment();
        }
        catch (InvalidOperationException ex)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidVersion, $"版本 '{current}' 无法自动递增：{ex.Message}", ex);
        }
    }

    /// <summary>手写版本校验：必须可解析且**严格大于**旧版。</summary>
    /// <param name="text">手写版本原文。</param>
    /// <param name="current">当前（旧）版本。</param>
    /// <exception cref="AssetEditorException">格式非法或未严格递增。</exception>
    public static AssetVersion ParseIncreasing(string text, AssetVersion current)
    {
        if (!AssetVersion.TryParse(text, out var version))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidVersion,
                $"版本 '{text}' 非法（期望 1..{AssetVersion.MaxSegments} 段纯数字，以 . 分隔）。");
        }

        RequireIncreasing(version, current);
        return version;
    }

    /// <summary>
    /// 手写版本校验（**组粒度**）：必须可解析且**严格大于**组内最大版本；
    /// 失败消息指名最大版本的来源条目（F2：同 sidecar 的宿主与内含资产版本必须一致）。
    /// </summary>
    /// <param name="text">手写版本原文。</param>
    /// <param name="current">组内最大版本。</param>
    /// <param name="currentOwner">最大版本来源条目（<c>{kind}：{id}</c>）。</param>
    /// <exception cref="AssetEditorException">格式非法或未严格递增。</exception>
    public static AssetVersion ParseIncreasing(string text, AssetVersion current, string currentOwner)
    {
        if (!AssetVersion.TryParse(text, out var version))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidVersion,
                $"版本 '{text}' 非法（期望 1..{AssetVersion.MaxSegments} 段纯数字，以 . 分隔）。");
        }

        if (!version.IsUpgradeOver(current))
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.VersionNotIncreasing,
                $"版本 '{version}' 未严格大于组内最大 '{current}'（来自 {currentOwner}）"
                + "——请使用 ≥ 其严格递增值（同 sidecar 的宿主与内含资产版本必须一致）。");
        }

        return version;
    }

    /// <summary>要求严格递增（降级与补 0 后相等皆拒绝）。</summary>
    /// <param name="candidate">候选版本。</param>
    /// <param name="current">当前（旧）版本。</param>
    /// <exception cref="AssetEditorException">未严格大于旧版。</exception>
    public static void RequireIncreasing(AssetVersion candidate, AssetVersion current)
    {
        if (candidate.IsUpgradeOver(current))
        {
            return;
        }

        throw new AssetEditorException(
            AssetEditorErrorCode.VersionNotIncreasing,
            $"版本 '{candidate}' 未严格大于旧版 '{current}'（降级或补 0 后相等均不允许）。");
    }

    /// <summary>
    /// 正式版 → 测试版：追加一段 <c>1</c>（<c>1.2.3</c> → <c>1.2.3.1</c>）。
    /// </summary>
    /// <param name="formal">正式版版本。</param>
    /// <exception cref="AssetEditorException">正式版已达 8 段（无法追加测试段）。</exception>
    public static AssetVersion TestVersionOf(AssetVersion formal)
    {
        if (formal.SegmentCount is < 1 or >= AssetVersion.MaxSegments)
        {
            throw new AssetEditorException(
                AssetEditorErrorCode.InvalidVersion,
                $"正式版 '{formal}' 无法追加测试段（段数须为 1..{AssetVersion.MaxSegments - 1}）。");
        }

        var segments = formal.Segments.ToArray().Append(1).ToArray();
        return AssetVersion.FromSegments(segments);
    }

    /// <summary>是否「正式版 ＋ 一个测试段」的形态（段数 +1、前缀相同、测试段 ≥ 1）。</summary>
    /// <param name="current">候选版本。</param>
    /// <param name="baseline">正式版基线。</param>
    public static bool IsTestFormOf(AssetVersion current, AssetVersion baseline)
    {
        if (baseline.SegmentCount == 0 || current.SegmentCount != baseline.SegmentCount + 1)
        {
            return false;
        }

        for (var i = 0; i < baseline.SegmentCount; i++)
        {
            if (current.SegmentAt(i) != baseline.SegmentAt(i))
            {
                return false;
            }
        }

        return current.SegmentAt(baseline.SegmentCount) >= 1;
    }

    /// <summary>
    /// 转正版本：去测试段并递增正式版尾段（<c>1.2.3.5</c> → <c>1.2.4</c>）。
    /// <para>非测试形态（未改动＝与基线同版本、或用户手写的版本）原样返回；无基线（本会话新建）亦原样返回。</para>
    /// </summary>
    /// <param name="current">当前版本。</param>
    /// <param name="baseline">正式版基线（本会话开始时的版本；新建资产＝null）。</param>
    public static AssetVersion Promote(AssetVersion current, AssetVersion? baseline)
    {
        if (baseline is not { } formal || !IsTestFormOf(current, formal))
        {
            return current;
        }

        var dropped = current.Segments.ToArray()[..^1];
        if (dropped.Length == 0)
        {
            return current;
        }

        var baseVersion = AssetVersion.FromSegments(dropped);
        return baseVersion.Increment();
    }
}
