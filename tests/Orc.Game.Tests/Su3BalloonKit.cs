using Orc.Cards;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡包夹具：「苏-3」（<c>packs/su3</c>，包 id <c>su3</c>）与「漂浮气球」（<c>packs/balloon</c>，包 id <c>balloon</c>）。
/// 五段管道与 <see cref="SimInfiltrationKit"/> 同构（装载 → 声明 → 注册 → 初始化 → 使用）。
/// </summary>
internal static class Su3BalloonKit
{
    internal const string Su3Id = "su3.su-3";
    internal const string BalloonId = "balloon.floating-balloon";
    internal const string PinPrefabId = "balloon.pin-on-turn-start";
    internal const string BlitzKeyword = "闪击";
    internal const string SuppressedKeyword = "被压制";

    internal sealed record PackData(
        IReadOnlyList<CardDefinitionEntry> Definitions,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations,
        IReadOnlyList<string> EffectDirectories);

    internal static bool TryLocate(
        out string su3Cards, out string balloonCards, out string su3Effects, out string balloonEffects)
    {
        var root = FindRepoRoot();
        su3Cards = Path.Combine(root, "packs", "su3", "cards");
        balloonCards = Path.Combine(root, "packs", "balloon", "cards");
        su3Effects = Path.Combine(root, "packs", "su3", "effects-v4");
        balloonEffects = Path.Combine(root, "packs", "balloon", "effects-v4");
        return Directory.Exists(su3Cards) && Directory.Exists(balloonCards)
            && Directory.Exists(su3Effects) && Directory.Exists(balloonEffects);
    }

    internal static PackData LoadPack(string su3Cards, string balloonCards, string su3Effects, string balloonEffects)
    {
        var left = CardDataLoader.LoadDirectory(su3Cards);
        var right = CardDataLoader.LoadDirectory(balloonCards);
        Assert.Empty(left.Failures);
        Assert.Empty(right.Failures);

        return new PackData(
            left.Definitions.Concat(right.Definitions).ToArray(),
            left.EffectDeclarations.Concat(right.EffectDeclarations).ToArray(),
            new[] { su3Effects, balloonEffects });
    }

    /// <summary>建局（手牌清空——断言基线可预期）。</summary>
    internal static async Task<Match> CreateMatchAsync(
        PackData pack, IEnumerable<string>? deckA = null, IEnumerable<string>? deckB = null)
    {
        var registry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in pack.Declarations)
        {
            registry.DeclarePrefab(cardId, prefabIds);
        }

        var match = new Match(
            new CardList(deckA ?? Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(deckB ?? Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(pack.Definitions),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            effectRegistry: registry,
            targeterBridge: CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        match.Engine.ScriptEvaluator ??= new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator ??= new CSharpSyncScriptEvaluator();
        foreach (var directory in pack.EffectDirectories)
        {
            var loaded = AssetPackLoader.Load(
                match.Engine.Model!, directory, match.Engine.ScriptEvaluator, match.Engine.SyncScriptEvaluator,
                handlerDsl: match.Engine.HandlerDsl);
            Assert.True(
                loaded.Loaded,
                "v4 装载失败：" + string.Join("；", loaded.Errors.Select(static e => $"{e.Category}/{e.Target}: {e.Message}")));
        }

        await match.Initialize();

        foreach (var player in match.Players)
        {
            foreach (var card in player.Hand.ToArray())
            {
                match.PlayerManager.ReturnToDeckTop(player, (CardBase)card);
            }
        }

        return match;
    }

    /// <summary>把一张卡放进手牌（不走抽牌链路——测试装配）。</summary>
    internal static async Task<CardBase> GiveAsync(Match match, Player player, string id)
    {
        var card = match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        player.Hand.Add(card);
        return card;
    }

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
