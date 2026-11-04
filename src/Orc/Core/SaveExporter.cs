using Orc.Core.Model;

namespace Orc.Core;

/// <summary>
/// 存档导出器逻辑（强类型面；本会话 S3／裁决 5／Q5）：
/// 输入＝<paramref name="self"/>（被存档的实体——**只读使用**）＋ <paramref name="context"/>（只读存档上下文）；
/// 输出＝<see cref="SaveModing"/>（作者构造的存档 moding）或 null。
/// <para>
/// **求值语义，不是执行语义**（裁决 5——为什么不是 handler）：有返回值、只读、无副作用、单条；
/// <see cref="KernelHandler"/> 的三条特性（可见性／段序／挂载包装）对它全部无意义。
/// 失败语义（Q5）：抛异常或返回 null ⇒ **整个存档失败**（由存档入口承担），不跳过该实体。
/// </para>
/// </summary>
/// <param name="self">被存档实体（根实体或被嵌入的附件组件）。</param>
/// <param name="context">只读存档上下文（<see cref="SaveContext"/>：非空只读运行时窄接口＋框架状态读面）。</param>
/// <returns>存档 moding；null＝本导出器放弃（存档入口视其为**整体失败**，不是跳过）。</returns>
public delegate SaveModing? SaveExporterLogic(OrchestrationEntity self, SaveContext context);

/// <summary>
/// 存档导出器（本会话 S3／裁决 5；判定器子类形态）：承载「实体 → 存档 moding」求值的独立无状态服务。
/// 形态复用 <see cref="ValidationJudicator"/> 同族：<see cref="Judicator{TDelegate}"/> 提供
/// 强类型面＋<see cref="Judicator.Adapt"/> 统一面（<c>object[] → object[]</c>）＋ moding 栈式改写
/// （<c>RegisterReplaceWrapper</c> 改写后存档结果随之改变——经条目 <see cref="ServiceRegistration{J}.Invoke"/> 统一解析点）＋无状态。
/// <para>
/// 统一面载荷＝<c>[self, SaveContext]</c>、输出＝<c>[SaveModing]</c>（导出器返回 null ⇒ 统一面输出 null，
/// 存档入口据此整体失败）。**基类显式拒绝 null 载荷与契约不符**（D53）——明确异常，不是 NRE；
/// <c>self</c>／<c>context</c> 为 null 同样拒绝（导出器没有「无实体可存」的合法形态）。
/// </para>
/// </summary>
public abstract class SaveExporter : Judicator<SaveExporterLogic>
{
    /// <summary>
    /// 导出逻辑（子类实现；默认逻辑——moding 生效时被栈顶替换逻辑取代）。
    /// 契约：只读、无副作用（存档不得改写实体状态、不得触发信号）；返回 null＝放弃（入口整体失败）。
    /// </summary>
    /// <param name="self">被存档实体。</param>
    /// <param name="context">只读存档上下文。</param>
    /// <returns>存档 moding；null＝放弃。</returns>
    protected abstract SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context);

    /// <inheritdoc />
    protected internal sealed override Func<object[]?, object[]?> Adapt(SaveExporterLogic handler)
        => args =>
        {
            if (args is null)
            {
                throw new ArgumentException(
                    "存档导出器载荷为 null（期望 [self, SaveContext]）——参数契约不符（fail-fast），"
                    + "不是可容忍的空载荷。", nameof(args));
            }

            if (args.Length != 2)
            {
                throw new ArgumentException(
                    $"存档导出器载荷长度 {args.Length} ≠ 2（期望 [self, SaveContext]）——参数契约不符（fail-fast）。",
                    nameof(args));
            }

            if (args[0] is not OrchestrationEntity self)
            {
                throw new ArgumentException(
                    $"存档导出器载荷[0] 期望 OrchestrationEntity，实际 {(args[0]?.GetType().Name ?? "null")}"
                    + "——参数契约不符（fail-fast）。", nameof(args));
            }

            if (args[1] is not SaveContext context)
            {
                throw new ArgumentException(
                    $"存档导出器载荷[1] 期望 SaveContext，实际 {(args[1]?.GetType().Name ?? "null")}"
                    + "——参数契约不符（fail-fast）。", nameof(args));
            }

            var moding = handler(self, context);
            return moding is null ? null : new object[] { moding };
        };

    /// <inheritdoc />
    public sealed override object[]? Invoke(object[]? args) => Adapt(ExportLogic)(args);
}
