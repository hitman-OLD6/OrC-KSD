using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Judicators;

namespace Orc.Game.Triggers;

/// <summary>
/// 费用校验触发器（2B；单位/指令的预打出触发器与打出触发器共用）：
/// 合法性验证（J2：判定器承载）＝指挥点检查——按名绑定「费用检查判定器」（<see cref="JudicatorNames.CostCheck"/>，
/// 逐点专属；装配期解析——解析动作即校验、未注册名 fail-fast）。
/// 被判定对象＝所属卡牌（以 Ref 形态显式提供）；规则承载见 <see cref="CostCheckJudicator"/>：
/// 卡主已加载（Owner 非空）且点数 ≥ 该卡「有效部署费」（W3-2 G5：修饰机制链输出——无修饰时＝花费组件基准）
/// ＝合法；否则拒绝（「仅本次取消」语义）。
/// 预打出开始（外部调用 Validate / 触发时固定先调用）与打出段复验（触发时固定先调用）经同一判定源
/// （同一绑定判定器——同读有效值；moding 改写即其全部引用点生效）。
/// 契约：只读、无副作用（判定器契约沿用）。
/// </summary>
public sealed class CostCheckTrigger : Trigger<CardTriggerView>
{
    private readonly CardBase _card;

    /// <summary>创建费用校验触发器（绑定所属卡牌；按名绑定费用检查判定器）。
    /// 验证读取其 Owner 与花费组件。</summary>
    /// <param name="name">触发器名称（如「预打出触发器」/「打出触发器」）。</param>
    /// <param name="card">所属卡牌。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（按名解析——对局路径＝注册表解析；
    /// 缺省＝null＝独立构造路径——内置默认解析、构造即可用）。</param>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    /// <exception cref="KeyNotFoundException">解析未注册名（解析动作即校验——装配期 fail-fast）。</exception>
    public CostCheckTrigger(
        string name,
        CardBase card,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(card);
        _card = card;

        // 合法性验证（J2）：按名绑定逐点专属验证判定器——解析动作即校验（未注册名＝装配期 fail-fast）。
        var binding = validationJudicatorResolver is not null
            ? validationJudicatorResolver(JudicatorNames.CostCheck)
            : BuiltInValidationJudicators.Resolve(JudicatorNames.CostCheck);
        BindValidation(binding, _ => _card.Ref, _ => _card.Owner?.Index);
    }
}
