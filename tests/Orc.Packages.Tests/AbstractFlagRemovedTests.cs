using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S32／D48 第 3 条**：换判据后，`abstract` 字段在**装载路径零读取**。
/// <para>
/// 措辞按 S14 的放宽口径：被禁的只有「用 <c>abstract</c> 判『是否需要实现』」——资产级「引用未饱和资产」
/// 拒绝（S28）与产物字段拒绝（schema 读到即拒）不算违反。模型上的 `Abstract` 属性已被删除，
/// 本断言把「不要再加回来」钉在源码层（行为层面由 `AbstractAssetTests` 覆盖）。
/// </para>
/// </summary>
public class AbstractFlagRemovedTests
{
    [Fact]
    public void 装载与展开路径不读取成员级abstract标志()
    {
        // 四个消费点：目录展开、产物装载、编辑器投影、共用核。
        foreach (var relative in new[]
                 {
                     "src/Orc/Core/Model/Prefabs.cs",
                     "src/Orc/Core/Model/AssetPackLoader.cs",
                     "src/Orc.Packages/Editing/PrefabDescriber.cs",
                     "src/Orc/Core/Model/PrefabExpansion.cs",
                 })
        {
            var path = Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"源文件缺失：{path}");

            var lineNumber = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                lineNumber++;
                var code = line;
                var comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    code = code[..comment];
                }

                foreach (var forbidden in new[] { ".Abstract", "abstractMember", "isAbstract", "AuthorizesModing" })
                {
                    Assert.False(
                        code.Contains(forbidden, StringComparison.Ordinal),
                        $"{relative}:{lineNumber} 仍在读取已废止的抽象标志（'{forbidden}'）：{line.Trim()}");
                }
            }
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
