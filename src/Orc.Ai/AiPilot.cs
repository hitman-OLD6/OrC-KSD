using Orc.Game;
using Orc.Game.Ai;
using Orc.Game.Ui;
using Orc.UI.Contracts;

namespace Orc.Ai;

/// <summary>
/// 启发式 AI 玩家（进程内；占一个座位）。
/// <para>
/// 驱动方式＝**宿主显式单步**：每次 <see cref="StepAsync"/> 执行「一个主动动作 ＋ 它引起的待决应答」；
/// 宿主循环调用即可推进整个回合。无后台线程、不与人类动作并发进入引擎（全程走服务层门禁）。
/// </para>
/// <para>
/// 视野＝座位过滤投影（不看对手手牌与卡组顺序）；待决请求只接管**自己动作链**引起的那些
/// （发起前声明预期应答者，见 <see cref="IAiMatchGateway.ExpectClaimant"/>）。
/// </para>
/// </summary>
public sealed class AiPilot
{
    /// <summary>AI 作为待决请求应答者的标识。</summary>
    public const string Claimant = "ai";

    private const int MaxTargetingRounds = 8;
    private const int MaxMulliganAttempts = 3;

    private readonly IAiMatchGateway _gateway;
    private readonly IUiProjectionProvider _projection;
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private bool _mulliganDone;
    private int _mulliganAttempts;

