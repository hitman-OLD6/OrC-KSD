using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Actions;

/// <summary>
/// 动作两端的**成员地址**查询（S13／U3；批次 7／§27 起只剩地址，不再有"按地址反查实例"的桥）。
/// <para>
/// U3 起 6 个 <c>I*Action</c> 契约的公开面只给**成员地址**（<c>engine#hand.add</c>…，R1d）——实现自己报地址，
/// 宿主不再维护"端 → 成员名"的映射表。但旧模型的执行面（<see cref="Orc.Core.Trigger{TView}"/> 上的
/// <c>Register</c>/<c>InvokeAsync</c>）与 band 挂接点（<c>KeywordEffects</c>／<c>CommandManager</c>）仍要按实例寻址，
/// 因此每端在 <see cref="ActionTriggerEnd"/> 里同时携带地址（新身份）与实例（旧身份），由本类按地址取回实例。
/// </para>
/// <para>
/// **过渡期唯一允许"按地址反查实例"的地方**：其它调用点一律先拿地址、再经本类取实例；U5 删掉旧类型后，
/// 本类与 <see cref="ActionTriggerEnd.Trigger"/> 一并删除（届时地址＝唯一身份，不需要解析）。
/// </para>
/// </summary>
public static class ActionEnds
{
    /// <summary>按成员地址取一端的登记项（未自报＝明确失败，不静默跳过）。</summary>
    /// <param name="ends">动作自报的端清单。</param>
    /// <param name="member">成员地址（<c>作用域#成员名</c>）。</param>
    /// <exception cref="ArgumentNullException">ends 为 null。</exception>
    /// <exception cref="InvalidOperationException">该端不在清单中。</exception>
    public static ActionTriggerEnd EndOf(IReadOnlyList<ActionTriggerEnd> ends, MemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(ends);

        foreach (var end in ends)
        {
            if (end.Member == member)
            {
                return end;
            }
        }

        throw new InvalidOperationException(
            $"动作端 '{member}' 不在动作自报的端清单中（U3：地址是登记身份，端必须自报地址）。");
    }

}
