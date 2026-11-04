using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡包夹具：「SIM 渗透」（<c>packs/sim-infiltration</c>，包 id <c>sim</c>）。
/// 五段管道与 <see cref="DiyExampleKit"/> 同构（装载 → 声明 → 注册 → 初始化 → 使用）。
/// </summary>
internal static class SimInfiltrationKit
{
    internal const string CardId = "sim.infiltration";

    internal sealed record PackData(
        IReadOnlyList<CardDefinitionEntry> Definitions,
        IReadOnlyList<EffectSnapshot> Prefabs,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations);

    internal static bool TryLocate(out string cardsDirectory, out string effectsDirectory)
    {
        var packRoot = Path.Combine(FindRepoRoot(), "packs", "sim-infiltration");
        cardsDirectory = Path.Combine(packRoot, "cards");
        effectsDirectory = Path.Combine(packRoot, "effects-v4");
        return Directory.Exists(cardsDirectory) && Directory.Exists(effectsDirectory);
    }

    internal static PackData LoadPack(string cardsDirectory)
    {
        var load = CardDataLoader.LoadDirectory(cardsDirectory);
        Assert.Empty(load.Failures);
        Assert.Empty(load.Warnings);
        return new PackData(load.Definitions, load.Prefabs, load.EffectDeclarations);
    }

    /// <summary>建局（手牌清空——断言基线可预期）。</summary>
    internal static async Task<Match> CreateMatchAsync(
        PackData pack, string effectsDirectory, ITargeterBridge? bridge = null, bool withoutBridge = false)
    {
        var registry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in pack.Declarations)
        {
            registry.DeclarePrefab(cardId, prefabIds);
        }

        var match = new Match(
            new CardList(new[] { CardId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9))),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(pack.Definitions),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            effectRegistry: registry,
            targeterBridge: withoutBridge ? null : bridge ?? CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        match.Engine.ScriptEvaluator ??= new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator ??= new CSharpSyncScriptEvaluator();
        var loaded = AssetPackLoader.Load(
            match.Engine.Model!, effectsDirectory, match.Engine.ScriptEvaluator, match.Engine.SyncScriptEvaluator,
            handlerDsl: match.Engine.HandlerDsl);
        Assert.True(
            loaded.Loaded,
            "v4 装载失败：" + string.Join("；", loaded.Errors.Select(static e => $"{e.Category}/{e.Target}: {e.Message}")));

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

    /// <summary>发一张「SIM 渗透」并补足点数（部署费 4；给 5 点）。</summary>
    internal static async Task<OrderCard> GiveSimAsync(Match match, Player player)
    {
        var card = (OrderCard)match.CardLibrary.Instantiate(CardId);
        await card.LoadAsync(player);
        player.Hand.Add(card);
        await match.ResourceManager.AddPointsAsync(player, 5);
        return card;
    }

    /// <summary>发一张普通手牌（步兵——有效部署费 1）。</summary>
    internal static async Task<UnitCard> GiveInfantryAsync(Match match, Player player)
    {
        var card = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
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

/// <summary>
/// 脚本化前端桥接：按序执行「选第几张／取消」脚本（脚本用尽＝取消）。
/// 请求次序＝效果里的调用次序（每轮：施放者 → 对手）。
/// </summary>
internal sealed class ScriptedBridge
{
    private readonly Queue<Func<TargetingRequestDescription, Ref<Entity>?>> _steps = new();

    internal ScriptedBridge(params Func<TargetingRequestDescription, Ref<Entity>?>[] steps)
    {
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }

        Bridge = new MockTargeterBridge
        {
            InteractionScript = (description, responder) =>
            {
                var step = _steps.Count > 0 ? _steps.Dequeue() : null;
                var pick = step?.Invoke(description);
                if (pick is null)
                {
                    responder.Cancel(description.RequestId);
                }
                else
                {
                    responder.Complete(
                        description.RequestId,
                        TargeterTestKit.Selection(description.Slots[0].Name, pick));
                }
            },
        };
    }

    internal MockTargeterBridge Bridge { get; }

    internal static Func<TargetingRequestDescription, Ref<Entity>?> FirstCard
        => description => description.AllowedTargets.FirstOrDefault();

    internal static Func<TargetingRequestDescription, Ref<Entity>?> CancelRequest
        => _ => null;

    internal static Func<TargetingRequestDescription, Ref<Entity>?> PickCard(Card card)
        => description => description.AllowedTargets.FirstOrDefault(reference => ReferenceEquals(reference.Value, card));
}

/// <summary>
/// 「SIM 渗透」端到端（R2–R7）：三轮循环、花费比较、交换 / 洗入两条分支、无效轮、取消 / 失败 / 拒绝三条失败路径。
/// 全部断言走世界状态与信号面，不比日志文本（日志只在「拒绝留痕」用例里查条目编号）。
/// </summary>
public class SimInfiltrationEffectTests
{
    [Fact]
    public async Task 三轮_花费相同_每轮交换_六条手牌信号且零洗切()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var bridge = new ScriptedBridge(
            Enumerable.Repeat(ScriptedBridge.FirstCard, 6).ToArray());
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var a1 = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var a2 = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var b1 = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var b2 = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(sim);

        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 3 轮 × 2 张落位 = 6 条 card.hand.add；零洗切、零爆牌、零弃置
        Assert.Equal(6, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));

