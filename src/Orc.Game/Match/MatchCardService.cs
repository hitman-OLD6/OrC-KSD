using System.Diagnostics.CodeAnalysis;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Players;

namespace Orc.Game;

// ─────────────────────────────────────────────────────────────────────────────
// S9（G6+G13 生成·复制·转换）：对局卡牌服务（服务面）——「卡牌工厂＋放置面」的统一操作面。
// ①服务面操作全集＝创建（Create）＋放置（Place）两原子：为效果提供「对局服务访问面」；
//   复制/转换不设专属操作（＝效果侧原子组合：复制〔读源卡定义 → Create → Place〕、转换〔离场/销毁 → Create → Place〕）；
//   类型增补不进本服务（属单位数据面受控入口——UnitCard.AddUnitTypeAsync）。
// ②统一放置服务（四类去向）：手牌（尾部追加＋满手爆牌裁决）、阵线（既有「加入路径」语义——非部署、不扣费、发 unit.joined）、
//   卡组顶（取件端）、洗入（装入＋统一洗切动作面——发 deck.shuffled）；＋相邻空槽解析（与「邻位动态候选」同源；解析与选取分离）。
// ③官方主形态＝「创建并放置」复合操作（一次复合调用；失败自动回收已创建实例——「不产生半放置态」由服务侧内建保证）。
// ④归属（Owner）＝落点玩家：创建时即按落点玩家预归属；放置校验「实例归属＝目标玩家」（跨玩家放置＝明确拒绝）。
// ⑤失败分层（Q&A-9·2(b) 三层）：调用方错误（null/未加载/跨玩家/重复归属/已销毁/门禁非「进行」态/前置契约不符）＝明确异常；
//   业务失败（槽被占/无相邻空槽/未选出槽位/类别不匹配等运行时竞争与约束）＝结果对象（不抛）。
// ⑥门禁：仅「进行」态（先例＝随机服务取用门禁）；准备态/终局后对外操作＝明确拒绝（异常）；独立构造（无状态读取器）＝无门禁。
// ⑦信号（接入既有/底层组合负责；服务不另发新信号）：手牌加入＝card.hand.add（恰一次、实际进入手牌路径）；
//   阵线加入＝unit.joined（既有加入链）；洗入＝deck.shuffled（统一洗切动作面）；满手爆牌＝销毁（含 card.destroyed）→
//   card.burned 恰一次（hand.add/drawn 零次——生成路径爆牌口径）；卡组顶装入＝静默；回收＝游戏层信号零发射。
// ⑧动作作用域：服务不自行包载（由调用方〔效果执行/动作入口〕的既有作用域自然聚合；无作用域语境＝不产段、信号照发）。
// ⑨可注入可测试：服务随对局装配（Match.CardService——注入各玩家「卡 → 玩家 → 服务」＋对局面动作），
//   亦可独立构造（公开构造——依赖以可替换形态注入：支援线查询/阵线放置/洗切动作/状态读取器均可缺省）；
//   生产装配路径与测试装配路径共享同一实现（不双实现）。
// ⑩接入面：效果运行期经 MatchCardService.ResolveFor（「卡 → 玩家 → 服务」——与 GameEnvironment/MatchRandomService 同构）取用。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>卡牌放置结局状态（三态；成功 / 爆牌 / 失败）。</summary>
public enum CardPlaceStatus
{
    /// <summary>放置完成（实例实际进入目标区域）。</summary>
    Placed,

    /// <summary>
    /// 爆牌（手牌目标专属）：满手（≥ <see cref="Player.HandLimit"/>）时放置到手牌＝KARDS 爆牌语义——
    /// 不经手牌（直爆）、实例已销毁；信号＝销毁（含 card.destroyed）→ card.burned 恰一次（hand.add 零次）。
    /// 结果为「成功」语义（裁决完成——非失败）。
    /// </summary>
    Burned,

    /// <summary>业务失败（未放置；已创建实例由组合侧回收——「无未归属残留」）。</summary>
    Rejected,
}

/// <summary>卡牌放置失败原因（类别化；成功/爆牌＝null）。</summary>
public enum CardPlaceFailureReason
{
    /// <summary>目标槽位非空（运行时状态竞争——业务失败、不抛）。</summary>
    TargetSlotOccupied,

