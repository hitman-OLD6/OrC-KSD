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
/// 示例卡包（批 7·交付演示）验收：<c>samples/card-pack-demo</c> 分发数据（2 卡目录 ＋ 1 效果库目录）
/// 的「json → 游戏内卡牌」全链演示——
/// ① 预检（<see cref="CardDistributionValidator"/>：双零 ＋ 统计面）；
/// ② 装载（<see cref="CardDataLoader"/> 逐目录读入，跨目录共享引用不触发同目录软隔离）；
/// ③ 注册（<c>DeclarePrefab</c> 声明 ＋ 内联 <c>RegisterPrefab</c> ＋ 效果库 <c>LoadDirectory</c>——注册先于卡加载）；
/// ④ 初始化（<c>Match.Initialize</c>：效果随卡装载）；
/// ⑤ 使用（真实打出／触发 → 效果行为断言；世界状态优先）。
/// 覆盖形态：基础单位（无效果）／内联 csx（对象态）／内联 csx（字符串态）／效果库引用（跨目录一效果两卡共享）／
/// 定向取卡（csx 受控面「从卡组抽取此牌」）。全部效果为 csx 形态——测试零 C# handler 注册（纯数据分发即可跑）；
/// 不依赖 outputs；目录缺失＝整组可辨识跳过（对齐 <see cref="OfficialCardDataSmokeTests"/> 的 FindRepoRoot 先例）。
/// 本文件同时是用法示例：五段管道按序组织、逐步骤注释。
/// </summary>
public class CardPackDemoTests
{
    // ---------- 示例包 id 契约（与 samples/card-pack-demo 数据一一对应） ----------

    private const string RiflemanId = "demo.rifleman";
    private const string GrenadierId = "demo.grenadier";
    private const string QuartermasterId = "demo.quartermaster";
    private const string VanguardId = "demo.vanguard";
    private const string ScoutId = "demo.scout";
    private const string ScoutName = "侦察兵";
    private const string GrenadeEffectId = "demo.grenade";
    private const string RallyEffectId = "demo.rally";
    private const string EnlistEffectId = "demo.enlist";

    // ==================== ① 预检（dry-run 校验） ====================

