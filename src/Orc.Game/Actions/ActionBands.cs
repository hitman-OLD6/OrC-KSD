namespace Orc.Game.Actions;

/// <summary>
/// 底层动作**触发端**的统一区段（S2）：三段接缝由 band 值静态表达，不依赖注册顺序。
/// <para>
/// 作用域只到「底层动作」：入口分支（撤退/被控/被指挥、抽卡组顶/抽指定）是上层触发端，
/// **不强制**声明本枚举；入口只负责「自身前置 → 调用底层动作 → 自身收尾」。
/// </para>
/// <para>
/// 顺序：<see cref="Initiate"/>（发起前）→ <see cref="Announce"/>（宣告后）→
/// <see cref="CounterGuard"/>/<see cref="Counter"/>（反制）→ <see cref="Adjust"/>（落地前调整）→
/// <see cref="Commit"/>（默认转发到处理端，priority 100）。
/// </para>
/// </summary>
public enum ActionTriggerBands
{
    /// <summary>发起前：阻止发起、改代价、改目标、改来源。</summary>
    Initiate = 1,

    /// <summary>宣告后（触发端末）：反制、重定向、复制、禁止反制。P1 仅指令使用，其余动作为空段。</summary>
    Announce = 2,

    /// <summary>禁止反制段（早于 <see cref="Counter"/> 判定）。</summary>
    CounterGuard = 3,

    /// <summary>反制段（P1 非指令动作为空段）。</summary>
    Counter = 4,

    /// <summary>落地前调整段（非指令动作保留为空段）。</summary>
    Adjust = 5,

    /// <summary>默认转发到处理端（每个底层动作触发端**恰注册一次**，priority 100）。</summary>
    Commit = 6,
}

/// <summary>
/// 底层动作**处理端**的统一区段（S2）：改写 → 否决 → 落地。
/// <para>
/// <see cref="Veto"/> 命中即 <c>ctx.Stop()</c>：本级后续事件（同段其他 Veto 与 Apply）全部不执行——「否决是终局」。
/// 需要「否决但留痕」时，在 Veto 段用更低 priority 的 handler 于否决前完成记录。
/// </para>
/// </summary>
public enum ActionHandleBands
{
    /// <summary>结果改写段（数值/归属/处置指向）。</summary>
    Rewrite = 1,

    /// <summary>否决段（免死/免疫等改写为不落地）。命中即停止本级后续。</summary>
    Veto = 2,

    /// <summary>落地段（默认 handler 在此调用动作服务面；priority 100）。</summary>
    Apply = 3,
}