    /// <summary>单位已单位化（非待放置状态——业务失败、不抛）。</summary>
    UnitAlreadyUnitized,

    /// <summary>无相邻空槽（解析为空——失败、不产生任何副作用）。</summary>
    NoAdjacentSlot,

    /// <summary>未选出槽位（选取方放弃——解析与选取分离下的选取环节放弃）。</summary>
    NoSlotSelected,

    /// <summary>放置类别约束不匹配（如非单位定义放置到支援线——业务失败、不抛）。</summary>
    CategoryMismatch,

    /// <summary>其他放置失败（防御/兜底类别）。</summary>
    PlacementRejected,
}

/// <summary>
/// 卡牌放置结果对象（不抛；消费方读 <see cref="Status"/> ＋ <see cref="FailureReason"/> 分流）：
/// 放置完成＝<see cref="CardPlaceStatus.Placed"/>；满手爆牌＝<see cref="CardPlaceStatus.Burned"/>（实例已销毁）；
/// 业务失败＝<see cref="CardPlaceStatus.Rejected"/>（携带原因类别）。<see cref="Card"/>＝关联实例
/// （放置/爆牌/被回收的实例；策略性失败〔未创建实例〕＝null）。
/// </summary>
public sealed class CardPlaceResult
{
    private CardPlaceResult(CardPlaceStatus status, CardPlaceFailureReason? failureReason, CardBase? card)
    {
        Status = status;
        FailureReason = failureReason;
        Card = card;
    }

    /// <summary>结局状态（三态）。</summary>
    public CardPlaceStatus Status { get; }

    /// <summary>失败原因（仅失败时非 null；成功/爆牌＝null）。</summary>
    public CardPlaceFailureReason? FailureReason { get; }

    /// <summary>关联实例（放置/爆牌/被回收的实例；策略性失败＝null）。失败回收后仍可读其生命周期（观察点＝Life.IsAlive）。</summary>
    public CardBase? Card { get; }

    /// <summary>是否成功（Placed 或 Burned——爆牌为成功语义的裁决完成）。</summary>
    public bool IsSuccess => Status != CardPlaceStatus.Rejected;

    /// <summary>创建「放置完成」结果（框架内部）。</summary>
    internal static CardPlaceResult Placed(CardBase card) => new(CardPlaceStatus.Placed, failureReason: null, card);

    /// <summary>创建「爆牌」结果（框架内部；满手裁决——实例已销毁）。</summary>
    internal static CardPlaceResult Burned(CardBase card) => new(CardPlaceStatus.Burned, failureReason: null, card);

    /// <summary>创建「业务失败」结果（框架内部；card＝关联实例〔被回收者；策略性失败＝null〕）。</summary>
    internal static CardPlaceResult Rejected(CardPlaceFailureReason reason, CardBase? card = null)
        => new(CardPlaceStatus.Rejected, reason, card);
}

/// <summary>
/// 对局卡牌服务（S9：卡牌工厂＋放置面——生成/复制/转换的公共受控面；详见文件头注释）。
/// 构造：对局装配路径经 Match 注入全部动作面（支援线查询、阵线放置动作、洗切动作、状态读取器）；
/// 独立构造路径（测试/工具）＝可只提供 engine＋library（核心面可用；对局面操作缺省＝明确异常）。
/// </summary>
public sealed class MatchCardService
{
    private readonly LogicEngine _engine;
    private readonly CardLibrary _library;
    private readonly Func<Player, BattleLine>? _supportLineProvider;
    private readonly Func<UnitCard, FormationGap, CancellationToken, Task<PlayResult>>? _joinUnitAction;
    private readonly Func<Player, CancellationToken, Task>? _shuffleDeckAction;
    private readonly Func<MatchState>? _stateProvider;
    private readonly Func<IAddToHandAction?>? _addToHandActionProvider;
    private readonly IMatchCardLoader? _cardLoader;
    private readonly HashSet<string> _runtimeRegistered = new(StringComparer.Ordinal);

