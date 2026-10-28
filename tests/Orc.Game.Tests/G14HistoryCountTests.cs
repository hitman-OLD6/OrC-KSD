using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// G14（历史计数与比较）机制级验收——四场景（W4-1 收尾批）：
/// ①使用计数（监听 card.played〔W4-1 升级载荷 { Card, Player }〕、按卡累计、结算读取）；
/// ②累计伤害阈值触发（订阅「造成攻击伤害」共享流程、承受方损伤量差值累计、≥阈值达成恰一次＋域重置加强）；
/// ③首次洗切（deck.shuffled 洗切信号〔初始化路径可观测〕＋首次标志自持＋公共洗切动作面）；
/// ④即时比较（读双方实时面板现算、不新增状态；含只读/无累积反证）。
/// 模式＝「自写监听 handler」：测试内构造有状态 handler、挂接既有触发器/订阅面、条件达成触发主动触发器；
/// 状态＝handler 私有闭环（不下放卡组件、不建共享查询面）；信号只报事实（「是否首次」由监听者自持标志判断）。
/// 另覆盖：W4-1 新增常量/载荷键与发射助手的契约断言（字面值冻结＋逐参校验）。
/// </summary>
public class G14HistoryCountTests
{
    // ---------- W4-1 契约（新增信号/载荷键字面值冻结） ----------

    [Fact]
    public void W4_1_New_Contract_Literals_Are_Frozen()
    {
        Assert.Equal("deck.shuffled", GameUpdates.DeckShuffled);
        Assert.Equal("Deck", GameUpdates.PayloadDeck);
        // 与引擎既有常量集无重叠（对外订阅契约；ordinal 序数同一性）
        var builtin = new[] { Updates.CardPlaced, Updates.CardDestroyed, Updates.CardData, Updates.EffectRemoved };
        Assert.DoesNotContain(GameUpdates.DeckShuffled, builtin);
        Assert.NotEqual(Updates.CardPlaced, GameUpdates.DeckShuffled);
    }

    // =========================== 场景①：使用计数 ===========================

