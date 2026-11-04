using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Managers;

/// <summary>
/// **手牌转移服务**（跨玩家手牌移动的动作族实现；与 <see cref="PlayerManager"/>／<c>MatchCardService</c> 并列）。
/// <para>
/// 两种形态：
/// </para>
/// <list type="bullet">
/// <item><b>交换</b>（<see cref="ExchangeAsync"/>）：两张手牌互换归属＋各自进入对方手牌（同事务、无「只成一半」）；</item>
/// <item><b>洗入</b>（<see cref="ShuffleIntoDeckAsync"/>）：一张手牌装入指定玩家卡组并对该卡组洗切（装**既有实例**、不是新建）。</item>
/// </list>
/// <para>
/// 与既有动作的边界：回迁（手牌 → **自己**卡组、不改归属）＝<see cref="PlayerManager.ReturnToDeck"/>；
/// 洗入自己卡组会被本服务**拒绝**（A 条），需要该语义的卡应调回迁。
/// 与 <c>EffectRuntime.ShuffleInAsync(viewer, cardName, count)</c> 的区别：那个按卡名**新建**一张，
/// 本服务**移动既有实例并翻转归属**——两者不是同一语义，不得互替。
/// </para>
/// <para>
/// **归属写面唯一入口**：<see cref="CardBase.ReassignOwner"/> 的调用权限只在本类（见 S1 门户注释）。
/// </para>
/// <para>
/// **失败形态**：两个方法返回 <see cref="Task"/>（无结果对象），失败一律抛
/// <see cref="InvalidOperationException"/>。理由＝这族动作唯一的失败来源是**调用方错误／不变量破坏**，
/// 不存在「合法的不可达」；给一个 Rejected 返回值等于给 csx 一条静默吞掉它的路。
/// 抛之前必写一条 Error 级日志（卡名 ＋ 清单条目编号 ＋ 双方玩家索引）——异常会中断效果，日志是唯一可追溯的锚点。
/// </para>
/// <para>
/// **执行段不可失败**（本类的核心不变量）：前置清单的每一条，都对应执行段里某一行「本会抛错」的条件；
/// 清单与执行段行一一对账，缺一即实现缺陷。执行段内因此不出现任何可失败调用（无字典查找、无 <c>await</c>）。
/// </para>
/// </summary>
public sealed class HandTransferService
{
    private readonly LogicEngine _engine;
    private readonly Func<CardBase, string?>? _definitionIdResolver;
    private readonly Func<Player, CancellationToken, Task>? _shuffleDeckAction;
    private readonly Func<IReadOnlyList<Player>>? _playersProvider;
    private readonly Func<MatchState>? _stateProvider;