    [Fact]
    public void 预检_示例包报双零与统计面()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过（演示数据职责——对齐 OfficialCardDataSmokeTests 先例）。
        }

        // ① 预检：分发数据静态校验（多目录合并；不注册、不建对局）。
        var report = CardDistributionValidator.Validate(
            new[] { cardsDirectory, supportDirectory }, new[] { libraryDirectory });

        // 问题面：Errors / Warnings 双零（含跨目录共享引用面）。
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
        Assert.True(report.HasNoErrors);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(0, report.UnresolvedReferenceCount);

        // 输入面：2 卡目录 ＋ 1 效果库目录。
        Assert.Equal(2, report.CardDirectoryCount);
        Assert.Equal(1, report.PrefabLibraryDirectoryCount);

        // 通过面：成功卡 5；成功快照 3（库 1 ＋ 内联 2）。
        Assert.Equal(5, report.SuccessfulCardCount);
        Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
        Assert.Equal(2, report.SuccessfulInlineSnapshotCount);
        Assert.Equal(3, report.SuccessfulSnapshotCount);
    }

    // ==================== ② 装载 → ③ 注册 → ④ 初始化（链路就绪） ====================

    [Fact]
    public async Task 装载与注册_五卡两内联一库全链就绪()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过。
        }

        // ② 装载：逐目录读入（纯数据产出；跨目录共享引用不触发同目录软隔离）。
        var pack = LoadPack(cardsDirectory, supportDirectory);

        Assert.Equal(5, pack.Definitions.Count); // 五卡（步枪兵／掷弹兵／军需官／先锋／侦察兵）
        Assert.Equal(2, pack.Prefabs.Count);     // 两条内联（demo.grenade／demo.enlist）

        // 效果声明：四张带效果卡各一条（步枪兵无效果＝无声明；内联先注册、以 id 并入声明）。
        Assert.Equal(4, pack.Declarations.Count);
        AssertDeclaration(pack, GrenadierId, GrenadeEffectId);
        AssertDeclaration(pack, ScoutId, EnlistEffectId);
        AssertDeclaration(pack, QuartermasterId, RallyEffectId);
        AssertDeclaration(pack, VanguardId, RallyEffectId);

        // ③ 注册 → ④ 初始化（注册先于卡加载——引用就绪前提）。
        var match = await CreatePackMatchAsync(pack, libraryDirectory);

        // 注册面就绪：库效果与两条内联效果均已在引擎预制体库。
        Assert.True(match.Engine.Prefabs.TryGetPrefab(RallyEffectId, out _));
        Assert.True(match.Engine.Prefabs.TryGetPrefab(GrenadeEffectId, out _));
        Assert.True(match.Engine.Prefabs.TryGetPrefab(EnlistEffectId, out _));
    }

    // ==================== ⑤ 使用：基础单位卡（无效果） ====================

    [Fact]
    public async Task 基础单位卡_真实打出_数值就绪()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过。
        }

        var match = await CreatePackMatchAsync(LoadPack(cardsDirectory, supportDirectory), libraryDirectory);
        var playerA = match.Players[0];

        // ⑤ 使用：真实打出（部署链驱动；步枪兵 0 费、数值 1 / 2）。
        var rifleman = await CommandTestKit.InstantiateLoadedAsync(match, playerA, RiflemanId, toHand: true);
        var slot = FindEmptySupportSlot(match, playerA);
        var result = await match.PlayManager.PlayUnitAsync(rifleman, slot);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Contains(rifleman, slot.Line.Members);

        // 数值就绪（battleStats 经加载链装配）。
        var stats = rifleman.GetData<BattleStatsData>();
        Assert.Equal(1, stats.Attack);
        Assert.Equal(2, stats.Defense);
    }

    // ==================== ⑤ 使用：内联 csx 效果卡（对象态） ====================

    [Fact]
    public async Task 内联csx效果卡_真实打出_对敌方造成伤害()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过。
        }

        var match = await CreatePackMatchAsync(LoadPack(cardsDirectory, supportDirectory), libraryDirectory);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 先备敌方目标（加入路径入场）：轰炸机（防御 2）。
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 0);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        // ⑤ 使用：真实打出掷弹兵（1 费）→ unit.deployed → 内联 csx 真编译执行（对敌方一个单位 1 伤）。
        var grenadier = await CommandTestKit.InstantiateLoadedAsync(match, playerA, GrenadierId, toHand: true);
        var result = await match.PlayManager.PlayUnitAsync(grenadier, FindEmptySupportSlot(match, playerA));
        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 行为断言（世界状态）：敌方防御 2 → 1（效果真实生效）。
        Assert.Equal(1, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }

    // ==================== ⑤ 使用：共享库效果卡（跨目录两张各自打出） ====================

    [Fact]
    public async Task 共享库效果卡_跨目录两张各自打出_各自触发()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过。
        }

        var match = await CreatePackMatchAsync(LoadPack(cardsDirectory, supportDirectory), libraryDirectory);
        var playerA = match.Players[0];
        using var recorder = new UpdateRecorder(match.Engine);

        // ⑤ 使用·第一张（cards/ 目录）：军需官（0 费）→ 集结号（demo.rally）触发 → 从卡组抽 1 张牌。
        var quartermaster = await CommandTestKit.InstantiateLoadedAsync(match, playerA, QuartermasterId, toHand: true);
        recorder.Clear();
        var deckBefore1 = playerA.Deck.Count;
        var result1 = await match.PlayManager.PlayUnitAsync(quartermaster, FindEmptySupportSlot(match, playerA));

        Assert.Equal(PlayResultStatus.Success, result1.Status);
        Assert.Equal(deckBefore1 - 1, playerA.Deck.Count); // 抽牌：卡组 -1
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));   // 抽牌信号恰一次
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd)); // 进手牌恰一次

        // ⑤ 使用·第二张（cards-support/ 目录）：先锋（1 费）——同一库效果、另一张卡各自可用。
        var vanguard = await CommandTestKit.InstantiateLoadedAsync(match, playerA, VanguardId, toHand: true);
        recorder.Clear();
        var deckBefore2 = playerA.Deck.Count;
        var result2 = await match.PlayManager.PlayUnitAsync(vanguard, FindEmptySupportSlot(match, playerA));

        Assert.Equal(PlayResultStatus.Success, result2.Status);
        Assert.Equal(deckBefore2 - 1, playerA.Deck.Count);
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
    }

    // ==================== ⑤ 使用：定向取卡（csx「从卡组抽取此牌」） ====================

    [Fact]
    public async Task 定向取卡_加入触发_从卡组抽取自己()
    {
        if (!TryLocatePackDirectories(out var cardsDirectory, out var supportDirectory, out var libraryDirectory))
        {
            return; // 示例卡包目录缺失：跳过。
        }

        // 卡组期场景：侦察兵 ×1 ＋ 步兵 ×9（宿主以卡组期承载——起手装载可能将其抽入）。
        var deckForPlayerA = new CardList(new[] { ScoutId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9)));
        var match = await CreatePackMatchAsync(LoadPack(cardsDirectory, supportDirectory), libraryDirectory, deckForPlayerA);
        var playerA = match.Players[0];

        // 宿主就位：起手装载被抽入的场合回迁卡组顶（静默回迁）——确保其在卡组中等待触发。
        var scout = FindScout(playerA);
        if (!playerA.Deck.ContainsInstance(scout))
        {
            match.PlayerManager.ReturnToDeckTop(playerA, scout);
        }

        Assert.True(playerA.Deck.ContainsInstance(scout));

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;

        // ⑤ 使用：触发条件＝一个单位加入（unit.joined）→ 装载于侦察兵的 csx handler 执行 →
        //    经受控面 EffectRuntime.FetchFromDeckAsync(self) 从卡组抽取自己（不得绕过触发直接调 API）。
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 行为断言（世界状态）：卡组 -1、手牌 +1（宿主实例回手）。
        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Contains(scout, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(scout));

        // 信号断言：card.drawn → card.hand.add（恰一次、顺序、载荷 { 玩家, 卡牌实例 }）。
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(
            new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd },
            recorder.Types.Where(type => type is GameUpdates.CardDrawn or GameUpdates.CardHandAdd).ToList());
        var drawn = Assert.Single(recorder.Updates, update => update.Type == GameUpdates.CardDrawn);
        Assert.Same(scout, drawn.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, drawn.Payload![GameUpdates.PayloadPlayer]);
    }

    // ==================== 辅助：五段管道（装载/注册/初始化）与定位 ====================

    /// <summary>装载产出（合并两个卡目录的读面结果；逐目录装载以保持跨目录共享引用免于同目录软隔离）。</summary>
    private sealed record PackData(
        IReadOnlyList<CardDefinitionEntry> Definitions,
        IReadOnlyList<EffectSnapshot> Prefabs,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations);

    /// <summary>② 装载：逐目录读入卡数据体（读面隔离面要求零失败/零警告）。</summary>
    private static PackData LoadPack(string cardsDirectory, string supportDirectory)
    {
        var loadCards = CardDataLoader.LoadDirectory(cardsDirectory);
        Assert.Empty(loadCards.Failures);
        Assert.Empty(loadCards.Warnings);

        var loadSupport = CardDataLoader.LoadDirectory(supportDirectory);
        Assert.Empty(loadSupport.Failures);
        Assert.Empty(loadSupport.Warnings);

        return new PackData(
            loadCards.Definitions.Concat(loadSupport.Definitions).ToList(),
            loadCards.Prefabs.Concat(loadSupport.Prefabs).ToList(),
            loadCards.EffectDeclarations.Concat(loadSupport.EffectDeclarations).ToList());
    }

    /// <summary>
    /// ③ 注册 → ④ 初始化：效果声明注册（DeclarePrefab）＋ 内联预制体注册（RegisterPrefab）＋
    /// 效果库装载（LoadDirectory——含内部注册）；随后 <c>Match.Initialize</c>（效果随卡装载）。
    /// 默认卡组＝步兵 ×10（与 CommandTestKit 定制定义集同源）；示例包定义集追加。
    /// </summary>
    private static async Task<Match> CreatePackMatchAsync(
        PackData pack, string libraryDirectory, CardList? deckForPlayerA = null)
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

        foreach (var snapshot in pack.Prefabs)
        {
            match.Engine.Prefabs.RegisterPrefab(snapshot);
        }

        var libraryLoad = match.Engine.Prefabs.LoadDirectory(libraryDirectory);
        Assert.Equal(1, libraryLoad.Loaded); // demo.rally
        Assert.Empty(libraryLoad.Failures);

        await match.Initialize();
        return match;
    }

    /// <summary>断言某卡的效果声明清单（恰一条、恰含指定 prefab id）。</summary>
    private static void AssertDeclaration(PackData pack, string cardId, string prefabId)
    {
        var declaration = Assert.Single(pack.Declarations, item => item.CardId == cardId);
        Assert.Equal(new[] { prefabId }, declaration.PrefabIds);
    }

    /// <summary>定位示例卡包三个必需目录（任一缺失＝整组跳过；相对仓库根——对齐 OfficialCardDataSmokeTests 先例）。</summary>
    private static bool TryLocatePackDirectories(
        out string cardsDirectory, out string supportDirectory, out string libraryDirectory)
    {
        var packRoot = Path.Combine(FindRepoRoot(), "samples", "card-pack-demo");
        cardsDirectory = Path.Combine(packRoot, "cards");
        supportDirectory = Path.Combine(packRoot, "cards-support");
        libraryDirectory = Path.Combine(packRoot, "effects");
        return Directory.Exists(cardsDirectory)
            && Directory.Exists(supportDirectory)
            && Directory.Exists(libraryDirectory);
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

    /// <summary>第一个空的可部署支援线槽（按空槽过滤——自动跳过 HQ 占位槽）。</summary>
    private static FormationGap FindEmptySupportSlot(Match match, Player player)
        => match.Battlefield.GetSupportLine(player).GetInsertionGaps().At(0);

    /// <summary>在玩家容器（手牌/卡组）中定位侦察兵实例（起手装载可能将其抽入——效果随卡存续、回迁后即可取件）。</summary>
    private static UnitCard FindScout(Player player)
    {
        foreach (var card in player.Hand)
        {
            if (card is UnitCard unit && string.Equals(unit.Definition.Name, ScoutName, StringComparison.Ordinal))
            {
                return unit;
            }
        }

        for (var i = 0; i < player.Deck.Count; i++)
        {
            if (string.Equals(player.Deck[i], ScoutId, StringComparison.Ordinal))
            {
                return (UnitCard)player.Deck.PeekInstance(i);
            }
        }

        throw new InvalidOperationException("未找到侦察兵宿主卡（测试装配错误）。");
    }
}
