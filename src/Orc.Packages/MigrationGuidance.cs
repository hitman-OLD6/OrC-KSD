namespace Orc.Packages;

/// <summary>
/// 迁移指引（S10a）：**宿主启动期不写盘**，但未迁移包的报错须**可操作**——
/// 指名包绝对路径，并附一条可直接复制执行的迁移命令（<c>dotnet run --project … -- --apply --package "&lt;包绝对路径&gt;"</c>）。
/// <para>
/// 命令里的工具工程路径在仓库内解析为绝对路径（从应用基目录与包目录逐级向上找
/// <c>tools/Orc.Tools.Migrate/Orc.Tools.Migrate.csproj</c>）；找不到时退化为 <c>&lt;仓库&gt;/tools/…</c> 占位，
/// 包路径始终为绝对路径。
/// </para>
/// </summary>
public static class MigrationGuidance
{
    /// <summary>迁移工具工程目录名。</summary>
    public const string ToolProjectDirectoryName = "Orc.Tools.Migrate";

    /// <summary>迁移工具工程文件名。</summary>
    public const string ToolProjectFileName = "Orc.Tools.Migrate.csproj";

    /// <summary>工具工程的仓库相对路径（<c>tools/Orc.Tools.Migrate</c>）。</summary>
    public const string ToolProjectRelativePath = "tools/" + ToolProjectDirectoryName;

    /// <summary>
    /// 生成可直接复制执行的迁移命令（含**包绝对路径**）。
    /// </summary>
    /// <param name="packageRoot">包根目录（相对路径按当前目录解析为绝对路径）。</param>
    /// <param name="toolProjectDirectory">工具工程目录（测试／宿主显式指定；缺省自动向上查找）。</param>
    /// <exception cref="ArgumentException">packageRoot 为 null/空白。</exception>
    public static string CommandFor(string packageRoot, string? toolProjectDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        var project = toolProjectDirectory is { Length: > 0 }
            ? Path.Combine(Path.GetFullPath(toolProjectDirectory), ToolProjectFileName)
            : FindToolProject(root);

        var projectArgument = project ?? Path.Combine("<仓库>", ToolProjectRelativePath.Replace('/', Path.DirectorySeparatorChar), ToolProjectFileName);
        return $"dotnet run --project \"{projectArgument}\" -- --apply --package \"{root}\"";
    }

    /// <summary>报错尾注：<c>{问题描述}（包：{绝对路径}）。修复：{可复制命令}</c>。</summary>
    /// <exception cref="ArgumentException">packageRoot 或 problem 为 null/空白。</exception>
    public static string ForPackage(string packageRoot, string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return $"{problem}（包：{Path.GetFullPath(packageRoot)}）。修复：{CommandFor(packageRoot)}";
    }

    /// <summary>
    /// 解析迁移工具工程文件（先看应用基目录，再看包目录的祖先；找不到＝null）。
    /// </summary>
    /// <param name="packageRoot">包根目录（可为 null）。</param>
    public static string? FindToolProject(string? packageRoot = null)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, packageRoot })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "tools", ToolProjectDirectoryName, ToolProjectFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
