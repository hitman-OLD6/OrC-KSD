using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Managers;

/// <summary>开局换牌（mulligan）结局状态（三态；沿用统一结果模式）。</summary>
public enum MulliganResultStatus
{
    /// <summary>成功（交互确认并完成换牌，或"确认不换牌"完成）。</summary>
    Success,

    /// <summary>取消（前端主动取消交互；零副作用——不换牌、不确认）。</summary>
    Cancelled,

    /// <summary>失败（携带原因类别，见 <see cref="MulliganFailureReason"/>）。</summary>
    Failed,
}

/// <summary>换牌失败原因（类别化；成功／取消＝null）。</summary>
public enum MulliganFailureReason
{
    /// <summary>对局已结束（终局门禁）。</summary>
    GameEnded,

    /// <summary>非"换牌"相位（相位门禁：准备／进行／结束）。</summary>
    PhaseBlocked,

    /// <summary>该方已确认（重复确认／重复换牌＝拒绝；幂等拒绝）。</summary>
    AlreadyConfirmed,

    /// <summary>交互失败（targeting 系统失败；细节见结果对象的 <see cref="MulliganResult.Targeting"/>）。</summary>
    TargetingFailed,

    /// <summary>换牌执行故障（契约兜底；不应发生的结构性错误，防御类别）。</summary>
    ReplaceFault,
}

/// <summary>
/// 换牌（mulligan）统一结果对象（不抛；消费方读 <see cref="Status"/> ＋ <see cref="FailureReason"/> 分流）。
/// <see cref="Targeting"/> 可选透传交互细节（取消／交互失败场景），其余为 null。
/// </summary>
public sealed class MulliganResult
{
    private MulliganResult(MulliganResultStatus status, MulliganFailureReason? failureReason, TargeterResult? targeting)
    {
        Status = status;
        FailureReason = failureReason;
        Targeting = targeting;
    }

    /// <summary>结局状态（三态）。</summary>
    public MulliganResultStatus Status { get; }

    /// <summary>失败原因（仅失败时非 null；成功／取消＝null）。</summary>
    public MulliganFailureReason? FailureReason { get; }

    /// <summary>交互细节透传（可空；取消／交互失败场景携带）。</summary>
    public TargeterResult? Targeting { get; }

    /// <summary>是否成功（便捷读面）。</summary>
    public bool IsSuccess => Status == MulliganResultStatus.Success;

    internal static MulliganResult Success() => new(MulliganResultStatus.Success, failureReason: null, targeting: null);

    internal static MulliganResult Cancelled(TargeterResult? targeting = null)
        => new(MulliganResultStatus.Cancelled, failureReason: null, targeting);

    internal static MulliganResult Failure(MulliganFailureReason reason, TargeterResult? targeting = null)
        => new(MulliganResultStatus.Failed, reason, targeting);
}

/// <summary>
/// 开局换牌（mulligan）管理器（A1；换牌相位的承载）：
/// ①换牌（<see cref="BeginMulliganAsync"/>）＝**特制槽位**交互（<see cref="MulliganSelectSlot"/>：候选＝该方手牌引用、
///   域判定"仍在手牌"、数量 0..手牌数、槽位参数＝该玩家）→ 确认＝退回卡组 → 重洗（恰一条 <c>deck.shuffled</c>）→
///   抽等量（**静默**：不经 <c>DrawCard</c>，不发 <c>card.drawn</c>／<c>card.hand.add</c>）→ **自动确认该方**；
/// ②确认（<see cref="ConfirmAsync"/>）＝不换牌直接确认；
/// ③双方确认＝经装配注入的"进入对局"回调（置"进行"＋执行先手第 1 回合）。
/// 门禁：仅"换牌"相位且该方未确认（其余＝明确拒绝、零副作用）；取消＝零副作用（不换、不确认）；
/// 空手牌＝零交互直接确认（避免死锁）。玩家归属校验失败＝明确异常（装配性错误、fail-fast）。
/// </summary>
public sealed class MulliganManager
{
    /// <summary>换牌选择槽位名（特制槽位；M1=b——前端据此识别"开局换牌"并播放专属动画）。</summary>
    public const string SlotName = "mulligan";

