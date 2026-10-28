using Orc.Core;
using Orc.Core.Model;

namespace Orc.Tests;

/// <summary>
/// v4 内核测试脚手架：给一个"只有内核基础预制体"的引擎挂上模型运行时，注册成员并执行。
/// <para>
/// 旧测试用 <c>Trigger&lt;TView&gt;</c> 当执行载体（会话＝触发器实例）。新模型没有这个对象：
/// 会话＝**成员执行**（<c>engine#&lt;成员名&gt;</c>），承载逻辑＝该成员的自有边。
/// 因此凡"跑一段逻辑并观察 ctx／流／停止"的用例，统一经本类。
/// </para>
/// </summary>
internal static class KernelTestKit
{
    /// <summary>取（或补建）内核模型运行时。</summary>
    internal static OrchestrationRuntime Runtime(LogicEngine engine)
        => engine.Model ??= new OrchestrationRuntime(
            KernelSignals.CreateCatalog(), new HandlerRegistry());

    /// <summary>在引擎实体上注册成员（重名＝<see cref="PrefabLoadException"/>，与运行期注册一致）。</summary>
    internal static MemberDefinition Register(LogicEngine engine, MemberDefinition member)
        => Runtime(engine).RegisterMember(Runtime(engine).Engine, member);

    /// <summary>注册并执行一次引擎成员（数据面形态：新建 <see cref="Context"/>）。</summary>
    internal static Task<MemberExecutionResult> RunAsync(
        LogicEngine engine, MemberDefinition member, IDictionary<string, object?>? data = null,
        CancellationToken ct = default)
    {
        Register(engine, member);
        return engine.InvokeMemberAsync(member.Name, data, ct);
    }

    /// <summary>
    /// 在当前执行会话内**另起一条子流**执行成员（数据面形态：新上下文无帧 ⇒ 补建帧、流挂调用方流）。
    /// 这是 v4 里唯一仍会"成子流"的调用形态（<see cref="OrchestrationRuntime.RunMemberAsync(OrchestrationEntity, string, Context, LogicEngine?, CancellationToken)"/>）。
    /// </summary>
    internal static Task<MemberExecutionResult> RunNestedAsync(
        LogicEngine engine, string memberName, CancellationToken ct = default)
    {
        var runtime = Runtime(engine);
        return runtime.RunMemberAsync(runtime.Engine, memberName, new Context(), engine, ct);
    }

    /// <summary>单条自有边的成员（可指定视图／可见性／位次／合法性检验）。</summary>
    internal static MemberDefinition Member(
        string name,
        OwnedEdge edge,
        ViewSpec? view = null,
        MemberVisibility visibility = MemberVisibility.Callable,
        OwnEdgePosition position = OwnEdgePosition.First,
        IMemberValidation? validation = null)
        => new(
            name,
            view ?? ViewSpec.Empty,
            visibility,
            new[] { edge },
            validation: validation,
            ownEdgePosition: position);
}
