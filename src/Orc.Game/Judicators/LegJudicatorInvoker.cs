using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;

namespace Orc.Game.Judicators;

/// <summary>
/// leg 资格判定器调用解包（K3；最小辅助——防解包散落）：
/// 统一签名调用（对局路径＝注册表条目句柄〔每次调用经统一解析点——moding 动态生效〕；独立构造路径＝内置实例主方法）
/// → 结构化结果解包（输出约定：<c>object[]{{LegEligibilityFailure?}}</c> 单元素；null＝通过）。
/// 消费方＝四调用点接入（CommandManager 可用性聚合／复验判定器装配注入）与测试。
/// 输出契约不符（null 数组/空/非枚举）＝fail-fast 族（明确失败、不静默容忍——与判定器输出契约口径一致）。
/// </summary>
internal static class LegJudicatorInvoker
{
    /// <summary>经注册表条目句柄调用并解包（对局路径；每次调用经统一解析点取栈顶——moding 动态生效）。</summary>
    public static LegEligibilityFailure? InvokeLeg(JudicatorRegistration registration, UnitCard unit, BoardPosition? position)
        => UnpackLeg(registration.Invoke(new object[] { unit, position! }), registration.Name);

    /// <summary>经内置实例主方法调用并解包（独立构造路径；无注册表、无改写通道）。</summary>
    public static LegEligibilityFailure? InvokeLeg(Judicator judicator, UnitCard unit, BoardPosition? position)
        => UnpackLeg(judicator.Invoke(new object[] { unit, position! }), judicator.GetType().Name);

    private static LegEligibilityFailure? UnpackLeg(object[]? raw, string source)
    {
        if (raw is { Length: 1 })
        {
            if (raw[0] is null)
            {
                return null; // 通过标记
            }

            if (raw[0] is LegEligibilityFailure failure)
            {
                return failure;
            }
        }

        throw new InvalidOperationException(
            $"leg 资格判定器 '{source}' 输出契约不符：期望 object[]{{LegEligibilityFailure?}}（1 元素）——fail-fast。");
    }
}
