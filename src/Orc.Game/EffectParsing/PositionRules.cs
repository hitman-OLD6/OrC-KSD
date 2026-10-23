using Orc.Cards;
using Orc.Game.Board;

namespace Orc.Game.EffectParsing;

/// <summary>
/// 位置读口的 csx 可达包装（S4）：提供 csx 可引用的「卡是否在前线」布尔读口。
/// <para>存在理由（技术约束）：csx 沙箱为**文本级屏蔽**（<c>CSharpScriptSecurityOptions</c> 的
/// 「屏蔽片段」列表含 <c>Environment</c>，Ordinal 子串匹配）⇒ csx 中不得出现 <c>GameEnvironment</c> 字样，
/// 故「卡 → 环境 → <see cref="GameEnvironment.IsOnFrontLine"/>」链收敛于此面、csx 只引用本包装
/// （与 <see cref="RevealRules"/> 同族先例）。</para>
/// <para>单一真源：判定仍由既有位置读口 <see cref="GameEnvironment.IsOnFrontLine"/> 产出（本面不重建位置语义）。
/// 降级：null 卡／无环境（脱局）＝不满足（false）、不抛错（对齐位置读口既有口径）。</para>
/// </summary>
public static class PositionRules
{
    /// <summary>
    /// 卡是否位于「前线」（中立共享前线线；与视角无关）。未入场（手牌/卡组/已离场）／无环境＝false、不抛错；
    /// 判定随位置实时变化（随动由「事件 → 重跑」保证）。
    /// </summary>
    /// <exception cref="ArgumentNullException">无（null＝false 降级）。</exception>
    public static bool IsOnFrontLine(Card? card)
        => card is not null
           && GameEnvironment.ResolveFor(card) is { } environment
           && environment.IsOnFrontLine(card);
}