    [Fact]
    public async Task Scenario1_Use_Count_Listens_CardPlayed_PerCard_And_Settles_By_Count()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: new[] { AltCommandDefinition });
        await match.Initialize();
        var player = match.Players[0];
        await match.ResourceManager.AddPointsAsync(player, 10); // 受控加值面：准备打出费用

        // 「X」＝轻指令（卡种/定义口径——同名卡＝同一定义）；监听 handler 自持计数（私有闭环）
        var watched = match.CardLibrary.Get(CommandTestKit.OrderCardId);
        var handler = new UseCountWatchHandler(match.Engine, player, watched);
        handler.Attach();

        // 使用序列：真实打出链驱动 card.played——3 张 X ＋ 1 张旁卡（不同定义）
        for (var i = 0; i < 3; i++)
        {
            var x = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, CommandTestKit.OrderCardId);
            Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.PlayOrderAsync(x)).Status);
        }

        var alt = await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match, player, AltCommandId);
        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.PlayOrderAsync(alt)).Status);

        // 按卡累计：事件 4 次（真实源联动）、X 命中恰 3 次（旁卡不入——卡种识别＝定义；归属过滤＝使用方）
        Assert.Equal(4, handler.SeenEvents);
        Assert.Equal(3, handler.UseCount);

        // 结算段读取：读取＝结算时点前已累计的使用数 → 产出与计数一致（『此前每使用过 1 张 X』式：触发主动触发器 N 次）
        var read = await handler.ResolveAsync();
        Assert.Equal(3, read);
        Assert.Equal(3, handler.FireCount);
    }

    // =========================== 场景②：累计伤害阈值触发 ===========================

    [Fact]
    public async Task Scenario2_Damage_Threshold_Fires_Once_And_Reactivates_After_Domain_Reset()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge, extraDefinitions: new[] { DummyDefinition });
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var dummy = await CommandTestKit.PrepareOnSupportAsync(match, playerB, DummyId, 1); // 承受方：攻 0 / 防 12
        var w1 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0); // 攻 2 / 防 5
        var w2 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var w3 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 2);
        await match.ResourceManager.AddPointsAsync(playerA, 10); // 受控加值面：准备攻击行动费（每击 1 点）

        var handler = new DamageThresholdWatchHandler(match.Engine, dummy, threshold: 3);
        handler.Attach();

        // 回合 1：三击（每击 2 伤——伤害数值真实取自结算〔承受方损伤量增量〕；承受方不反击）
        await StrikeAsync(match, bridge, w1, dummy);
        Assert.Equal(1, handler.SeenCount);
        Assert.Equal(2, handler.TotalDamage);
        Assert.Equal(0, handler.FireCount); // 2 < 3：未达成（零副作用）

        await StrikeAsync(match, bridge, w2, dummy);
        Assert.Equal(2, handler.SeenCount);
        Assert.Equal(4, handler.TotalDamage);
        Assert.Equal(1, handler.FireCount); // 4 ≥ 3：达成恰一次（数值口径证据——按事件次数 2 < 3 则不会触发）

        await StrikeAsync(match, bridge, w3, dummy);
        Assert.Equal(3, handler.SeenCount);
        Assert.Equal(6, handler.TotalDamage);
        Assert.Equal(1, handler.FireCount); // 已达成 → 同域不重复触发
        Assert.Equal(6, dummy.GetData<UnitStateData>().DefenseLoss); // 累计值与结算真源（损伤量）一致

        // 域重置（回合结束）：域状态清零（累计归零、已触发标志复位）
        await match.EndTurn(); // → 回合 2（B）
        Assert.Equal(1, handler.ResetCount);
        Assert.Equal(0, handler.TotalDamage);

        await match.EndTurn(); // → 回合 3（A）
        Assert.Equal(2, handler.ResetCount);

        // 域重置后重新累计：两击（4 伤）再次达成 → 恰一次（『每回合……』式加强断言）
        await StrikeAsync(match, bridge, w1, dummy);
        Assert.Equal(2, handler.TotalDamage);
        Assert.Equal(1, handler.FireCount); // 重置后 2 < 3：未达成

        await StrikeAsync(match, bridge, w2, dummy);
        Assert.Equal(4, handler.TotalDamage);
        Assert.Equal(2, handler.FireCount); // 再次达成恰一次
        Assert.Equal(10, dummy.GetData<UnitStateData>().DefenseLoss);

        Assert.Equal(5, handler.SeenCount); // 全程受击事件数＝3（回合 1）＋2（回合 3）
    }

    // =========================== 场景③：首次洗切 ===========================

    [Fact]
    public async Task Scenario3A_Initial_Shuffles_Are_Observable_To_Subscriber_Attached_Before_Initialize()
    {
        // 验收①「初始化路径可订阅、测试可观测」：订阅（Initialize 前挂接）→ 收到初始化洗切信号
        var match = GameTestData.CreateStandardMatch(seed: 42);
        var signals = new List<(object? Player, object? Deck)>();
        using var subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.DeckShuffled)
            {
                signals.Add((payload![GameUpdates.PayloadPlayer], payload[GameUpdates.PayloadDeck]));
            }

            return Task.CompletedTask;
        });

        await match.Initialize();

        // 初始化恰两条（单副卡组粒度；玩家索引升序）；每条归属可对应到玩家与卡组
        Assert.Equal(2, signals.Count);
        Assert.Same(match.Players[0], signals[0].Player);
        Assert.Same(match.Players[0].Deck, signals[0].Deck);
        Assert.Same(match.Players[1], signals[1].Player);
        Assert.Same(match.Players[1].Deck, signals[1].Deck);
    }

    [Fact]
    public async Task Scenario3B_First_Shuffle_Flag_Is_SelfHeld_Fires_Once_And_Not_Repeats()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize(); // 初始化洗切（两条）发生在挂接之前——监听窗口自挂接时点起、不回溯

        // 首次语义监听（归属过滤＝「友方首次洗切」式——仅观察指定玩家卡组）
        var handler = new FirstShuffleWatchHandler(match.Engine, match.Players[0]);
        handler.Attach();
        Assert.Equal(0, handler.SeenCount); // 初始化洗切未被观察（不回溯——无状态遗留）

        // 对手洗切：归属过滤——不达成
        await match.ShuffleDeckAsync(match.Players[1]);
        Assert.Equal(0, handler.SeenCount);
        Assert.Equal(0, handler.FireCount);

        // 友方首次洗切（经公共洗切动作面——真实路径）：达成恰一次（触发主动触发器恰一次）
        await match.ShuffleDeckAsync(match.Players[0]);
        Assert.Equal(1, handler.SeenCount);
        Assert.Equal(1, handler.FireCount);

        // 后续洗切（同一观察口径）：标志自持 → 不重复触发
        await match.ShuffleDeckAsync(match.Players[0]);
        Assert.Equal(2, handler.SeenCount);
        Assert.Equal(1, handler.FireCount);

        await match.ShuffleDeckAsync(match.Players[1]); // 对手再洗：仍过滤
        Assert.Equal(2, handler.SeenCount);
        Assert.Equal(1, handler.FireCount);
    }

    // =========================== 场景④：即时比较 ===========================

    [Fact]
    public async Task Scenario4_Instant_Comparison_Reads_Live_State_And_Adds_No_New_State()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize(); // 回合 1（A 行动）：A 槽 1、B 槽 0
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var evaluator = new PointSlotLeadEvaluator(match.Engine, playerA, playerB);

        // 实时读取现算：A 槽 1 − B 槽 0 ＝ 1（无历史/累计）
        Assert.Equal(1, await evaluator.EvaluateAndFireAsync());
        Assert.Equal(1, evaluator.FireCount);

        // 反证 a：多次比较互不影响（连续比较结果稳定、无累积效应）
        Assert.Equal(1, await evaluator.EvaluateAndFireAsync());
        Assert.Equal(1, await evaluator.EvaluateAndFireAsync());
        Assert.Equal(3, evaluator.FireCount);

        // 反证 b：比较本身不引发任何数值变更/更新发射（只读验证）
        using var recorder = new UpdateRecorder(match.Engine);
        var slotsBefore = (playerA.PointSlots, playerB.PointSlots);
        var pointsBefore = (playerA.Points, playerB.Points);
        var handBefore = (playerA.Hand.Count, playerB.Hand.Count);
        var lead = await evaluator.EvaluateAndFireAsync();
        Assert.Equal(1, lead);
        Assert.Empty(recorder.Types); // 零更新发射
        Assert.Equal(slotsBefore, (playerA.PointSlots, playerB.PointSlots));
        Assert.Equal(pointsBefore, (playerA.Points, playerB.Points));
        Assert.Equal(handBefore, (playerA.Hand.Count, playerB.Hand.Count));

        // 随动：改变实时值 → 重新比较结果即变（比较逻辑不缓存）
        await match.EndTurn(); // → 回合 2（B）：B 槽 1 → 差值 1 − 1 ＝ 0
        Assert.Equal(0, await evaluator.EvaluateAndFireAsync());

        await match.EndTurn(); // → 回合 3（A）：A 槽 2 → 差值 2 − 1 ＝ 1
        Assert.Equal(1, await evaluator.EvaluateAndFireAsync());
        Assert.Equal(5, evaluator.FireCount); // 1 ＋ 1 ＋ 1 ＋ 1 ＋ 0 ＋ 1
    }

    // ---------- 辅助 ----------

    private const string AltCommandId = "u_alt_cmd";

    private const string DummyId = "u_dummy_g14";

    /// <summary>旁卡指令（不同定义；「按卡」识别负控用）。</summary>
    private static CardDefinitionEntry AltCommandDefinition => new(
        AltCommandId,
        new CardDefinition(
            "旁路指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>反伤木桩（承受方：攻 0〔不反击〕/ 防 12〔诸击不死〕）。</summary>
    private static CardDefinitionEntry DummyDefinition => new(
        DummyId,
        new CardDefinition(
            "反伤木桩", deployCost: 1, operateCost: 1, attack: 0, defense: 12,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>驱动一次攻击（激活 → 交互脚本 → 选中目标 → 执行）。</summary>
    private static async Task StrikeAsync(Match match, MockTargeterBridge bridge, UnitCard attacker, UnitCard target)
    {
        CommandTestKit.Activate(attacker);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, attacker, target.Ref);
        Assert.Equal(CommandResultStatus.Success, result.Status);
    }
}

// ---------- 场景①：使用计数监听 handler（自写监听 handler 模式；状态＝私有闭环） ----------

/// <summary>
/// card.played 监听 handler（场景①「每使用过 1 张 X」）：自建被动触发器挂总线；
/// 事件过滤（使用方＝指定玩家 ∧ 卡种/定义＝X）→ 自持计数（私有闭环）；
/// 结算段读取＝<see cref="ResolveAsync"/>（据计数产出——触发自持主动触发器 N 次）。
/// </summary>
internal sealed class UseCountWatchHandler
{
    private readonly LogicEngine _engine;
    private readonly Player _player;
    private readonly CardDefinition _watchedDefinition;
    private readonly string _watchName;
    private readonly string _fireSink;
    private IDisposable? _watch;

    public UseCountWatchHandler(LogicEngine engine, Player player, CardDefinition watchedDefinition)
    {
        _engine = engine;
        _player = player;
        _watchedDefinition = watchedDefinition;
        _watchName = $"{watchedDefinition.Name}/使用计数监听";
        _fireSink = GameKernelTestKit.Sink(engine, $"{watchedDefinition.Name}/结算产出目标", () => FireCount += 1);
    }

    /// <summary>收到的 card.played 事件总数（过滤前；真实源联动证据）。</summary>
    public int SeenEvents { get; private set; }

    /// <summary>自持计数（『此前每使用过 1 张 X』的「X 使用数」；私有闭环）。</summary>
    public int UseCount { get; private set; }

    /// <summary>结算产出的触发次数（可断言结果）。</summary>
    public int FireCount { get; private set; }

    public void Attach()
        => _watch ??= _engine.Watch<CardPlayedWatchView>(GameUpdates.CardPlayed, _watchName, this, OnPlayedAsync);

    private Task OnPlayedAsync(CardPlayedWatchView view, Context ctx, CancellationToken ct)
    {
        SeenEvents += 1;

        if (view.Player is not Player player || !ReferenceEquals(player, _player))
        {
            return Task.CompletedTask; // 归属过滤：仅累计指定使用方的使用
        }

        if (view.Card is not CardBase card || !ReferenceEquals(card.Definition, _watchedDefinition))
        {
            return Task.CompletedTask; // 按卡识别：按卡的定义（注册键/卡种）区分——同名卡＝同一定义
        }

        UseCount += 1;
        return Task.CompletedTask;
    }

    /// <summary>结算段读取（模拟检验结算点）：读取＝结算时点前已累计的使用数；据计数产出——触发主动触发器 N 次。</summary>
    public async Task<int> ResolveAsync(CancellationToken ct = default)
    {
        var count = UseCount;
        for (var i = 0; i < count; i++)
        {
            await GameKernelTestKit.FireAsync(_engine, _fireSink, ct);
        }

        return count;
    }
}

// ---------- 场景②：累计伤害阈值监听 handler ----------

/// <summary>
/// 累计伤害阈值监听 handler（场景②「累计受到伤害≥3 时」）：订阅**受伤害信号** <c>card.damaged</c>
/// （伤害结算后、防御实际变化恰一次——读取结算后的承受方损伤量真实变化）；状态＝私有闭环：
/// 上次观测损伤量＋域内累计＋已触发标志；≥阈值达成 → 触发自持主动触发器恰一次（同域不重复）；
/// 域＝回合（turn.end 重置——累计归零、标志复位）。伤害数值＝损伤量差值（真实取自结算，非事件次数）。
/// <para>
/// 夹具口径（v4）：原订阅面＝「造成攻击伤害」共享流程成员 <c>engine#attack-damage</c>（外来边 priority 200）。
/// v4 里该成员是管道型（自有边位次＝last 之外，外来边先于默认结算跑），探针因此在**结算前**执行、损伤量增量恒 0。
/// 等价观测面＝结算后发射的 <c>card.damaged</c>（E1-33：防御实际变化才发）——同一承受方、同一"每击一次"粒度。
/// </para>
/// </summary>
internal sealed class DamageThresholdWatchHandler
{
    private readonly LogicEngine _engine;
    private readonly UnitCard _watched;
    private readonly int _threshold;
    private readonly string _fireSink;
    private readonly string _watchName;
    private IDisposable? _watch;
    private IDisposable? _damageWatch;
    private int _lastSeenLoss;
    private bool _firedInDomain;

    public DamageThresholdWatchHandler(LogicEngine engine, UnitCard watched, int threshold)
    {
        _engine = engine;
        _watched = watched;
        _threshold = threshold;
        _fireSink = GameKernelTestKit.Sink(engine, $"{watched.Name}/阈值触发目标", () => FireCount += 1);
        _watchName = $"{watched.Name}/域重置监听";
    }

    /// <summary>受击结算观测次数（指定承受方；订阅生效证据）。</summary>
    public int SeenCount { get; private set; }

    /// <summary>域内累计伤害（数值取自结算——承受方损伤量增量）。</summary>
    public int TotalDamage { get; private set; }

    /// <summary>阈值达成触发次数（恰一次语义的可断言观测面）。</summary>
    public int FireCount { get; private set; }

    /// <summary>域重置次数（回合结束驱动）。</summary>
    public int ResetCount { get; private set; }

    /// <summary>订阅受伤害信号（结算后）＋挂载域重置监听。</summary>
    public void Attach()
    {
        _damageWatch ??= _engine.Watch<CardDamagedWatchView>(
            GameUpdates.CardDamaged, $"{_watched.Name}/伤害结算监听", this, OnDamageResolvedAsync);
        _watch ??= _engine.Watch<CardEventView>(GameUpdates.TurnEnd, _watchName, this, OnTurnEndAsync);
    }

    private async Task OnDamageResolvedAsync(CardDamagedWatchView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard card || !ReferenceEquals(card, _watched))
        {
            return; // 仅统计「指定承受方」受到的伤害
        }

        SeenCount += 1;
        var loss = _watched.GetData<UnitStateData>().DefenseLoss;
        var damage = loss - _lastSeenLoss; // 本次结算的实际伤害＝损伤量增量（真实取自结算）
        _lastSeenLoss = loss;
        if (damage <= 0)
        {
            return; // 无实际伤害的结算不计数（防御）
        }

        TotalDamage += damage;
        if (!_firedInDomain && TotalDamage >= _threshold)
        {
            _firedInDomain = true;
            await GameKernelTestKit.FireAsync(_engine, _fireSink, ct); // 阈值达成 → 触发产出目标（恰一次）
        }
    }

    private Task OnTurnEndAsync(CardEventView view, Context ctx, CancellationToken ct)
    {
        ResetCount += 1;
        TotalDamage = 0;
        _firedInDomain = false;
        return Task.CompletedTask;
    }
}

// ---------- 场景③：首次洗切监听 handler ----------

/// <summary>
/// 首次洗切监听 handler（场景③「首次洗切卡组时」）：自建被动触发器挂总线（hooks＝deck.shuffled）；
/// 归属过滤（仅观察指定玩家卡组）→ 首次达成标志自持 → 触发自持主动触发器恰一次；后续洗切不重复触发。
/// 「是否首次」不入信号——由监听者自持标志判断；监听窗口自挂接时点起、不回溯。
/// </summary>
internal sealed class FirstShuffleWatchHandler
{
    private readonly LogicEngine _engine;
    private readonly Player _player;
    private readonly string _watchName;
    private readonly string _fireSink;
    private IDisposable? _watch;

    public FirstShuffleWatchHandler(LogicEngine engine, Player player)
    {
        _engine = engine;
        _player = player;
        _watchName = $"{player.Index}/首次洗切监听";
        _fireSink = GameKernelTestKit.Sink(engine, $"{player.Index}/首次洗切触发目标", () => FireCount += 1);
    }

    /// <summary>归属过滤后的观察窗口计数（窗口内令标志可参与判定的信号数）。</summary>
    public int SeenCount { get; private set; }

    /// <summary>首次达成触发次数（恰一次语义的可断言观测面）。</summary>
    public int FireCount { get; private set; }

    public void Attach()
        => _watch ??= _engine.Watch<DeckShuffleWatchView>(GameUpdates.DeckShuffled, _watchName, this, OnShuffledAsync);

    private async Task OnShuffledAsync(DeckShuffleWatchView view, Context ctx, CancellationToken ct)
    {
        if (view.Player is not Player player || !ReferenceEquals(player, _player))
        {
            return; // 归属过滤（「友方首次洗切」式——仅观察指定玩家卡组）
        }

        SeenCount += 1;
        if (FireCount > 0)
        {
            return; // 标志自持：已达成 → 不重复触发
        }

        await GameKernelTestKit.FireAsync(_engine, _fireSink, ct);
    }
}

// ---------- 场景④：即时比较评估器 ----------

/// <summary>
/// 即时比较评估器（场景④「每比友方多 1 个指挥点槽」式）：读双方实时面板（指挥点槽）现算差值，
/// 据差值触发主动触发器 N 次；无自持计数/缓存/历史（不新增状态——比较结果始终来自实时读取）。
/// </summary>
internal sealed class PointSlotLeadEvaluator
{
    private readonly LogicEngine _engine;
    private readonly Player _self;
    private readonly Player _opponent;
    private readonly string _fireSink;

    public PointSlotLeadEvaluator(LogicEngine engine, Player self, Player opponent)
    {
        _engine = engine;
        _self = self;
        _opponent = opponent;
        _fireSink = GameKernelTestKit.Sink(engine, $"即时比较/差值产出目标（{self.Index}）", () => FireCount += 1);
    }

    /// <summary>差值的触发产出次数（可断言结论）。</summary>
    public int FireCount { get; private set; }

    /// <summary>即时比较：读实时面板现算（自身槽 − 对手槽）；据差值产出——触发主动触发器 N 次。返回差值。</summary>
    public async Task<int> EvaluateAndFireAsync(CancellationToken ct = default)
    {
        var lead = _self.PointSlots - _opponent.PointSlots; // 实时读取（无缓存/无历史）
        for (var i = 0; i < lead; i++)
        {
            await GameKernelTestKit.FireAsync(_engine, _fireSink, ct);
        }

        return lead;
    }
}

// ---------- 测试视图（载荷键＝属性名约定） ----------

/// <summary>card.played 监听视图（W4-1 升级载荷：{ Card, Player }——被使用卡实例＋使用方）。</summary>
[ContextView]
public class CardPlayedWatchView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    [Optional]
    [Read]
    public virtual object? Player { get; set; }
}

/// <summary>deck.shuffled 监听视图（载荷：{ Player, Deck }——被洗切卡组归属玩家＋被洗切卡组）。</summary>
[ContextView]
public class DeckShuffleWatchView
{
    [Optional]
    [Read]
    public virtual object? Player { get; set; }

    [Optional]
    [Read]
    public virtual object? Deck { get; set; }
}

/// <summary>card.damaged 监听视图（载荷：{ Card, Amount, Source }——受方侧；伤害结算后发射、防御实际变化恰一次）。</summary>
[ContextView]
public class CardDamagedWatchView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    [Optional]
    [Read]
    public virtual int Amount { get; set; }
}
