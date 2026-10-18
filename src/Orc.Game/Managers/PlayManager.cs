using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Orc.Game.Managers;

/// <summary>
/// 打出管理器（2B 打出链服务与交互入口）：
/// ①单位预打出（<see cref="BeginUnitPrePlayAsync"/>）——开始＝触发预打出触发器（内含指挥点合法性验证；不足＝拒绝）
///   ＋候选检查（候选＝己方支援阵型全部插入空隙（含总部边界）；为空＝拒绝、不进入交互）；
///   交互＝targeter 请求（候选＝FormationGap 引用）；确认（前端/mock 经交互完成路径提交选中空隙）＝自动衔接打出链；
///   取消（经交互取消路径）＝终止预打出（零副作用——不发更新、卡留手、无费用扣除、无槽位/组件变化）。
///   「开始/取消/确认」形态：开始＝本入口；取消/确认＝交互完成路径（桥接 Complete/Cancel 驱动，等价于前端拖回/释放手势）。
/// ②单位打出链直接驱动（<see cref="PlayUnitAsync"/>）：外层复验＋部署链＋收尾扣费（部署路径）。
/// ③加入路径（<see cref="JoinUnitAsync"/>）：加入触发器 → 共用单位化触发器；不扣费、不走部署词条（后端入口、不问来源）。
/// ④指令（<see cref="PlayCommandAsync"/>）：预打出段（验证＋handler 捕获；默认无交互、零更新）自动衔接打出段
///   （card.played → 主动 handler 集 → 收尾扣费/离手）；捕获结果自动移交为打出触发器 object? 参数。
/// ⑤反制（<see cref="UseCounterAsync"/>）：单入口状态翻转（激活 ↔ 取消；仅己方回合）。
/// 结果统一经 <see cref="PlayResult"/>（不抛；失败原因类别化；取消＝<see cref="PlayResultStatus.Cancelled"/>）；
/// 拒绝/失败/取消均不发任何游戏更新、不进入后续步骤。
/// 终局门禁（后置项 B）：对局已结束＝全部入口拒绝（失败结果、零副作用、状态不推进）。
/// 无效驱动（未加载卡等装配性错误）＝明确异常（fail-fast）；玩家动作级失败＝结果对象（不抛）。
/// 依赖：引擎（发射/触发）、目标选择管理器（单位预打出的交互承载）、战场（候选计算）、
/// 回合管理器（反制「仅己方回合」；可空——独立构造场景下反制不可用、使用将抛明确异常）、
/// 对局生命周期（终局门禁；可空＝独立构造场景无门禁）。
/// </summary>
public sealed class PlayManager
{
    private readonly LogicEngine _engine;
    private readonly TargeterManager _targeterManager;
    private readonly Battlefield _battlefield;
    private readonly TurnManager? _turnManager;
    private readonly MatchLifecycle? _lifecycle;
    private readonly Func<Player, Entity?, Entity?, bool> _inputAllowed;
    private readonly Func<Player, bool>? _turnOwner;

    /// <summary>创建打出管理器。</summary>
    /// <param name="engine">对局引擎（发射更新 / 触发子触发器）。</param>
    /// <param name="targeterManager">目标选择管理器（单位预打出交互）。</param>
    /// <param name="battlefield">战场（部署候选＝己方支援阵型插入空隙的计算源）。</param>
    /// <param name="turnManager">回合管理器（反制「仅己方回合」的当前行动方真源；可空——缺省＝独立构造场景，反制使用抛明确异常）。</param>
    /// <param name="lifecycle">对局生命周期（终局门禁——后置项 B；缺省＝null＝独立构造场景无门禁）。</param>
    /// <param name="inputAllowed">行动输入准入通道（前端状态）。</param>
    /// <param name="turnOwner">回合归属判定通道（R4；缺省＝null＝按 turnManager 当前行动方引用比较）。</param>
    /// <exception cref="ArgumentNullException">engine / targeterManager / battlefield 为 null。</exception>
    public PlayManager(
        LogicEngine engine,
        TargeterManager targeterManager,
        Battlefield battlefield,
        TurnManager? turnManager = null,
        MatchLifecycle? lifecycle = null,
        Func<Player, Entity?, Entity?, bool>? inputAllowed = null,
        Func<Player, bool>? turnOwner = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(targeterManager);
        ArgumentNullException.ThrowIfNull(battlefield);

        _engine = engine;
        _targeterManager = targeterManager;
        _battlefield = battlefield;
        _turnManager = turnManager;
        _lifecycle = lifecycle;
        _inputAllowed = inputAllowed ?? ((_, _, _) => true);
        _turnOwner = turnOwner;
    }

