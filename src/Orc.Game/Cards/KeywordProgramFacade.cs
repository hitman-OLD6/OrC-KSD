using Orc.Cards;

namespace Orc.Game.Cards;

/// <summary>
/// 词条程序运行时的**动作实现访问面**（P5b）：把默认动作所需、原本为 internal 的运行时操作收攏为具名公开操作。
/// <para>
/// 只暴露动作装配/结算真正需要的四项；不给插件万能句柄，也不放开词条程序的其余内部面。
/// </para>
/// </summary>
public static class KeywordProgramFacade
{
    /// <summary>进入「效果攻击」作用域（射程绕过与效果攻击标记的读取点）。</summary>
    public static IDisposable BeginEffectAttack(UnitCard card, bool bypassRange = false)
        => KeywordProgramRuntime.BeginEffectAttack(card, bypassRange);

    /// <summary>派发攻击事件（<c>unit.attack.before</c> / <c>unit.attack.after</c>）。</summary>
    public static Task AttackEvent(UnitCard unit, Card? target, bool before, CancellationToken ct)
        => KeywordProgramRuntime.AttackEvent(unit, target, before, ct);

    /// <summary>取走攻击者的临时词条标记（决定是否发 <c>keyword.attack.finished</c>）。</summary>
    public static bool TakeTemporary(Card card) => KeywordProgramRuntime.TakeTemporary(card);

    /// <summary>清理一个词条组件的临时修饰。</summary>
    public static Task ClearTemporary(ProgramKeywordComponent component, CancellationToken ct)
        => component.ClearTemporary(ct);
}
