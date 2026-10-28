using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Packages;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// A3 验收（v4 组件在**真实对局**里跑通）：
/// <list type="bullet">
/// <item>① <c>effects-v4/</c> 包经生产装载链（<see cref="PackageRegistrar"/> → <see cref="CardPackAssembler"/>）进对局；</item>
/// <item>② 卡加载时组件装配 → 审查面出现 <c>engine#&lt;信号&gt;</c> 上的边（"边建了"）；</item>
/// <item>③ 真实打出产生信号 → 组件 handler 执行 → 世界状态变化（"信号来了→handler 真跑"）；</item>
/// <item>④ 同一信号上 v4 组件边与旧端点边**并存**时的执行序（新面先跑、旧面后跑）。</item>
/// </list>
/// <para>
/// 为什么必须是对局级用例：A2 之前 v4 组件的边永远不执行（<c>EmitAsync</c> 生产调用点为 0），
/// 迁移等价用例都是**显式** <c>EmitAsync</c>；本文件是第一条"生产发射 → 组件 handler"的端到端证据。
/// </para>
/// </summary>
public class V4PackInMatchTests
{
    private const string VanguardId = "pack/demo/demo.vanguard";
    private const string RallyComponentId = "demo.rally";

    // ---------- ① 生产装载 + ② 边建立 + ③ handler 真跑 ----------


    // ---------- ④ 联合次序：同信号上 v4 组件边 + 旧端点边 ----------


    // ---------- ⑤ v4 指令卡：施放走组件成员（不经 ActiveEffect/ICastAction） ----------


    // ---------- 夹具 ----------

    private static Task<Match> CreateDemoV4MatchAsync() => CreateV4MatchAsync("card-pack-demo");

    private static Task<Match> CreateDiyV4MatchAsync() => CreateV4MatchAsync("card-pack-diy");

    /// <summary>按生产链路装配一个 v4 包：<see cref="PackageRegistrar"/> → <see cref="CardPackAssembler"/> → Initialize。</summary>
    private static async Task<Match> CreateV4MatchAsync(string packDirectory)
    {
        var root = Path.Combine(FindRepoRoot(), "samples", packDirectory);
        Assert.True(Directory.Exists(root), $"示例包缺失：{root}");

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var content = PackageRegistrar.Register(root, manifest, new AssetRegistry()).Content;
        Assert.NotNull(content.ComponentsDirectory); // 该包提供 effects-v4/（生产装载只装 v4）

        var registry = new CardEffectRegistry();
        var match = new Match(
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(content.Definitions),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true, AllowRandomTargeting = true },
            effectRegistry: registry,
            targeterBridge: CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        // 生产装载：注册先于卡加载（与宿主装配序一致）。
        CardPackAssembler.Attach(registry, match.Engine, new[] { content });
        Assert.NotEmpty(match.Engine.Model!.Catalog.Definitions);

        await match.Initialize();
        return match;
    }

    private static FormationGap FindEmptySupportSlot(Match match, Orc.Game.Players.Player player)
        => match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
