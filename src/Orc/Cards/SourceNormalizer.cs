using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 来源归一化唯一入口（R9）：把写入边界收到的「来源」值统一转换为契约形态 <see cref="Card"/>。
/// 支持五类输入：<see cref="Card"/>（直通）、<see cref="Ref{T}"/>（存活且目标为 Card ⇒ 目标卡）、
/// 失效引用（⇒ null ＋ 留痕）、其它对象（⇒ null ＋ 留痕）、null（⇒ null，无来源＝合法空值）。
/// 失效/错型一律降级为 null、不抛；留痕经 <see cref="CardsLog"/> 写入当前执行流（无引擎时不留痕）。
/// 错形态（数据面仍出现与属性声明不符的值）不在此处兜底——视图 getter 按既有 <c>Unbox_Any</c> 语义抛
/// <see cref="InvalidCastException"/>。
/// </summary>
public static class SourceNormalizer
{
    /// <summary>
    /// 归一化为卡牌（失效/非 Card 目标 → null ＋ 隔离留痕；不抛）。
    /// </summary>
    /// <param name="value">写入边界收到的原始值（Card / Ref&lt;Entity&gt; / 其它 / null）。</param>
    /// <param name="engine">留痕目标引擎（null＝不留痕——无执行流可写）。</param>
    /// <param name="context">留痕定位串（如 <c>command.triggerCard</c>；仅进关键词，不参与判定）。</param>
    public static Card? ToCard(object? value, LogicEngine? engine = null, string? context = null)
    {
        switch (value)
        {
            case null:
                return null; // 无来源＝合法空值（不猜测、不回退）

            case Card card:
                return card;

            case Ref<Entity> reference when !reference.IsAlive:
                WriteTrace(engine, context, $"来源引用 '{reference.Name}' 已失效，降级为 null。");
                return null;

            case Ref<Entity> reference:
                if (reference.TargetForObservation is Card target)
                {
                    return target;
                }

                WriteTrace(
                    engine,
                    context,
                    $"来源引用 '{reference.Name}' 指向非 Card（{reference.TargetForObservation.GetType().Name}），降级为 null。");
                return null;

            default:
                WriteTrace(
                    engine,
                    context,
                    $"来源值类型 '{value.GetType().Name}' 不是 Card／Ref<Entity>，降级为 null。");
                return null;
        }
    }

    private static void WriteTrace(LogicEngine? engine, string? context, string message)
    {
        if (engine is null)
        {
            return; // 无引擎＝无流可写（独立构造路径；不引入第二静默通道）
        }

        var keywords = new List<string> { "source", "normalize" };
        if (!string.IsNullOrWhiteSpace(context))
        {
            keywords.Add(context);
        }

        CardsLog.Write(engine, "source", message, LogLevel.Warning, keywords);
    }
}
