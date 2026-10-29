namespace Orc.Game.Model;

/// <summary>
/// 三套 **band 方案**的唯一真源（R1i 修订 2026-10-29）：段序＝旧 <c>ActionBands.cs</c> 枚举的数值序
/// （该文件随批次 5 删除，原文见 commit <c>0ba6ad3^</c>）。
/// <para>
/// 三层分工（requirements R1i 修订）：<c>wrap.phase</c> 管成员外围、<c>band</c> 管成员内部边次序、
/// <c>seam</c> 管 handler 内部的具名插入点。成员用 <see cref="Orc.Core.Model.MemberDefinition.Bands"/>
/// 选一套方案，用自己的 <see cref="Orc.Core.Model.MemberDefinition.Band"/> 给自有边定段；
/// 外来挂载边用 <see cref="Orc.Core.Model.MountEdge.Band"/> 定段（缺省＝与自有边同段）。
/// </para>
/// <para>
/// **段名只在这里出现一次**：<c>EngineProcesses.ActionEndPositions</c>、动作端的挂载点（死亡改写／指令反制）
/// 一律引用本类的常量，不写裸字符串——否则"段名改一处、次序静默错一处"。
/// </para>
/// </summary>
public static class BandSchemes
{
    // ---------- 段名常量（三套方案共用同一批名字，值的归属由方案决定） ----------

    /// <summary>触发端首段：发起前（阻止发起、改代价、改目标、改来源）。</summary>
    public const string Initiate = "Initiate";

    /// <summary>触发端：宣告后（指令用；反制、重定向、复制、禁止反制）。</summary>
    public const string Announce = "Announce";

    /// <summary>触发端：禁止反制段（早于 <see cref="Counter"/> 判定）。</summary>
    public const string CounterGuard = "CounterGuard";

    /// <summary>反制段（触发端与指令流各有一套同名段）。</summary>
    public const string Counter = "Counter";

    /// <summary>触发端：落地前调整段。</summary>
    public const string Adjust = "Adjust";

    /// <summary>触发端末段：默认转发到处理端。</summary>
    public const string Commit = "Commit";

    /// <summary>处理端首段：结果改写（数值／归属／处置指向）。</summary>
    public const string Rewrite = "Rewrite";

    /// <summary>处理端：否决段（免死／免疫等改写为不落地）。</summary>
    public const string Veto = "Veto";

    /// <summary>处理端末段：落地（默认 handler 在此调用动作服务面）。</summary>
    public const string Apply = "Apply";

    /// <summary>指令流：结算段。</summary>
    public const string Resolve = "Resolve";

    /// <summary>指令流末段：收尾（扣费／离手）。</summary>
    public const string Finalize = "Finalize";

    // ---------- 三套方案（有序；下标即段序，小者先执行） ----------

    /// <summary>动作**触发端**方案：<see cref="Initiate"/> → <see cref="Announce"/> → <see cref="CounterGuard"/>
    /// → <see cref="Counter"/> → <see cref="Adjust"/> → <see cref="Commit"/>。</summary>
    public static IReadOnlyList<string> ActionTrigger { get; } = new[]
    {
        Initiate, Announce, CounterGuard, Counter, Adjust, Commit,
    };

    /// <summary>动作**处理端**方案：<see cref="Rewrite"/> → <see cref="Veto"/> → <see cref="Apply"/>。</summary>
    public static IReadOnlyList<string> ActionHandle { get; } = new[]
    {
        Rewrite, Veto, Apply,
    };

    /// <summary>**指令流**方案：<see cref="Counter"/> → <see cref="Resolve"/> → <see cref="Finalize"/>。</summary>
    public static IReadOnlyList<string> OrderFlow { get; } = new[]
    {
        Counter, Resolve, Finalize,
    };
}
