using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Judicators;

/// <summary>
/// 交战判定器调用解包（K1；最小辅助——防解包散落）：
/// 统一签名调用（对局路径＝注册表条目句柄〔每次调用经统一解析点——moding 动态生效〕；独立构造路径＝内置实例主方法）
/// → 布尔输出解包（载荷约定：各判定器输出＝<c>object[]{{bool}}</c> 单元素）。
/// 消费方＝组合器内子规则调用（C1→C2/C3/C4）与调用点接入（CommandManager/测试；K3 加性：move.frontline-enemy
/// 布尔输出解包亦经本辅助——调用解包逻辑全域单源）。
/// 输出契约不符（null/空/非 bool）＝fail-fast 族（明确失败、不静默容忍——与判定器输出契约口径一致）。
/// </summary>
internal static class CombatJudicatorInvoker
{
    /// <summary>
    /// 经注册表条目句柄调用并解包 bool（对局路径；每次调用经统一解析点取栈顶——moding 动态生效）。
    /// 词条规则覆盖已是各判定器上的 moding（L2：内置插件注册段声明），本调用点不再做特判。
    /// </summary>
    public static bool InvokeBool(JudicatorRegistration registration, params object[] args)
        => UnpackBool(registration.Invoke(args), registration.Name);

    /// <summary>经内置实例主方法调用并解包 bool（独立构造路径；无注册表、无改写通道）。</summary>
    public static bool InvokeBool(Judicator judicator, params object[] args)
        => UnpackBool(judicator.Invoke(args), judicator.GetType().Name);

    private static bool UnpackBool(object[]? raw, string source)
    {
        if (raw is { Length: 1 } && raw[0] is bool value)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"判定器 '{source}' 输出契约不符：期望 object[]{{bool}}（1 元素）——fail-fast。");
    }
}
