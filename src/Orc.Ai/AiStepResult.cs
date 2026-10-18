using Orc.Game.Ai;

namespace Orc.Ai;

/// <summary>AI 单步执行结果（一次 <see cref="AiPilot.StepAsync"/> ＝ 一个主动动作 ＋ 其待决应答）。</summary>
/// <param name="Performed">是否实际执行了动作（false＝本步无事可做/未轮到）。</param>
/// <param name="Kind">动作类别（<c>mulligan</c>/<c>play.unit</c>/<c>play.command</c>/<c>play.counter</c>/<c>command.attack</c>/<c>command.move</c>/<c>end-turn</c>/<c>idle</c>）。</param>
/// <param name="Outcome">动作结局（无动作＝null）。</param>
/// <param name="TurnEnded">本步是否结束了 AI 的回合。</param>
/// <param name="Note">说明（未行动的原因或失败摘要）。</param>
public sealed record AiStepResult(
    bool Performed,
    string Kind,
    AiActionOutcome? Outcome,
    bool TurnEnded,
    string? Note = null)
{
    /// <summary>无事可做/未轮到的结局。</summary>
    public static AiStepResult Idle(string note) => new(false, "idle", null, false, note);

    /// <summary>动作是否成功（完成/取消均算成功；失败才算失败）。</summary>
    public bool Succeeded => Outcome is null || !Outcome.Failed;
}
