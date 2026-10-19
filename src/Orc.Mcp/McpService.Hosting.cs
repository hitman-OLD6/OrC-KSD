using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Mcp.Hosting;
using Orc.Mcp.Match;

namespace Orc.Mcp;

/// <summary>
/// 服务层的**宿主集成面**与**强类型动作面**实现（与 MCP 工具共用同一执行器、门禁与 pending 状态机）。
/// </summary>
public sealed partial class McpService : IMcpHost, IMcpActions
{
    private readonly List<Action<PendingSelection>> _targetingSubscribers = new();
    private bool _targetingBridgeHooked;

    // ---------- IMcpHost ----------

    /// <inheritdoc />
    public Orc.Game.Match? ActiveMatch => Executor.Match;

    /// <inheritdoc />
    public bool TryGetActiveMatch([NotNullWhen(true)] out Orc.Game.Match? match)
    {
        match = Executor.Match;
        return match is not null;
    }

    /// <inheritdoc />
    public event EventHandler<MatchLifecycleEventArgs>? MatchChanged;

    /// <inheritdoc />
    public IDisposable SubscribeTargetingRequests(Action<PendingSelection> onRequest)
    {
        ArgumentNullException.ThrowIfNull(onRequest);

        lock (_targetingSubscribers)
        {
            _targetingSubscribers.Add(onRequest);
            if (!_targetingBridgeHooked)
            {
                Bridge.PendingPublished += DispatchPending;
                _targetingBridgeHooked = true;
            }
        }

        return new Subscription(() =>
        {
            lock (_targetingSubscribers)
            {
                _targetingSubscribers.Remove(onRequest);
            }
        });
    }

