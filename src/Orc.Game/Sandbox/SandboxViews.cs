using Orc.Cards;
using Orc.Core;
using Orc.Game.Players;

namespace Orc.Game.Sandbox;

// ─────────────────────────────────────────────────────────────────────────────
// 沙盒原语视图（六原语共用）。
// 共用 ⇒ 绑定期不做必填校验：参数缺失只能由 handler 首行兜住（写 Result.Reason =
// "invalid-arguments"，禁止抛异常——Trigger.InvokeAsync 会吞掉异常并记 Normal）。
// 键名＝属性名；载荷由 SandboxPrimitives 入口填充。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 沙盒原语统一视图：<see cref="DefinitionId"/>＝instantiate 的卡定义 id；
/// <see cref="Owner"/>＝归属／目标玩家；<see cref="Line"/>＋<see cref="Slot"/>＝落点线名与线内索引（0..4）；
/// <see cref="Card"/>＝被操作实体（unitize / move / remove）；
/// <see cref="Amount"/>＝数量（draw 张数 / points 目标值）；<see cref="Result"/>＝结局载体（入口创建、handler 回填）。
/// </summary>
[ContextView]
public class SandboxPrimitiveView
{
    /// <summary>卡定义 id（instantiate）。</summary>
    [Optional]
    [Read]
    public virtual string? DefinitionId { get; set; }

    /// <summary>玩家（归属／目标）。</summary>
    [Optional]
    [Read]
    public virtual Player? Owner { get; set; }

    /// <summary>去向判别（instantiate）：<c>hand</c> / <c>deck_top</c> / <c>support</c> / <c>frontline</c>。</summary>
    [Optional]
    [Read]
    public virtual string? Zone { get; set; }

    /// <summary>落点线名：<c>support</c> 或 <c>frontline</c>。</summary>
    [Optional]
    [Read]
    public virtual string? Line { get; set; }

    /// <summary>线内槽位索引（0..4；两条线各自 0–4）。</summary>
    [Optional]
    [Read]
    public virtual int? Slot { get; set; }

    /// <summary>被操作实体（单位卡／任意卡）。</summary>
    [Optional]
    [Read]
    public virtual Card? Card { get; set; }

    /// <summary>数量／目标值（语义随原语）。</summary>
    [Optional]
    [Read]
    public virtual int? Amount { get; set; }

    /// <summary>结局载体（框架内部；handler 回填，入口读取）。</summary>
    [Optional]
    [Read]
    public virtual SandboxPrimitiveResult? Result { get; set; }
}
