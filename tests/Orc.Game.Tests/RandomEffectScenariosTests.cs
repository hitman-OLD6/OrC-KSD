using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 第 2 批 G8（效果级随机服务）验收——效果级场景测试面（①②③④⑤）：
/// ①随机消灭（真实效果：取样→统一死亡流程，不经交互）；②随机分配（真实效果：循环取样 6 次「选目标→+1 防御」，
/// 允许重复、真实落值）；③随机转移（真实效果：随机选目标→伤害经既有结算路径；边界披露＝重定向完整语义随第 3 批）；
/// ④随机词条（升级版：真实词条授予＋读取面——桩记录器已替换；完整链〔行为生效/确定性/池边界〕
/// 见 <see cref="RandomBattleKeywordChainTests"/>）；⑤确定性（同种子同操作序列复现）。
/// 取用口径：真实效果类经效果注册表注册、经装载链在卡加载时点生效；运行时经接入面
/// （<see cref="MatchRandomService.ResolveFor"/>——「卡 → 玩家 → 服务」）取用服务。
/// </summary>
public class RandomEffectScenarioTests
{
    private const string EliminateCardId = "u_rand_elim";
    private const string DistributeCardId = "u_rand_dist";
    private const string TransferCardId = "u_rand_tran";
    private const string KeywordCardId = "u_rand_kw";

    /// <summary>场景④升级版宿主（单位卡——「对单位授予」语义；授予/读取经词条管理组件）。</summary>
    private static CardDefinitionEntry KeywordHostDefinition()
        => new(KeywordCardId, new CardDefinition(
            "随机词条测试单位", deployCost: 1, operateCost: 1, attack: 1, defense: 1,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>场景④升级版池：3 元打标子集（元素取自打标面、不另立副本；保持与桩相同的池规模——⑤消费结构不变）。</summary>
    private static IReadOnlyList<string> KeywordScenePool()
        => BattleKeywordChainKit.Pool(KeywordIds.Blitz, KeywordIds.SmokeScreen, KeywordIds.Armor);

    /// <summary>场景卡定义（指令卡——效果宿主；不参与战斗）。</summary>
    private static CardDefinitionEntry SceneDefinition(string id, string name)
        => new(id, new CardDefinition(
            name, deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));

    // ---------- 场景运行（返回完整观察结果；供正式断言与探针共用） ----------

    private sealed record EliminateRun(
        IReadOnlyList<UnitCard> Candidates,
        UnitCard Chosen,
        int ChosenIndex,
        int DiedCount,
        bool ChosenDestroyed,
        bool ChosenOffField,
        bool ChosenSlotCleared,
        bool AllOthersAlive);

    private static async Task<EliminateRun> RunEliminateAsync(int seed)
    {
        var registry = new CardEffectRegistry();
        registry.Register("effect.rand.eliminate", _ => new RandomEliminateEffect());
        registry.Declare(EliminateCardId, new[] { "effect.rand.eliminate" });

        var match = CommandTestKit.CreateCommandMatch(
            seed: seed,
            effectRegistry: registry,
            extraDefinitions: new[] { SceneDefinition(EliminateCardId, "随机消灭测试卡") });
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 友方 2（不应被选）＋敌方 3（候选：枚举序＝敌方支援线[1]→[2]→前线[3] 过滤后序＝[前线3, 支援1, 支援2]）
        var friendly1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var friendly2 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 3);

        var candidates = RandomSceneKit.CollectAliveUnits(match.Battlefield, playerA, sameSide: false); // 敌对方＝playerA 视角非己方
        var slotsBefore = candidates.ToDictionary(unit => unit, unit => unit.GetData<UnitStateData>().Position);

        var diedCount = 0;
        using var probe = match.Engine.Subscribe((type, _, _) =>
        {
            if (type == GameUpdates.CardDied)
            {
                diedCount += 1;
            }

            return Task.CompletedTask;
        });

        var card = (OrderCard)match.CardLibrary.Instantiate(EliminateCardId);
        await card.LoadAsync(playerA);
        var effect = Assert.IsType<RandomEliminateEffect>(Assert.Single(card.Effects));

        await effect.CastAsync(match.Engine); // 施放（不经任何交互面）

        var chosen = Assert.IsType<UnitCard>(effect.LastChosen);
        var chosenIndex = candidates.FindIndex(unit => ReferenceEquals(unit, chosen));
        var state = chosen.GetData<UnitStateData>();
        var others = new List<UnitCard> { friendly1, friendly2 };
        others.AddRange(candidates.Where(unit => !ReferenceEquals(unit, chosen)));

        return new EliminateRun(
            candidates,
            chosen,
            chosenIndex,
            diedCount,
            state.IsDestroyed,
            state.Position is null,
            slotsBefore[chosen]?.IsEmpty ?? false,
            others.All(unit => !unit.GetData<UnitStateData>().IsDestroyed));
    }

    private sealed record DistributeRun(
        IReadOnlyList<UnitCard> Candidates,
        IReadOnlyList<int> PickIndices,
        int[] DefenseDeltas,
        int TotalDelta);

    private static async Task<DistributeRun> RunDistributeAsync(int seed)
    {
        var registry = new CardEffectRegistry();
        registry.Register("effect.rand.distribute", _ => new RandomDistributeEffect());
        registry.Declare(DistributeCardId, new[] { "effect.rand.distribute" });

        var match = CommandTestKit.CreateCommandMatch(
            seed: seed,
            effectRegistry: registry,
            extraDefinitions: new[] { SceneDefinition(DistributeCardId, "随机分配测试卡") });
        await match.Initialize();
        var playerA = match.Players[0];

        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);

        var candidates = RandomSceneKit.CollectAliveUnits(match.Battlefield, playerA, sameSide: true);
        var before = candidates.Select(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)).ToArray();

