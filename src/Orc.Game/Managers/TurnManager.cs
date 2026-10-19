using Orc.Cards;
using Orc.Core;
using Orc.Game.Players;

namespace Orc.Game.Managers;

/// <summary>
/// 回合管理器（回合领域真源：回合数、当前行动方）：回合推进编排（"信号先行、处理随后"模型）。
/// 回合开始序列＝turn.start.before → turn.start →（开始处理：结算 → 单位行动状态恢复〔2C 加性〕→ 抽牌）→ turn.start.after；
/// 结算＝资源管理器（槽 += 递增判定器值〔默认 1〕至上限 → 槽变化才发 <c>slot.changed</c> → 点数＝槽值）、
/// 行动状态恢复＝注入钩子（指挥管理器：行动方在场单位重置两 bool＋词条运行态清零；
/// 未接线＝跳过）、抽牌＝玩家管理器（缺省：先手第 1 回合不抽、其余照抽 1——K4：AllowFirstTurnDraw 配置可关闭该例外；抽时所发 card.drawn 位于 start 与 after 之间）；
/// 回合结束序列＝turn.end.before → turn.end → 切换当前方、回合数 +1（点数保留——X3：回合结束不清零、敌方回合内保留）。
/// 所有 turn 系列更新经总线 Emit（载荷＝{ 玩家, 回合数 }）；全部顺序 await 完结（调用返回即结算与更新完结）。
/// 终局门禁（后置项 B）：对局已结束＝EndTurn 拒绝（可空生命周期；缺省＝独立构造场景无门禁）。
/// </summary>
public sealed class TurnManager
{
    private readonly LogicEngine _engine;
    private readonly PlayerManager _playerManager;
    private readonly ResourceManager _resourceManager;
    private readonly MatchLifecycle? _lifecycle;
    private readonly bool _allowFirstTurnDraw; // K4：允许先手第 1 回合抽牌（缺省 false＝保持唯一抽牌例外）
    private Player? _currentPlayer;

    internal TurnManager(
        LogicEngine engine,
        PlayerManager playerManager,
        ResourceManager resourceManager,
        MatchLifecycle? lifecycle = null,
        bool allowFirstTurnDraw = false)
    {
        _engine = engine;
        _playerManager = playerManager;
        _resourceManager = resourceManager;
        _lifecycle = lifecycle;
        _allowFirstTurnDraw = allowFirstTurnDraw;
    }

    /// <summary>
    /// 单位行动状态恢复钩子（2C 加性；装配方注入——对局 Initialize 内接线指挥管理器的恢复逻辑；
    /// 未接线（独立构造场景）＝null＝跳过）。调用时点＝回合开始处理段（turn.start.after 前完成；
    /// 与段内其他步骤〔指挥点结算、抽牌〕无顺序依赖）。
    /// </summary>
    internal Action<Player>? ActionStateRefresher { get; set; }

    /// <summary>回合数（全局递增；回合 1＝先手首回合）。</summary>
    public int TurnNumber { get; private set; }

    /// <summary>当前行动方（尚未发起首个回合时访问＝抛错）。</summary>
    /// <exception cref="InvalidOperationException">回合尚未开始（当前行动方未定）。</exception>
    public Player CurrentPlayer => _currentPlayer ?? throw new InvalidOperationException("回合尚未开始（当前行动方未定）。");

    /// <summary>
    /// 初始化动作：置回合数＝1、当前行动方＝先手；发起先手回合开始序列（本轮抽牌按抽牌规则裁决——先手第 1 回合＝唯一抽牌例外、缺省不抽）。只允许一次。
    /// </summary>
    /// <exception cref="ArgumentNullException">firstPlayer 为 null。</exception>
    /// <exception cref="InvalidOperationException">先手回合已发起（重复发起被拒绝）。</exception>
    internal async Task StartFirstTurn(Player firstPlayer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(firstPlayer);
        if (_currentPlayer is not null)
        {
            throw new InvalidOperationException("先手回合已发起（重复发起被拒绝）。");
        }

        TurnNumber = 1;
        _currentPlayer = firstPlayer;
        await BeginTurnAsync(ct);
    }

    /// <summary>
    /// 结束当前方回合：turn.end.before → turn.end → 切换当前方、回合数 +1 → 回合开始序列（双人对局轮流；点数保留——X3）。
    /// </summary>
    /// <exception cref="InvalidOperationException">对局已结束（终局，不能推进回合）；或回合尚未开始，不能结束回合。</exception>
    public async Task EndTurn(CancellationToken ct = default)
    {
        if (_lifecycle is not null && !_lifecycle.IsActionAllowed)
        {
            throw new InvalidOperationException(_lifecycle.IsEnded
                ? "对局已结束（终局），不能推进回合。"
                : "对局不在进行相位（准备/换牌），不能推进回合。");
        }

        if (_currentPlayer is null)
        {
            throw new InvalidOperationException("回合尚未开始，不能结束回合。");
        }

        var endingPlayer = _currentPlayer;
        await EmitTurnAsync(GameUpdates.TurnEndBefore, endingPlayer, TurnNumber, ct);
        await EmitTurnAsync(GameUpdates.TurnEnd, endingPlayer, TurnNumber, ct);

        // 切换当前方、回合数 +1（静默；X3：不再清零点数——点数在回合结束与敌方回合内保留）
        _currentPlayer = _playerManager.Players[(endingPlayer.Index + 1) % 2];
        TurnNumber += 1;

        await BeginTurnAsync(ct);
    }

    /// <summary>回合开始序列：before → start →（开始处理：结算 → 单位行动状态恢复〔2C〕→ 抽牌）→ after（全部顺序 await 完结）。</summary>
    private async Task BeginTurnAsync(CancellationToken ct)
    {
        var current = CurrentPlayer;
        await EmitTurnAsync(GameUpdates.TurnStartBefore, current, TurnNumber, ct);
        await EmitTurnAsync(GameUpdates.TurnStart, current, TurnNumber, ct);

        // 回合开始处理：资源结算（槽 += 递增判定器值〔默认 1〕至上限 → 槽变化才发 slot.changed → 点数＝槽值）
        // → 单位行动状态恢复 → 抽牌（需抽时发 card.drawn）
        await _resourceManager.SettleAsync(current, ct);
        ActionStateRefresher?.Invoke(current);
        if (ShouldDrawForTurn(TurnNumber))
        {
            await _playerManager.DrawCard(current, ct);
        }

        await EmitTurnAsync(GameUpdates.TurnStartAfter, current, TurnNumber, ct);
    }

    /// <summary>
    /// 抽牌规则：先手第 1 回合（全局回合 1）不抽（唯一抽牌例外——<c>allowFirstTurnDraw</c>＝true 时关闭该例外、照抽）；
    /// 其余回合（含后手首回合与自回合 3 起双方各回合）照抽 1。
    /// </summary>
    private bool ShouldDrawForTurn(int turnNumber) => turnNumber != 1 || _allowFirstTurnDraw;

    /// <summary>发射 turn 系列更新（载荷＝{ 玩家, 回合数, Source }；回合信号无引发者——Source 恒 null）。</summary>
    private Task EmitTurnAsync(
        string updateType, Player player, int turnNumber, CancellationToken ct = default, Card? source = null)
        => _engine.Emit(
            updateType,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadPlayer] = player,
                [GameUpdates.PayloadTurnNumber] = turnNumber,
                [GameUpdates.PayloadSource] = source,
            },
            ct);
}