    /// <inheritdoc />
    public bool TrySubmitTargeting(
        string sessionId,
        IReadOnlyList<int> picks,
        bool cancel,
        string? claimant,
        out string? reason)
    {
        var claimantId = claimant ?? TargeterBridgeAdapter.ClaimantHost;

        try
        {
            var outcome = Executor
                .SubmitAsync(sessionId, picks, cancel, CancellationToken.None, claimantId)
                .GetAwaiter()
                .GetResult();

            if (outcome.Status == ActionOutcome.StatusFailed)
            {
                reason = outcome.FailureMessage ?? "提交未生效。";
                return false;
            }

            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryClaimTargeting(string sessionId, string claimant, out string? reason)
        => Bridge.TryClaim(sessionId, claimant, out reason);

    /// <inheritdoc />
    public SegmentConsumer SegmentConsumer { get; private set; } = SegmentConsumer.Service;

    /// <inheritdoc />
    public void SetSegmentConsumer(SegmentConsumer consumer) => SegmentConsumer = consumer;

    /// <summary>服务层是否应自行取段（宿主未声明担任取用者时）。</summary>
    public bool ShouldServicePullSegments => SegmentConsumer == SegmentConsumer.Service;

    /// <inheritdoc />
    public IReadOnlyList<Orc.Output.EventSegment> PullSegments(int? take = null)
    {
        var snapshot = Segments.Snapshot();
        if (take is null or <= 0 || take.Value >= snapshot.Count)
        {
            return snapshot;
        }

        return snapshot.Skip(snapshot.Count - take.Value).ToArray();
    }

    /// <inheritdoc />
    public void IngestSegments(IReadOnlyList<Orc.Output.EventSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments.AddRange(segments);
    }

    /// <summary>
    /// 装配活跃对局（**不广播**——此刻对局尚未 Initialize，包激活相未跑、投影提供者未注册）。
    /// 生命周期事件在 <see cref="NotifyMatchReady"/> 时广播。
    /// </summary>
    internal void AttachMatch(Orc.Game.Match match) => Executor.Attach(match);

    /// <summary>
    /// 对局**就绪**（Initialize 完成、包激活相已跑）后广播 <see cref="MatchLifecycleChange.Created"/>——
    /// 宿主据此建立投影 provider（太早会拿不到 provider 工厂）。
    /// </summary>
    internal void NotifyMatchReady()
    {
        if (Executor.Match is { } match)
        {
            MatchChanged?.Invoke(this, new MatchLifecycleEventArgs(MatchLifecycleChange.Created, match));
        }
    }

    /// <summary>摘除活跃对局并广播生命周期事件（服务层内部使用）。</summary>
    internal Orc.Game.Match? DetachMatch()
    {
        var match = Executor.Detach();
        if (match is not null)
        {
            MatchChanged?.Invoke(this, new MatchLifecycleEventArgs(MatchLifecycleChange.Disposed, match));
        }

        return match;
    }

    private void DispatchPending(PendingSelection pending)
    {
        Action<PendingSelection>[] subscribers;
        lock (_targetingSubscribers)
        {
            subscribers = _targetingSubscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(pending);
            }
            catch (Exception)
            {
                // 订阅者异常不得影响对局（与 UI 投影"运行期隔离"同口径）。
            }
        }
    }

    // ---------- IMcpActions ----------

    /// <inheritdoc />
    public Task<ActionOutcome> EndTurnAsync(CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                await m.EndTurn(ct).ConfigureAwait(false);
                return ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "match.end_turn",
                    ["state"] = m.State.ToString(),
                    ["phase"] = m.Phase.ToString(),
                    ["turn"] = m.TurnNumber,
                    ["currentPlayerIndex"] = m.CurrentPlayer?.Index,
                });
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> ConcedeAsync(int ownerIndex, CancellationToken ct = default)
        => Executor.RunAsync(
            m =>
            {
                if (!CardResolver.TryResolvePlayer(m, ownerIndex, out var player, out var error))
                {
                    return Task.FromResult(ActionOutcome.Failed(McpCodes.InvalidArguments, error));
                }

                var result = m.Concede(player!, ct);
                return Task.FromResult(ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "match.concede",
                    ["status"] = result.ToString(),
                    ["state"] = m.State.ToString(),
                    ["winnerIndex"] = m.Winner?.Index,
                    ["endReason"] = m.EndReason?.ToString(),
                }));
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> MulliganBeginAsync(int ownerIndex, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolvePlayer(m, ownerIndex, out var player, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.BeginMulliganAsync(player!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromMulligan(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> MulliganDoneAsync(int ownerIndex, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolvePlayer(m, ownerIndex, out var player, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.MulliganDone(player!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromMulligan(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> PlayUnitPreAsync(string cardRef, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.BeginUnitPrePlayAsync(card!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> PlayUnitAsync(string cardRef, int ownerIndex, int slotIndex, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                if (!CardResolver.TryResolveSupportSlot(m, ownerIndex, slotIndex, out var slot, out error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.PlayUnitAsync(card!, slot!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> JoinUnitAsync(string cardRef, int ownerIndex, int slotIndex, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                if (!CardResolver.TryResolveSupportSlot(m, ownerIndex, slotIndex, out var slot, out error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.JoinUnitAsync(card!, slot!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> PlayCommandPreAsync(string cardRef, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.OrderCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.BeginOrderPrePlayAsync(card!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> PlayCommandAsync(string cardRef, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.OrderCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.PlayOrderAsync(card!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> UseCounterAsync(string cardRef, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.CounterCard>(m, cardRef, out var card, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await m.PlayManager.UseCounterAsync(card!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromPlay(result);
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> BeginCommandAsync(string unitRef, CancellationToken ct = default)
        => RunUnitCommandAsync(unitRef, static (m, unit, ct) => m.CommandManager.BeginCommandAsync(unit, ct), ct);

    /// <inheritdoc />
    public Task<ActionOutcome> BeginMoveAsync(string unitRef, CancellationToken ct = default)
        => RunUnitCommandAsync(unitRef, static (m, unit, ct) => m.CommandManager.BeginMoveAsync(unit, ct), ct);

    /// <inheritdoc />
    public Task<ActionOutcome> BeginAttackAsync(string unitRef, CancellationToken ct = default)
        => RunUnitCommandAsync(unitRef, static (m, unit, ct) => m.CommandManager.BeginAttackAsync(unit, ct), ct);

    /// <inheritdoc />
    public Task<ActionOutcome> LeaveBattlefieldAsync(string unitRef, CancellationToken ct = default)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, unitRef, out var unit, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                await m.CommandManager.LeaveBattlefieldAsync(unit!, ct).ConfigureAwait(false);
                return ActionOutcome.Completed(new JsonObject
                {
                    ["kind"] = "command.leave",
                    ["status"] = "Success",
                });
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> GetCommandAvailabilityAsync(string unitRef, CancellationToken ct = default)
        => Executor.RunAsync(
            m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, unitRef, out var unit, out var error))
                {
                    return Task.FromResult(ActionOutcome.Failed(McpCodes.InvalidArguments, error));
                }

                var availability = m.CommandManager.GetCommandAvailability(unit!);
                return Task.FromResult(EngineResultMapper.Query("command.availability", AvailabilityJson(availability)));
            },
            ct);

    /// <inheritdoc />
    public Task<ActionOutcome> CanPlayAsync(string cardRef, CancellationToken ct = default)
        => Executor.RunAsync(
            m =>
            {
                if (!CardResolver.TryResolveCard(m, cardRef, out var card, out var error))
                {
                    return Task.FromResult(ActionOutcome.Failed(McpCodes.InvalidArguments, error));
                }

                if (card is not Orc.Game.Cards.CardBase cardBase)
                {
                    return Task.FromResult(ActionOutcome.Failed(
                        McpCodes.InvalidArguments,
                        $"引用键 '{cardRef}' 指向的对象不是游戏层卡牌（无法判定可打出性）。"));
                }

                return Task.FromResult(EngineResultMapper.Query("play.can_play", new JsonObject
                {
                    ["ref"] = cardRef,
                    ["canPlay"] = m.PlayManager.CanPlay(cardBase),
                }));
            },
            ct);

    private Task<ActionOutcome> RunUnitCommandAsync(
        string unitRef,
        Func<Orc.Game.Match, Orc.Game.Cards.UnitCard, CancellationToken, Task<Orc.Game.Commanding.CommandResult>> action,
        CancellationToken ct)
        => Executor.RunAsync(
            async m =>
            {
                if (!CardResolver.TryResolveCard<Orc.Game.Cards.UnitCard>(m, unitRef, out var unit, out var error))
                {
                    return ActionOutcome.Failed(McpCodes.InvalidArguments, error);
                }

                var result = await action(m, unit!, ct).ConfigureAwait(false);
                return EngineResultMapper.FromCommand(result);
            },
            ct);

    private static JsonNode AvailabilityJson(Orc.Game.Commanding.CommandAvailability availability)
        => new JsonObject
        {
            ["kind"] = "command.availability",
            ["ineligibleReason"] = availability.IneligibleReason?.ToString(),
            ["anyActionAvailable"] = availability.AnyActionAvailable,
            ["moveAvailable"] = availability.Move?.CanUse,
            ["attackAvailable"] = availability.Attack?.CanUse,
            ["moveBlocked"] = availability.Move?.BlockReason?.ToString(),
            ["attackBlocked"] = availability.Attack?.BlockReason?.ToString(),
        };

    /// <summary>退订句柄（Dispose 幂等）。</summary>
    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _dispose, null);
            action?.Invoke();
        }
    }
}
