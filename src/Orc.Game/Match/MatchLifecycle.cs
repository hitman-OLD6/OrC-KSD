using Orc.Game.Players;

namespace Orc.Game;

/// <summary>
/// 对局生命周期（后置项 B 加性；状态与胜者的单一真源）：准备 → 换牌（mulligan） → 进行 → 结束（单向、不可逆）。
/// 换牌相位（A1 受控变更加性）：Initialize 完成装配与起手装载后置"换牌"——双方确认后置"进行"并执行先手第 1 回合；
/// 换牌期间动作入口一律拒绝（**相位门禁统一经 <see cref="IsActionAllowed"/> 判定**——避免多点各自扩展产生相位漏洞）。
/// 终局（HQ 生命≤0／认输触发）：状态置"结束"＋胜者与终局原因记录；其后所有游戏动作入口经本对象判拒
/// （对外面拒绝、零副作用、状态不推进），只读查询面保持可用。终局不发射任何更新（不发额外通知）。
/// 独立构造（脱离对局）＝管理器生命周期参数缺省 null——行为与既有装配一致（无门禁来源）。
/// </summary>
public sealed class MatchLifecycle
{
    /// <summary>当前状态（初始＝准备；MarkMulligan 后＝换牌；MarkInProgress 后＝进行；End 后＝结束）。</summary>
    public MatchState State { get; private set; } = MatchState.Preparing;

    /// <summary>胜者（仅终局后非 null：＝使对方 HQ 归零／对方认输的一方；非终局＝null；平局不处理——本批无同时归零路径）。</summary>
    public Player? Winner { get; private set; }

    /// <summary>终局原因（仅终局后非 null；与 <see cref="Winner"/> 并列记录——HQ 归零／认输）。</summary>
    public MatchEndReason? EndReason { get; private set; }

    /// <summary>是否已终局（动作入口门禁的读取面之一）。</summary>
    public bool IsEnded => State == MatchState.Ended;

    /// <summary>是否处于换牌（mulligan）相位。</summary>
    public bool IsMulligan => State == MatchState.Mulligan;

    /// <summary>
    /// 是否允许动作（**相位门禁统一判定面**）：仅"进行"相位允许出牌／指挥／结束回合／认输／目标发起；
    /// 准备／换牌／结束一律拒绝（各入口统一读本面）。
    /// </summary>
    public bool IsActionAllowed => State == MatchState.InProgress;

    /// <summary>置"换牌"态（对局 Initialize 完成装配与起手装载后调用；先手回合延后至双方确认）。</summary>
    internal void MarkMulligan() => State = MatchState.Mulligan;

    /// <summary>置"进行"态（无换牌装配＝Initialize 完成后调用；换牌路径＝双方确认后调用）。</summary>
    internal void MarkInProgress() => State = MatchState.InProgress;

    /// <summary>
    /// 置终局（记录胜者与原因；已终局＝幂等忽略，防御）。
    /// 调用时点＝HQ 伤害结算点／认输入口／管理员终止；当次结算收尾照常完成（内部步骤不经门禁）。
    /// <para>
    /// **相位不设限**：准备期与换牌期同样可以置终局——管理员终止（<c>Match.Abort</c>）在任何相位都必须生效，
    /// 否则"开局后尚未进入进行态"的房间无法被收回。是否允许某个**动作入口**发起终局，由该入口自己判定
    /// （例如 <c>Concede</c> 仍要求进行相位），本方法只管"置终局"这一事实。
    /// </para>
    /// </summary>
    /// <param name="winner">胜者（使对方 HQ 归零／对方认输的一方）；<c>null</c>＝无胜者（终止）。</param>
    /// <param name="reason">终局原因。</param>
    internal void End(Player? winner, MatchEndReason reason)
    {
        if (State == MatchState.Ended)
        {
            return; // 幂等：已终局＝忽略（防御）
        }

        Winner = winner;
        EndReason = reason;
        State = MatchState.Ended;
    }
}
