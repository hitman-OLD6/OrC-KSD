using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Orc.TestSupport;

/// <summary>重解析点探测结果。</summary>
/// <param name="CanCreate">本机是否可创建目录链接／junction。</param>
/// <param name="Reason">不可创建的原因（可创建＝null）。</param>
public sealed record ReparsePointProbeResult(bool CanCreate, string? Reason);

/// <summary>
/// **重解析点探测与建链的唯一实现**（F4/I15）：全仓只有本文件调用
/// <see cref="Directory.CreateSymbolicLink(string, string)"/> 与 <c>mklink /J</c>。
/// <para>
/// 探测一次并缓存（<see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>）；Windows 先 symlink、失败退 junction，
/// Unix 直接 symlink。探测后**单层删链接**再删根目录，避免误删目标内容。
/// </para>
/// <para>
/// 失败用例的 <c>skipped</c> ＝ 该环境**未验证**，不等于通过：CI 应至少保留一个能建链的作业（已知边界 B-6）。
/// </para>
/// </summary>
public static class ReparsePointProbe
{
    private static readonly Lazy<ReparsePointProbeResult> LazyResult =
        new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>本机是否可创建重解析点。</summary>
    public static bool CanCreate => LazyResult.Value.CanCreate;

    /// <summary>不可创建的原因（可创建＝空串）。</summary>
    public static string Reason => LazyResult.Value.Reason ?? string.Empty;

    /// <summary>建立目录链接／junction（与探测同一套「symlink 优先、junction 退路」逻辑；失败＝false ＋ 原因）。</summary>
    /// <param name="linkPath">链接路径（不得已存在）。</param>
    /// <param name="targetPath">目标目录。</param>
    /// <param name="reason">失败原因（成功＝null）。</param>
    public static bool TryCreate(string linkPath, string targetPath, out string? reason)
        => TryCreateCore(linkPath, targetPath, out reason);

    private static ReparsePointProbeResult Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-reparse-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");

        try
        {
            Directory.CreateDirectory(target);
            if (!TryCreateCore(link, target, out var reason))
            {
                return new ReparsePointProbeResult(false, reason);
            }

            // 单层删链接（非递归）：绝不触碰目标内容。
            Directory.Delete(link, recursive: false);
            if (!Directory.Exists(target))
            {
                return new ReparsePointProbeResult(false, "删除链接时连带删除了目标目录内容。");
            }

            return new ReparsePointProbeResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new ReparsePointProbeResult(false, $"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响探测结论。
            }
        }
    }

    private static bool TryCreateCore(string linkPath, string targetPath, out string? reason)
    {
        reason = null;
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return OperatingSystem.IsWindows()
                ? TryCreateJunction(linkPath, targetPath, out reason)
                : Fail(ex, out reason);
        }
    }

    private static bool Fail(Exception ex, out string? reason)
    {
        reason = $"{ex.GetType().Name}：{ex.Message}";
        return false;
    }

    /// <summary>Windows 非管理员回退：<c>cmd /c mklink /J</c>（junction 不需要符号链接特权）。</summary>
    private static bool TryCreateJunction(string linkPath, string targetPath, out string? reason)
    {
        reason = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                reason = "无法启动 cmd.exe（junction 回退不可用）。";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // 进程已退出：忽略。
                }

                reason = "mklink /J 超时（10s）。";
                return false;
            }

            if (process.ExitCode == 0 && Directory.Exists(linkPath))
            {
                return true;
            }

            reason = $"mklink /J 退出码 {process.ExitCode}：{output.Trim()}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Fail(ex, out reason);
        }
    }
}

/// <summary>
/// 需要真实重解析点的 <c>[Fact]</c>：本机无法建链时**显式 Skip**（原因含「本机无法创建重解析点」），
/// 方法体内**不得**再用 <c>return</c> 静默跳过（F4）。
/// </summary>
public sealed class ReparsePointFactAttribute : FactAttribute
{
    /// <summary>构造：读取一次性探测结果并决定是否 Skip。</summary>
    public ReparsePointFactAttribute()
    {
        if (!ReparsePointProbe.CanCreate)
        {
            Skip = $"本机无法创建重解析点：{ReparsePointProbe.Reason}";
        }
    }
}
