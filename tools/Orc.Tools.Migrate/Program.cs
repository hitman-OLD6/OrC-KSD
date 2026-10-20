using Orc.Core;
using Orc.Packages;
using Orc.Plugins.BuiltIn;

namespace Orc.Tools.Migrate;

/// <summary>
/// 存量迁移 CLI（S10a/I9）：**薄壳**——参数解析 ＋ 人读渲染；迁移逻辑全部在
/// <see cref="AssetMigrationTool"/> 的纯函数里（<see cref="MigrationPlan"/> 可被测试直接断言）。
/// <para>
/// 用法：<c>dotnet run --project tools/Orc.Tools.Migrate -- [--apply] [--force] [--package &lt;包目录&gt;]… [--root &lt;资产根&gt;]</c>
/// </para>
/// <para>
/// 默认 <b>dry-run</b>（只打印计划、不写盘）；<c>--apply</c> 才落盘；<c>--force</c> 才重写既有 sidecar。
/// </para>
/// </summary>
internal static class Program
{
    private const string Usage = """
        用法：dotnet run --project tools/Orc.Tools.Migrate -- [选项]

          --package <目录>   要迁移的包目录（可重复）
          --root <目录>      资产根目录：迁移其下一层全部包（默认 ./samples，若存在）
          --apply            写盘（默认 dry-run，只打印计划）
          --force            重写既有 sidecar（默认对已存在 sidecar 一律 Skip）
          -h, --help         显示本帮助

        退出码：0＝无变更或无障碍；1＝存在障碍（需人工处理）；2＝参数错误。
        """;

    private static int Main(string[] args)
    {
        if (args.Contains("--plugin-assets", StringComparer.Ordinal))
        {
            return WritePluginAssets(args);
        }

        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"参数错误：{error}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var targets = ResolveTargets(options, out var resolutionError);
        if (resolutionError is not null)
        {
            Console.Error.WriteLine($"参数错误：{resolutionError}");
            return 2;
        }

        Console.WriteLine(options.Apply
            ? "模式：apply（写盘）" + (options.Force ? " ＋ force（重写既有 sidecar）" : "")
            : "模式：dry-run（不写盘；加 --apply 才落盘）");
        Console.WriteLine();

        var plans = new List<MigrationPlan>();
        var failures = 0;
        foreach (var root in targets)
        {
            try
            {
                var plan = AssetMigrationTool.Plan(root, options.Force);
                plans.Add(plan);
                if (options.Apply)
                {
                    var result = AssetMigrationTool.Apply(plan);
                    Render(plan, result.WrittenFiles.Count);
                }
                else
                {
                    Render(plan, writtenFiles: 0);
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                failures++;
                Console.Error.WriteLine($"失败：{root}：{ex.Message}");
            }
        }

        var obstacles = plans.Sum(plan => plan.Obstacles.Count);
        Console.WriteLine();
        Console.WriteLine(
            $"汇总：包 {plans.Count}｜Create {plans.Sum(p => p.CountOf(MigrationAction.Create))}"
            + $"｜Skip {plans.Sum(p => p.CountOf(MigrationAction.Skip))}"
            + $"｜Overwrite {plans.Sum(p => p.CountOf(MigrationAction.Overwrite))}"
            + $"｜障碍 {obstacles}"
            + (options.Apply
                ? $"｜已写盘 {plans.Where(p => p.HasPendingWrites).Sum(p => p.PendingWrites().Count)} 个文件"
                : $"｜待写 {plans.Sum(p => p.PendingWrites().Count)} 个文件（dry-run 未写盘）"));

        return obstacles > 0 || failures > 0 ? 1 : 0;
    }

    private static void Render(MigrationPlan plan, int writtenFiles)
    {
        Console.WriteLine($"包 {plan.PackageId}  {plan.PackageRoot}");
        var assetCount = $"{plan.Entries.Sum(entry => entry.Assets.Count)} 条资产";
        RenderFile("manifest", plan.Manifest);
        RenderFile("lock.formal", plan.FormalLock, assetCount);
        RenderFile("lock.test", plan.TestLock, assetCount);

        foreach (var entry in plan.Entries)
        {
            Console.WriteLine($"  {entry.Action,-9} {entry.SidecarFile}");
            foreach (var asset in entry.Assets)
            {
                Console.WriteLine(
                    $"            {AssetKindTokens.Token(asset.Kind)}:{asset.Id} {asset.Version}  {asset.Guid}");
            }
        }

        foreach (var obstacle in plan.Obstacles)
        {
            Console.WriteLine($"  障碍      {(obstacle.File.Length == 0 ? "（包级）" : obstacle.File)}：{obstacle.Reason}");
        }

        if (writtenFiles > 0)
        {
            Console.WriteLine($"  已写 {writtenFiles} 个文件");
        }

        Console.WriteLine();
    }

    private static void RenderFile(string label, MigrationFileAction action, string? note = null)
        => Console.WriteLine($"  {action.Action,-9} {action.RelativePath,-20} {label}{(note is null ? "" : "  " + note)}");

    private static IReadOnlyList<string> ResolveTargets(Options options, out string? error)
    {
        error = null;
        var targets = new List<string>(options.Packages);
        if (options.Root is { } root)
        {
            targets.AddRange(AssetMigrationTool.EnumeratePackageRoots(root));
        }

        if (targets.Count == 0 && options.Packages.Count == 0 && options.Root is null)
        {
            var defaultRoot = Path.Combine(Environment.CurrentDirectory, "samples");
            if (Directory.Exists(defaultRoot))
            {
                targets.AddRange(AssetMigrationTool.EnumeratePackageRoots(defaultRoot));
            }
            else
            {
                error = "未指定 --package／--root，且当前目录下无 samples/。";
                return targets;
            }
        }

        if (targets.Count == 0)
        {
            error = "未发现任何包目录。";
        }

        return targets.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    private static bool TryParse(string[] args, out Options options, out string? error)
    {
        var apply = false;
        var force = false;
        var help = false;
        string? root = null;
        var packages = new List<string>();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--dry-run":
                    apply = false;
                    break;
                case "--force":
                    force = true;
                    break;
                case "-h" or "--help":
                    help = true;
                    break;
                case "--package" or "--root":
                    var name = args[i];
                    if (i + 1 >= args.Length)
                    {
                        options = default!;
                        error = $"{name} 需要一个目录参数。";
                        return false;
                    }

                    var value = args[++i];
                    if (name == "--package")
                    {
                        packages.Add(Path.GetFullPath(value));
                    }
                    else
                    {
                        root = Path.GetFullPath(value);
                    }

                    break;
                default:
                    options = default!;
                    error = $"未知参数 '{args[i]}'。";
                    return false;
            }
        }

        options = new Options(apply, force, help, packages, root);
        return true;
    }

