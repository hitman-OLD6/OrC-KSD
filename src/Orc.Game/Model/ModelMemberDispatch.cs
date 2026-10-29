using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// **生产端发射接线**（b4／U3-U4）：把"成员只有声明、没有发射"的缺口逐点补上。
/// <para>
/// 现状与缺口（§21.3 七条失败的共同结构）：动作端／流程端的成员在预制体里**声明齐全**、审查图里**可寻址可挂载**，
/// 但真正的调用点仍是旧身份触发器的 <c>Trigger&lt;TView&gt;.InvokeAsync</c>——组件资产对这些成员的
/// <c>wraps</c>／<c>mounts</c> 因此**永远不被调用**（装载门只做静态规划，不改变调用路）。
/// </para>
/// <para>
/// 本类只做一件事：在**原调用点**把"该成员执行一次"补上，旧触发器**原样保留**（双面并存）——
/// 于是旧效果链与 v4 组件边同时可达，且不需要改任何资产。
/// </para>
/// <para>
/// **为什么不是 <c>LogicEngine.Emit</c>**：<c>Emit</c> 是**信号面**（写更新条目＋通知外部观察者＋要求目标成员是
/// 信号）；<c>unit-attack</c>／<c>action-budget</c>／<c>targeting</c> 是**流程成员**（<c>Process</c> 声明，
/// 不是信号）——对它们 <c>Emit</c> 会走"运行期注册"分支并因同名成员已存在而抛
/// <see cref="SignalNotRegisteredException"/>。成员执行面＝<see cref="OrchestrationRuntime.RunMemberAsync"/>。
/// </para>
/// <para>
/// **未装配编排运行时（独立构造／纯内核用例）＝无操作**：本类是**加性**接线，绝不让旧路径因模型缺失而不可用。
/// </para>
/// <para>
/// **数据面是副本（b4 收尾已给出贯通入口）**：<see cref="OrchestrationRuntime.RunMemberAsync"/> 按
/// <see cref="Context"/> 语义**拷贝**一份数据面，因此组件 handler 对 <c>view.&lt;可写字段&gt;</c> 的写回
/// **不会**回到本方法的调用方字典。这恰好与旧路径等价（旧直接调用的 handler 只做实体状态改写，不回写调用方字典），
/// 所以当前三个接线点都没有行为差异；但"组件就地改写本次数值并让调用方读回"这条能力在 b4 收尾前**不成立**
/// ——那需要把动作自带的自有边整体搬进成员（同一个 <see cref="Context"/> 贯通），届时本类退场。
/// **b4 收尾已落地**：<see cref="InvokeEngineMemberAsync(LogicEngine, string, Context, CancellationToken)"/>
/// 就是那条贯通入口，五个伤害端（<c>combat-damage</c>／<c>effect-damage</c>／<c>battle-damage</c>／
/// <c>damage-apply</c>／<c>attack-damage</c>）经它执行，<c>KeywordActionEndBridge</c> 随之删除。
/// </para>
/// <para>
/// **可见性**：`public` 只因调用点横跨程序集（<c>Orc.Foundation.Actions.AttackAction</c> 无法访问 <c>Orc.Game</c> 的
/// internal）。它**不是插件契约**：csx／资产作者一律不直接调它（组件资产经 <c>mounts</c>／<c>wraps</c> 声明，
/// 由本类所在的调用点转发），也不进契约冻结面（<see cref="Orc.Game.Tests"/> 的 <c>ContractSnapshot</c> 只覆盖
/// 门面类型）。
/// </para>
/// </summary>
public static class ModelMemberDispatch
{
    /// <summary>在**引擎实体**的某成员上执行一次（<c>engine#&lt;成员名&gt;</c>）；未接线／未声明＝无操作。</summary>
    /// <param name="engine">对局引擎（模型面来源）。</param>
    /// <param name="memberName">成员名（<see cref="EngineProcesses"/> 常量）。</param>
    /// <param name="data">数据面（构件同旧触发器载荷：视图字段名即键）。</param>
    /// <param name="ct">取消令牌。</param>
    public static Task InvokeEngineMemberAsync(
        LogicEngine engine, string memberName, IDictionary<string, object?> data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(data);

        return engine.Model is { } runtime
            ? InvokeAsync(runtime, engine, runtime.Engine, memberName, data, ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// 在**宿主实例**的某成员上执行一次（<c>host#&lt;成员名&gt;</c>，如卡牌上的 <c>targeting</c>）；未接线／无实体＝无操作。
    /// </summary>
    /// <param name="engine">对局引擎（模型面来源）。</param>
    /// <param name="host">宿主实体（卡牌的编排实体；null＝该宿主没有实体，如 HQ——保持旧面）。</param>
    /// <param name="memberName">成员名（<see cref="CardProcesses"/> 常量）。</param>
    /// <param name="data">数据面。</param>
    /// <param name="ct">取消令牌。</param>
    public static Task InvokeHostMemberAsync(
        LogicEngine engine, OrchestrationEntity? host, string memberName,
        IDictionary<string, object?> data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(data);

        return engine.Model is { } runtime && host is not null
            ? InvokeAsync(runtime, engine, host, memberName, data, ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// 在**引擎实体**的某成员上执行一次，**沿用调用方的 <see cref="Context"/>**（b4／U5：动作端接缝）。
    /// <para>
    /// 与 <see cref="InvokeEngineMemberAsync(LogicEngine, string, IDictionary{string, object?}, CancellationToken)"/>
    /// 的唯一区别是**不复制数据面**：组件成员对 <c>view.Amount</c>／<c>view.IsZeroed</c> 等可写字段的写回
    /// 落在调用方同一份数据面上，因此"减伤／免疫／封顶"这类**就地改写本次数值**的语义成立。
    /// 上一轮用「发射」替代转发时恰恰丢了这一点（副本＝写回丢失），也正因为如此旧的动作端桥不能直接删：
    /// 它的全部作用就是"让组件拿到同一个 Context"。
    /// </para>
    /// <para>
    /// **返回值＝成员面是否真的执行了**：未装配运行时／该成员未声明＝<c>false</c>，**调用方必须回落到旧触发器**
    /// ——否则"模型在但成员不在"（裁剪过的目录／自定义装配）会变成**静默不结算**，比抛错更难查。
    /// </para>
    /// </summary>
    /// <param name="engine">对局引擎（模型面来源，同时是本次执行帧的归属引擎）。</param>
    /// <param name="memberName">成员名（<see cref="EngineProcesses"/> 常量）。</param>
    /// <param name="ctx">调用方上下文（数据面共享）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成员面已执行＝<c>true</c>；未装配运行时／未声明该成员＝<c>false</c>（调用方回落旧触发器）。</returns>
    public static async Task<bool> InvokeEngineMemberAsync(
        LogicEngine engine, string memberName, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(ctx);

        if (engine.Model is not { } runtime || runtime.Engine.FindMember(memberName) is null)
        {
            return false;
        }

        await runtime.RunMemberAsync(runtime.Engine, memberName, ctx, engine, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 在成员上执行一次（**建帧路径**）：必须走带 <see cref="Context"/> 与引擎的重载——只有它会补建执行帧，
    /// 而 <c>ctx.SeamAsync</c>（成员具名缝，R1i 修订 2026-10-29）与 <c>ctx.Engine</c>／<c>Stop</c>／<c>Interrupt</c>
    /// 全部读执行帧：走无帧重载会让 handler 内的缝调用抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    private static Task InvokeAsync(
        OrchestrationRuntime runtime, LogicEngine engine, OrchestrationEntity entity, string memberName,
        IDictionary<string, object?> data, CancellationToken ct)
    {
        // 未声明的成员＝该面在本对局不存在（旧包／裁剪过的预制体）。静默跳过，不引入新的失败面。
        if (entity.FindMember(memberName) is null)
        {
            return Task.CompletedTask;
        }

        return runtime.RunMemberAsync(entity, memberName, new Context(data), engine, ct);
    }
}