    // ---------- ① 单位预打出（开始→交互→确认/取消；一次调用链） ----------

    /// <summary>
    /// 单位预打出（一次调用链：开始 → 交互 → 确认/取消 →〔确认时〕自动衔接打出链）：
    /// ①开始：触发预打出触发器（内含指挥点验证——不足＝拒绝〔预打出阶段〕）；候选检查（己方支援阵型插入空隙——为空＝拒绝）；
    /// ②交互：发起 targeter 请求（候选＝FormationGap 引用；单选）；等待结局（前端/mock 经桥接完成或取消）；
    /// ③确认：取出选中空隙 → 自动衔接 <see cref="PlayUnitAsync"/>；取消：终止（零副作用）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌未加载归属（装配性错误、fail-fast）。</exception>
    public async Task<PlayResult> BeginUnitPrePlayAsync(UnitCard card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var owner = RequireOwner(card);
        if (CheckAdmission(card, true) is { } admission) return PlayResult.Failure(admission);


        // 终局门禁（后置项 B）：对局已结束＝入口拒绝（不触发预打出、不发起 targeter 请求、零副作用）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return PlayResult.Failure(PlayFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return PlayResult.Failure(PlayFailureReason.PhaseBlocked);
            }
        }

        // 动作作用域（UI 消费桥接）：本动作产生的事件聚合为一段（外层优先——内部衔接的打出链合并入本段）。
        await using var _actionScope = _engine.BeginAction();

        // ① 开始：触发预打出触发器（验证＝指挥点检查；不足＝拒绝、不发起 targeter 请求、不发任何更新）。
        // 捕获箱与指令预打出同形：单位预打出也可声明选靶（指向型部署），handler 经 CaptureBox 提交/取消。
        var captureBox = new CardCaptureBox();
        var preStream = await card.PrePlayTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = owner,
                [nameof(CardTriggerView.CaptureBox)] = captureBox,
                [nameof(CardTriggerView.SelectorSlots)] = card.HandOriginSlots,
            },
            ct);

        if (preStream.Outcome == ExecutionOutcome.ValidationRejected)
        {
            return PlayResult.Failure(PlayFailureReason.PrePlayPointShortage);
        }

        if (preStream.Outcome != ExecutionOutcome.Normal)
        {
            return PlayResult.Failure(PlayFailureReason.PlayChainFault);
        }

        if (captureBox.IsPrePlayCancelled)
        {
            return PlayResult.Cancelled(); // 预打出选靶被取消：打出不发生（零副作用）
        }

        // Engine-owned insertion gaps: before, between and after all units and HQ.
        var candidates = DeploymentLines(card).SelectMany(line=>line.GetInsertionGaps()).ToArray();
        if (candidates.Length == 0)
        {
            return PlayResult.Failure(PlayFailureReason.PrePlayNoAvailableSlots);
        }

        // ② 交互：targeter 请求（候选＝FormationGap 引用；候选由后端给出——Q19＝a）。
        FormationGap? selectedGap = null;
        var result = await _targeterManager.RunAsync(async flow =>
        {
            var step = await flow.Step(
                SelectorTemplates.Slot,
                new ReferenceSetParameter(candidates.Select(gap => gap.Ref), 1, 1));

            // 非法选择＝同一选择器重入（Q8a）。
            while (step.IsFailed && step.Failure == SelectorFailureReason.InvalidSelection)
            {
                step = await flow.Retry<Ref<Entity>>();
            }

            if (step.IsCancelled)
            {
                return TargeterResult.Cancelled();
            }

            if (step.IsFailed)
            {
                return TargeterResult.FromSelectorFailure(step.Failure);
            }

            selectedGap = step.Value?.Value as FormationGap;
            return selectedGap is null
                ? TargeterResult.Failed(TargeterFailureReason.InvalidSelection)
                : TargeterResult.Ok();
        });

        if (result.IsCancelled)
        {
            return PlayResult.Cancelled(result);
        }

        if (!result.IsOk)
        {
            return PlayResult.Failure(PlayFailureReason.TargetingFailed, result);
        }

        // ③ 确认：选中阵型空隙（身份及占用快照复验）→ 自动衔接打出链。
        if (selectedGap is null)
        {
            return PlayResult.Failure(PlayFailureReason.TargetingFailed, result);
        }

        return await PlayUnitAsync(card, selectedGap, ct);
    }

    // ---------- ② 单位打出链（直接驱动；部署路径） ----------

    /// <summary>
    /// 单位打出链（部署路径）：外层复验（指挥点）→ 打出链（card.played → 部署链 → 收尾扣费/离手）。
    /// 前置（防御性、明确失败结果、零副作用）：目标槽位须为空槽、单位未单位化——违反＝拒绝。
    /// 复验失败＝打出链中止（仅本次取消语义＋留痕；不部署、不扣费、不发更新、不离手）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / target 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌未加载归属（装配性错误、fail-fast）。</exception>
    public async Task<PlayResult> PlayUnitAsync(UnitCard card, Slot target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(target);
        var owner = RequireOwner(card);
        if (CheckAdmission(card, false) is { } admission) return PlayResult.Failure(admission);


        // 终局门禁（后置项 B）：对局已结束＝入口拒绝（零副作用、状态不推进）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return PlayResult.Failure(PlayFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return PlayResult.Failure(PlayFailureReason.PhaseBlocked);
            }
        }

        if (!target.IsEmpty)
        {
            return PlayResult.Failure(PlayFailureReason.TargetSlotOccupied);
        }

        if (card.TryGetData<UnitStateData>(out _))
        {
            return PlayResult.Failure(PlayFailureReason.UnitAlreadyUnitized);
        }

        // 动作作用域（UI 消费桥接）：本动作产生的事件聚合为一段。
        await using var _actionScope = _engine.BeginAction();

        if(!await KeywordProgramRuntime.PreparePlayAsync(card,ct))return PlayResult.Cancelled();
        var stream = await card.PlayTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = owner,
                [GameUpdates.PayloadPosition] = target,
            },
            ct);

        return stream.Outcome switch
        {
            ExecutionOutcome.Normal => PlayResult.Success(),
            ExecutionOutcome.ValidationRejected => PlayResult.Failure(PlayFailureReason.PlayVerificationRejected),
            _ => PlayResult.Failure(PlayFailureReason.PlayChainFault),
        };
    }

    /// <summary>Canonical deployment portal. Rejects stale/foreign gaps before any game side effect.</summary>
    public async Task<PlayResult> PlayUnitAsync(UnitCard card, FormationGap gap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card); ArgumentNullException.ThrowIfNull(gap);
        var owner = RequireOwner(card);
        if (CheckAdmission(card, true) is { } admission) return PlayResult.Failure(admission);
        if (card.TryGetData<UnitStateData>(out _)) return PlayResult.Failure(PlayFailureReason.UnitAlreadyUnitized);
        if (!DeploymentLines(card).Any(line=>ReferenceEquals(gap.Line,line)) || !gap.IsCurrent)
            return PlayResult.Failure(PlayFailureReason.FormationGapUnavailable);
        await using var scope = _engine.BeginAction();
        if(!await KeywordProgramRuntime.PreparePlayAsync(card,ct))return PlayResult.Cancelled();
        var stream = await card.PlayTrigger.InvokeAsync(_engine, new Dictionary<string, object?>
        {
            [GameUpdates.PayloadCard] = card, [GameUpdates.PayloadPlayer] = owner,
            [nameof(CardTriggerView.FormationGap)] = gap,
        }, ct);
        return stream.Outcome switch
        {
            ExecutionOutcome.Normal => PlayResult.Success(),
            ExecutionOutcome.ValidationRejected => PlayResult.Failure(PlayFailureReason.PlayVerificationRejected),
            _ => PlayResult.Failure(PlayFailureReason.PlayChainFault),
        };
    }

    /// <summary>Pure entry availability. Executes the same native validation judicators;
    /// effect-specific selections and revalidation still happen in the play chain.</summary>
    public bool CanPlay(CardBase card)
    {
        if (CheckAdmission(card, true) is not null) return false;
        return card switch
        {
            UnitCard unit => !unit.TryGetData<UnitStateData>(out _)
                && unit.PrePlayTrigger.EvaluateValidation(Array.Empty<Ref<Entity>>()).IsValid
                && DeploymentLines(unit).Any(line=>line.GetInsertionGaps().Count>0),
            CommandCard command => command.PrePlayTrigger.EvaluateValidation(Array.Empty<Ref<Entity>>()).IsValid,
            CounterCard counter => counter.UseCounterTrigger.EvaluateValidation(Array.Empty<Ref<Entity>>()).IsValid,
            _ => false,
        };
    }

    public IReadOnlyList<FormationGap> GetDeploymentGaps(UnitCard card) => CanPlay(card)
        ? DeploymentLines(card).SelectMany(line=>line.GetInsertionGaps()).ToArray() : Array.Empty<FormationGap>();

    private IEnumerable<BattleLine> DeploymentLines(UnitCard card)
    {
        yield return _battlefield.GetSupportLine(card.Owner!);
        if(KeywordProgramRuntime.Resolve(card,"deploy.frontline",0)!=0 && !_battlefield.FrontLine.Any(slot=>slot.Occupant is UnitCard enemy && !ReferenceEquals(enemy.Owner,card.Owner)))yield return _battlefield.FrontLine;
    }

    private PlayFailureReason? CheckAdmission(CardBase card, bool requireHand)
    {
        if (_lifecycle?.IsEnded == true) return PlayFailureReason.GameEnded;
        if (_lifecycle is not null && !_lifecycle.IsActionAllowed) return PlayFailureReason.PhaseBlocked;
        var owner = card.Owner;
        if (owner is null) return PlayFailureReason.CardNotInHand;
        // Full match user entrances require native ownership/hand membership. Standalone fixtures
        // keep their established low-level semantics; JoinUnitAsync never reads frontend state.
        if (requireHand && _turnManager is not null)
        {
            var ownerTurn = _turnOwner is not null
                ? _turnOwner(owner)
                : ReferenceEquals(_turnManager.CurrentPlayer, owner);
            if (!ownerTurn) return PlayFailureReason.NonOwnerTurn;
            if (!owner.Hand.Contains(card)) return PlayFailureReason.CardNotInHand;
        }
        return _inputAllowed(owner, card, null) ? null : PlayFailureReason.InteractionBlocked;
    }

    // ---------- ③ 加入路径 ----------

    /// <summary>
    /// 加入路径（后端入口；不问来源、非交互路径）：加入触发器 → 共用单位化触发器；不扣费、不走部署词条；
    /// 单位化完成后发 unit.joined。目标槽位由调用方给定、须为空槽（非空＝拒绝、可观测；邻位规则不适用）。
    /// 归属不要求（加入不涉及手牌/扣费）；单位化装配失败不留矛盾中间态。
    /// </summary>
    /// <exception cref="ArgumentNullException">card / target 为 null。</exception>
    public async Task<PlayResult> JoinUnitAsync(UnitCard card, Slot target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(target);

        // 终局门禁（后置项 B）：对局已结束＝入口拒绝（零副作用、状态不推进）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return PlayResult.Failure(PlayFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return PlayResult.Failure(PlayFailureReason.PhaseBlocked);
            }
        }

        if (!target.IsEmpty)
        {
            return PlayResult.Failure(PlayFailureReason.TargetSlotOccupied);
        }

        if (card.TryGetData<UnitStateData>(out _))
        {
            return PlayResult.Failure(PlayFailureReason.UnitAlreadyUnitized);
        }

        // 动作作用域（UI 消费桥接）：本动作产生的事件聚合为一段。
        await using var _actionScope = _engine.BeginAction();

        var stream = await card.JoinTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = card.Owner,
                [GameUpdates.PayloadPosition] = target,
            },
            ct);

        return stream.Outcome switch
        {
            ExecutionOutcome.Normal => PlayResult.Success(),
            _ => PlayResult.Failure(PlayFailureReason.PlayChainFault),
        };
    }

    // ---------- ④ 指令（统一两段式「预行为」：预打出→打出；确认自动衔接） ----------

    /// <summary>
    /// 指令预行为入口（统一两段式「预行为」形态；S4）：UI 只调本入口——引擎在入口内完成验证与交互编排，
    /// 桥接确认/取消应答驱动后续执行链；不暴露第二段执行 API（与单位 <see cref="BeginUnitPrePlayAsync"/> 同形）。
    /// ①预打出段：触发预打出触发器（内含指挥点验证——不足＝拒绝）；执行预打出 handler 集（装配期注册；登记序；
    ///   异常沿用引擎隔离；捕获经 <see cref="CardCaptureBox"/> 提交；取消经 <see cref="CardCaptureBox.CancelPrePlay"/> 显式请求）；
    ///   选择器槽位声明（<c>SelectorSlots</c>）随触发数据提供，交互经桥接应答驱动；
    /// ②打出段（确认后自动衔接）：触发打出触发器（复验指挥点）——card.played → 主动 handler 集（登记序）→ 收尾（扣费→离手）；
    ///   捕获结果自动移交为 object? 参数（单引用/列表/targeter 皆可承载）。
    /// 预打出段失败/取消 ⇒ 打出不发生（零副作用——不发更新、卡留手、无扣费/离手）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌未加载归属（装配性错误、fail-fast）。</exception>
    public Task<PlayResult> BeginCommandPrePlayAsync(CommandCard card, CancellationToken ct = default)
        => RunCommandChainAsync(card, ct);

    /// <summary>
    /// 指令打出（兼容入口；受控变更登记：原对外形态保留、内部收敛到预行为入口 <see cref="BeginCommandPrePlayAsync"/>，
    /// 行为不变——同一实现、一次调用链）。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌未加载归属（装配性错误、fail-fast）。</exception>
    public Task<PlayResult> PlayCommandAsync(CommandCard card, CancellationToken ct = default)
        => RunCommandChainAsync(card, ct);

    private async Task<PlayResult> RunCommandChainAsync(CommandCard card, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);
        var owner = RequireOwner(card);
        if (CheckAdmission(card, true) is { } admission) return PlayResult.Failure(admission);


        // 终局门禁（后置项 B）：对局已结束＝入口拒绝（零副作用、状态不推进）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return PlayResult.Failure(PlayFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return PlayResult.Failure(PlayFailureReason.PhaseBlocked);
            }
        }

        // 动作作用域（UI 消费桥接）：本动作产生的事件聚合为一段（预打出段＋打出段合并入本段）。
        await using var _actionScope = _engine.BeginAction();

        // ① 预打出段：验证＋handler 捕获集（默认无 handler＝无交互、零更新）。
        var captureBox = new CardCaptureBox();
        var preStream = await card.PrePlayTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = owner,
                [nameof(CardTriggerView.CaptureBox)] = captureBox,
                [nameof(CardTriggerView.SelectorSlots)] = card.HandOriginSlots,
            },
            ct);

        if (preStream.Outcome == ExecutionOutcome.ValidationRejected)
        {
            return PlayResult.Failure(PlayFailureReason.PrePlayPointShortage);
        }

        if (preStream.Outcome != ExecutionOutcome.Normal)
        {
            return PlayResult.Failure(PlayFailureReason.PlayChainFault);
        }

        if (captureBox.IsPrePlayCancelled)
        {
            return PlayResult.Cancelled();
        }

        if (CheckAdmission(card, true) is { } commitAdmission) return PlayResult.Failure(commitAdmission);

        // ② 打出段：复验＋宣告＋主动 handler 集＋收尾；捕获结果自动移交为 object? 参数。
        if(!await KeywordProgramRuntime.PreparePlayAsync(card,ct))return PlayResult.Cancelled();
        var playStream = await card.PlayTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = owner,
                [nameof(CardTriggerView.Argument)] = captureBox.Result,
            },
            ct);

        return playStream.Outcome switch
        {
            ExecutionOutcome.Normal => PlayResult.Success(),
            ExecutionOutcome.ValidationRejected => PlayResult.Failure(PlayFailureReason.PlayVerificationRejected),
            _ => PlayResult.Failure(PlayFailureReason.PlayChainFault),
        };
    }

    // ---------- ⑤ 反制（单入口状态翻转） ----------

    /// <summary>
    /// 反制使用（单入口状态翻转）：未激活＝激活流程（验证〔指挥点〕→ 扣点 → 置激活 ＋ 注册效果 handler）；
    /// 已激活＝取消流程（退点〔无条件、同额〕→ 取消激活 ＋ 取消注册）。
    /// 仅己方回合（该卡所属玩家是当前行动方）；拒绝＝明确失败结果（不改变状态、不扣点、不注册、不发任何游戏更新）。
    /// 使用反制不执行 targeter 交互、不发射任何游戏更新。
    /// </summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌未加载归属；或本管理器未装配回合上下文（反制使用需要「当前行动方」）。</exception>
    public async Task<PlayResult> UseCounterAsync(CounterCard card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var owner = RequireOwner(card);
        if (CheckAdmission(card, false) is { } admission) return PlayResult.Failure(admission);


        // 终局门禁（后置项 B）：对局已结束＝入口拒绝（激活/取消均拒绝；零副作用、状态不推进）
        if (_lifecycle is not null)
        {
            if (_lifecycle.IsEnded)
            {
                return PlayResult.Failure(PlayFailureReason.GameEnded);
            }

            if (!_lifecycle.IsActionAllowed)
            {
                return PlayResult.Failure(PlayFailureReason.PhaseBlocked);
            }
        }

        if (_turnManager is null)
        {
            throw new InvalidOperationException(
                "反制使用需要回合上下文（当前行动方），本管理器未装配回合管理器（独立构造场景不支持反制使用）。");
        }

        // 动作作用域（UI 消费桥接）：本动作产生的事件聚合为一段。
        await using var _actionScope = _engine.BeginAction();

        // 前置判定（与验证同源；J2：经「反制使用验证判定器」执行——与触发器验证同一绑定判定器，
        // moding 改写两侧同步生效；用于失败原因的可辨识映射——按取数语义消费拒绝类别）。
        var verdict = card.UseCounterTrigger.EvaluateValidation(Array.Empty<Ref<Entity>>());
        if (!verdict.IsValid)
        {
            return PlayResult.Failure(verdict.RejectionReason switch
            {
                CounterUseRejection.NotOwnerTurn => PlayFailureReason.CounterNotOwnerTurn,
                CounterUseRejection.NotEnoughPoints => PlayFailureReason.CounterPointShortage,
                // 降级（缺类别/不可辨识）：一般性失败原因（规范内缺省——明确、不伪造具体类别）
                _ => PlayFailureReason.CounterRejected,
            });
        }

        var stream = await card.UseCounterTrigger.InvokeAsync(
            _engine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = card,
                [GameUpdates.PayloadPlayer] = owner,
            },
            ct);

        return stream.Outcome switch
        {
            ExecutionOutcome.Normal => PlayResult.Success(),
            ExecutionOutcome.ValidationRejected => PlayResult.Failure(PlayFailureReason.PlayChainFault), // 防御（同源判定已前置）
            _ => PlayResult.Failure(PlayFailureReason.PlayChainFault),
        };
    }

    /// <summary>归属要求（装配性错误：未加载卡不可驱动打出链——fail-fast）。</summary>
    private static Player RequireOwner(CardBase card)
        => card.Owner ?? throw new InvalidOperationException(
            $"卡牌 '{card.Name}' 未加载归属（未完成 LoadAsync），不能驱动打出链。");
}
