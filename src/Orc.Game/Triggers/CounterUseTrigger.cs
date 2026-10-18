using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Judicators;

namespace Orc.Game.Triggers;

/// <summary>
/// 使用反制触发器（2B；反制唯一触发器；单入口状态翻转的验证承载）：
/// 合法性验证（J2：判定器承载）＝反制使用检查——按名绑定「反制使用检查判定器」（<see cref="JudicatorNames.CounterUse"/>，
/// 逐点专属；装配期解析——解析动作即校验、未注册名 fail-fast）。
/// 被判定对象＝所属反制卡（以 Ref 形态显式提供）；规则承载见 <see cref="CounterUseJudicator"/>：
/// ① 仅己方回合（该卡所属玩家是当前行动方；回合上下文经 <see cref="CardBase.TurnPlayerProvider"/> 读取）；
/// ② 未激活时另检查指挥点（已激活＝取消流程、无条件通过）。
/// 未激活 ⇒ 激活流程；已激活 ⇒ 取消流程（由默认事件按当前激活状态分派）。
/// 「仅己方回合」违反 / 激活点数不足＝拒绝（仅本次取消）；激活时的验证即唯一验证点（无复验概念）。
/// 与入口预检同一判定源（打出管理器前置判定经同一绑定判定器——moding 改写两侧同步生效）。
/// </summary>
public sealed class CounterUseTrigger : Trigger<CardTriggerView>
{
    private readonly CounterCard _card;

    /// <summary>创建使用反制触发器（绑定所属反制卡；按名绑定反制使用检查判定器）。
    /// 验证读取其归属、激活状态与花费组件。</summary>
    /// <param name="card">所属反制卡。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（按名解析——对局路径＝注册表解析；
    /// 缺省＝null＝独立构造路径——内置默认解析、构造即可用）。</param>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="KeyNotFoundException">解析未注册名（解析动作即校验——装配期 fail-fast）。</exception>
    public CounterUseTrigger(
        CounterCard card,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null)
        : base("使用反制的触发器")
    {
        ArgumentNullException.ThrowIfNull(card);
        _card = card;

        // 合法性验证（J2）：按名绑定逐点专属验证判定器——解析动作即校验（未注册名＝装配期 fail-fast）。
        var binding = validationJudicatorResolver is not null
            ? validationJudicatorResolver(JudicatorNames.CounterUse)
            : BuiltInValidationJudicators.Resolve(JudicatorNames.CounterUse);
        BindValidation(binding, _ => _card.Ref, _ => _card.Owner?.Index);
    }
}