    private readonly LogicEngine _engine;
    private readonly TargeterManager _targeterManager;
    private readonly PlayerManager _playerManager;
    private readonly MatchRandomService _randomService;
    private readonly MatchLifecycle _lifecycle;
    private readonly Func<CardBase, string?> _definitionIdOf;
    private readonly Func<CancellationToken, Task> _enterPlayAsync;
    private readonly HashSet<Player> _confirmed = new();

    /// <summary>创建换牌管理器。</summary>
    /// <param name="engine">对局引擎（发射 deck.shuffled）。</param>
    /// <param name="targeterManager">目标选择管理器（换牌交互承载）。</param>
    /// <param name="playerManager">玩家管理器（双方成员与手牌/卡组真源）。</param>
    /// <param name="randomService">对局随机服务（重洗随机源——可复现）。</param>
    /// <param name="lifecycle">对局生命周期（相位门禁读取面）。</param>
    /// <param name="definitionIdOf">定义 id 解析（卡 → 注册 id；用于退回卡组条目——不可解析＝跳过该张，防御）。</param>
    /// <param name="enterPlayAsync">双方确认后的"进入对局"回调（置"进行"＋执行先手第 1 回合；由对局装配提供）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 null。</exception>
    public MulliganManager(
        LogicEngine engine,
        TargeterManager targeterManager,
        PlayerManager playerManager,
        MatchRandomService randomService,
        MatchLifecycle lifecycle,
        Func<CardBase, string?> definitionIdOf,
        Func<CancellationToken, Task> enterPlayAsync)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(targeterManager);
        ArgumentNullException.ThrowIfNull(playerManager);
        ArgumentNullException.ThrowIfNull(randomService);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(definitionIdOf);
        ArgumentNullException.ThrowIfNull(enterPlayAsync);

