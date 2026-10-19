using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Commanding;

// ─────────────────────────────────────────────────────────────────────────────
// 2C 指挥系统触发器视图（数据键约定＝属性名；实体类值统一为引用形态 Ref<Entity>）：
// ①指挥触发器视图（Card＝指挥单位引用、Player＝行动方、FlowBox＝流程箱）；
// ②单位移动触发器视图（Unit / OldPosition / NewPosition——均引用、与 unit.position.changed 载荷同口径）；
// ③单位攻击触发器视图（Attacker / Target——目标引用：单位目标＝单位引用、HQ 目标＝HQ 占位槽位引用）；
// ④造成攻击伤害触发器视图（Attacker / Target / Resolution——同一承载供伏击判定读取）。
// X1 加性：4 视图均增加「触发者卡牌」（TriggerCard）承载——引发该操作的效果宿主卡引用
// （非空＝效果引发且知来源卡；空＝玩家主动操作／未携带——两集合互斥穷尽）；
// 语义口径：主动指挥（含其嵌套调用）＝缺省空；效果引发＝调用方显式携带、随同一效果链传递（攻击→伤害链内一致）；
// 缺省调用（未携带）＝空（向后兼容：既有调用与断言不受影响）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 指挥触发器视图（2C；一次指挥流程编排的载荷引用面）：
/// <see cref="Card"/>＝被拖动单位引用（<c>Ref&lt;Entity&gt;</c>）、<see cref="Player"/>＝行动方（＝单位所有者＝当前回合方）、
/// <see cref="FlowBox"/>＝流程箱（结局出参载体；框架内部）。
/// 全可选（可从任意载荷绑定）；数据键＝属性名。
/// </summary>
[ContextView]
public class CommandTriggerView
{
    /// <summary>指挥单位引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向单位卡实例）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Card { get; set; }

    /// <summary>行动方（可选；值＝Player 对象引用）。</summary>
    [Optional]
    [Read]
    public virtual object? Player { get; set; }

    /// <summary>指挥流程箱（可选；值＝<see cref="CommandFlowBox"/>——结局出参承载）。</summary>
    [Optional]
    [Read]
    public virtual object? FlowBox { get; set; }

    /// <summary>
    /// 触发者卡牌引用（X1 加性；可选）：引发本次操作的效果宿主卡——非空＝效果引发（且知来源卡）；空＝玩家主动操作（非效果引发）。
    /// 值＝<see cref="Card"/> 对象引用（R9 起为归一化后的卡对象；判等以对象同一性为准）；
    /// 缺省调用（未携带）＝空（向后兼容）。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }
}

/// <summary>
/// 单位移动触发器视图（2C）：<see cref="Unit"/>＝移动单位引用；<see cref="OldPosition"/>＝原槽位引用；
/// <see cref="NewPosition"/>＝目标槽位引用（分派时选中的目标空槽）——均引用形态、与 unit.position.changed 载荷同口径。
/// 全可选；数据键＝属性名。
/// </summary>
[ContextView]
public class UnitMoveTriggerView
{
    /// <summary>移动单位引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向单位卡实例）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Unit { get; set; }

    /// <summary>原槽位引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向 Slot）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? OldPosition { get; set; }

    /// <summary>目标槽位引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向 Slot）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? NewPosition { get; set; }

    /// <summary>
    /// 触发者卡牌引用（X1 加性；可选）：引发本次操作的效果宿主卡——非空＝效果引发（且知来源卡）；空＝玩家主动操作（非效果引发）。
    /// 值＝<see cref="Card"/> 对象引用（R9 起归一化为卡对象；判等以对象同一性为准）；缺省调用（未携带）＝空（向后兼容）。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }
}

/// <summary>
/// 单位攻击触发器视图（2C）：<see cref="Attacker"/>＝攻击者引用；<see cref="Target"/>＝攻击目标引用
/// （单位目标＝单位引用；HQ 目标＝HQ 实体引用〔hq.Ref——W3-3 实体化，槽位引用不再作为 HQ 目标产出〕——
/// 两分支可判别、与候选引用面同源）。
/// 全可选；数据键＝属性名。
/// </summary>
[ContextView]
public class UnitAttackTriggerView
{
    /// <summary>攻击者引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向单位卡实例）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Attacker { get; set; }

    /// <summary>攻击目标引用（可选；值＝<c>Ref&lt;Entity&gt;</c>——单位引用或 HQ 实体引用）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Target { get; set; }

    /// <summary>
    /// 效果宿主卡（E1 加性可选面；与 <c>CardEventView.Host</c> 同一手法）：触发器所有者是带宿主的效果时由执行链注入，
    /// 供动态（csx）handler 取"施动卡"。
    /// </summary>
    [Optional]
    [Read]
    public virtual object? Host { get; set; }

    /// <summary>
    /// 触发者卡牌引用（X1 加性；可选）：引发本次攻击的效果宿主卡——非空＝效果引发（且知来源卡）；空＝玩家主动操作（非效果引发）。
    /// 值＝<see cref="Card"/> 对象引用（R9 起归一化为卡对象；判等以对象同一性为准）；缺省调用（未携带）＝空（向后兼容）。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（S11 加性；框架内部——入口只读结局，作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual Orc.Game.Actions.ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 「造成攻击伤害」触发器视图（2C；与对局同生的内置共享流程触发器——每次单位 vs 单位攻击结算必经其执行）：
/// <see cref="Attacker"/>＝攻击者引用；<see cref="Target"/>＝被攻击单位引用（同一承载——与攻击触发器数据同源，
/// 供伏击判定读取）；<see cref="Resolution"/>＝结算记录（伏击改写标志）。全可选；数据键＝属性名。
/// </summary>
[ContextView]
public class AttackDamageTriggerView
{
    /// <summary>攻击者引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向单位卡实例）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Attacker { get; set; }

    /// <summary>被攻击单位引用（可选；值＝<c>Ref&lt;Entity&gt;</c> 指向单位卡实例）。</summary>
    [Optional]
    [Read]
    public virtual Ref<Entity>? Target { get; set; }

    /// <summary>攻击伤害结算记录（可选；值＝<see cref="AttackDamageResolution"/>）。</summary>
    [Optional]
    [Read]
    public virtual object? Resolution { get; set; }

    /// <summary>
    /// 触发者卡牌引用（X1 加性；可选）：引发本次操作的效果宿主卡——非空＝效果引发（且知来源卡）；空＝玩家主动操作（非效果引发）。
    /// 值＝<see cref="Card"/> 对象引用（R9 起归一化为卡对象；判等以对象同一性为准）；缺省调用（未携带）＝空（向后兼容）；
    /// 同一效果引发的「攻击→伤害」链中触发者随链路传递（伤害链路读数＝攻击链读数）。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（S11 加性；框架内部——默认结算回填结局，作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual Orc.Game.Actions.ActionTargetResult? Result { get; set; }
}