        var card = (OrderCard)match.CardLibrary.Instantiate(DistributeCardId);
        await card.LoadAsync(playerA);
        var effect = Assert.IsType<RandomDistributeEffect>(Assert.Single(card.Effects));

        await effect.CastAsync(match.Engine); // 施放（循环取样 6 次——逐点独立、允许重复）

        var after = candidates.Select(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)).ToArray();
        var deltas = after.Select((value, index) => value - before[index]).ToArray();
        var pickIndices = effect.PickLog
            .Select(chosen => candidates.FindIndex(unit => ReferenceEquals(unit, chosen)))
            .ToArray();

        return new DistributeRun(candidates, pickIndices, deltas, deltas.Sum());
    }

    private sealed record TransferRun(
        IReadOnlyList<UnitCard> Candidates,
        UnitCard Chosen,
        int ChosenIndex,
        int[] DefenseBefore,
        int[] DefenseAfter);

    private static async Task<TransferRun> RunTransferAsync(int seed)
    {
        const int transferDamage = 2;

        var registry = new CardEffectRegistry();
        registry.Register("effect.rand.transfer", _ => new RandomTransferEffect(transferDamage));
        registry.Declare(TransferCardId, new[] { "effect.rand.transfer" });

        var match = CommandTestKit.CreateCommandMatch(
            seed: seed,
            effectRegistry: registry,
            extraDefinitions: new[] { SceneDefinition(TransferCardId, "随机转移测试卡") });
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 3);

        var candidates = RandomSceneKit.CollectAliveUnits(match.Battlefield, playerA, sameSide: false); // 敌对方＝playerA 视角非己方
        var before = candidates.Select(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)).ToArray();

        var card = (OrderCard)match.CardLibrary.Instantiate(TransferCardId);
        await card.LoadAsync(playerA);
        var effect = Assert.IsType<RandomTransferEffect>(Assert.Single(card.Effects));

        await effect.CastAsync(match.Engine);

        var chosen = Assert.IsType<UnitCard>(effect.LastChosen);
        var chosenIndex = candidates.FindIndex(unit => ReferenceEquals(unit, chosen));
        var after = candidates.Select(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense)).ToArray();

        return new TransferRun(candidates, chosen, chosenIndex, before, after);
    }

    /// <summary>④升级版运行：施放 → 取样（PickOne·池＝打标子集）→ 对单位宿主真实授予 → 从读取面读出被授予词条。</summary>
    private static async Task<string> RunKeywordChainAsync(int seed)
    {
        var pool = KeywordScenePool();
        var registry = new CardEffectRegistry();
        registry.Register("effect.rand.kw", _ => new RandomKeywordGrantEffect(pool));
        registry.Declare(KeywordCardId, new[] { "effect.rand.kw" });

        var match = CommandTestKit.CreateCommandMatch(
            seed: seed,
            effectRegistry: registry,
            extraDefinitions: new[] { KeywordHostDefinition() });
        await match.Initialize();
        var playerA = match.Players[0];

        var host = await CommandTestKit.PrepareOnSupportAsync(match, playerA, KeywordCardId, 1);
        var effect = Assert.IsType<RandomKeywordGrantEffect>(Assert.Single(host.Effects));
        await effect.CastAsync(match.Engine);

        var granted = BattleKeywordRules.GetBattleKeywords(host); // 真实授予读取面（替代桩记录器）
        var chosen = Assert.Single(granted);
        Assert.Contains(chosen, pool); // 授予登记成立：取样结果 ∈ 池
        return chosen;
    }

    /// <summary>⑤复现序列：同一对局内先执行④（词条取样）再执行②（6 次循环取样）——结果序列化。</summary>
    private static async Task<string> RunReplaySequenceAsync(int seed)
    {
        var pool = KeywordScenePool();
        var registry = new CardEffectRegistry();
        registry.Register("effect.rand.kw", _ => new RandomKeywordGrantEffect(pool));
        registry.Register("effect.rand.distribute", _ => new RandomDistributeEffect());
        registry.Declare(KeywordCardId, new[] { "effect.rand.kw" });
        registry.Declare(DistributeCardId, new[] { "effect.rand.distribute" });

        var match = CommandTestKit.CreateCommandMatch(
            seed: seed,
            effectRegistry: registry,
            extraDefinitions: new[]
            {
                KeywordHostDefinition(),
                SceneDefinition(DistributeCardId, "随机分配测试卡"),
            });
        await match.Initialize();
        var playerA = match.Players[0];

        var support1 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var support2 = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var front = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var candidates = new[] { support1, support2, front };

        // ④（升级版：真实授予读取）——宿主不上场：不影响 ② 候选（覆盖不减）。
        var host = await CommandTestKit.InstantiateLoadedAsync(match, playerA, KeywordCardId);
        var keywordEffect = Assert.IsType<RandomKeywordGrantEffect>(Assert.Single(host.Effects));
        await keywordEffect.CastAsync(match.Engine);

        var distributeCard = (OrderCard)match.CardLibrary.Instantiate(DistributeCardId);
        await distributeCard.LoadAsync(playerA);
        var distributeEffect = Assert.IsType<RandomDistributeEffect>(Assert.Single(distributeCard.Effects));
        await distributeEffect.CastAsync(match.Engine); // ②（6 次取样 → 防御增量）

        var deltas = candidates.Select(unit => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense) - 5).ToArray();
        var granted = string.Join(",", BattleKeywordRules.GetBattleKeywords(host)); // ④升级版读取面（真实授予）
        return $"kw=[{granted}];dist=[{string.Join(",", deltas)}]";
    }

    // ---------- ① 随机消灭（真实效果：取样 → 统一死亡流程；不经交互） ----------

    [Fact]
    public async Task Scenario1_Random_Elimination_Kills_Expected_Unit_Without_Interaction()
    {
        var run = await RunEliminateAsync(seed: 42);

        // 候选枚举序（固定）：玩家A支援线 → 前线 → 玩家B支援线、按槽位索引（过滤＝敌对方）
        // → 候选 ＝ [敌方前线[3], 敌方支援线[1], 敌方支援线[2]]
        Assert.Equal(3, run.Candidates.Count);

        // 选择正确性：固定种子 42 下的确定性输出（回归锚点）
        Assert.Equal(0, run.ChosenIndex);
        Assert.Same(run.Candidates[0], run.Chosen);

        // 死亡恰一次（可观察：card.died 恰一条）
        Assert.Equal(1, run.DiedCount);

        // 状态可观测（已销毁 / 离场 / 清位）
        Assert.True(run.ChosenDestroyed);
        Assert.True(run.ChosenOffField);
        Assert.True(run.ChosenSlotCleared);

        // 其余单位（含友方 2 名）全部存活——选择与死亡链仅作用于选中者
        Assert.True(run.AllOthersAlive);
    }

    // ---------- ② 随机分配（真实效果：循环取样 6 次「选目标 → +1 防御」） ----------

    [Fact]
    public async Task Scenario2_Random_Distribution_Samples_Six_Times_And_Really_Lands()
    {
        var run = await RunDistributeAsync(seed: 42);

        Assert.Equal(3, run.Candidates.Count);

        // 6 次循环取样（逐点独立、允许重复）——固定种子 42 下的确定性输出
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 1 }, run.PickIndices.ToArray());
        Assert.Contains(run.PickIndices, index => index != 0); // 非退化（与「恒取首项」退化实现可区分）

        // 真实落值（既有防御力受控变更面——修饰机制）：各目标增量与取样序列一致；总增量＝6
        Assert.Equal(new[] { 4, 2, 0 }, run.DefenseDeltas);
        Assert.Equal(6, run.TotalDelta);
    }

    // ---------- ③ 随机转移（真实效果：随机选目标 → 伤害经既有结算路径） ----------

    [Fact]
    public async Task Scenario3_Random_Transfer_Applies_Damage_To_Chosen_Target()
    {
        var run = await RunTransferAsync(seed: 42);

        Assert.Equal(3, run.Candidates.Count);
        Assert.Equal(0, run.ChosenIndex); // 固定种子 42 下的确定性输出
        Assert.Same(run.Candidates[0], run.Chosen);

        // 伤害真实结算（既有伤害结算路径——防御下降可观测）：选中者 −2；其余候选不动
        Assert.Equal(run.DefenseBefore[0] - 2, run.DefenseAfter[0]);
        for (var i = 1; i < run.Candidates.Count; i++)
        {
            Assert.Equal(run.DefenseBefore[i], run.DefenseAfter[i]);
        }

        // 边界披露：重定向完整语义随第 3 批；本处仅验证「随机选择→伤害应用」环节（详见效果类注释）
    }

    // ---------- ④ 随机词条（升级版：真实授予 ＋ 读取面） ----------

    [Fact]
    public async Task Scenario4_Keyword_Grant_Lands_Real_Grant_With_Real_Draw_Path()
    {
        // 桩→升级：记录器断言（「授予调用」）→ 真实授予读取（对单位宿主授予后经读取面可读）；
        // 完整链（行为生效/确定性/池边界）见 RandomBattleKeywordChainTests。
        var first = await RunKeywordChainAsync(seed: 42);
        Assert.Equal(KeywordIds.Blitz, first); // 固定种子 42 下的确定性输出（回归锚点）

        // 取样路径真实（独立证据保留）：更换种子 → 取样结果变化（非固定映射、非恒取首项）。
        var alternate = await RunKeywordChainAsync(seed: 43);
        Assert.Equal(KeywordIds.SmokeScreen, alternate); // 固定种子 43 下的确定性输出（回归锚点）
        Assert.NotEqual(first, alternate);
    }

    // ---------- ⑤ 确定性（固定种子下相同操作序列 → 相同随机结果） ----------

    [Fact]
    public async Task Scenario5_Same_Seed_Same_Operation_Sequence_Replays_Identical_Draw_Sequence()
    {
        var first = await RunReplaySequenceAsync(seed: 777);
        var second = await RunReplaySequenceAsync(seed: 777);
        var other = await RunReplaySequenceAsync(seed: 778);

        // 复现断言（含④升级版取样环节 ＋ ②循环取样）：同种子、同操作序列 → 结果逐位一致
        Assert.Equal(first, second);
        Assert.Equal("kw=[闪击];dist=[1,1,4]", first); // 回归锚点（实测固化；旧锚 kw=kw.alpha;dist=[1,1,4]——变化说明见实现记录）

        // 换种子 → 结果变化（种子生效；可选辅助）
        Assert.NotEqual(first, other);
    }

    // ---------- 脱局降级（接入面不可用语境：功能不可用、不抛错、不失败） ----------

    [Fact]
    public async Task Detached_Effect_Execution_Degrades_Without_Error()
    {
        // 独立构造卡（脱离对局——无归属、无服务注入）：效果执行＝功能不可用、不抛错、不失败（沿用既有先例）
        var engine = new LogicEngine();
        var standalone = ModifierTestKit.CreateBareUnit(engine);
        var effect = new RandomEliminateEffect();
        standalone.AddEffect(effect);

        await effect.CastAsync(engine); // 不抛错（服务解析＝null → 效果跳过取样）

        Assert.Null(effect.LastChosen); // 未取样（功能不可用）；无异常传播、对局内步骤零执行
        Assert.Same(effect, Assert.Single(standalone.Effects)); // 效果列表保留（仅不产生行为）
    }
}

