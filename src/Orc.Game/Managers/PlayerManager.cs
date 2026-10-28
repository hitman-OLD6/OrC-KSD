using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Collections;
using Orc.Game.Players;

namespace Orc.Game.Managers;

/// <summary>定向取卡（Deck Fetch·批 6）结局状态（三态：完成 / 爆牌 / 失败）。</summary>
public enum DeckFetchStatus
{
    /// <summary>完成（卡实际自卡组取件并进入手牌）。</summary>
    Fetched,

    /// <summary>
    /// 爆牌（满手裁决·**完成语义**——对齐 <c>CardPlaceStatus.Burned</c>「裁决完成——非失败」口径）：
    /// 卡已自卡组移除并销毁（card.drawn 恰一次 → 销毁 → card.burned 恰一次；card.hand.add 零次——不经手牌）。
    /// </summary>
    Burned,

    /// <summary>失败（未执行；零变更、零信号——可编程处置）。</summary>
    Rejected,
}

/// <summary>定向取卡失败原因（类别化；完成/爆牌＝null；字面稳定、测试锁定）。</summary>
public enum DeckFetchFailureReason
{
    /// <summary>卡不在所声明玩家卡组中（含从未在过、已被取出／重复调用——失败路径幂等零副作用）。</summary>
    NotInDeck,

    /// <summary>卡已销毁（引用已失效——判定先于容器归属：「已销毁＋不在卡组」＝本类别）。</summary>
    CardDestroyed,

    /// <summary>目标无效（未加载／独立构造／定义未注册于本对局；含跨玩家——声明玩家≠卡归属玩家）。</summary>
    InvalidTarget,

    /// <summary>服务不可用／门禁（准备态／初始化加载未完成／非可操作相位／终局后——对齐批 4 服务不可用／门禁先例）。</summary>
    Unavailable,
}

/// <summary>
/// 定向取卡结果对象（不抛；消费方读 <see cref="Status"/> ＋ <see cref="FailureReason"/> 分流）：
/// 完成＝<see cref="DeckFetchStatus.Fetched"/>；爆牌＝<see cref="DeckFetchStatus.Burned"/>（完成语义——被爆卡已销毁）；
/// 失败＝<see cref="DeckFetchStatus.Rejected"/>（携带原因类别）。
/// <see cref="Card"/>＝关联卡片引用（完成＝已入手的卡；爆牌＝被爆卡〔生命周期仍可读——观察点＝Life.IsAlive〕；
/// 失败＝目标卡〔按可用性，可为 null〕）。
/// 两层同型：<see cref="PlayerManager.FetchFromDeckAsync"/> 动作（动作层）与
/// csx 受控面（<c>EffectRuntime.FetchFromDeckAsync</c>）返回**同一类型**。
/// </summary>
public sealed class DeckFetchResult
{
    private DeckFetchResult(DeckFetchStatus status, DeckFetchFailureReason? failureReason, Card? card)
    {
        Status = status;
        FailureReason = failureReason;
        Card = card;
    }

    /// <summary>结局状态（三态）。</summary>
    public DeckFetchStatus Status { get; }

    /// <summary>失败原因（仅失败时非 null；完成/爆牌＝null）。</summary>
    public DeckFetchFailureReason? FailureReason { get; }

    /// <summary>关联卡片引用（完成＝已入手的卡；爆牌＝被爆卡〔仍可读其生命周期〕；失败＝目标卡〔按可用性，可为 null〕）。</summary>
    public Card? Card { get; }

    /// <summary>是否成功（Fetched 或 Burned——爆牌为完成语义的裁决完成）。</summary>
    public bool IsSuccess => Status != DeckFetchStatus.Rejected;

    /// <summary>创建「完成」结果（框架内部）。</summary>
    internal static DeckFetchResult Fetched(Card card) => new(DeckFetchStatus.Fetched, failureReason: null, card);

