using Orc.Cards;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Players;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// DIY 示例共用夹具：定位 <c>samples/card-pack-diy</c>、装载数据、注册效果、建局。
/// 与 <see cref="CardPackDemoTests"/> 同一五段管道（装载→声明→注册→初始化→使用），供示例 1/2/3/5 复用。
/// </summary>
internal static class DiyExampleKit
{
    internal sealed record PackData(
        IReadOnlyList<CardDefinitionEntry> Definitions,
        IReadOnlyList<EffectSnapshot> Prefabs,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations);

    /// <summary>
    /// 定位卡包两个目录（任一缺失＝调用方跳过：示例数据职责）。
    /// <para>
    /// v4-only（§23.8 第 1 项）：行为资产的装载目录是 <c>effects-v4/</c>（组件资产），旧的 <c>effects/</c>
    /// 已归档到 <c>samples/_legacy/</c> 且不再参与装载。
    /// </para>
    /// </summary>
    internal static bool TryLocatePack(out string cardsDirectory, out string componentsDirectory)
    {
        var packRoot = Path.Combine(FindRepoRoot(), "samples", "card-pack-diy");
        cardsDirectory = Path.Combine(packRoot, "cards");
        componentsDirectory = Path.Combine(packRoot, "effects-v4");
        return Directory.Exists(cardsDirectory) && Directory.Exists(componentsDirectory);
    }

    /// <summary>装载卡目录（读面要求零失败/零警告；本包无内联快照）。</summary>
    internal static PackData LoadPack(string cardsDirectory)
    {
        var load = CardDataLoader.LoadDirectory(cardsDirectory);
        Assert.Empty(load.Failures);
        Assert.Empty(load.Warnings);
        return new PackData(load.Definitions, load.Prefabs, load.EffectDeclarations);
    }

    /// <summary>声明效果 → 装载 v4 组件资产 → 建局初始化（注册先于卡加载）。</summary>
    internal static async Task<Match> CreateMatchAsync(
        PackData pack, string componentsDirectory, CardList? deckForPlayerA = null, bool allowRandomTargeting = false)
    {
        var registry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in pack.Declarations)
        {
            registry.DeclarePrefab(cardId, prefabIds);
        }

        var match = new Match(
            deckForPlayerA ?? new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(pack.Definitions),
            seed: 42,
            // S9／R2：sel=one 属"外部裁定"——用例扮演前端（自动选首个）；random 需显式开启（R2c）。
            options: new MatchOptions { SkipMulligan = true, AllowRandomTargeting = allowRandomTargeting },
            effectRegistry: registry,
            targeterBridge: CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        // v4-only：效果资产的唯一装载面是组件资产目录（AssetPackLoader 走"全量校验 → 统一注册"）。
        match.Engine.ScriptEvaluator ??= new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator ??= new CSharpSyncScriptEvaluator();
        var loaded = AssetPackLoader.Load(
            match.Engine.Model!, componentsDirectory, match.Engine.ScriptEvaluator, match.Engine.SyncScriptEvaluator,
            // 真实链路：Match 构造期已装配 handler DSL（`Engine.HandlerDsl ??= HandlerDslAssembly.Create()`），
            // 装载器必须拿到同一份——否则含 DSL 成员的资产会被判成「本环境未装配编译器」（capability）。
            handlerDsl: match.Engine.HandlerDsl);
        Assert.True(
            loaded.Loaded,
            "v4 装载失败：" + string.Join("；", loaded.Errors.Select(static e => $"{e.Category}/{e.Target}: {e.Message}")));

        await match.Initialize();
        return match;
    }

    /// <summary>实例化一张指令卡→加载→入手→补足点数（示例 1/2/3 使用）。</summary>
    internal static async Task<OrderCard> PrepareCommandAsync(Match match, Player player, string cardId)
    {
        var card = (OrderCard)match.CardLibrary.Instantiate(cardId);
        await card.LoadAsync(player);
        player.Hand.Add(card);
        await match.ResourceManager.AddPointsAsync(player, 5);
        return card;
    }

    internal static FormationGap FindEmptySupportSlot(Match match, Player player)
        => match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);

    internal static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