/// <summary>随机场景测试辅助（候选收集——与效果侧同一枚举口径：支援线→前线→支援线、按槽位序）。</summary>
internal static class RandomSceneKit
{
    /// <summary>存活单位收集（先战场三条线〔玩家A支援线 → 前线 → 玩家B支援线〕、按槽位索引序；过滤＝归属方＋未死亡）。</summary>
    public static List<UnitCard> CollectAliveUnits(Battlefield battlefield, Player side, bool sameSide)
    {
        var result = new List<UnitCard>();
        foreach (var line in new[] { battlefield.PlayerASupportLine, battlefield.FrontLine, battlefield.PlayerBSupportLine })
        {
            foreach (var slot in line)
            {
                if (slot.Occupant is UnitCard unit
                    && !unit.GetData<UnitStateData>().IsDestroyed
                    && unit.Owner is { } owner
                    && (ReferenceEquals(owner, side) == sameSide))
                {
                    result.Add(unit);
                }
            }
        }

        return result;
    }
}

// ---------- 效果视图与真实效果类（验收①②③④；经注册表注册、经装载链在卡加载时点生效） ----------

/// <summary>随机场景施放视图（本批效果不消费施放载荷——[Optional] 锚点满足视图模板）。</summary>
[ContextView]
public class RandomCastView
{
    [Optional]
    [Read]
    public virtual object? Anchor { get; set; }
}