    /// <summary>
    /// 创建手牌转移服务。
    /// </summary>
    /// <param name="engine">对局引擎（信号发射 ＋ 拒绝留痕）。</param>
    /// <param name="definitionIdResolver">
    /// 定义 id 反查（装入卡组的最小素材；对局装配＝<c>MatchCardService.TryResolveDefinitionId</c> 的适配 lambda）。
    /// **不另实现一套反查**——卡库是唯一定义真源。缺省 null＝独立构造时洗入不可用（明确拒绝）。
    /// </param>
    /// <param name="shuffleDeckAction">
    /// 洗切动作面（对局装配＝<c>Match.ShuffleDeckAsync</c>——发 <c>deck.shuffled</c> 恰一次）。
    /// 缺省 null＝洗入不可用（明确拒绝——装配性错误）。
    /// </param>
    /// <param name="playersProvider">
    /// 本对局玩家清单（D 条：目标玩家须属本对局）。对局装配＝<c>PlayerManager.Players</c>；
    /// 缺省 null＝独立构造跳过该项门禁（对齐 <c>MatchCardService</c> 的缺省口径）。
    /// </param>
    /// <param name="stateProvider">
    /// 对局状态读取器（门禁——仅「进行」态可转移）。对局装配＝<c>Match.State</c>；
    /// 缺省 null＝独立构造无门禁（对齐 <c>MatchCardService.EnsureInProgress</c> 口径）。
    /// </param>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    public HandTransferService(
        LogicEngine engine,
        Func<CardBase, string?>? definitionIdResolver = null,
        Func<Player, CancellationToken, Task>? shuffleDeckAction = null,
        Func<IReadOnlyList<Player>>? playersProvider = null,
        Func<MatchState>? stateProvider = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _definitionIdResolver = definitionIdResolver;
        _shuffleDeckAction = shuffleDeckAction;
        _playersProvider = playersProvider;
        _stateProvider = stateProvider;
    }

    // ---------- 交换（双卡事务） ----------

    /// <summary>
    /// **交换两张手牌的归属**（双向、同事务）：<paramref name="first"/> → <paramref name="second"/> 的玩家手牌，
    /// <paramref name="second"/> → <paramref name="first"/> 的玩家手牌；两张牌实例不变（不销毁重建），
    /// 不产生 <c>deck.shuffled</c>，不做爆牌裁决（净零，两牌仍在手牌）。
    /// <para>
    /// **信号**：两张牌**全部落位之后**才发，次序固定＝<paramref name="first"/> 然后 <paramref name="second"/>
    /// （＝调用方传参顺序；<paramref name="first"/> 须为施放者侧那张）。<paramref name="source"/> 原样写入信号载荷。
    /// 不在此处按 <c>source.Owner</c> 反推次序——那会让「把 second 传成施放者侧」的调用方静默改变信号次序。
    /// </para>
    /// <para>
    /// **为什么直调 <c>GameUpdates.EmitCardHandAdd</c>、不复用加入手牌动作**：容器变更必须收进无 await 块
    /// （执行段不可失败）；不是为了绕爆牌——两条路都不爆牌（服务面 <c>AddCardToHandAsync</c> 只有两行、
    /// 爆牌裁决在动作处理端 <c>AddToHandAction</c>）。
    /// </para>
    /// <para>
    /// **已知后果**：<c>card.hand.add</c> 属 <c>GameEnvironment.IsRerunTrigger</c>，一次交换＝两轮全卡重跑
    /// （两条独立信号、不合并——两张牌是两条独立事实；归属变了、光环受益面可能变，这是正确后果）。
    /// </para>
    /// </summary>
    /// <param name="first">被转移卡之一（须为施放者侧那张——本次序即 <c>card.hand.add</c> 的发射次序）。</param>
    /// <param name="second">被转移卡之二。</param>
    /// <param name="source">结算中的施放源卡（**必填非空**；参与任一被转移侧＝拒绝）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">first / second / source 为 null。</exception>
    /// <exception cref="InvalidOperationException">任一前置断言不成立（拒绝——零副作用、零信号）。</exception>
    public async Task ExchangeAsync(Card first, Card second, Card source, CancellationToken ct = default)
    {
        // ── 前置断言清单 B（S2）——逐条对账执行段，全部通过才产生副作用 ──
        // 1. 非空
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(source);

        // 2. 门禁（F）
        EnsureInProgress("B#2");

        // 3. 两者 Owner 非空（未加载/独立构造/非卡牌实体＝装配性错误）
        var firstBase = first as CardBase;
        var secondBase = second as CardBase;
        if (firstBase?.Owner is not { } firstOwner)
        {
            throw Reject("B#3", $"卡牌 '{first.Name}' 未加载归属（未完成 LoadAsync——不可转移）", first, second);
        }

        if (secondBase?.Owner is not { } secondOwner)
        {
            throw Reject("B#3", $"卡牌 '{second.Name}' 未加载归属（未完成 LoadAsync——不可转移）", first, second);
        }

        // 4. 两张必须是不同实例（同一张牌自换＝净变化为零）
        if (ReferenceEquals(first, second))
        {
            throw Reject("B#4", $"两张待交换卡是同一实例（'{first.Name}'）——自换无意义", first, second);
        }

        // 5. 各在其归属方手牌（C：归属与容器不一致＝拒绝，不顺手修正；同时保证两处 Remove 命中）
        if (!firstOwner.Hand.Contains(first))
        {
            throw Reject("B#5", $"卡牌 '{first.Name}' 不在其归属玩家 #{firstOwner.Index} 的手牌中（归属与容器不一致）", first, second);
        }

        if (!secondOwner.Hand.Contains(second))
        {
            throw Reject("B#5", $"卡牌 '{second.Name}' 不在其归属玩家 #{secondOwner.Index} 的手牌中（归属与容器不一致）", first, second);
        }

        // 6. 不同属一个玩家（B：一次自换＝两条伪事实＋两轮全卡重跑，净变化为零＝纯噪音）
        if (ReferenceEquals(firstOwner, secondOwner))
        {
            throw Reject("B#6", "两张待交换卡同属一个玩家（自换被拒绝——净变化为零）", first, second);
        }

        // 7. 目标手牌不含对方实例（保证两处 Add 不抛）
        if (secondOwner.Hand.Contains(first))
        {
            throw Reject("B#7", $"玩家 #{secondOwner.Index} 的手牌已含 '{first.Name}'（同一实例重复归属被拒绝）", first, second);
        }

        if (firstOwner.Hand.Contains(second))
        {
            throw Reject("B#7", $"玩家 #{firstOwner.Index} 的手牌已含 '{second.Name}'（同一实例重复归属被拒绝）", first, second);
        }

        // 8. 生命周期存活（已销毁实例不构成可转移对象）
        if (!first.Life.IsAlive)
        {
            throw Reject("B#8", $"卡牌 '{first.Name}' 已销毁（不可转移）", first, second);
        }

        if (!second.Life.IsAlive)
        {
            throw Reject("B#8", $"卡牌 '{second.Name}' 已销毁（不可转移）", first, second);
        }

        // 9. 结算中的施放源卡不得参与（强制点：不依赖调用方传不传 exclude）
        if (ReferenceEquals(source, first) || ReferenceEquals(source, second))
        {
            throw Reject("B#9", $"结算中的施放源卡 '{source.Name}' 不得作为被转移侧", first, second);
        }

        // ── 执行段 A（同步、不可失败；对象引用全部来自局部变量） ──
        // 顺序：先移出 → 再改归属 → 最后装入。改归属必须在移出之后、装入之前：
        // 移出用的是旧归属手牌（局部变量持有），装入用的是新归属手牌（同一局部变量换绑后的值）。
        var firstSource = firstOwner;
        var secondSource = secondOwner;
        firstSource.Hand.Remove(first);
        secondSource.Hand.Remove(second);
        firstBase.ReassignOwner(secondSource);
        secondBase.ReassignOwner(firstSource);
        secondSource.Hand.Add(first);
        firstSource.Hand.Add(second);

        // ── 执行段 B（信号；两张牌全部落位之后才发，次序＝first → second） ──
        await GameUpdates.EmitCardHandAdd(_engine, secondSource, first, ct, source).ConfigureAwait(false);
        await GameUpdates.EmitCardHandAdd(_engine, firstSource, second, ct, source).ConfigureAwait(false);
    }

    // ---------- 洗入（单卡事务） ----------

    /// <summary>
    /// **把一张手牌装入指定玩家卡组并洗切**：卡离开原归属方手牌、进入 <paramref name="targetPlayer"/> 卡组，
    /// <c>Owner</c> 改为目标玩家（跨玩家转移）。装入本身**静默**（不发 <c>card.hand.add</c>——牌不是进了手牌；
    /// 对齐 <c>ReturnToDeck</c> 既有静默语义）；洗切经统一动作面发 <c>deck.shuffled</c> 恰一次。
    /// <para>
    /// **洗切失败不回滚**：执行段结束时转移已是既成事实，洗切只影响观察者（信号）与顺序；
    /// 洗切面可用性已在**前置**阶段断言（本方法内不存在「转一半」的窗口）。**禁止**后人加 try/catch 回滚——
    /// 那会造出一条「归属改了又改回去」的路径，比不回滚更糟。
    /// </para>
    /// <para>
    /// 与 <c>EffectRuntime.ShuffleInAsync(viewer, cardName, count)</c> 的命名邻近风险：那个按卡名**新建**一张塞入卡组；
    /// 本方法**移动既有实例并翻转归属**。两者不是同一语义。
    /// </para>
    /// </summary>
    /// <param name="card">被转移的手牌（须在其归属方手牌中）。</param>
    /// <param name="targetPlayer">目标玩家（须属本对局，且 ≠ 卡当前归属）。</param>
    /// <param name="source">结算中的施放源卡（**必填非空**；等于被转移卡＝拒绝）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">card / targetPlayer / source 为 null。</exception>
    /// <exception cref="InvalidOperationException">任一前置断言不成立（拒绝——零副作用、零信号）。</exception>
    public async Task ShuffleIntoDeckAsync(
        Card card, Player targetPlayer, Card source, CancellationToken ct = default)
    {
        // ── 前置断言清单 A（S2）——逐条对账执行段，全部通过才产生副作用 ──
        // 1. 非空
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(targetPlayer);
        ArgumentNullException.ThrowIfNull(source);

        // 2. 门禁（F）
        EnsureInProgress("A#2");

        // 3. 归属非空
        if (card is not CardBase cardBase || cardBase.Owner is not { } owner)
        {
            throw Reject("A#3", $"卡牌 '{card.Name}' 未加载归属（未完成 LoadAsync——不可转移）", card, targetPlayer: targetPlayer);
        }

        // 4. 不得洗入自己卡组（A：那是回迁语义，入口＝PlayerManager.ReturnToDeck）
        if (ReferenceEquals(owner, targetPlayer))
        {
            throw Reject(
                "A#4",
                $"目标玩家 #{targetPlayer.Index} 就是卡牌 '{card.Name}' 的当前归属（洗入自己卡组被拒绝——请用回迁 ReturnToDeck）",
                card, targetPlayer: targetPlayer);
        }

        // 5. 归属与容器一致（C：以 card.Owner 为源玩家，不改签名；同时保证 Remove 命中）
        if (!owner.Hand.Contains(card))
        {
            throw Reject(
                "A#5",
                $"卡牌 '{card.Name}' 不在其归属玩家 #{owner.Index} 的手牌中（归属与容器不一致）",
                card, targetPlayer: targetPlayer);
        }

        // 6. 生命周期存活
        if (!card.Life.IsAlive)
        {
            throw Reject("A#6", $"卡牌 '{card.Name}' 已销毁（不可转移）", card, targetPlayer: targetPlayer);
        }

        // 7. 目标卡组不含同一实例（保证 InsertInstanceAt 不抛）
        if (targetPlayer.Deck.ContainsInstance(cardBase))
        {
            throw Reject(
                "A#7",
                $"玩家 #{targetPlayer.Index} 的卡组已含 '{card.Name}'（同一实例重复归属被拒绝）",
                card, targetPlayer: targetPlayer);
        }

        // 8. 目标玩家属本对局（D；独立构造＝跳过该项门禁）
        if (_playersProvider?.Invoke() is { } players && !players.Contains(targetPlayer))
        {
            throw Reject("A#8", $"玩家 #{targetPlayer.Index} 不属于本对局（目标玩家无效）", card, targetPlayer: targetPlayer);
        }

        // 9. 结算中的施放源卡不得参与（强制点）
        if (ReferenceEquals(source, card))
        {
            throw Reject("A#9", $"结算中的施放源卡 '{source.Name}' 不得作为被转移侧", card, targetPlayer: targetPlayer);
        }

        // 10. 定义 id 反查成功（装入卡组的最小素材；先于一切变更）
        var definitionId = _definitionIdResolver?.Invoke(cardBase);
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            throw Reject(
                "A#10",
                $"卡牌 '{card.Name}' 的定义未注册于本对局卡牌库（素材解析失败）",
                card, targetPlayer: targetPlayer);
        }

        // 11. 洗切面已装配（洗入的必要条件；缺省＝独立构造不支持洗入）
        var shuffle = _shuffleDeckAction
            ?? throw Reject("A#11", "洗切动作面未装配（独立构造场景不支持洗入——装配性错误）", card, targetPlayer: targetPlayer);

        // ── 执行段（同步、不可失败；源玩家存局部变量——改归属会覆盖 card.Owner） ──
        var sourceOwner = owner;
        sourceOwner.Hand.Remove(card);
        cardBase.ReassignOwner(targetPlayer); // 必须在转入目标容器之前：归属先到位，随后装入的容器才是「新归属方的容器」
        targetPlayer.Deck.InsertInstanceAt(0, definitionId!, cardBase);

        // 洗切（async）：此后的失败**不回滚**（转移已是既成事实；见方法注释）。
        await shuffle(targetPlayer, ct).ConfigureAwait(false);
    }

    // ---------- 内部：门禁与拒绝留痕 ----------

    /// <summary>门禁：仅「进行」态可转移（准备态/终局后＝明确拒绝）；独立构造（无状态读取器）＝无门禁约束。</summary>
    private void EnsureInProgress(string rule)
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

        throw Reject(
            rule,
            state == MatchState.Ended
                ? "对局已结束（终局）——手牌转移被拒绝"
                : "对局尚未进入'进行'态：手牌转移不可用（须先成功完成 Initialize）");
    }

    /// <summary>
    /// 拒绝（抛之前先留痕）：日志带**卡名 ＋ 清单条目编号 ＋ 双方玩家索引**——
    /// 异常会中断效果，日志是唯一能追溯「为什么这轮炸了」的锚点（对齐 <c>PlayerManager.RecordCardLoadFailure</c>
    /// 的留痕风格）；不把原因只塞进异常 message。
    /// </summary>
    private InvalidOperationException Reject(
        string rule, string detail, Card? first = null, Card? second = null, Player? targetPlayer = null)
    {
        var firstIndex = IndexOf(first);
        var secondIndex = second is null && targetPlayer is not null ? $"目标#{targetPlayer.Index}" : IndexOf(second);
        var names = string.Join(
            "、",
            new[] { first?.Name, second?.Name }.Where(static name => name is not null));

        var message = $"手牌转移被拒绝（条目 {rule}）：{detail}。"
            + $"涉及卡：[{names}]；双方玩家索引：#{firstIndex} / #{secondIndex}";

        _engine.RootStream.WriteLog(
            "手牌转移",
            message,
            LogLevel.Error,
            new[] { "hand", "transfer", "error" });

        return new InvalidOperationException(message);
    }

    /// <summary>玩家索引（卡 → 归属 → 索引；不可达＝<c>-</c>）。</summary>
    private static string IndexOf(Card? card) => card switch
    {
        null => "-",
        Hq hq => hq.Owner.Index.ToString(),
        CardBase cardBase => cardBase.Owner is { } owner ? owner.Index.ToString() : "-",
        _ => "-",
    };
}
