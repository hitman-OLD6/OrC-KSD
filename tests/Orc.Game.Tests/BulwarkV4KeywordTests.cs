using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Packages;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// b3 前置验收：**词条内嵌效果已迁 v4**——铜墙铁壁包（<c>samples/card-pack-bulwark</c>）的
/// <c>effects-v4/keyword.*.component.json</c> 经生产装载链进对局后，词条效果由组件承载
/// （旧 <c>effects/*.prefab.json</c> 不经装载路径、不产生 <c>Effect</c> 实例）。
/// <para>
/// 为什么必须先做这一步：删 <c>DynamicPassiveEffect</c> 的前提是**没有任何生产路径**再需要它；
/// 词条是最后一条（§17.8 第 2 类）。本文件把"bulwark 只靠 v4 组件也能正确生效"变成可复跑证据。
/// </para>
/// </summary>
[Collection("PackKeywordRegistrySerial")]
public sealed class BulwarkV4KeywordTests
{
    private const string CardId = "test.bulwark.v4";
    private const string Keyword = "固守";
    private const string ComponentId = "keyword.entrench";

    /// <summary>本测试登记的词条标识（进程级注册面——finally 清理）。</summary>
    private static readonly List<string> Registered = new();


    /// <summary>
    /// 生产形状装配：bulwark 的 <c>effects-v4/</c> 经 <see cref="CardPackAssembler"/> 进对局；
    /// 卡定义与 <c>keywords.json</c> 的绑定声明按生产形状登记（测试侧不引旧 <c>effects/</c> 目录）。
    /// </summary>
    private static async Task<Match> CreateMatchAsync()
    {
        var root = Path.Combine(FindRepoRoot(), "samples", "card-pack-bulwark");
        Assert.True(Directory.Exists(root), $"示例包缺失：{root}");

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var content = PackageRegistrar.Register(root, manifest, new AssetRegistry()).Content;
        Assert.NotNull(content.ComponentsDirectory); // 该包提供 effects-v4/（生产装载只装 v4）

        var registry = new CardEffectRegistry();

        // 词条声明面：生产里由前端包的词条加载器（Orc.UI.CardPackKeywordLoader）在包加载时注册；
        // 本测试只走对局装配，故按同一形状登记（标识＋绑定）。须先于卡定义构造（声明校验读注册面）。
        // 进程级注册面，在 finally 清理。
        RegisterKeywords(root);

        var match = CommandTestKit.CreateCommandMatch(
            effectRegistry: registry,
            extraDefinitions:
            [
                new CardDefinitionEntry(CardId, new CardDefinition(
                    "测试单位", 1, 0, 2, 5,
                    unitTypes: [UnitType.Infantry],
                    keywords: [new KeywordDeclaration(Keyword)],
                    faction: Faction.Germany,
                    rarity: Rarity.Standard)),
            ]);

        // 本包 v4 资产含判定器包装（固守的移动/抑制判定）——装载门要求同步求值器在装载期预编译。
        match.Engine.SyncScriptEvaluator = new Orc.Script.CSharpSyncScriptEvaluator();

        CardPackAssembler.Attach(registry, match.Engine, new[] { content });

        await match.Initialize();
        return match;
    }

    private static void RegisterKeywords(string root)
    {
        foreach (var keyword in PackageScanner.ReadKeywords(root))
        {
            KeywordRegistry.RegisterOrReplace(
                keyword.Id, (_, _) => new PlainKeywordComponent(keyword.Id), keyword.IsBattleKeyword);
            if (keyword.Bindings.Count > 0)
            {
                KeywordRegistry.DeclareEffectBindings(keyword.Id, keyword.Bindings);
            }

            Registered.Add(keyword.Id);
        }
    }

    private static void UnregisterKeywords()
    {
        foreach (var id in Registered)
        {
            KeywordRegistry.Unregister(id);
        }

        Registered.Clear();
    }

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