/// <summary>
/// 验收①随机消灭（真实效果类）：施放时经接入面取对局随机服务 → 从敌方存活单位取样（PickOne）→
/// 对选中者施以致死伤害（经既有防御门户 → 防御归零统一死亡衔接——统一死亡流程；不经任何交互面）。
/// </summary>
internal sealed class RandomEliminateEffect : ActiveEffect<RandomCastView>
{
    public RandomEliminateEffect()
        : base("随机消灭")
        => CastTrigger.Register("施放", OnCastAsync);

    /// <summary>最近一次选中的单位（测试断言面；未执行＝null）。</summary>
    public UnitCard? LastChosen { get; private set; }

    private async Task OnCastAsync(RandomCastView view, Context ctx, CancellationToken ct)
    {
        var random = MatchRandomService.ResolveFor(Host);
        if (random is null)
        {
            return; // 脱局降级：功能不可用、不抛错、不失败
        }

        if (Host is not CardBase host || host.Owner is not { } owner
            || GameEnvironment.ResolveFor(Host) is not { } environment)
        {
            return;
        }

        var candidates = RandomSceneKit.CollectAliveUnits(environment.Battlefield, owner, sameSide: false);
        if (candidates.Count == 0)
        {
            return;
        }

        var chosen = random.PickOne(candidates);
        LastChosen = chosen;
        var lethal = chosen.Modifiers.GetEffectiveValue(CardStatFields.Defense); // 当前有效防御＝致死量
        await chosen.ApplyDefenseDamageAsync(lethal, ct);
    }
}

