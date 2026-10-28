using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Packages;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// b4 验收：**生产端发射接线后，样例包的 v4 面行为成立**（§21.3 那 7 条失败的逐条对应）。
/// <para>
/// 与旧面的验收套件（<see cref="SpaceWarKeywordTests"/>／<see cref="BulwarkKeywordTests"/>／
/// <see cref="DiyExample03_DestroyedCountTests"/>／<see cref="DiyExample05_DiscardFetchTests"/>）**并列**：
/// 那些用例走旧 <c>effects/</c> 预制体（旧效果链），本文件走 <c>effects-v4/</c> 组件（生产新面），
/// 断言语义逐条相同。两者并存的意义＝迁移期"两个世代都还能跑"，而不是"新面把旧面替掉"。
/// </para>
/// <para>
/// 为什么必须单独有一份：v4 组件的执行面**完全依赖成员发射**——成员只有声明、没有发射时，
/// 审查图（边、gap、位次）全绿而行为静默失效（§21.3 的教训）。本文件的每组断言都同时钉住
/// "行为发生了"与"边确实在 v4 面上"。
/// </para>
/// </summary>
[Collection("PackKeywordRegistrySerial")]
public sealed class V4SampleEmissionTests : IDisposable
{
    private const string Shield = "强磁护盾";
    private const string Reaction = "磁反应装甲";
    private const string Mobility = "机动";

    private static readonly string SpaceWarRoot = PackRootOf("card-pack-space-war");
    private static readonly string BulwarkRoot = PackRootOf("card-pack-bulwark");
    private static readonly string DiyRoot = PackRootOf("card-pack-diy");

