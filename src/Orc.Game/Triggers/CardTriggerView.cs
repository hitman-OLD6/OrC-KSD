using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Targeting;

namespace Orc.Game.Triggers;

/// <summary>
/// 卡牌触发器视图（游戏层触发器上下文）：承载一次卡牌侧触发（预打出 / 打出 / 使用反制 / 部署 / 加入 / 单位化等）的载荷引用。
/// 全可选（可从任意载荷绑定）；Card / Player 为载荷对象的弱类型引用（Card＝卡牌实例、Player＝玩家对象）。
/// 2A 用于触发器声明位与注册表示例；2B 起链内容填入（属性加性扩展：Position＝目标槽位、CaptureBox＝预打出捕获箱、Argument＝打出参数）。
/// 数据键约定＝属性名（与 GameUpdates.PayloadCard / PayloadPlayer / PayloadPosition 字面值一致）。
/// </summary>
[ContextView]
public class CardTriggerView
{
    /// <summary>卡牌引用（可选）。</summary>
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    /// <summary>玩家引用（可选）。</summary>
    [Optional]
    [Read]
    public virtual object? Player { get; set; }

    /// <summary>目标槽位（可选；2B 加性扩展）：部署 / 加入 / 单位化链的目标空槽位（Slot 对象）。</summary>
    [Optional]
    [Read]
    public virtual Slot? Position { get; set; }

    [Optional]
    [Read]
    public virtual FormationGap? FormationGap { get; set; }

    /// <summary>预打出捕获箱（可选；2B 加性扩展）：指令预打出 handler 经此提交捕获值（由打出链创建并随触发数据注入）。</summary>
    [Optional]
    [Read]
    public virtual CardCaptureBox? CaptureBox { get; set; }

    /// <summary>打出参数（可选；2B 加性扩展）：打出触发器承载的 object? 参数（单引用 / 列表 / targeter 皆可；由预打出捕获结果传入）。</summary>
    [Optional]
    [Read]
    public virtual object? Argument { get; set; }

    /// <summary>
    /// 选择器槽位声明（可选；S3 加性扩展）：本卡在预打出阶段可用的选择器槽位（由打出链自卡牌声明注入——
    /// 单位/指令各自声明；装配方预打出 handler 据此发起交互；槽位参数经交互请求描述交付前端）。
    /// 空＝本卡预打出无选择器需求（沿用默认无交互、零更新）。
    /// </summary>
    [Optional]
    [Read]
    public virtual IReadOnlyList<Selector>? SelectorSlots { get; set; }

    /// <summary>
    /// 事件引发者（R9 加性可选面）：信号载荷 <c>Source</c> 的引发卡（只读、可空）。
    /// 与 <see cref="TriggerCard"/> 分工：本面答「谁引发了这次事件」，后者答「当前 handler 的触发者」。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? Source { get; set; }

    /// <summary>
    /// 承受方（P4 加性可选面）：反制适配视图经此携带「被反制指令的施放目标」（可空）。
    /// 非反制路径＝null（不改变既有卡牌触发语义）。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? Target { get; set; }

    /// <summary>
    /// 当前 handler 的触发者卡牌（R9 加性可选面）：帧值（卡/效果注册项＝该卡/效果宿主卡；链上最后者胜），可空。
    /// </summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }
}