/// <summary>
/// 验收②随机分配（真实效果类）：施放时循环取样 6 次「选目标 → +1 防御力」——逐点独立随机、允许重复选中、
/// 可叠加；真实落值经既有防御力受控变更面（修饰机制；来源＝本效果）。
/// </summary>
internal sealed class RandomDistributeEffect : ActiveEffect<RandomCastView>
{
    /// <summary>随机分配次数（验收口径：6 次循环取样组合）。</summary>
    public const int DistributionCount = 6;

    public RandomDistributeEffect()
        : base("随机分配")
        => CastTrigger.Register("施放", OnCastAsync);

    /// <summary>逐次选中记录（测试断言面——6 次取样序列）。</summary>
    public List<UnitCard> PickLog { get; } = new();

    private async Task OnCastAsync(RandomCastView view, Context ctx, CancellationToken ct)
    {
        var random = MatchRandomService.ResolveFor(Host);
        if (random is null)
        {
            return;
        }

        if (Host is not CardBase host || host.Owner is not { } owner
            || GameEnvironment.ResolveFor(Host) is not { } environment)
        {
            return;
        }

        var candidates = RandomSceneKit.CollectAliveUnits(environment.Battlefield, owner, sameSide: true);
        for (var i = 0; i < DistributionCount; i++)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            var chosen = random.PickOne(candidates); // 逐点独立随机（可重复选中）
            PickLog.Add(chosen);
            await chosen.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Defense, 1, this), ct);
        }
    }
}