    /// <summary>创建「爆牌」结果（框架内部；满手裁决——实例已销毁）。</summary>
    internal static DeckFetchResult Burned(Card card) => new(DeckFetchStatus.Burned, failureReason: null, card);

    /// <summary>创建「失败」结果（框架内部；类别＋目标卡〔按可用性〕）。</summary>
    internal static DeckFetchResult Rejected(DeckFetchFailureReason reason, Card? card = null)
        => new(DeckFetchStatus.Rejected, reason, card);
}

/// <summary>
/// 玩家管理器（玩家领域真源）：创建双玩家、提供玩家访问、执行卡组加载与抽牌操作。
/// 卡组加载（对局开始；2A 新增）：逐玩家（索引升序）逐张——卡组取件（洗牌后顺序）→ 经卡牌库实例化（三类卡之一）
/// → 装配到卡组条目（同一性：即后续起手 / 抽牌所得实例）→ 执行加载模板（其内广播 card.load）。
/// 抽牌链路＝卡组条目（加载实例）取件 → 加入手牌：起手装载＝静默版本（不发更新）；
/// 回合抽牌发 card.drawn → card.hand.add（顺序：drawn 先、hand.add 后；载荷均＝{ 玩家, 卡牌实例 }；粒度不同、并存）；
/// 满手（≥ HandLimit〔9〕）＝HandLimit 爆牌裁决（G7；直爆口径：不经手牌、手牌全程保持上限——发 card.drawn 恰一次
/// → 销毁〔引擎既有机制〕→ 发 card.burned 恰一次；card.hand.add 零次；KARDS 爆牌语义；起手装载不受裁决）。
/// 手牌动作面（G7；范围修正后——KARDS 官方无弃牌堆/墓地语义）：弃置动作（从手牌移除＋销毁＋card.discarded 信号——
/// 归属口径＝卡当前所在手牌、拒绝语义明确、非幂等）与回迁动作（手牌 → 卡组：跨集合受控动作——移出＋装入一体；
/// 位置＝卡组顶/指定位置；静默）。弃置与爆牌仅共享「销毁」步骤（引擎既有销毁）；信号发射各自独立
/// （弃置＝card.discarded、爆牌＝card.burned——爆牌不经弃置动作）。
/// 〔批 6 定向取卡加性面〕FetchFromDeckAsync＝「从卡组定向取出指定卡实例并移动到目标区域（手牌）」的受控动作——
/// 抽取语义（对齐抽牌链路；取件位置＝指定实例）：完成＝card.drawn → card.hand.add；满手＝爆牌裁决（drawn → 销毁 →
/// burned、hand.add 零次、卡自卡组移除）；失败＝结果对象（三态＋类别；零变更、零信号）；
/// 落点＝卡归属玩家（＝声明玩家——同玩家容器间移动，不支持跨玩家转移）；结果类型 <see cref="DeckFetchResult"/>（两层同型）。
/// 空卡组抽牌＝抛错（集合契约）；"卡组为空的游戏层处理（疲劳等）"属后续批次。
/// 〔W1-1 G12 加性面〕对局级卡牌 ID 水位线：加载时逐张分配自增整数 ID（承载于卡上）→ 初始化加载完成后快照水位线
/// （＝已分配最大值；起手装载之前）→ 构筑外判定（ID ＞ 水位线）与访问口（本类 <see cref="IsOutsideDeck"/>）。
/// </summary>
public sealed class PlayerManager
{
    private readonly LogicEngine _engine;
    private readonly CardLibrary _library;
    private readonly Func<IDrawAction?>? _drawActionProvider;
    private readonly List<Player> _players = new();
    private int _lastIssuedCardId;
    private int? _cardIdWatermark;

    internal PlayerManager(LogicEngine engine, CardLibrary library, Func<IDrawAction?>? drawActionProvider = null)
    {
        _engine = engine;
        _library = library;
        _drawActionProvider = drawActionProvider;
    }