    /// <summary>
    /// 创建卡牌服务。
    /// </summary>
    /// <param name="engine">对局引擎（信号发射/销毁辅助）。</param>
    /// <param name="library">卡牌库（创建＝工厂＋定义读面）。</param>
    /// <param name="supportLineProvider">支援线查询面（相邻解析＋线归属校验；对局装配＝战场支援线；缺省＝阵线面不可用）。</param>
    /// <param name="joinUnitAction">阵线放置动作面（既有「加入路径」语义；对局装配＝PlayManager.JoinUnitAsync；缺省＝阵线放置不可用）。</param>
    /// <param name="shuffleDeckAction">洗切动作面（洗入；对局装配＝Match.ShuffleDeckAsync——发 deck.shuffled；缺省＝洗入不可用）。</param>
    /// <param name="stateProvider">对局状态读取器（门禁——仅「进行」态；缺省＝独立构造无门禁）。</param>
    /// <param name="addToHandActionProvider">加入手牌动作面（S7：放置到手牌的唯一入口；缺省＝独立构造时按需自建最小实例）。</param>
    /// <param name="cardLoader">
    /// 卡牌加载器端口（S7：局内新增卡——token／升级／老兵——走**单卡 <c>LoadBatchAsync</c>**（一元清单），
    /// 并同步更新中间层；缺省＝null＝独立构造路径——按卡库既有条目直接实例化）。
    /// </param>
    /// <exception cref="ArgumentNullException">engine / library 为 null。</exception>
    public MatchCardService(
        LogicEngine engine,
        CardLibrary library,
        Func<Player, BattleLine>? supportLineProvider = null,
        Func<UnitCard, FormationGap, CancellationToken, Task<PlayResult>>? joinUnitAction = null,
        Func<Player, CancellationToken, Task>? shuffleDeckAction = null,
        Func<MatchState>? stateProvider = null,
        CardEffectRegistry? effectRegistry = null,
        Func<IAddToHandAction?>? addToHandActionProvider = null,
        IMatchCardLoader? cardLoader = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(library);

        _engine = engine;
        _library = library;
        _supportLineProvider = supportLineProvider;
        _joinUnitAction = joinUnitAction;
        _shuffleDeckAction = shuffleDeckAction;
        _stateProvider = stateProvider;
        _effectRegistry = effectRegistry;
        _addToHandActionProvider = addToHandActionProvider;
        _cardLoader = cardLoader;
    }

    /// <summary>加入手牌动作面（对局路径＝装配链注入的同一实例；独立构造＝经动作目录查询面按默认实现集创建并登记）。</summary>
    private IAddToHandAction AddToHandAction()
        => _addToHandActionProvider?.Invoke() ?? ActionRuntime.ResolveAddToHand(_engine);

    private readonly CardEffectRegistry? _effectRegistry;

    // ---------- 运行期卡注册（S4·R5/D7·对局级） ----------

    /// <summary>
    /// **对局级运行期卡注册**（只追加不覆盖）：登记定义、效果声明与内联预制体——此后
    /// <see cref="CreateAsync"/> 即可按该 id 造卡（实例经对局级 ID 水位线判为"构筑之外"）。
    /// 顺序不变量：预制体与声明**先于**任何实例化（否则该卡装载时无效果）。
    /// </summary>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    /// <exception cref="ArgumentException">registration.Id 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">对局态门禁（准备态/终局后）；或同 id 已注册（只追加不覆盖）。</exception>
    public void RegisterCard(RuntimeCardRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Id);
        EnsureInProgress();

        // ① 预制体登记（内联）——先于声明与实例化。
        foreach (var snapshot in registration.Prefabs)
        {
            _engine.Prefabs.RegisterPrefab(snapshot);
        }

        // ② 效果声明（卡 → 预制体 id）。
        if (_effectRegistry is not null && registration.PrefabIds.Count > 0)
        {
            _effectRegistry.DeclarePrefab(registration.Id, registration.PrefabIds);
        }

        // ③ 定义登记（同 id 重复＝拒绝；library 自身即 fail-fast）。
        _library.Register(registration.Id, registration.Definition);