/// <summary>
/// 验收③随机转移（真实效果类；本处仅验证「随机选择环节」）：施放时取对局随机服务 → 从敌方存活单位取样
/// （PickOne）→ 伤害经既有伤害结算路径应用于选中者（可观测＝防御下降）。
/// 边界披露：重定向完整语义（将原目标所受伤害改由随机目标承受等）随第 3 批；本处仅验证随机选择→伤害应用闭环。
/// </summary>
internal sealed class RandomTransferEffect : ActiveEffect<RandomCastView>
{
    private readonly int _damage;

    public RandomTransferEffect(int damage)
        : base("随机转移")
    {
        _damage = damage;
        CastTrigger.Register("施放", OnCastAsync);
    }

    /// <summary>最近一次选中的单位（测试断言面；未执行＝null）。</summary>
    public UnitCard? LastChosen { get; private set; }

    private async Task OnCastAsync(RandomCastView view, Context ctx, CancellationToken ct)
    {
        var random = MatchRandomService.ResolveFor(Host);
        if (random is null)
        {
            return;
        }

        if (Host is not CardBase host || host.Owner is not { } owner
            || GameEnvironment.ResolveFor(Host) is not { } environment)
        {
            return;
        }

        var candidates = RandomSceneKit.CollectAliveUnits(environment.Battlefield, owner, sameSide: false);
        if (candidates.Count == 0)
        {
            return;
        }

        var chosen = random.PickOne(candidates);
        LastChosen = chosen;
        await chosen.ApplyDefenseDamageAsync(_damage, ct); // 伤害经既有伤害结算路径（防御门户 → 跑链 → 集中触发）
    }
}