    /// <summary>
    /// 抽牌动作面（S7）：对局路径＝装配链注入的同一实例；独立构造/装配早期＝经动作目录查询面按默认实现集
    /// 创建并登记（同一实体、同一实现——不存在第二条抽牌实现）。
    /// </summary>
    private IDrawAction DrawAction()
        => _drawActionProvider?.Invoke() ?? ActionRuntime.ResolveDraw(_engine);

    /// <summary>双玩家（[0]＝玩家A/第一位玩家、[1]＝玩家B；顺序＝创建序）。</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>
    /// 卡级装载失败出口（装配方注入；null＝无报告面——失败只留引擎日志）。
    /// <para>
    /// 粒度＝**一次装载批量一次**：卡组加载会逐张判定，逐张回调会让报告被反复重建
    /// （同一批的修订被算成多次）；故 <see cref="LoadDecksAsync"/> 收尾时把本次全部失败一次性交出去。
    /// </para>
    /// </summary>
    internal Action<IReadOnlyList<CardComponentFailureData>>? ComponentFailureSink { get; set; }

    /// <summary>初始化动作：创建双玩家（资源初始：槽 0 / 点数 0；卡组＝传入名单；手牌空；HQ 随创建〔W3-3〕）。只允许一次。</summary>
    /// <exception cref="InvalidOperationException">双玩家已创建（重复创建被拒绝）。</exception>
    internal void CreatePlayers(CardList deckForPlayerA, CardList deckForPlayerB)
    {
        if (_players.Count != 0)
        {
            throw new InvalidOperationException("双玩家已创建（重复创建被拒绝）。");
        }

        var playerA = new Player(0, deckForPlayerA, _engine); // W3-3：引擎注入——HQ 随 Player 创建（构造期绑定）
        var playerB = new Player(1, deckForPlayerB, _engine);

        // 引擎托管引用表：玩家无 Entity.Id ⇒ 由引用表分配引用键（同一对局内稳定）。
        playerA.ReferenceKey = _engine.References.Assign(playerA);
        playerB.ReferenceKey = _engine.References.Assign(playerB);

        _players.Add(playerA);
        _players.Add(playerB);
    }

    /// <summary>
    /// 对局开始卡牌加载（初始化动作；2A 新增）：逐玩家（索引升序＝玩家A 在前）逐张执行——
    /// 实例化（按定义类别产出三类卡之一）→ 装配到卡组条目 → 加载模板（模板内：对局级 ID 分配〔W1-1〕＋
    /// 元数据装配〔W1-1〕＋词条装载＋广播 card.load，载荷 {Card, Player}）。
    /// 加载顺序＝玩家索引升序 × 洗牌后卡组顺序（同种子＋同参 → 逐位稳定）；加载不改计数（仅装配）。
    /// 失败直接上抛（未注册 id / 加载模板异常＝初始化 fail-fast）。
    /// <para>
    /// **例外（§33）**：卡牌装载模板自己判定的加载失败（<see cref="CardLoadFailedException"/>）＝**单卡裁决**——
    /// 失败原因在收尾时**一次性**交给报告面（<see cref="ComponentFailureSink"/>；卡牌加载报告把它标成 invalid），
    /// 该卡**不产生**（卡组条目摘除、实例销毁），其余卡照常装载；
    /// 一张坏卡不阻断整局（否则症状与原因隔两层：整局不开、却说不出哪张卡坏了）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">双玩家尚未创建。</exception>
    /// <exception cref="KeyNotFoundException">卡组含未注册 id（实例化失败）。</exception>
    internal async Task LoadDecksAsync(CancellationToken ct = default)
    {
        var failures = new List<CardComponentFailureData>();
        foreach (var player in _players)
        {
            var deck = player.Deck;
            for (var i = 0; i < deck.Count;)
            {
                var cardId = deck[i];
                var card = _library.Instantiate(cardId);
                deck.AttachInstanceAt(i, card);
                try
                {
                    await card.LoadAsync(player, ct);
                }
                catch (CardLoadFailedException ex)
                {
                    RecordCardLoadFailure(cardId, ex, failures);
                    deck.RemoveInstance(card); // 该卡位不产生卡（条目摘除——不留未装配实例的悬空位）
                    card.Destroy();
                    continue; // 条目已摘除：同一索引即下一张卡（索引不推进）
                }

                i++;
            }
        }

        if (failures.Count > 0)
        {
            ComponentFailureSink?.Invoke(failures); // 整批一次（报告面只重建一次）
        }
    }

