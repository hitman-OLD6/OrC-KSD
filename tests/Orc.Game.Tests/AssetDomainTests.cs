using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S6 资产域（R14/D20）验收：域标记**随实例走**（实例可查 <c>IsTestAsset</c>）、卡库条目记录域，
/// 且**全仓无玩法逻辑读取域标记**（静态检索为证）。
/// </summary>
public class AssetDomainTests
{
    private static CardDefinition Definition(string name)
        => new(name, deployCost: 1, operateCost: 1, attack: 1, defense: 1,
            faction: Faction.Germany, rarity: Rarity.Standard);

    [Fact]
    public void Instances_Carry_Domain_From_Library_Entry()
    {
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("pack/demo/a", Definition("甲"), AssetDomain.Test);
        library.Register("pack/demo/b", Definition("乙"));

        var test = library.Instantiate("pack/demo/a");
        var formal = library.Instantiate("pack/demo/b");

        Assert.True(test.IsTestAsset);
        Assert.Equal(AssetDomain.Test, test.AssetDomain);
        Assert.False(formal.IsTestAsset);
        Assert.Equal(AssetDomain.Formal, formal.AssetDomain);
        Assert.Equal(AssetDomain.Test, library.DomainOf("pack/demo/a"));
    }

    [Fact]
    public void Replace_Updates_Domain_And_Instantiate_Sees_It()
    {
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("pack/demo/a", Definition("甲"));

        Assert.False(library.Instantiate("pack/demo/a").IsTestAsset);

        library.Replace("pack/demo/a", Definition("甲（测试）"), AssetDomain.Test);

        Assert.True(library.Instantiate("pack/demo/a").IsTestAsset);
    }

    [Fact]
    public void Remove_Detaches_Entry_And_Domain()
    {
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("pack/demo/a", Definition("甲"), AssetDomain.Test);

        Assert.True(library.Remove("pack/demo/a"));
        Assert.False(library.Contains("pack/demo/a"));
        Assert.False(library.Remove("pack/demo/a")); // 幂等
        Assert.Throws<KeyNotFoundException>(() => library.Instantiate("pack/demo/a"));
    }

    [Fact]
    public void No_Gameplay_Logic_Reads_Asset_Domain()
    {
        var root = RepositoryRoot();
        var allowedFiles = new[]
        {
            "src/Orc/Core/AssetDomain.cs",
            "src/Orc.Game/Cards/CardBase.cs",
            "src/Orc.Game/Cards/CardLibrary.cs",
            "src/Orc.Manager/AssetLibrary.cs",  // 资产管理面（域的合法消费方：按域枚举/对账资产条目）
            "src/Orc.Server/Pool/PackPool.cs",  // 卡池对账面（域的合法消费方：按域核对卡池装配来源）
        };

        var allowedPrefixes = new[]
        {
            "src/Orc.Game/Cards/Loading/",
            "src/Orc.Packages/",           // 注册/装载/报告面（非玩法）
            "src/Orc.Mcp/",                // 诊断工具面（域的合法消费方：报告）
            "src/Orc.UI/",                 // 展示面（域的合法消费方：UI）
            "src/Orc.UI.Contracts/",
        };

        var offenders = new List<string>();
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            // bin/obj 产物不参与静态检索（无源码语义）。
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var hitsDomain = text.Contains("IsTestAsset", StringComparison.Ordinal)
                || text.Contains("AssetDomain", StringComparison.Ordinal);
            if (!hitsDomain)
            {
                continue;
            }

            hits.Add(relative);
            if (allowedFiles.Contains(relative, StringComparer.Ordinal)
                || allowedPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "域标记被非授权位置读取（域的合法消费方只有构筑/存档、UI 展示、诊断报告）："
            + string.Join("、", offenders));

        // 检索面自检：已知的域载体必须命中（防止扫描面失效导致「空扫描即通过」）。
        Assert.Contains("src/Orc.Game/Cards/CardBase.cs", hits);
        Assert.Contains("src/Orc.Game/Cards/CardLibrary.cs", hits);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（OrcEngine.slnx）。");
    }
}