/// <summary>
/// 验收④随机词条（升级版：真实授予载体）——B1④桩的升级形态：
/// 施放时经接入面取对局随机服务 → 从词条池取样（PickN〔不放回〕或循环 PickOne〔允许重复〕）→
/// 对宿主真实授予（词条管理组件登记——「从集合取样 → 授予调用」的桩记录器由此替换）。
/// 池＝调用方经对战词条打标面构建的子集（<see cref="BattleKeywordChainKit.Pool"/>——读取引用＋校验、不另立副本）；
/// 参值词条（重甲）被随机授予时取默认参值 1（A2 工程约定）。
/// 边界（空池/池不足）＝受控失败：不抛未受控中断（不抛断链）、拒绝/既定语义如实承载
/// （<see cref="LastRejection"/> 明确记录、不静默吞噬）、不无限重试、不授予。
/// 观测面（测试断言用）：<see cref="DrawLog"/>（取样序列）/<see cref="GrantResults"/>（授予结果，含重复幂等）/
/// <see cref="LastRejection"/>（边界受控失败记录）。
/// </summary>
internal sealed class RandomKeywordGrantEffect : ActiveEffect<RandomCastView>
{
    private readonly IReadOnlyList<string> _pool;
    private readonly int _count;
    private readonly bool _usePickN;

    public RandomKeywordGrantEffect(IReadOnlyList<string> pool, int count = 1, bool usePickN = false)
        : base("随机词条")
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _pool = pool;
        _count = count;
        _usePickN = usePickN;
        CastTrigger.Register("施放", OnCastAsync);
    }

    /// <summary>取样序列（逐次选取序——观测面）。</summary>
    public List<string> DrawLog { get; } = new();

    /// <summary>授予尝试结果（与 <see cref="DrawLog"/> 一一对应；重复选中＝幂等 false——按既有授予语义、不新定义）。</summary>
    public List<bool> GrantResults { get; } = new();

    /// <summary>边界受控失败记录（空池/池不足；null＝无边界失败——可断言/可诊断、不静默吞噬）。</summary>
    public string? LastRejection { get; private set; }

    private async Task OnCastAsync(RandomCastView view, Context ctx, CancellationToken ct)
    {
        var random = MatchRandomService.ResolveFor(Host);
        if (random is null)
        {
            return; // 脱局降级：功能不可用、不抛错、不失败（沿用先例）
        }

        if (Host is not CardBase host)
        {
            return;
        }

        if (_pool.Count == 0)
        {
            // 空池＝受控失败：「空候选集＝明确拒绝」原语语义的链层承载——不抛断链、不静默、不授予、不重试。
            LastRejection = "空池拒绝：无候选可取样（原语语义＝空候选集明确拒绝）——链层受控承载（不抛断链、不授予、不重试）。";
            return;
        }

        if (_usePickN && _count > _pool.Count)
        {
            // 池不足＝受控失败：「不放回取样不可满足＝明确拒绝」原语既定语义的链层承载。
            LastRejection =
                $"池不足拒绝：取样数 {_count} 超过池数 {_pool.Count}（原语既定语义＝不放回取样不可满足、明确拒绝）——链层受控承载（不抛断链、不授予、不重试）。";
            return;
        }

        if (_usePickN)
        {
            foreach (var chosen in random.PickN(_pool, _count))
            {
                await GrantOneAsync(host, chosen);
            }

            return;
        }

        for (var i = 0; i < _count; i++)
        {
            var chosen = random.PickOne(_pool); // 循环取样（允许重复选中——重复按授予幂等语义处置）
            await GrantOneAsync(host, chosen);
        }
    }

    private async Task GrantOneAsync(CardBase host, string chosen)
    {
        DrawLog.Add(chosen);
        GrantResults.Add(await host.Keywords.GrantAsync(chosen, chosen == KeywordIds.Armor ? 1 : null)); // 参值词条默认参值 1
    }
}