    /// <summary>
    /// 卡级装载失败留痕（§33）：组装报告面的失败条目（卡库键 ＋ 资产 id ＋ 原因枚举）
    /// ＋ 引擎总流一条可关联日志（报告是判据，日志是同一次失败的追踪锚点）。
    /// </summary>
    private void RecordCardLoadFailure(
        string cardId, CardLoadFailedException ex, List<CardComponentFailureData> failures)
    {
        failures.Add(new CardComponentFailureData(
            cardId,
            // 游戏层不持有资产 GUID（那是装载链的定版产物）：按卡库键命中报告卡位，再由报告侧反查条目。
            AssetGuid: null,
            Issues: new[]
            {
                CardFieldIssue.Invalid(ex.AssetId, "component", null, ex.Reason),
            }));

        _engine.RootStream.WriteLog(
            "卡牌加载",
            $"卡牌 '{cardId}'（{ex.CardName}）加载失败——该卡不产生（详见卡牌加载报告对应卡位）：{ex.Message}",
            LogLevel.Error,
            new[] { "card", "load", "error", cardId });
    }

    // ---------- 对局级卡牌 ID 水位线（W1-1 G12 加性面） ----------

    /// <summary>
    /// 分配下一个对局级卡牌 ID（对局级自增序列；由加载路径经卡牌 ID 提供器逐张调用——
    /// 「分配顺序＝加载推进顺序」：玩家索引升序 × 洗牌后卡组顺序）。数值读面不作公开要求（内部面）。
    /// </summary>
    internal int NextCardId() => ++_lastIssuedCardId;

    /// <summary>
    /// 初始化加载完成后快照水位线（＝此时已分配卡牌 ID 的最大值——初始化域末位）。
    /// 调用时机＝双方卡组全量加载完成后、任何后续动作（起手装载等）之前；快照后 <see cref="IsOutsideDeck"/> 可用；
    /// 快照前判定＝明确失败。
    /// </summary>
    internal void SnapshotCardIdWatermark() => _cardIdWatermark = _lastIssuedCardId;

    /// <summary>水位线（已快照＝初始化域末位 ID；未快照＝null；数值读面——不作公开要求、内部面）。</summary>
    internal int? CardIdWatermark => _cardIdWatermark;

    /// <summary>
    /// 构筑外判定（W1-1；访问口）：卡牌 ID ＞ 初始化水位线＝true（构筑外生成牌）；≤＝false（初始化内卡）。
    /// 判定为纯读、无副作用、不触发更新（P1 精神）。
    /// 未分配 ID 的卡（未加载 / 独立构造 / 无提供器）＝明确失败（拒绝——不静默返回 false）；
    /// 水位线未快照（初始化加载未完成）＝明确失败。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">card 未分配对局级 ID；或水位线尚未快照。</exception>
    public bool IsOutsideDeck(CardBase card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (_cardIdWatermark is null)
        {
            throw new InvalidOperationException(
                "卡牌 ID 水位线尚未快照（初始化加载未完成）——构筑外判定不可用（明确失败）。");
        }

        if (card.MatchCardId is not { } id)
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 未分配对局级 ID（未加载/独立构造的卡不能参与构筑外判定——明确失败）。");
        }