        _engine = engine;
        _targeterManager = targeterManager;
        _playerManager = playerManager;
        _randomService = randomService;
        _lifecycle = lifecycle;
        _definitionIdOf = definitionIdOf;
        _enterPlayAsync = enterPlayAsync;
    }

    /// <summary>该方是否已确认（只读面）。</summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    public bool IsConfirmed(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return _confirmed.Contains(player);
    }

    /// <summary>是否双方均已确认（只读面）。</summary>
    public bool AllConfirmed => _confirmed.Count == _playerManager.Players.Count;

    /// <summary>
    /// 开局换牌（唯一对外入口；M2=a）：仅"换牌"相位且该方未确认——
    /// 发起特制槽位交互；确认＝执行换牌并自动确认该方；取消＝零副作用；空手牌＝零交互直接确认。
    /// </summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局。</exception>
    public async Task<MulliganResult> BeginMulliganAsync(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        RequireParticipant(player);

        if (CheckGate(player) is { } gated)
        {
            return gated;
        }

        var hand = player.Hand.ToArray();
        if (hand.Length == 0)
        {
            return await ConfirmCoreAsync(player, ct); // 空手牌：零交互直接确认（避免死锁）
        }

        IReadOnlyList<Ref<Entity>> selected = Array.Empty<Ref<Entity>>();
        var result = await _targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Mulligan,
                new ReferenceSetParameter(
                    hand.Select(card => card.Ref),
                    min: 0,
                    max: hand.Length,
                    domainValidator: reference => player.Hand.Any(card => ReferenceEquals(card, reference.Value)),
                    tag: player)
                {
                    // R8：换牌类别（此前仅靠 tag: player 由宿主兜底归属，类别仍缺）。
                    Owner = player,
                    Action = SelectorActions.Mulligan,
                });

            // 非法选择＝同一选择器重入（Q8a）。
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<IReadOnlyList<Ref<Entity>>>();
            }

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            selected = step.Value ?? Array.Empty<Ref<Entity>>();
            return TargeterResult.Ok();
        });

        if (result.IsCancelled)
        {
            return MulliganResult.Cancelled(result); // 取消＝零副作用（不换、不确认）
        }

        if (!result.IsOk)
        {
            return MulliganResult.Failure(MulliganFailureReason.TargetingFailed, result);
        }

        await ReplaceAsync(player, selected, ct);
        return await ConfirmCoreAsync(player, ct); // 确认＝换牌后自动确认该方
    }

    /// <summary>确认（不换牌直接确认；M2=a 的第二入口）。</summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    /// <exception cref="ArgumentException">指定玩家不属于本对局。</exception>
    public async Task<MulliganResult> ConfirmAsync(Player player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        RequireParticipant(player);

        if (CheckGate(player) is { } gated)
        {
            return gated;
        }

        return await ConfirmCoreAsync(player, ct);
    }

    /// <summary>换牌执行：退回卡组（尾部）→ 重洗（恰一条 deck.shuffled）→ 抽等量（静默）。空选＝不换牌（零洗切、零信号）。</summary>
    private async Task ReplaceAsync(Player player, IReadOnlyList<Ref<Entity>> selected, CancellationToken ct)
    {
        var returning = new List<(CardBase Card, string Id)>(selected.Count);
        foreach (var reference in selected)
        {
            if (reference.Value is not CardBase card)
            {
                continue; // 产出保证为卡引用；防御
            }

            var id = _definitionIdOf(card);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue; // 不可解析＝跳过该张（防御；不阻断其余）
            }

            returning.Add((card, id!));
        }

        if (returning.Count == 0)
        {
            return; // 空选＝不换牌：不重洗、不发信号（静默口径）
        }

        foreach (var (card, id) in returning)
        {
            if (player.Hand.Remove(card))
            {
                player.Deck.InsertInstanceAt(player.Deck.Count, id, card);
            }
        }

        player.Deck.Shuffle(_randomService);
        await GameUpdates.EmitDeckShuffled(_engine, player, player.Deck, ct);

        // 抽等量（静默：不经 DrawCard——不发 card.drawn / card.hand.add）
        var draw = Math.Min(returning.Count, player.Deck.Count);
        for (var i = 0; i < draw; i++)
        {
            player.Hand.Add(player.Deck.DrawInstance());
        }
    }

    /// <summary>确认内核：标记该方 done；双方 done＝调用"进入对局"回调（置"进行"＋先手第 1 回合）。</summary>
    private async Task<MulliganResult> ConfirmCoreAsync(Player player, CancellationToken ct)
    {
        _confirmed.Add(player);
        if (AllConfirmed)
        {
            await _enterPlayAsync(ct);
        }

        return MulliganResult.Success();
    }

    /// <summary>门禁判定（相位／终局／已确认）；通过＝null。</summary>
    private MulliganResult? CheckGate(Player player)
    {
        if (_lifecycle.IsEnded)
        {
            return MulliganResult.Failure(MulliganFailureReason.GameEnded);
        }

        if (!_lifecycle.IsMulligan)
        {
            return MulliganResult.Failure(MulliganFailureReason.PhaseBlocked);
        }

        if (_confirmed.Contains(player))
        {
            return MulliganResult.Failure(MulliganFailureReason.AlreadyConfirmed);
        }

        return null;
    }

    /// <summary>玩家归属校验（不属于本对局＝明确异常，fail-fast）。</summary>
    private void RequireParticipant(Player player)
    {
        if (!_playerManager.Players.Contains(player))
        {
            throw new ArgumentException("指定玩家不属于本对局（换牌动作被拒绝）。", nameof(player));
        }
    }
}