        // ④ 记账：该 id 是**运行期直接注册**的（无资产身份）——单卡装载路径按此识别「不进资产解析」的卡。
        _runtimeRegistered.Add(registration.Id);
    }

    // ---------- 创建（卡牌工厂） ----------

    /// <summary>
    /// 创建：定义 id ＋ 归属玩家（创建即预归属——落点玩家）→ 已按加载模板就绪（词条/效果/ID/元数据齐备）、
    /// 可放置的实例（区域未归属〔待放置〕；card.load 照常发射——信号事实性）。
    /// 派生/衍生卡与普通卡共用本通路（统一创建；「构筑外」经对局级 ID 水位线自动判定——新实例 ID ＞ 水位线）。
    /// </summary>
    /// <param name="definitionId">定义注册 id（须已注册于本对局卡牌库）。</param>
    /// <param name="owner">归属玩家（＝预期落点玩家）。</param>
    /// <exception cref="ArgumentNullException">owner 为 null。</exception>
    /// <exception cref="ArgumentException">definitionId 为 null/空白。</exception>
    /// <exception cref="KeyNotFoundException">定义不存在/未注册（输入契约无效——明确失败、不产生任何实例残留）。</exception>
    /// <exception cref="InvalidOperationException">对局态门禁（准备态/终局后——明确拒绝）。</exception>
    public async Task<CardBase> CreateAsync(string definitionId, Player owner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(owner);
        EnsureInProgress();

        // S7：局内新增卡（token／升级／老兵）走**单卡加载路径**（一元清单的整批调用——同一入口，不设第二条装载路径）。
        // 装载只做解析／校验／定义生成与中间层同步，实例 ID 仍由下方 LoadAsync 按水位线顺序分配。
        var libraryKey = await LoadSingleAsync(definitionId, owner, ct).ConfigureAwait(false);

        var card = _library.Instantiate(libraryKey); // 未注册＝明确失败（KeyNotFoundException——先于任何状态变更、无残留）
        await card.LoadAsync(owner, ct);
        return card;
    }

    /// <summary>
    /// 单卡装载（S7；一元清单）：经卡牌加载器端口解析／校验／落地，返回**卡库键**（解析后的应用层 ID）。
    /// <para>
    /// 结局为「卡位留空」时：卡库里已有该键（运行期直接注册的卡——无资产身份，不进资产解析）⇒ 按既有条目继续；
    /// 否则＝明确失败（未注册／非法卡不生成）。无端口的独立构造路径＝原样返回入参。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">卡未由卡牌加载器产出可玩定义（未注册／非法）。</exception>
    private async Task<string> LoadSingleAsync(string definitionId, Player owner, CancellationToken ct)
    {
        if (_cardLoader is null)
        {
            return definitionId;
        }

        var slot = MatchCardSlot.ForPlayer(owner.Index, 0, definitionId);
        var outcome = await _cardLoader.LoadBatchAsync(new[] { slot }, ct).ConfigureAwait(false);
        var result = outcome.Slots.Count == 1 ? outcome.Slots[0] : CardSlotOutcome.Empty;

        if (result.IsLoaded && result.AppId is { Length: > 0 } appId)
        {
            return appId;
        }

        if (_runtimeRegistered.Contains(definitionId) && _library.Contains(definitionId))
        {
            // 运行期直接注册的卡（RuntimeCardRegistration——无资产身份、无 GUID）：装载链无定义可解析，
            // 按卡库既有条目实例化（该路径的注册/去重语义由 RegisterCard 自身负责）。
            return definitionId;
        }

        throw new KeyNotFoundException(
            $"卡 '{definitionId}' 未由卡牌加载器产出可玩定义（未注册或判非法——实例化被拒绝）。");
    }

    /// <summary>
    /// 读源卡定义（复制/转换等「读源卡定义」组合的读辅助）：反查源卡定义在本对局卡牌库的注册 id；
    /// 未注册＝false（调用侧按「不可读」处置）。源卡仅提供定义（不传递实例状态/归属）；不依赖源实例存活
    /// （已离场/已销毁实例仍可按其定义复制/转换）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null（防御拒绝——调用方错误口径）。</exception>
    public bool TryResolveDefinitionId(CardBase card, [NotNullWhen(true)] out string? definitionId)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _library.TryGetRegisteredId(card.Definition, out definitionId);
    }

    /// <summary>
    /// 卡池枚举读面（C2 加性读面）：本局卡牌库已注册定义的全体（id → 定义；只读转发——单源不另建）。
    /// 用途＝效果侧构造「开发/发现」取样池与筛选候选集（服务只对给定序列取样；筛选与合法性由调用方负责）；
    /// 注册/修改权能不经本面暴露（只读枚举）。
    /// </summary>
    public IReadOnlyDictionary<string, CardDefinition> RegisteredDefinitions => _library.Definitions;

    // ---------- 放置（四类去向） ----------

    /// <summary>
    /// 放置到手牌（尾部追加——「加入」语义）：实际进入手牌＝发 card.hand.add（恰一次——「其他来源」接入点）；
    /// 满手（≥ <see cref="Player.HandLimit"/>）＝爆牌裁决（直爆：不经手牌；销毁〔含 card.destroyed〕→
    /// card.burned 恰一次；结果＝<see cref="CardPlaceStatus.Burned"/>）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / player 为 null。</exception>
    /// <exception cref="InvalidOperationException">门禁；未加载归属；跨玩家；重复归属；已销毁（前置契约——明确拒绝）。</exception>
    public async Task<CardPlaceResult> PlaceToHandAsync(CardBase card, Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        ValidatePlacementTarget(card, player);

        // 入口适配器（S7）：只把 ActionResult 翻译为 CardPlaceResult；写入与爆牌裁决都在「加入手牌」动作的处理端。
        var result = await AddToHandAction().AddAsync(card, player, triggerCard: null, ct).ConfigureAwait(false);
        var outcome = result.Targets[0];
        return outcome.Outcome switch
        {
            ActionOutcomeKind.Applied => CardPlaceResult.Placed(card),
            _ when outcome.Reason == ActionFailureKind.HandFull => CardPlaceResult.Burned(card),
            _ => CardPlaceResult.Rejected(CardPlaceFailureReason.PlacementRejected, card),
        };
    }

    /// <summary>
    /// 放置到支援线（指定空槽——既有「加入路径」语义：非部署——发 unit.joined、不扣费、不走部署词条）。
    /// 槽位由调用方指定、须为空槽（被占＝业务失败结果对象）；槽位须在目标玩家的支援线内（前置契约）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / player / slot 为 null。</exception>
    /// <exception cref="ArgumentException">槽位不在目标玩家支援线内（前置契约不符——明确拒绝）。</exception>
    /// <exception cref="InvalidOperationException">门禁；未加载归属；跨玩家；重复归属；已销毁；阵线面未装配。</exception>
    public async Task<CardPlaceResult> PlaceToSupportLineAsync(
        UnitCard card, Player player, FormationGap slot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(slot);
        ValidatePlacementTarget(card, player);

        var line = RequireSupportLine(player);
        if (!ContainsSlot(line, slot))
        {
            throw new ArgumentException(
                $"目标槽位 '{slot.Name}' 不在玩家 #{player.Index} 的支援线内（前置契约不符——放置被拒绝）。", nameof(slot));
        }

        var action = _joinUnitAction
            ?? throw new InvalidOperationException(
                "阵线放置动作面未装配（独立构造场景不支持阵线放置——装配性错误）。");

        var result = await action(card, slot, ct);
        return result.Status switch
        {
            PlayResultStatus.Success => CardPlaceResult.Placed(card),
            _ when result.FailureReason == PlayFailureReason.TargetSlotOccupied
                => CardPlaceResult.Rejected(CardPlaceFailureReason.TargetSlotOccupied, card),
            _ when result.FailureReason == PlayFailureReason.UnitAlreadyUnitized
                => CardPlaceResult.Rejected(CardPlaceFailureReason.UnitAlreadyUnitized, card),
            _ => CardPlaceResult.Rejected(CardPlaceFailureReason.PlacementRejected, card),
        };
    }

    /// <summary>
    /// 放置到卡组顶（装入取件端——下次抽取取到）：静默（卡组操作本身无信号；不洗切）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / player 为 null。</exception>
    /// <exception cref="InvalidOperationException">门禁；未加载归属；跨玩家；重复归属；已销毁；定义未注册（素材解析失败）。</exception>
    public Task<CardPlaceResult> PlaceToDeckTopAsync(CardBase card, Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        ValidatePlacementTarget(card, player);

        var definitionId = ResolveDefinitionId(card); // 素材解析（可能拒绝——先于任何变更）
        player.Deck.InsertInstanceAt(0, definitionId, card); // 装入取件端（静默——无信号）
        return Task.FromResult(CardPlaceResult.Placed(card));
    }

    /// <summary>
    /// 洗入（装入卡组＋执行既有统一洗切动作面）：发 deck.shuffled 恰一次（洗切动作面统一负责）；
    /// 洗的位置随机性由洗切自然产生（不建「随机位置插入」专门机制）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / player 为 null。</exception>
    /// <exception cref="InvalidOperationException">门禁；未加载归属；跨玩家；重复归属；已销毁；定义未注册；洗切动作面未装配。</exception>
    public async Task<CardPlaceResult> PlaceIntoDeckShuffledAsync(
        CardBase card, Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);
        ValidatePlacementTarget(card, player);

        var shuffle = _shuffleDeckAction
            ?? throw new InvalidOperationException(
                "洗切动作面未装配（独立构造场景不支持洗入——装配性错误）。");

        var definitionId = ResolveDefinitionId(card); // 素材解析（可能拒绝——先于任何变更）
        player.Deck.InsertInstanceAt(0, definitionId, card); // 装入（取件端；插入后洗切——复合语义）
        await shuffle(player, ct);
        return CardPlaceResult.Placed(card);
    }

    // ---------- 相邻空槽解析（解析与选取分离——选取由调用方决定） ----------

    /// <summary>
    /// 相邻空槽解析（目标玩家支援线——与既有「邻位动态候选」同源〔单源不另建规则〕：遍历线内被占槽位〔含 HQ〕、
    /// 取左右空邻位、去重、索引升序；跨线不计）：返回候选集；选取由调用方决定（选定后经「放置到指定槽」落位；
    /// 「随机选一个」＝调用方经随机服务组合）。解析为空＝空列表（组合侧失败＝无候选、不产生任何副作用）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">门禁；支援线查询面未装配。</exception>
    public IReadOnlyList<FormationGap> GetAdjacentEmptySlots(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        EnsureInProgress();
        return RequireSupportLine(player).GetInsertionGaps();
    }

    // ---------- 复合操作（「创建并放置」——官方主形态；失败自动回收已创建实例） ----------

    /// <summary>创建并放置到手牌（一次复合调用；失败自动回收——手牌去向的失败点＝爆牌为成功语义，回收仅防御兜底）。</summary>
    public Task<CardPlaceResult> CreateAndPlaceToHandAsync(
        string definitionId, Player player, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(player);
        return CreateAndPlaceCoreAsync(definitionId, player, card => PlaceToHandAsync(card, player, ct), ct);
    }

    /// <summary>
    /// 创建并放置到支援线（一次复合调用；失败自动回收已创建实例——「不产生半放置态」服务侧内建）。
    /// 类别预检：非单位定义＝业务失败（不创建实例——预防式零副作用）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">定义不存在/未注册（输入契约无效——明确失败）。</exception>
    public Task<CardPlaceResult> CreateAndPlaceToSupportLineAsync(
        string definitionId, Player player, FormationGap slot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(slot);

        var definition = _library.Get(definitionId); // 未注册＝明确失败（先于创建）
        if (definition.Category != CardCategory.Unit)
        {
            return Task.FromResult(CardPlaceResult.Rejected(CardPlaceFailureReason.CategoryMismatch));
        }

        return CreateAndPlaceCoreAsync(
            definitionId, player, card => PlaceToSupportLineAsync((UnitCard)card, player, slot, ct), ct);
    }

    /// <summary>创建并放置到卡组顶（一次复合调用；静默）。</summary>
    public Task<CardPlaceResult> CreateAndPlaceToDeckTopAsync(
        string definitionId, Player player, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(player);
        return CreateAndPlaceCoreAsync(definitionId, player, card => PlaceToDeckTopAsync(card, player, ct), ct);
    }

    /// <summary>创建并洗入卡组（一次复合调用；发 deck.shuffled 恰一次）。</summary>
    public Task<CardPlaceResult> CreateAndPlaceIntoDeckShuffledAsync(
        string definitionId, Player player, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(player);
        return CreateAndPlaceCoreAsync(
            definitionId, player, card => PlaceIntoDeckShuffledAsync(card, player, ct), ct);
    }

    /// <summary>
    /// 创建并放置到相邻处（一次复合调用——官方主形态；解析与选取分离：服务解析候选、<paramref name="slotSelector"/>
    /// 由调用方提供选取策略〔如随机服务 PickOne〕、返回 null＝放弃选择）。
    /// 顺序：先判定（解析——空＝失败、不创建实例：零副作用）→ 选取 → 选取校验 → 就绪（创建）→ 替换（放置；
    /// 失败自动回收）。
    /// </summary>
    /// <param name="definitionId">定义注册 id（须为单位定义——非单位＝业务失败）。</param>
    /// <param name="player">归属/落点玩家。</param>
    /// <param name="slotSelector">选取策略（接收候选集，返回选中槽位或 null＝放弃；返回候选集外槽位＝调用方错误）。</param>
    /// <exception cref="ArgumentNullException">player / slotSelector 为 null。</exception>
    /// <exception cref="ArgumentException">选取的槽位不在解析候选集内（调用方错误——明确拒绝）。</exception>
    /// <exception cref="KeyNotFoundException">定义不存在/未注册。</exception>
    public async Task<CardPlaceResult> CreateAndPlaceToAdjacentAsync(
        string definitionId, Player player, Func<IReadOnlyList<FormationGap>, FormationGap?> slotSelector,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(slotSelector);

        var definition = _library.Get(definitionId); // 未注册＝明确失败（先于解析/创建）
        if (definition.Category != CardCategory.Unit)
        {
            return CardPlaceResult.Rejected(CardPlaceFailureReason.CategoryMismatch);
        }

        var candidates = GetAdjacentEmptySlots(player); // 解析（先于创建——预防式）
        if (candidates.Count == 0)
        {
            return CardPlaceResult.Rejected(CardPlaceFailureReason.NoAdjacentSlot); // 空解析＝失败、不创建实例（零副作用）
        }

        var slot = slotSelector(candidates); // 选取（调用方策略）
        if (slot is null)
        {
            return CardPlaceResult.Rejected(CardPlaceFailureReason.NoSlotSelected);
        }

        if (!candidates.Contains(slot))
        {
            throw new ArgumentException(
                "选取的槽位不在解析候选集内（调用方错误——放置被拒绝）。", nameof(slotSelector));
        }

        return await CreateAndPlaceCoreAsync(
            definitionId, player, card => PlaceToSupportLineAsync((UnitCard)card, player, slot, ct), ct);
    }

    // ---------- 接入面解析（卡 → 玩家 → 服务；与 GameEnvironment/MatchRandomService.ResolveFor 同构） ----------

    /// <summary>
    /// 卡 → 卡牌服务解析（读取路径「卡 → 玩家 → 服务」的收敛点）：
    /// 卡经归属玩家取服务；未加载（无归属）/独立构造（未注入）/非卡实体＝null（不可达——调用侧按「功能不可用、不抛错、不失败」处置）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public static MatchCardService? ResolveFor(Orc.Cards.Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card switch
        {
            Hq hq => hq.Owner.CardService,
            CardBase cardBase => cardBase.Owner?.CardService,
            _ => null,
        };
    }

    // ---------- 内部：复合执行（创建 → 放置 →〔失败〕回收） ----------

    /// <summary>
    /// 复合执行内核（官方主形态）：创建 → 放置；业务失败＝回收已创建实例（销毁——无未归属残留；游戏层信号零发射）；
    /// 异常路径同回收（防御——「无未归属残留」普适标准）。回收经销毁面（引擎级 card.destroyed 随销毁链——
    /// 「回收场景可接受」口径；实现选取并记录）。
    /// </summary>
    private async Task<CardPlaceResult> CreateAndPlaceCoreAsync(
        string definitionId, Player player, Func<CardBase, Task<CardPlaceResult>> place, CancellationToken ct)
    {
        var card = await CreateAsync(definitionId, player, ct);
        CardPlaceResult result;
        try
        {
            result = await place(card);
        }
        catch
        {
            await _engine.DestroyCard(card); // 防御：异常路径回收（无未归属残留普适）
            throw;
        }

        if (result.Status == CardPlaceStatus.Rejected)
        {
            await _engine.DestroyCard(card); // 业务失败回收（服务侧内建；不依赖调用方）
        }

        return result;
    }

    // ---------- 内部：前置校验与辅助 ----------

    /// <summary>
    /// 放置前置校验（共用；违反＝调用方错误——明确异常）：门禁（仅「进行」态）；卡已加载归属；
    /// 实例归属＝目标玩家（跨玩家放置＝创建时预归属原则的调用方组合错误）；
    /// 实例生命周期存活（已销毁＝不可放置）；未归属（重复放置既有归属实例＝拒绝——实例唯一归属不变量）；
    /// 未在场（Position 非空＝已在场）；未死亡（已死亡/已毁＝尸体不可放置）。
    /// </summary>
    private void ValidatePlacementTarget(CardBase card, Player player)
    {
        EnsureInProgress();

        if (card.Owner is null)
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 未加载归属（未完成 LoadAsync——不可放置；装配性错误）。");
        }

        if (!ReferenceEquals(card.Owner, player))
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 的归属（玩家 #{card.Owner.Index}）与放置目标玩家 #{player.Index} 不一致"
                + "（跨玩家放置被拒绝——创建即按落点预归属）。");
        }

        if (!card.Life.IsAlive)
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 已销毁（不可放置——已销毁实例不构成可放置对象）。");
        }

        if (player.Hand.Contains(card) || player.Deck.ContainsInstance(card))
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 已归属玩家 #{player.Index} 的手牌/卡组（重复放置被拒绝——实例唯一归属不变量）。");
        }

        if (card.TryGetData<UnitStateData>(out var state))
        {
            if (state.IsDestroyed)
            {
                throw new InvalidOperationException(
                    $"单位 '{card.Name}' 已死亡/已毁（不可放置——尸体不构成可放置对象）。");
            }

            if (state.Line is not null)
            {
                throw new InvalidOperationException(
                    $"单位 '{card.Name}' 已在场（重复放置被拒绝——实例唯一归属不变量）。");
            }
        }
    }

    /// <summary>门禁：仅「进行」态可操作（准备态/终局后＝明确拒绝）；独立构造（无状态读取器）＝无门禁约束。</summary>
    private void EnsureInProgress()
    {
        if (_stateProvider is null)
        {
            return; // 独立构造（无对局生命周期）：无门禁约束
        }

        var state = _stateProvider();
        if (state == MatchState.InProgress)
        {
            return;
        }

        throw new InvalidOperationException(state == MatchState.Ended
            ? "对局已结束（终局），卡牌服务操作被拒绝。"
            : "对局尚未进入'进行'态：卡牌服务操作不可用（须先成功完成 Initialize）。");
    }

    /// <summary>支援线查询面解析（未装配＝明确异常——独立构造场景阵线面不可用）。</summary>
    private BattleLine RequireSupportLine(Player player)
        => _supportLineProvider?.Invoke(player)
            ?? throw new InvalidOperationException(
                "支援线查询面未装配（独立构造场景不支持阵线面——装配性错误）。");

    /// <summary>线内槽位归属校验（引用相等——线内索引枚举）。</summary>
    private static bool ContainsSlot(BattleLine line, FormationGap slot)
    {
        foreach (var candidate in line.Members)
        {
            if (ReferenceEquals(candidate, slot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析卡牌的定义注册 id（素材解析：经卡牌库反查；未注册＝明确拒绝、不静默）。</summary>
    private string ResolveDefinitionId(CardBase card)
    {
        if (_library.TryGetRegisteredId(card.Definition, out var id))
        {
            return id;
        }

        throw new InvalidOperationException(
            $"卡牌 '{card.Name}' 的定义未注册于本对局卡牌库（素材解析失败——明确拒绝）。");
    }
}
