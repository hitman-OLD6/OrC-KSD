using Orc.Game;
using Orc.Mcp;
using Orc.Packages;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S3/S7 收口：**全仓仅一份卡引用解析实现**——`AssetIdBridge`。
/// <para>
/// 背景：`MatchContentLoader.ResolveCardId` 是过渡实现（S3 明列「待 S7 接线时删除」）。S7 接线完成后
/// 仓库内不得再出现第二份「应用层 ID／裸局部 ID → 全名」的解析逻辑，否则同一歧义判定会有两个真源。
/// </para>
/// </summary>
public class CardIdResolutionSingleSourceTests
{
    [Fact]
    public void MatchContentLoader_Is_Gone()
    {
        var types = typeof(McpService).Assembly.GetTypes();
        Assert.DoesNotContain(types, type => type.Name == "MatchContentLoader");
        Assert.DoesNotContain(types, type => type.Name == "MatchContentLoadResult");
    }

    [Fact]
    public void No_Source_Declares_A_Second_Reference_Resolver()
    {
        // 过渡实现的入口名（删干净＝源码零命中）。
        const string legacySymbol = "ResolveCardId";

        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(legacySymbol, StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, $"{legacySymbol} 仍有残留实现：{string.Join("、", offenders)}");

        // 解析面唯一入口：bridge 的 Resolve 签名（全集＝这两条重载）。
        var methods = typeof(AssetIdBridge)
            .GetMethods()
            .Where(method => method.Name == "Resolve")
            .ToArray();
        Assert.Equal(2, methods.Length);
    }

    [Fact]
    public void Game_Layer_Does_Not_Know_Guids()
    {
        // 对局层（含 Match / CardLibrary / 端口）不引用资产层 ID：类型签名里不出现 Guid 形态的资产身份。
        var matchAssembly = typeof(Orc.Game.Match).Assembly;
        Assert.DoesNotContain(
            matchAssembly.GetTypes(),
            type => type.Name is "AssetRegistry" or "AssetIdBridge" or "AssetRegistration" or "AssetLock");
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