        // 归属随交换转移到对方；双方手牌总数不变（各 2）
        Assert.Equal(2, a.Hand.Count);
        Assert.Equal(2, b.Hand.Count);
        foreach (var card in new CardBase[] { a1, a2, b1, b2 })
        {
            Assert.True(card.Life.IsAlive);
            var owner = card.Owner!;
            Assert.Contains(card, owner.Hand);
        }

        // 三轮后的归属（每张牌参与轮数不同：参与两次＝回到原归属，一次＝翻到对方）
        Assert.Same(a, a1.Owner); // 第 1／3 轮参与 → 换两次
        Assert.Same(b, a2.Owner); // 第 2 轮参与 → 换一次
        Assert.Same(b, b1.Owner);
        Assert.Same(a, b2.Owner);

        // 本卡按 order 流程离手＋扣费（部署费 4）
        Assert.DoesNotContain(sim, a.Hand);
        Assert.Equal(pointsBefore - 4, a.Points);
    }

    [Fact]
    public async Task 花费不等_各自洗入对方卡组_两条洗切且零手牌信号()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var bridge = new ScriptedBridge(ScriptedBridge.FirstCard, ScriptedBridge.FirstCard);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var mine = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var theirs = await SimInfiltrationKit.GiveInfantryAsync(match, b);

        // R3：基准费相同、一方被 CostMod 改过 ⇒ 判「不相同」（比较读有效值）
        await a.EffectRuntime!.DeployCostModAsync(theirs, 1);
        Assert.Equal(1, mine.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Equal(2, theirs.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));

        var mineDeckBefore = a.Deck.Count;
        var theirsDeckBefore = b.Deck.Count;
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(sim);

        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 两张都离开手牌、进入对方卡组；归属已翻转
        Assert.Same(b, mine.Owner);
        Assert.Same(a, theirs.Owner);
        Assert.True(b.Deck.ContainsInstance(mine));
        Assert.True(a.Deck.ContainsInstance(theirs));
        Assert.Equal(mineDeckBefore + 1, a.Deck.Count);
        Assert.Equal(theirsDeckBefore + 1, b.Deck.Count);

        // 各一次洗切；装入静默（零 card.hand.add）
        Assert.Equal(2, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));

        // 第 2/3 轮：施放者手里只剩本卡 ⇒ 自动取消（无请求），卡照常离手扣费
        Assert.Equal(2, bridge.Bridge.Begins.Count);
        Assert.DoesNotContain(sim, a.Hand);
        Assert.Equal(pointsBefore - 4, a.Points);
    }

    [Fact]
    public async Task 无效轮_玩家取消_三轮各消耗一次且卡正常离手()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var bridge = new ScriptedBridge(
            Enumerable.Repeat(ScriptedBridge.CancelRequest, 3).ToArray());
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var mine = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var theirs = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(sim);

        Assert.Equal(PlayResultStatus.Success, result.Status);

        // 恰三轮尝试（施放者取消 ⇒ 该轮不再问对手）
        Assert.Equal(3, bridge.Bridge.Begins.Count);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));

        // 零归属变更、零转移；本卡照常离手扣费
        Assert.Same(a, mine.Owner);
        Assert.Same(b, theirs.Owner);
        Assert.Contains(mine, a.Hand);
        Assert.DoesNotContain(sim, a.Hand);
        Assert.Equal(pointsBefore - 4, a.Points);
    }

    [Fact]
    public async Task 第二轮无效_第三轮照常发起()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var bridge = new ScriptedBridge(
            ScriptedBridge.FirstCard,
            ScriptedBridge.FirstCard,
            ScriptedBridge.CancelRequest,
            ScriptedBridge.FirstCard,
            ScriptedBridge.FirstCard);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var a1 = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var a2 = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var b1 = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var b2 = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(sim);

        Assert.Equal(PlayResultStatus.Success, result.Status);

        Assert.Equal(5, bridge.Bridge.Begins.Count); // 2 ＋ 1（取消）＋ 2
        Assert.Equal(4, recorder.CountOf(GameUpdates.CardHandAdd)); // 两轮交换
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));

        Assert.Same(b, a1.Owner);
        Assert.Same(b, a2.Owner);
        Assert.Same(a, b1.Owner);
        Assert.Same(a, b2.Owner);
        Assert.Equal(pointsBefore - 4, a.Points);
    }

    [Fact]
    public async Task 手里只剩本卡_三轮全无效_无异常且卡仍离手()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var bridge = new ScriptedBridge(
            Enumerable.Repeat(ScriptedBridge.CancelRequest, 3).ToArray());
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var theirs = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayOrderAsync(sim);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Same(b, theirs.Owner);
        Assert.DoesNotContain(sim, a.Hand);
        Assert.Empty(a.Hand);
        Assert.Equal(pointsBefore - 4, a.Points);
    }

    [Fact]
    public async Task 系统失败_无桥接_抛异常_牌留手未扣费()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, withoutBridge: true);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var mine = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var theirs = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var pointsBefore = a.Points;

        using var recorder = new UpdateRecorder(match.Engine);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => match.PlayManager.PlayOrderAsync(sim));

        // Failed（系统原因）必须抛——不能静默吞成「这一轮什么都没发生」
        Assert.Contains("手牌选择系统失败", ex.Message, StringComparison.Ordinal);
        Assert.Contains(sim, a.Hand);
        Assert.Same(a, mine.Owner);
        Assert.Same(b, theirs.Owner);
        Assert.Equal(pointsBefore, a.Points); // 未扣费
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
    }

    [Fact]
    public async Task 动作层拒绝_归属与手牌不一致_抛异常含条目编号且牌留手()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var corrupted = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var theirs = await SimInfiltrationKit.GiveInfantryAsync(match, b);

        // 人为破坏不变量：卡仍留在 A 手牌，但归属被改成 B（归属与容器不一致）。
        // 选牌层不排除它（它确实在 A 的手牌里）——强制点在动作层。
        corrupted.ReassignOwner(b);

        using var recorder = new UpdateRecorder(match.Engine);
        var pointsBefore = a.Points;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => match.PlayManager.PlayOrderAsync(sim));

        Assert.Contains("B#5", ex.Message, StringComparison.Ordinal);
        Assert.Contains(sim, a.Hand);
        Assert.Equal(pointsBefore, a.Points);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Same(b, theirs.Owner);

        // 拒绝留痕：日志里有条目编号（异常会中断效果，日志是唯一锚点）
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Message.Contains("B#5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 部分生效_首轮成功后次轮拒绝_首轮转移不回滚()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        // 第 1 轮：施放者选 a1、对手选 b1（两次「取首个」）；
        // 第 2 轮：施放者手里只剩 [bad, b1]，取首个＝被破坏的 bad ⇒ 动作层拒绝。
        var bridge = new ScriptedBridge(
            ScriptedBridge.FirstCard,
            ScriptedBridge.FirstCard,
            ScriptedBridge.FirstCard,
            ScriptedBridge.FirstCard);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory, bridge.Bridge);
        var a = match.Players[0];
        var b = match.Players[1];

        var sim = await SimInfiltrationKit.GiveSimAsync(match, a);
        var a1 = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var bad = await SimInfiltrationKit.GiveInfantryAsync(match, a);
        var b1 = await SimInfiltrationKit.GiveInfantryAsync(match, b);
        var b2 = await SimInfiltrationKit.GiveInfantryAsync(match, b);

        bad.ReassignOwner(b); // 破坏不变量（仍在 A 手牌）

        using var recorder = new UpdateRecorder(match.Engine);
        var pointsBefore = a.Points;

        await Assert.ThrowsAnyAsync<Exception>(() => match.PlayManager.PlayOrderAsync(sim));

        // 第 1 轮交换仍在（既成事实，不回滚）
        Assert.Same(b, a1.Owner);
        Assert.Contains(a1, b.Hand);
        Assert.Same(a, b1.Owner);
        Assert.Contains(b1, a.Hand);
        Assert.Equal(2, recorder.CountOf(GameUpdates.CardHandAdd));

        // 本卡留手、未扣费（中断在 Finalize 之前）
        Assert.Contains(sim, a.Hand);
        Assert.Equal(pointsBefore, a.Points);
    }

    [Fact]
    public async Task OpponentOf_未加载卡返回null_正常返回唯一对家()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var pack = SimInfiltrationKit.LoadPack(cardsDirectory);
        var match = await SimInfiltrationKit.CreateMatchAsync(pack, effectsDirectory);
        var a = match.Players[0];
        var b = match.Players[1];

        var played = await SimInfiltrationKit.GiveSimAsync(match, a);
        var runtime = played.Owner!.EffectRuntime!;

        Assert.Same(b, runtime.OpponentOf(played));

        // 未加载（无归属）＝null、不抛
        var unloaded = new OrderCard(match.Engine, new CardDefinition(
            "未加载指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            category: CardCategory.Command, faction: Faction.Italy, rarity: Rarity.Special));
        Assert.Null(runtime.OpponentOf(unloaded));
    }

    [Fact]
    public void 卡包预检_errors为零()
    {
        if (!SimInfiltrationKit.TryLocate(out var cardsDirectory, out var effectsDirectory))
        {
            return;
        }

        var report = CardDistributionValidator.Validate(new[] { cardsDirectory }, new[] { effectsDirectory });

        Assert.Empty(report.Errors);
        Assert.True(report.HasNoErrors);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(1, report.SuccessfulCardCount);
        Assert.Equal(1, report.SuccessfulLibrarySnapshotCount);
    }
}
