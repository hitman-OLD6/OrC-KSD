using System.Text.Json;

namespace Orc.Packages.Loading;

/// <summary>
/// 包目录问题的受控码（**结构化**；用于「该目录还能不能当作集合条目」的判定）。
/// <para>
/// 判定只读磁盘（不写、不装配）：它回答的是「条目是否仍然有效」，而不是「整池能否装配」。
/// 装配可行性的唯一判据是 <see cref="MatchCardAssembly.Inspect"/>。
/// </para>
/// </summary>
public static class PackageProbeCodes
{
    /// <summary>目录不存在（含路径本身非法）。</summary>
    public const string DirectoryMissing = "directory-missing";

    /// <summary>目录在、但缺 <c>manifest.json</c>。</summary>
    public const string ManifestMissing = "manifest-missing";

    /// <summary>manifest 不可解析；或包级结构性非法（sidecar／GUID 重复等，见 <see cref="PackageInspection"/> 的说明）。</summary>
    public const string ManifestInvalid = "manifest-invalid";

    /// <summary>manifest 的 <c>kind</c> 不是 cardpack。</summary>
    public const string KindNotCardpack = "kind-not-cardpack";

    /// <summary>读盘 IO 失败（含无权限）——该条目按无效处理，不让对账整体失败。</summary>
    public const string IoError = "io-error";

    /// <summary>目录的 <c>manifest.id</c> 已与注册时不同（目录被替换成另一个包）。</summary>
    public const string IdChanged = "id-changed";

    /// <summary>同 <c>(id, version)</c> 出现在两个目录（对账保留先注册者，移除后来者）。</summary>
    public const string DuplicateKey = "duplicate-key";
}

/// <summary>
/// 单个卡包目录的探测结果（**只读**）。
/// </summary>
/// <param name="Directory">归一化后的绝对目录。</param>
/// <param name="Ok">是否是可用的 cardpack 目录。</param>
/// <param name="Manifest">解析出的清单（失败＝null）。</param>
/// <param name="ErrorCode">失败码（见 <see cref="PackageProbeCodes"/>；成功＝null）。</param>
/// <param name="Error">人读原因（成功＝null）。</param>
/// <param name="Cause">原始异常（无＝null；上层据此区分「无权限」与普通 IO 失败）。</param>
/// <param name="Warnings">manifest 解析期告警（迁移提示等；失败＝空）。</param>
public sealed record PackageProbeResult(
    string Directory,
    bool Ok,
    PackageManifest? Manifest,
    string? ErrorCode,
    string? Error,
    Exception? Cause,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>manifest 解析期告警（未探测＝空）。</summary>
    public IReadOnlyList<string> EffectiveWarnings => Warnings ?? Array.Empty<string>();

    /// <summary>包 id（失败或无清单＝null）。</summary>
    public string? PackageId => Manifest?.Id;

    /// <summary>包版本（内容指纹；失败或无清单＝null）。</summary>
    public string? Version => Manifest?.Version.ToString();

    /// <summary>包 GUID（失败或无清单＝null）。</summary>
    public string? Guid => Manifest?.Guid.ToString("N");
}

/// <summary>
/// 卡包目录探测（**单一实现**）：集合条目的有效性与 <see cref="MatchCardAssembly.Inspect"/> 的逐包行共用本判定。
/// <para>
/// 只做 manifest 级判定（目录／清单／kind／id 漂移／IO）；包内资产的结构性非法由注册期判定，
/// 结果同样体现为「该包不可装载」（见 <see cref="PackageInspection.ErrorCode"/>）。
/// </para>
/// </summary>
public static class PackageDirectoryProbe
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// 探测一个卡包目录。
    /// </summary>
    /// <param name="directory">目录（绝对路径；相对路径按进程 cwd 归一，调用方负责拒绝相对路径）。</param>
    /// <param name="expectedPackageId">期望的包 id（注册时登记的快照；<c>null</c>＝不校验 id 漂移）。</param>
    public static PackageProbeResult Probe(string? directory, string? expectedPackageId = null)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new PackageProbeResult(
                    directory ?? string.Empty, false, null, PackageProbeCodes.DirectoryMissing, "卡包目录为空。", null);
            }

            fullPath = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PackageProbeResult(
                directory!, false, null, PackageProbeCodes.DirectoryMissing, $"卡包目录不可用：{ex.Message}", ex);
        }

        try
        {
            if (!Directory.Exists(fullPath))
            {
                return new PackageProbeResult(
                    fullPath, false, null, PackageProbeCodes.DirectoryMissing, $"卡包目录不存在：{fullPath}", null);
            }

            var manifestPath = Path.Combine(fullPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return new PackageProbeResult(
                    fullPath,
                    false,
                    null,
                    PackageProbeCodes.ManifestMissing,
                    $"目录 '{fullPath}' 不是卡包（缺 manifest.json）——加载严格按元数据（R3），目录链已退役。",
                    null);
            }

            PackageManifest manifest;
            IReadOnlyList<string> manifestWarnings;
            try
            {
                manifest = PackageManifest.Parse(File.ReadAllText(manifestPath), out manifestWarnings);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
            {
                return new PackageProbeResult(
                    fullPath,
                    false,
                    null,
                    PackageProbeCodes.ManifestInvalid,
                    $"目录 '{fullPath}' 的 manifest.json 不可解析：{ex.Message}",
                    ex);
            }

            if (manifest.Kind != PackageKind.CardPack)
            {
                return new PackageProbeResult(
                    fullPath,
                    false,
                    manifest,
                    PackageProbeCodes.KindNotCardpack,
                    $"注册面只接受 kind=cardpack 的清单（实际 {manifest.Kind}）。",
                    null);
            }

            if (!string.IsNullOrWhiteSpace(expectedPackageId)
                && !string.Equals(manifest.Id, expectedPackageId, StringComparison.Ordinal))
            {
                return new PackageProbeResult(
                    fullPath,
                    false,
                    manifest,
                    PackageProbeCodes.IdChanged,
                    $"目录 '{fullPath}' 的 manifest.id 已由 '{expectedPackageId}' 变为 '{manifest.Id}'"
                    + "（目录被替换成另一个包）——该条目失效。",
                    null);
            }

            return new PackageProbeResult(fullPath, true, manifest, null, null, null, manifestWarnings);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PackageProbeResult(fullPath, false, null, PackageProbeCodes.IoError, $"目录不可读：{ex.Message}", ex);
        }
        catch (IOException ex)
        {
            return new PackageProbeResult(fullPath, false, null, PackageProbeCodes.IoError, $"读目录失败：{ex.Message}", ex);
        }
    }
}
