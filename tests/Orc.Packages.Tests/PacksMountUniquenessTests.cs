using Orc.Core.Model;
using Orc.Game.Model;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R40 的存量面守护（机械复核，不靠逐份目检）**：
/// <list type="number">
///   <item>仓内 <c>packs/</c> 全部组件资产装载仍通过（零存量迁移的直接证据）；</item>
///   <item>全库组件资产**无 <c>extends</c>**、单份资产内部**无重复挂载／包装**——
///     把 R40 勘定的三个事实变成可执行断言，防止将来新增资产静默踩雷。</item>
/// </list>
/// <para>
/// 模板库（<c>packs/effect-templates/</c>）不进对局装载链（R33），只参与第 2 条的静态断言。
/// </para>
/// </summary>
public class PacksMountUniquenessTests
{
    [Fact]
    public void 全部卡包的组件资产装载仍通过()
    {
        var packsRoot = Path.Combine(FindRepoRoot(), "packs");
        var packDirectories = Directory.EnumerateDirectories(packsRoot)
            .Where(static d => File.Exists(Path.Combine(d, "manifest.json")))
            .Where(static d => Directory.Exists(Path.Combine(d, "effects-v4")))
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(packDirectories);

        foreach (var pack in packDirectories)
        {
            var load = AssetPackLoader.Load(
                GameEnginePrefabs.CreateRuntime(),
                Path.Combine(pack, "effects-v4"),
                new CSharpScriptEvaluator(),
                new CSharpSyncScriptEvaluator(),
                packId: Path.GetFileName(pack));

            Assert.True(
                load.Loaded,
                $"包 '{Path.GetFileName(pack)}' 装载失败：" +
                string.Join("；", load.Errors.Select(static e => $"[{e.Category}] {e.Target}：{e.Message}")));
            Assert.NotEmpty(load.AssetIds);
        }
    }

    [Fact]
    public void 全库组件资产无extends且无重复挂载包装()
    {
        var root = FindRepoRoot();
        var files = new[] { "packs", "samples" }
            .Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(
                directory, "*.component.json", SearchOption.AllDirectories))
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var asset = AssetSchema.Read(File.ReadAllText(file), Path.GetFileName(file));

            // ① 全库无 extends（R40 勘定：168 份资产全部无 extends ⇒ 零存量迁移）。
            Assert.Null(asset.Extends);

            // ② 单份资产内部无重复 (target, member)（R40.1：同层重复同样拒绝）。
            var duplicateMount = asset.Mounts
                .GroupBy(static m => (m.Target, m.Member))
                .FirstOrDefault(static g => g.Count() > 1);
            Assert.True(
                duplicateMount is null,
                $"{Path.GetFileName(file)} 有重复挂载 '{duplicateMount?.Key.Target}' → '{duplicateMount?.Key.Member}'。");

            // ③ 单份资产内部无重复 (目标, phase, handler, seam)（R40.2 的包装四元组）。
            var duplicateWrap = asset.Wraps
                .GroupBy(static w => (w.Target.Key, w.Phase, w.HandlerId))
                .FirstOrDefault(static g => g.Count() > 1);
            Assert.True(
                duplicateWrap is null,
                $"{Path.GetFileName(file)} 有重复包装 '{duplicateWrap?.Key.Key}'（phase={duplicateWrap?.Key.Phase}，"
                + $"handler='{duplicateWrap?.Key.HandlerId}'）。");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("未能定位仓库根（OrcEngine.sln）。");
    }
}
