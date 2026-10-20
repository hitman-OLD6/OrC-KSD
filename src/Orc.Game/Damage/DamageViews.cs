using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;

namespace Orc.Game.Damage;

/// <summary>
/// 伤害**触发端**视图（R11；三端共用：<c>combat.damage</c> / <c>effect.damage</c> / <c>battle.damage</c>）：
/// 触发端只允许改写 <see cref="Amount"/>（或加条件），**不得扣数值**——扣数值只在处理端。
/// <para>
/// 注意 <see cref="Source"/> 是**流程视图**的「施动方」：与 R9 信号视图（<c>CardEventView.Source</c>＝事件引发者）
/// 同名不同义——这里问「这一下是谁打的」，那里问「这条事件是谁引发的」。
/// </para>
/// </summary>
[ContextView]
public class DamageEventView
{
    /// <summary>施动方（伤害来源；空＝无归属伤害——效果可无施动卡）。</summary>
    [Optional]
    [Read]
    public virtual Card? Source { get; set; }

    /// <summary>受方（单位或 HQ；必填）。</summary>
    [Read]
    public virtual Card Target { get; set; } = null!;

    /// <summary>伤害量（触发端唯一可改写项；改写后由处理端读取）。</summary>
    [Mutate]
    public virtual int Amount { get; set; }

    /// <summary>伤害种类（<see cref="DamageKinds"/> 三值之一；只读——种类由入口决定，不允许中途改）。</summary>
    [Read]
    public virtual string Kind { get; set; } = string.Empty;

    /// <summary>本次执行的结果载体（框架内部；作者不读写——处理端回填实扣量，入口读 <c>ActionResult</c>）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }

    /// <summary>
    /// 效果宿主（注入型 handler 的「哪张卡注册了我」；由 <c>injects</c> 框架侧注入，非信号载荷）。
    /// 供数据侧 handler 在共享触发器内定位自身宿主；非注入场景＝null。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? Host { get; set; }
}

/// <summary>
/// 伤害**处理端**视图（R11；<c>damage.apply</c>——唯一扣数值处）：在触发端改写之后执行，
/// 除 <see cref="Amount"/> 外还携带结果位 <see cref="IsZeroed"/>（免疫等置位；置位＝本次不扣）。
/// </summary>
[ContextView]
public class DamageApplyView
{
    /// <summary>施动方（同 <see cref="DamageEventView.Source"/> 口径）。</summary>
    [Optional]
    [Read]
    public virtual Card? Source { get; set; }

    /// <summary>受方（单位或 HQ；必填）。</summary>
    [Read]
    public virtual Card Target { get; set; } = null!;

    /// <summary>伤害量（已过触发端改写；处理端仍可改写，但只有默认 handler 会真正使用）。</summary>
    [Mutate]
    public virtual int Amount { get; set; }

    /// <summary>伤害种类（三值之一；与触发端载荷一致）。</summary>
    [Read]
    public virtual string Kind { get; set; } = string.Empty;

    /// <summary>结果位：置位＝本次归零（免疫），默认 handler 跳过扣减。</summary>
    [Mutate]
    public virtual bool IsZeroed { get; set; }

    /// <summary>本次执行的结果载体（框架内部；作者不读写——默认 handler 回填实扣量）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }

    /// <summary>
    /// 效果宿主（注入型 handler 的「哪张卡注册了我」；由 <c>injects</c> 框架侧注入，非信号载荷）。
    /// 供数据侧 handler 在共享触发器内定位自身宿主；非注入场景＝null。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? Host { get; set; }
}