    /// <summary>创建 AI 玩家。</summary>
    /// <param name="gateway">对局操作面（唯一门禁/唯一桥；同一实例亦承载人类与 agent）。</param>
    /// <param name="projection">该对局的投影提供者（宿主在 <c>MatchChanged(Created)</c> 后创建）。</param>
    /// <param name="seat">AI 占用的座位（默认 1）。</param>
    public AiPilot(IAiMatchGateway gateway, IUiProjectionProvider projection, int seat = 1)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(projection);
        if (seat is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "座位须为 0 或 1。");
        }

        _gateway = gateway;
        _projection = projection;
        Seat = seat;
    }

    /// <summary>AI 占用的座位。</summary>
    public int Seat { get; }

    /// <summary>执行一步（一个动作 ＋ 其待决应答）。</summary>
    public async Task<AiStepResult> StepAsync(CancellationToken ct = default)
    {
        if (!_gateway.TryGetActiveMatch(out var match))
        {
            return AiStepResult.Idle("无活跃对局。");
        }

        if (match.State == MatchState.Ended)
        {
            return AiStepResult.Idle("对局已结束。");
        }

        var view = _projection.Project(match, match.Players[Seat]);

        if (string.Equals(view.Phase, "mulligan", StringComparison.Ordinal))
        {
            return await StepMulliganAsync(ct).ConfigureAwait(false);
        }

        if (!string.Equals(view.Phase, "play", StringComparison.Ordinal))
        {
            return AiStepResult.Idle("对局已结束。");
        }

        if (!string.Equals(view.ActivePlayerSide, "self", StringComparison.Ordinal))
        {
            return AiStepResult.Idle("未轮到 AI。");
        }

        // 出牌段不查询合法性（投影的 CanPlayCard 恒 false，引擎判定为准）；
        // 只有进入指挥段才查询「指挥可用性」——避免用投影的静态标志导致大量无效动作。
        var intent = HeuristicPolicy.DecidePlay(view, _failed);
        if (intent is null)
        {
            var (attackable, movable) = await QueryCommandsAsync(view, ct).ConfigureAwait(false);
            intent = HeuristicPolicy.Decide(view, _failed, attackable, movable);
        }

        if (intent is null)
        {
            return await EndTurnAsync(ct).ConfigureAwait(false);
        }

        var outcome = intent.Kind switch
        {
            HeuristicPolicy.PlayUnit => await RunAsync(
                () => _gateway.PlayUnitAsync(intent.Ref!, Seat, intent.Slot, ct), ct).ConfigureAwait(false),
            HeuristicPolicy.PlayCommand => await RunAsync(
                () => _gateway.PlayCommandPreAsync(intent.Ref!, ct), ct).ConfigureAwait(false),
            HeuristicPolicy.PlayCounter => await RunAsync(
                () => _gateway.UseCounterAsync(intent.Ref!, ct), ct).ConfigureAwait(false),
            HeuristicPolicy.Attack => await RunAsync(
                () => _gateway.BeginAttackAsync(intent.Ref!, ct), ct, intent.AttackPower).ConfigureAwait(false),
            HeuristicPolicy.Move => await RunAsync(
                () => _gateway.BeginMoveAsync(intent.Ref!, ct), ct).ConfigureAwait(false),
            _ => await RunAsync(() => _gateway.EndTurnAsync(ct), ct).ConfigureAwait(false),
        };

        if (outcome.Failed)
        {
            _failed.Add(HeuristicPolicy.KeyOf(intent));
            return new AiStepResult(true, intent.Kind, outcome, false, outcome.FailureMessage);
        }

        return new AiStepResult(true, intent.Kind, outcome, false);
    }

    private async Task<AiStepResult> StepMulliganAsync(CancellationToken ct)
    {
        if (_mulliganDone)
        {
            return AiStepResult.Idle("换牌已确认，等待对手。");
        }

        // 换牌策略＝不换：直接确认（`MulliganDone` 即「不换牌直接确认」入口，无需打开选择器）。
        var done = await RunAsync(() => _gateway.MulliganDoneAsync(Seat, ct), ct).ConfigureAwait(false);
        if (!done.Failed)
        {
            _mulliganDone = true;
        }
        else if (++_mulliganAttempts >= MaxMulliganAttempts)
        {
            _mulliganDone = true; // 反复失败＝不再重试（等对手推进相位，避免空转）
        }

        return new AiStepResult(true, "mulligan", done, false, done.FailureMessage);
    }

    private async Task<AiStepResult> EndTurnAsync(CancellationToken ct)
    {
        var outcome = await RunAsync(() => _gateway.EndTurnAsync(ct), ct).ConfigureAwait(false);
        if (outcome.Failed)
        {
            return new AiStepResult(true, HeuristicPolicy.EndTurn, outcome, false, outcome.FailureMessage);
        }

        _failed.Clear();
        _mulliganAttempts = 0;
        return new AiStepResult(true, HeuristicPolicy.EndTurn, outcome, true);
    }

    /// <summary>
    /// 执行一个动作并接管它引起的待决请求（自己动作链）：声明预期应答者 → 动作 → 逐个应答选择器。
    /// </summary>
    private async Task<AiActionOutcome> RunAsync(
        Func<Task<AiActionOutcome>> action, CancellationToken ct, int? attackPower = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        _gateway.ExpectClaimant(Claimant);
        var outcome = await action().ConfigureAwait(false);

        var rounds = 0;
        while (outcome.IsTargeting
            && outcome.Pending is { } pending
            && rounds++ < MaxTargetingRounds)
        {
            var picks = await ChoosePickAsync(pending, attackPower, ct).ConfigureAwait(false);
            outcome = await _gateway
                .SubmitAsync(pending.SessionId, picks, cancel: false, ct, Claimant)
                .ConfigureAwait(false);

            if (outcome.Failed)
            {
                break;
            }
        }

        return outcome;
    }

    /// <summary>查询每个己方单位的指挥可用性（只读；攻击/移动的合法动作源）。</summary>
    private async Task<(IReadOnlySet<string> Attackable, IReadOnlySet<string> Movable)> QueryCommandsAsync(
        UiMatchView view, CancellationToken ct)
    {
        var attackable = new HashSet<string>(StringComparer.Ordinal);
        var movable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in view.SelfLine)
        {
            var availability = await _gateway.GetCommandAvailabilityAsync(unit.Uid, ct).ConfigureAwait(false);

            if (availability.AttackAvailable)
            {
                attackable.Add(unit.Uid);
            }

            if (availability.MoveAvailable)
            {
                movable.Add(unit.Uid);
            }
        }

        return (attackable, movable);
    }

    private Task<IReadOnlyList<int>> ChoosePickAsync(AiPending pending, int? attackPower, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.Candidates.Count == 0)
        {
            // 标识类候选：需选择＝取第 1 项；可选（Min=0）＝空选择。
            IReadOnlyList<int> identifiers = pending.Min > 0 && pending.Identifiers.Count > 0
                ? new[] { 1 }
                : Array.Empty<int>();
            return Task.FromResult(identifiers);
        }

        if (string.Equals(pending.SelectorName, "mulligan", StringComparison.Ordinal))
        {
            // 换牌固定「不换」；引擎要求至少选 1 张时退让为选第 1 张（避免会话滞留）。
            return Task.FromResult<IReadOnlyList<int>>(pending.Min > 0
                ? new[] { pending.Candidates[0].Index }
                : Array.Empty<int>());
        }

        if (string.Equals(pending.SelectorName, "slot", StringComparison.Ordinal))
        {
            // 候选按引擎给出顺序（槽序）＝取第一个（最靠前）。
            return Task.FromResult<IReadOnlyList<int>>(new[] { pending.Candidates[0].Index });
        }

        if (string.Equals(pending.SelectorName, "fieldUnit", StringComparison.Ordinal))
        {
            var view = TryProject();
            return Task.FromResult(PickAttackTarget(pending, view, attackPower));
        }

        return Task.FromResult<IReadOnlyList<int>>(new[] { pending.Candidates[0].Index });
    }

    private UiMatchView? TryProject()
    {
        return _gateway.TryGetActiveMatch(out var match)
            ? _projection.Project(match, match.Players[Seat])
            : null;
    }

    /// <summary>
    /// 攻击目标优先级：可一击击杀的敌单位（攻最高）→ 敌方 HQ → 其它敌单位（攻×血最高）。
    /// <paramref name="attackPower"/> 缺失时退化为「攻×血最高」。
    /// </summary>
    private static IReadOnlyList<int> PickAttackTarget(
        AiPending pending, UiMatchView? view, int? attackPower)
    {
        var units = pending.Candidates
            .Where(c => string.Equals(c.Kind, "unit", StringComparison.Ordinal))
            .ToArray();
        var hq = pending.Candidates.FirstOrDefault(c => string.Equals(c.Kind, "hq", StringComparison.Ordinal));

        if (attackPower is { } power && view is not null)
        {
            var killable = units
                .Select(c => (Candidate: c, Defense: DefenseOf(view, c.ReferenceKey)))
                .Where(x => x.Defense is { } d && d <= power)
                .OrderByDescending(x => x.Defense)
                .ToArray();

            if (killable.Length > 0)
            {
                return new[] { killable[0].Candidate.Index };
            }

            if (hq is not null)
            {
                return new[] { hq.Index };
            }
        }
        else if (hq is not null && units.Length == 0)
        {
            return new[] { hq.Index };
        }

        var strongest = units
            .Select(c => (Candidate: c, Score: ScoreOf(view, c.ReferenceKey)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (strongest.Candidate is not null)
        {
            return new[] { strongest.Candidate.Index };
        }

        return hq is not null
            ? new[] { hq.Index }
            : new[] { pending.Candidates[0].Index };
    }

    private static int? DefenseOf(UiMatchView view, string referenceKey)
        => view.EnemyLine
            .FirstOrDefault(c => string.Equals(c.Uid, referenceKey, StringComparison.Ordinal))
            ?.EffectiveDefense;

    private static int ScoreOf(UiMatchView? view, string referenceKey)
    {
        var card = view?.EnemyLine
            .FirstOrDefault(c => string.Equals(c.Uid, referenceKey, StringComparison.Ordinal));
        return card is null
            ? 0
            : (card.EffectiveAttack ?? 0) * (card.EffectiveDefense ?? 1);
    }
}