    private sealed record Options(
        bool Apply,
        bool Force,
        bool ShowHelp,
        IReadOnlyList<string> Packages,
        string? Root);

    /// <summary>
    /// 插件资产清单生成（D63/Q34）：读内置 core 的代码声明 → 写 <c>assets.meta.json</c> ＋ 两 lock。
    /// 用法：<c>--plugin-assets [--plugins-root &lt;目录&gt;]</c>（默认源码树内置插件根）。
    /// </summary>
    private static int WritePluginAssets(string[] args)
    {
        var pluginsRoot = ReadOption(args, "--plugins-root")
            ?? Path.Combine("src", "Orc.Plugins.BuiltIn", "package", "plugins");

        try
        {
            var package = BuiltInPluginPackage.Load(pluginsRoot);
            var source = package.AssetSource
                ?? throw new InvalidOperationException("内置插件未提供资产声明面。");

            var registry = new AssetRegistry();
            var result = PluginAssetRegistrar.Register(package.Manifest, source, registry);
            PluginAssetManifestWriter.Write(
                package.RootDirectory,
                package.Manifest,
                result.Registrations,
                BuiltInCoreAssetSource.AssemblyName);

            Console.WriteLine($"插件资产清单已写出：{package.RootDirectory}");
            Console.WriteLine($"  资产 {result.Registrations.Count} 条（{BuiltInCoreAssetSource.AssemblyName}）");
            Console.WriteLine(
                $"  {PluginAssetRegistrar.ManifestFileName} / {AssetLockJson.FormalFileName} / {AssetLockJson.TestFileName}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"生成失败：{ex.Message}");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
