namespace Orc.Packages;

/// <summary>已发现的包（manifest ＋ 包根目录）。</summary>
/// <param name="Manifest">已解析校验的清单。</param>
/// <param name="RootDirectory">包根目录（资源相对路径的基准）。</param>
public sealed record DiscoveredPackage(PackageManifest Manifest, string RootDirectory);

/// <summary>包发现报告：成功清单 ＋ 失败明细（单包失败＝隔离记录、不阻断其余）＋ 迁移告警。</summary>
/// <param name="Packages">发现的包（稳定序＝目录名序）。</param>
/// <param name="Failures">失败明细（<c>路径：原因</c>）。</param>
/// <param name="Warnings">迁移告警（如已退役的包级 <c>dependsOn</c>；不阻断）。</param>
public sealed record PackageDiscoveryReport(
    IReadOnlyList<DiscoveredPackage> Packages,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>迁移告警（未提供＝空）。</summary>
    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
}

/// <summary>
/// 包发现（S5）：两种方式皆可——①**约定目录扫描**（<c>&lt;base&gt;/plugins/&lt;包&gt;/manifest.json</c> 与
/// <c>&lt;base&gt;/packs/&lt;包&gt;/manifest.json</c>）；②**显式清单**（一行一个包根目录）。
/// 单包 manifest 非法＝隔离记录、不阻断其余。目录不存在＝空报告。
/// </summary>
public static class PackageDiscovery
{
    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>插件根目录名。</summary>
    public const string PluginsRootName = "plugins";

    /// <summary>卡包根目录名。</summary>
    public const string PacksRootName = "packs";

    /// <summary>约定目录扫描（<c>plugins/</c> 与 <c>packs/</c> 分置；目录不存在＝跳过）。</summary>
    /// <exception cref="ArgumentNullException">baseDirectory 为 null。</exception>
    public static PackageDiscoveryReport Discover(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var failures = new List<string>();
        var warnings = new List<string>();
        var found = new List<DiscoveredPackage>();

        ScanRoot(Path.Combine(baseDirectory, PluginsRootName), PackageKind.Plugin, found, failures, warnings);
        ScanRoot(Path.Combine(baseDirectory, PacksRootName), PackageKind.CardPack, found, failures, warnings);

        var ordered = found
            .OrderBy(p => p.RootDirectory, StringComparer.Ordinal)
            .ToArray();
        return new PackageDiscoveryReport(ordered, failures, warnings);
    }

    /// <summary>显式清单（每行一个包根目录；<c>#</c> 开头＝注释、空行＝跳过）。</summary>
    /// <exception cref="ArgumentNullException">listFilePath 为 null。</exception>
    public static PackageDiscoveryReport DiscoverFromList(string listFilePath)
    {
        ArgumentNullException.ThrowIfNull(listFilePath);

        var failures = new List<string>();
        var warnings = new List<string>();
        var found = new List<DiscoveredPackage>();
        if (!File.Exists(listFilePath))
        {
            return new PackageDiscoveryReport(found, new[] { $"{listFilePath}：清单文件不存在" });
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(listFilePath)) ?? ".";
        foreach (var raw in File.ReadAllLines(listFilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var directory = Path.IsPathRooted(line) ? line : Path.Combine(baseDirectory, line);
            LoadOne(directory, expected: null, found, failures, warnings);
        }

        var ordered = found
            .OrderBy(p => p.RootDirectory, StringComparer.Ordinal)
            .ToArray();
        return new PackageDiscoveryReport(ordered, failures, warnings);
    }

    private static void ScanRoot(
        string root,
        PackageKind expected,
        List<DiscoveredPackage> found,
        List<string> failures,
        List<string> warnings)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            LoadOne(directory, expected, found, failures, warnings);
        }
    }

    private static void LoadOne(
        string directory,
        PackageKind? expected,
        List<DiscoveredPackage> found,
        List<string> failures,
        List<string> warnings)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            failures.Add($"{manifestPath}：缺少清单文件");
            return;
        }

        try
        {
            var manifest = PackageManifest.Parse(File.ReadAllText(manifestPath), out var migrationWarnings);
            foreach (var warning in migrationWarnings)
            {
                warnings.Add($"{manifestPath}：{warning}");
            }

            if (expected is { } kind && manifest.Kind != kind)
            {
                failures.Add(
                    $"{manifestPath}：包类型 '{manifest.Kind}' 与所在根目录不符（期望 '{kind}'）");
                return;
            }

            found.Add(new DiscoveredPackage(manifest, directory));
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            failures.Add($"{manifestPath}：{ex.Message}");
        }
    }
}