    private static readonly object Gate = new();
    private static readonly List<string> Registered = new();

    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var id in Registered)
            {
                KeywordRegistry.Unregister(id);
            }

            Registered.Clear();
        }
    }

    // ---------- ① engine#unit-attack（§21.3 第 1 条） ----------

    [Fact]
    public async Task v4_强磁护盾_指挥攻击后失去_本次仍减伤()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateKeywordMatch(SpaceWarRoot, "test.v4.shield", [new KeywordDeclaration(Shield)], bridge);
        await match.Initialize();

        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.v4.shield", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        // 边在：`wraps: engine#unit-attack`（after）落成一条包装登记。
        Assert.True(
            match.Engine.Model!.Wraps.All.Any(w => w.Target.Key == "member:engine#unit-attack"),
            "engine#unit-attack 上没有组件包装登记");

        Assert.True(unit.Keywords.Has(Shield));
        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Players[1].Hq.Ref);

        Assert.True(result.IsSuccess);
        Assert.False(unit.Keywords.Has(Shield), "指挥攻击后应失去「强磁护盾」（本次减伤仍生效）");
    }

    // ---------- ② host#targeting（§21.3 第 2 条） ----------

    [Fact]
    public async Task v4_磁反应装甲_被敌方指向扣层_归零移除()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateKeywordMatch(SpaceWarRoot, "test.v4.reaction", [new KeywordDeclaration(Reaction, 2)], bridge);
        await match.Initialize();

        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.v4.reaction", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        // 边在：`mounts: host#targeting` 落在该单位实体上。
        var entity = unit.ModelEntity!;
        var hasTargetingEdge = match.Engine.Model!.ComponentsOf(entity)
            .SelectMany(static a => a.Handles)
            .Any(static h => h.Edge.Declaration == "host#targeting");
        Assert.True(hasTargetingEdge, "host#targeting 上没有组件挂载边");

        Assert.Equal(2, unit.Keywords.GetValue(Reaction));

        // 友方指向不扣层。
        await TargetingRules.NotifyAsync(match.Engine, unit, match.Players[0].Hq, TargetingRules.KindEffect);
        Assert.Equal(2, unit.Keywords.GetValue(Reaction));

        // 敌方指向：2 → 1。
        await TargetingRules.NotifyAsync(match.Engine, unit, match.Players[1].Hq, TargetingRules.KindEffect);
        Assert.Equal(1, unit.Keywords.GetValue(Reaction));

        // 再指向：1 → 0 ⇒ 失去词条。
        await TargetingRules.NotifyAsync(match.Engine, unit, match.Players[1].Hq, TargetingRules.KindCommand);
        Assert.False(unit.Keywords.Has(Reaction));
    }

    // ---------- ③ engine#action-budget（§21.3 第 3、4 条） ----------

    [Fact]
    public async Task v4_机动_首次攻击免额度消耗_并失去词条()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateKeywordMatch(BulwarkRoot, "test.v4.mobility", [new KeywordDeclaration(Mobility)], bridge);
        await match.Initialize();

        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.v4.mobility", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        Assert.True(
            match.Engine.Model!.Engine.FindEdges("action-budget")?.Count > 0,
            "engine#action-budget 上没有组件挂载边");

        var quotaBefore = unit.GetData<CommandData>().AttackQuota;
        Assert.True(unit.Keywords.Has(Mobility));
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Players[1].Hq.Ref)).IsSuccess);

        Assert.False(unit.Keywords.Has(Mobility));
        Assert.Equal(quotaBefore, unit.GetData<CommandData>().AttackQuota); // 本次攻击未扣（免费）
    }

    [Fact]
    public async Task v4_机动_奋战_先攻击可打三次且不能移动()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateKeywordMatch(
            BulwarkRoot, "test.v4.fury", [new KeywordDeclaration(Mobility), new KeywordDeclaration(KeywordIds.Fury)], bridge);
        await match.Initialize();

        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.v4.fury", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        Assert.Equal(2, unit.GetData<CommandData>().AttackQuota); // 奋战：基础 +1

        var hq = match.Players[1].Hq.Ref;
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);

        Assert.False(unit.Keywords.Has(Mobility));
        Assert.False(match.CommandManager.GetCommandAvailability(unit).Move.CanUse, "机动+奋战先攻击：不能移动");
        Assert.False((await match.CommandManager.BeginAttackAsync(unit)).IsSuccess);
    }

    // ---------- ④ card.discarded / card.died（§21.3 第 5、6 条） ----------

    [Fact]
    public async Task v4_战地救护_敌方弃置友方手牌_从卡组抽取自身()
    {
        var match = await CreateDiyV4MatchAsync(
            new CardList(new[] { "diy.field-rescue" }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9))));
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var rescue = DiyRescueOf(playerA);
        if (!playerA.Deck.ContainsInstance(rescue))
        {
            match.PlayerManager.ReturnToDeckTop(playerA, rescue);
        }

        Assert.True(playerA.Deck.ContainsInstance(rescue));
        Assert.True(
            match.Engine.Model!.Engine.FindEdges(GameUpdates.CardDiscarded)?.Count > 0,
            "engine#card.discarded 上没有组件挂载边");

        var enemyCard = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var victim = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var deckBefore = playerA.Deck.Count;

        await match.PlayerManager.DiscardCardAsync(playerA, victim, source: enemyCard);

        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.Contains(rescue, playerA.Hand);
    }

    [Fact]
    public async Task v4_纪奠_打出_按本局被摧毁单位数回复总部()
    {
        var match = await CreateDiyV4MatchAsync();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var command = (OrderCard)match.CardLibrary.Instantiate("diy.memorial");
        await command.LoadAsync(playerA);
        playerA.Hand.Add(command);
        await match.ResourceManager.AddPointsAsync(playerA, 5);

        Assert.True(
            match.Engine.Model!.Engine.FindEdges(GameUpdates.CardDied)?.Count > 0,
            "engine#card.died 上没有组件挂载边");

        // 双方各阵亡一个单位。
        var ownVictim = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var enemyVictim = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.True(await Orc.Game.Effects.EffectRuntime.ResolveFor(ownVictim)!.KillAsync(ownVictim));
        Assert.True(await Orc.Game.Effects.EffectRuntime.ResolveFor(enemyVictim)!.KillAsync(enemyVictim));

        Assert.Equal(2, match.Engine.Variables.Get<int>("diy.example3.destroyed"));

        var hqBefore = playerA.Hq.Health;
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(hqBefore + 2, playerA.Hq.Health);
    }

    // ---------- 夹具 ----------

    /// <summary>
    /// 按生产形状建局：定制卡定义（含词条声明）＋ 该包的 <c>effects-v4/</c> 经
    /// <see cref="AssetPackLoader"/> 装进编排运行时（**只装 v4**，旧 <c>effects/</c> 不参与）。
    /// </summary>
    private static Match CreateKeywordMatch(
        string packRoot, string cardId, IReadOnlyList<KeywordDeclaration> keywords, MockTargeterBridge bridge)
    {
        foreach (var declaration in keywords)
        {
            EnsureKeyword(packRoot, declaration.Id);
        }

        var match = CommandTestKit.CreateCommandMatch(
            bridge,
            extraDefinitions:
            [
                new CardDefinitionEntry(cardId, new CardDefinition(
                    "测试单位", 1, 0, 2, 5,
                    unitTypes: [UnitType.Infantry],
                    keywords: keywords,
                    faction: Faction.Germany,
                    rarity: Rarity.Standard)),
            ]);

        match.Engine.ScriptEvaluator = new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator = new CSharpSyncScriptEvaluator();
        LoadV4(match, packRoot);
        return match;
    }

    private static void LoadV4(Match match, string packRoot)
    {
        var directory = Path.Combine(packRoot, "effects-v4");
        Assert.True(Directory.Exists(directory), $"v4 资产目录缺失：{directory}");

        var loaded = AssetPackLoader.Load(
            match.Engine.Model!, directory, match.Engine.ScriptEvaluator, match.Engine.SyncScriptEvaluator);
        Assert.True(
            loaded.Loaded,
            "v4 装载失败：" + string.Join("；", loaded.Errors.Select(static e => $"{e.Category}/{e.Target}: {e.Message}")));
    }

    /// <summary>DIY 包：卡定义取自 <c>cards/</c>（裸 id），组件取自 <c>effects-v4/</c>（生产装载面）。</summary>
    private static async Task<Match> CreateDiyV4MatchAsync(CardList? deckForPlayerA = null)
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out _))
        {
            throw new InvalidOperationException("DIY 示例包缺失（测试装配错误）。");
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var registry = new CardEffectRegistry();
        var match = new Match(
            deckForPlayerA ?? new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(pack.Definitions),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true, AllowRandomTargeting = true },
            effectRegistry: registry,
            targeterBridge: CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        match.Engine.SyncScriptEvaluator = new CSharpSyncScriptEvaluator();
        LoadV4(match, DiyRoot);

        await match.Initialize();
        return match;
    }

    private static UnitCard DiyRescueOf(Player player)
    {
        for (var i = 0; i < player.Deck.Count; i++)
        {
            if (string.Equals(player.Deck[i], "diy.field-rescue", StringComparison.Ordinal))
            {
                return (UnitCard)player.Deck.PeekInstance(i);
            }
        }

        foreach (var card in player.Hand)
        {
            if (card is UnitCard unit && string.Equals(unit.Definition.Name, "战地救护", StringComparison.Ordinal))
            {
                return unit;
            }
        }

        throw new InvalidOperationException("未找到战地救护宿主卡（测试装配错误）。");
    }

    /// <summary>词条声明面：生产里由前端包加载器在包加载时注册；本套件按同一形状登记（并声明 v4 组件绑定）。</summary>
    private static void EnsureKeyword(string packRoot, string keyword)
    {
        lock (Gate)
        {
            if (!KeywordRegistry.IsDefined(keyword))
            {
                KeywordRegistry.Register(keyword, (_, value) => new PlainKeywordComponent(keyword, value), isBattleKeyword: false);
                Registered.Add(keyword);
            }

            foreach (var declared in PackageScanner.ReadKeywords(packRoot))
            {
                if (!string.Equals(declared.Id, keyword, StringComparison.Ordinal) || declared.Bindings.Count == 0)
                {
                    continue;
                }

                KeywordRegistry.DeclareEffectBindings(keyword, declared.Bindings);
            }
        }
    }

    private static string PackRootOf(string pack)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "samples", pack);
    }
}