        return id > _cardIdWatermark.Value;
    }

    /// <summary>起手装载（静默——不发更新）：重复 count 次执行抽牌链路。</summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">count 为负。</exception>
    /// <exception cref="InvalidOperationException">卡组为空（空集合 Draw 被拒绝）。</exception>
    public void LoadOpeningHand(Player player, int count)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "装载张数不能为负。");
        }

        for (var i = 0; i < count; i++)
        {
            DrawIntoHand(player);
        }
    }

    /// <summary>
    /// 回合抽牌（G7 起含 HandLimit 爆牌裁决）——入口适配器（S7）：只把 <see cref="ActionResult"/> 翻译为此前的结果面。
    /// 抽牌本身＝<see cref="IDrawAction.DrawTopAsync"/>（卡组顶触发端 → 共享抽牌处理端 → 加入手牌子动作）；
    /// 未满手＝<c>card.drawn</c> → 入列表 → <c>card.hand.add</c>；满手＝<c>card.drawn</c> → 销毁 → <c>card.burned</c>
    /// （<c>card.hand.add</c> 零次）。
    /// 返回抽到的卡牌实例（爆牌路径＝被爆卡引用——爆掉的牌「算被抽到」、不算「进过手牌」）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡组为空（空集合 Draw 被拒绝）。</exception>
    public async Task<CardBase> DrawCard(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var result = await DrawAction().DrawTopAsync(player, triggerCard: null, ct).ConfigureAwait(false);
        return result.Targets[0].Subject
            ?? throw new InvalidOperationException("抽牌结果缺少卡牌实例（动作契约错误）。");
    }

    // ---------- 手牌动作面（G7：弃置 / 回迁；范围修正后——KARDS 官方无弃牌堆/墓地语义） ----------

    /// <summary>
    /// 弃置动作（G7 手牌操作与弃置）：从手牌移除 → 销毁（接线引擎既有销毁机制——生命周期终止＋效果清理）→
    /// 发射 <see cref="GameUpdates.CardDiscarded"/>（{ Card, Player }；恰一次）——三者为一个受控动作；
    /// 三路径（选择弃/随机弃/指定弃）＝调用方组合调用本动作（不设专属封装）。
    /// 归属口径＝「卡当前所在手牌」（不设「仅己方」限制——他方手牌的弃置经调用方组合合法可达）。
    /// 前提校验失败（卡不在所声明玩家手牌中）＝明确拒绝（可区分成功/拒绝；不静默、非幂等）；
    /// 对同一卡重复弃置＝第二次因前提失败而拒绝。次序：移除 → 销毁（含资源清理）→ 信号（观察者所见即终态）。
    /// R9：<paramref name="source"/>＝施动方（可空）——恒写入 <c>card.discarded</c> 载荷的 <c>Source</c>；
    /// 不传＝null（不猜测、不回退到被弃卡/玩家）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player / card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡不在该玩家手牌中（前提校验失败——明确拒绝）。</exception>
    public async Task DiscardCardAsync(
        Player player, CardBase card, CancellationToken ct = default, Card? source = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);

        if (!player.Hand.Contains(card))
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 不在玩家 #{player.Index} 的手牌中（弃置动作被拒绝——归属口径＝卡当前所在手牌）。");
        }

        player.Hand.Remove(card); // 前提已校验：必命中（移除）——移除后处置链无失败点（成功即完整弃置）
        await HandLimitBurn.DestroyAndEmitDiscardedAsync(_engine, player, card, ct, source);
    }

    /// <summary>
    /// 回迁动作（G7 回迁原子化）：将手牌中的卡装回该玩家卡组顶（位置＝索引 0——下次抽取取件端）；
    /// 跨集合受控动作（手牌移出＋装入卡组一体完成）；同一实例——移出手牌后被装入卡组、不丢失不重复（成功即完整迁移）。
    /// 输入面最小：仅凭「手牌中的卡引用」即可（条目 id 由实现自行解析）；回迁静默（不发信号）；
    /// 「洗入」＝本动作与既有洗切动作面（<see cref="Match.ShuffleDeckAsync"/>）的调用方组合（不设洗入专属封装）。
    /// 全体用例（"将所有手牌洗入卡组"）：手牌为空＝无操作（不洗切、不发信号——调用方组合语义）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player / card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡不在该玩家手牌中；或卡组侧已含同一实例（重复归属被拒绝）。</exception>
    public void ReturnToDeckTop(Player player, CardBase card) => ReturnToDeck(player, card, 0);

    /// <summary>
    /// 回迁动作（指定位置；G7）：0 基——索引 0＝下次抽取取件端、有效范围 0..N（N＝装入前集合大小；含尾部）。
    /// 其余语义同 <see cref="ReturnToDeckTop"/>（「尾部/其它位置」＝指定位置的特例 p＝N，不另设形态）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player / card 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">position 越界（明确拒绝）。</exception>
    /// <exception cref="InvalidOperationException">卡不在该玩家手牌中；或卡组侧已含同一实例（重复归属被拒绝）。</exception>
    public void ReturnToDeck(Player player, CardBase card, int position)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);

        if (!player.Hand.Contains(card))
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 不在玩家 #{player.Index} 的手牌中（回迁动作被拒绝——归属口径＝卡当前所在手牌）。");
        }

        if (position < 0 || position > player.Deck.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), position, $"回迁位置越界（合法范围：0..{player.Deck.Count}——0＝卡组顶/下次抽取取件端）。");
        }

        if (player.Deck.ContainsInstance(card))
        {
            throw new InvalidOperationException(
                $"卡牌 '{card.Name}' 已在玩家 #{player.Index} 的卡组中（重复归属被拒绝——实例唯一归属不变量）。");
        }

        var definitionId = ResolveDefinitionId(card); // 素材解析（可能拒绝——先于任何变更）

        player.Hand.Remove(card); // 全部校验通过：移出 → 装入（两步之间无失败点——成功即完整迁移）
        player.Deck.InsertInstanceAt(position, definitionId, card);
    }

    // ---------- 定向取卡（批 6·Q7：从卡组定向取件到目标区域——本批目标区域锁定手牌） ----------

    /// <summary>
    /// 定向取卡动作（批 6·Q7）：从卡组定向取出指定卡实例并移动到目标区域（手牌）的受控动作。
    /// 语义＝**抽取**（对齐抽牌链路；取件位置＝指定实例，而抽牌＝卡组顶）：正常路径（未满手）＝取出＋进手牌＋
    /// 发 <c>card.drawn</c> → <c>card.hand.add</c>（顺序：drawn 先、hand.add 后；载荷均＝{ 玩家, 卡牌实例 }；
    /// 均恰一次）；满手（≥ <see cref="Player.HandLimit"/>〔9〕）＝**爆牌裁决**（对齐 DrawCard 路径「算被抽到」口径：
    /// <c>card.drawn</c> 恰一次 → 销毁〔引擎既有机制〕→ <c>card.burned</c> 恰一次；<c>card.hand.add</c> 零次；卡自卡组移除）。
    /// 调用面：任意指定实例、**单张**（「取 N 张」＝调用方连续组合、各自独立结果、非原子）；
    /// 归属口径＝「卡当前所在卡组」（不设「仅己方」限制——他方卡组经调用方组合合法可达）；
    /// 落点＝卡归属玩家（＝声明玩家——同玩家容器间移动〔其卡组 → 其手牌〕，不支持跨玩家转移）；
    /// 入位＝**尾部追加**（对齐一切「抽牌/生成到手牌」先例）。
    /// 失败分层：参数层（null）＝异常（全库一致 fail-fast）；其余一切语义层结局＝结果对象（三态＋类别；
    /// 判定优先级：参数层 → <see cref="DeckFetchFailureReason.InvalidTarget"/> → <see cref="DeckFetchFailureReason.Unavailable"/>
    /// → <see cref="DeckFetchFailureReason.CardDestroyed"/> → <see cref="DeckFetchFailureReason.NotInDeck"/>）。
    /// 原子性：失败＝零变更（卡留原处、手牌不变、零信号；可重复调用——**失败路径幂等零副作用**）；
    /// 爆牌＝卡已自卡组移除并销毁（变更已发生、完成语义）；完成＝移除＋进手牌＋信号。
    /// 单线程语义（与引擎一致）。
    /// </summary>
    /// <exception cref="ArgumentNullException">player / card 为 null（参数层——编程契约错误 fail-fast）。</exception>
    public async Task<DeckFetchResult> FetchFromDeckAsync(
        Player player, CardBase card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);

        // ① 引用契约（InvalidTarget——引用失效优先于环境/生命周期/容器状态）：
        if (card.Owner is null)
        {
            // 未加载／独立构造（无归属）——结果化、不抛（面向 csx 的运行时动作：效果域需要统一的结构化消费面）。
            return DeckFetchResult.Rejected(DeckFetchFailureReason.InvalidTarget, card);
        }

        if (!ReferenceEquals(card.Owner, player))
        {
            // 跨玩家（声明玩家≠卡归属玩家）＝参数自洽校验失败（归属口径＝卡当前所在卡组——不设「仅己方」限制）。
            return DeckFetchResult.Rejected(DeckFetchFailureReason.InvalidTarget, card);
        }

        if (!_library.TryGetRegisteredId(card.Definition, out _))
        {
            // 定义未注册于本对局卡牌库（非本局引用——按引用相等反查）。
            return DeckFetchResult.Rejected(DeckFetchFailureReason.InvalidTarget, card);
        }

        // ② 门禁（Unavailable——对局初始化未完成〔卡牌 ID 水位线未快照〕；零副作用、不抛）：
        if (_cardIdWatermark is null)
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.Unavailable, card);
        }

        // ③ 生命周期（CardDestroyed）先于容器归属（NotInDeck）——「已销毁＋不在卡组」＝CardDestroyed：
        if (!card.Life.IsAlive)
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.CardDestroyed, card);
        }

        // ④ 容器归属（NotInDeck——含从未在过、已被取出／重复调用；失败路径幂等零副作用）：
        if (!player.Deck.ContainsInstance(card))
        {
            return DeckFetchResult.Rejected(DeckFetchFailureReason.NotInDeck, card);
        }

        // 执行段：前置校验全部通过——抽牌动作（指定牌触发端 → 共享处理端 → 加入手牌子动作）。
        // 满手＝爆牌（对齐抽牌路径「算被抽到」：drawn → 销毁 → burned；hand.add 零次）。
        var drawn = await DrawAction().DrawSpecificAsync(card, player, triggerCard: null, ct).ConfigureAwait(false);
        var outcome = drawn.Targets[0];
        return outcome.Outcome switch
        {
            ActionOutcomeKind.Applied => DeckFetchResult.Fetched(card),
            _ when outcome.Reason == ActionFailureKind.HandFull => DeckFetchResult.Burned(card),
            _ => DeckFetchResult.Rejected(DeckFetchFailureReason.Unavailable, card),
        };
    }

    /// <summary>抽牌核心链路（静默）：卡组条目取件（加载实例通道）→ 加入手牌。</summary>
    private CardBase DrawIntoHand(Player player)
    {
        var card = player.Deck.DrawInstance(); // 空集合 / 未加载抛错（集合契约）
        player.Hand.Add(card);
        return card;
    }

    /// <summary>
    /// 解析卡牌的定义注册 id（回迁素材：经卡牌库反查——定义实例引用相等；按注册序取首个匹配）。
    /// 未注册于本对局卡牌库（如独立构造的卡）＝明确拒绝（不静默）。
    /// </summary>
    private string ResolveDefinitionId(CardBase card)
    {
        foreach (var pair in _library.Definitions)
        {
            if (ReferenceEquals(pair.Value, card.Definition))
            {
                return pair.Key;
            }
        }

        throw new InvalidOperationException(
            $"卡牌 '{card.Name}' 的定义未注册于本对局卡牌库（回迁素材解析失败——明确拒绝）。");
    }
}
