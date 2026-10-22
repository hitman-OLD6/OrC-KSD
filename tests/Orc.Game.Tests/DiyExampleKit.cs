using Orc.Cards;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Players;
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

    /// <summary>定位卡包两个目录（任一缺失＝调用方跳过：示例数据职责）。</summary>
    internal static bool TryLocatePack(out string cardsDirectory, out string effectsDirectory)
    {
        var packRoot = Path.Combine(FindRepoRoot(), "samples", "card-pack-diy");
        cardsDirectory = Path.Combine(packRoot, "cards");
        effectsDirectory = Path.Combine(packRoot, "effects");
        return Directory.Exists(cardsDirectory) && Directory.Exists(effectsDirectory);
    }

    /// <summary>装载卡目录（读面要求零失败/零警告；本包无内联快照）。</summary>
    internal static PackData LoadPack(string cardsDirectory)
    {
        var load = CardDataLoader.LoadDirectory(cardsDirectory);
        Assert.Empty(load.Failures);
        Assert.Empty(load.Warnings);
        return new PackData(load.Definitions, load.Prefabs, load.EffectDeclarations);
    }

    /// <summary>声明效果 → 装载效果库 → 建局初始化（注册先于卡加载）。</summary>
    internal static async Task<Match> CreateMatchAsync(
        PackData pack, string effectsDirectory, CardList? deckForPlayerA = null)
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
            options: new MatchOptions { SkipMulligan = true },
            effectRegistry: registry,
            packageActivator: TestHost.Activator);

        var library = match.Engine.Prefabs.LoadDirectory(effectsDirectory);
        Assert.Empty(library.Failures);

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
        => match.Battlefield.GetSupportLine(player).GetInsertionGaps()[0];

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
