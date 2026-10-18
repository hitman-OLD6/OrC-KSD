namespace Orc.Core;

/// <summary>
/// 判定器（J1 机制骨架）：由游戏规则预定义的独立无状态服务（对局加载时加载）。
/// 形态：abstract 基类＋统一主方法 <see cref="Invoke"/>（同步）——调用侧经统一主方法；
/// 子类声明强类型 delegate＋覆写主方法做「拆包→强类型调用→组包」适配（强类型面面向实现/测试侧）。
/// 无状态：判定器不持有对局状态，输入经参数载荷；不得引入跨调用可变状态字段。
/// moding（逻辑替换）与全局生效（统一解析点）由注册表承载（<see cref="JudicatorRegistry"/>）——
/// 判定器条目自带等价句柄产物（注册所得＝解析所得、同一可寻址锚；「不得存在无法被 moding 的注册项」）。
/// </summary>
public abstract class Judicator : IUnifiedService
{
    /// <summary>
    /// 统一主方法（同步；调用侧唯一入口）：接收载荷（null＝空载荷）、返回结果（null＝无返回）。
    /// 子类覆写实现「拆包（载荷）→ 强类型调用 → 组包（返回）」适配；建议经 <see cref="Unpack{T}"/> 辅助拆包。
    /// </summary>
    /// <param name="args">载荷（调用方约定的参数序列；可为 null）。</param>
    /// <returns>结果（调用方约定的返回序列；可为 null）。</returns>
    public abstract object[]? Invoke(object[]? args);

    /// <summary>
    /// 拆包辅助（供子类适配使用）：取载荷第 <paramref name="index"/> 个参数并转换为 <typeparamref name="T"/>。
    /// 语义：参数缺失或类型不符＝明确失败（<see cref="ArgumentException"/>——参数契约不符归 fail-fast 族、不静默容忍）；
    /// <typeparamref name="T"/> 为引用类型/可空值类型时，null 载荷值合法（返回 null）。
    /// </summary>
    /// <param name="args">载荷（可为 null）。</param>
    /// <param name="index">参数下标（从 0 起）。</param>
    /// <returns>转换后的参数值。</returns>
    /// <exception cref="ArgumentException">载荷缺少该参数，或参数类型不符（无法转换为 <typeparamref name="T"/>）。</exception>
    protected static T Unpack<T>(object[]? args, int index)
    {
        if (args is null || index < 0 || index >= args.Length)
        {
            throw new ArgumentException(
                $"判定器载荷缺少第 {index} 个参数（期望 {typeof(T).Name}）——参数契约不符（fail-fast）。", nameof(args));
        }

        var value = args[index];
        if (value is T typed)
        {
            return typed;
        }

        if (value is null && default(T) is null)
        {
            return default!; // 引用类型/可空值类型：null 载荷值合法
        }

        throw new ArgumentException(
            $"判定器载荷第 {index} 个参数类型不符：期望 {typeof(T).Name}，实际 {(value is null ? "null" : value.GetType().Name)}——参数契约不符（fail-fast）。",
            nameof(args));
    }
}

/// <summary>
/// 判定器（强类型适配型；J1 机制骨架）：子类声明强类型 delegate（类型参数 <typeparamref name="TDelegate"/>）并实现
/// <see cref="Adapt"/>——强类型 delegate → 统一签名 的适配（拆包→强类型调用→组包）。
/// 用途：moding 双形态之「强类型封装便捷面」的适配来源——经强类型 delegate 注入时（注册表强类型注入面）
/// 由机制调用本适配、注入者无需手工拆包/组包；与统一签名底层等效替换（同一条目、同一栈、同一解析点——不形成第二解析点或分栈）。
/// 主方法覆写可复用本适配（如 Invoke ⇒ Adapt(默认逻辑)(载荷)），亦可自行书写——等价。
/// </summary>
/// <typeparam name="TDelegate">子类声明的强类型 delegate（该判定器的签名）。</typeparam>
public abstract class Judicator<TDelegate> : Judicator, IStrongTypedService<TDelegate>, IServiceSignature
    where TDelegate : Delegate
{
    private static readonly (Type[] Parameters, Type Return) Signature = ReadSignature();

    /// <summary>强类型 delegate 的参数类型（包装器严格校验用）。</summary>
    IReadOnlyList<Type> IServiceSignature.ParameterTypes => Signature.Parameters;

    /// <summary>强类型 delegate 的返回类型（包装器严格校验用）。</summary>
    Type IServiceSignature.ReturnType => Signature.Return;

    private static (Type[] Parameters, Type Return) ReadSignature()
    {
        var invoke = typeof(TDelegate).GetMethod("Invoke")
            ?? throw new InvalidOperationException(
                $"强类型判定器 delegate '{typeof(TDelegate).Name}' 缺少 Invoke 方法（签名不可读）。");

        var parameters = invoke.GetParameters();
        var types = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            types[i] = parameters[i].ParameterType;
        }

        return (types, invoke.ReturnType);
    }

    /// <summary>
    /// 把强类型 delegate（该判定器签名的替换逻辑）适配为统一签名（拆包→强类型调用→组包）；子类实现。
    /// 适配错误（如载荷与强类型参数不符）＝fail-fast 族：明确失败、不静默容忍。
    /// </summary>
    /// <param name="handler">强类型替换逻辑（子类声明的 delegate 类型）。</param>
    /// <returns>统一签名（同步）的等效逻辑。</returns>
    protected internal abstract Func<object[]?, object[]?> Adapt(TDelegate handler);

    /// <inheritdoc />
    Func<object[]?, object[]?> IStrongTypedService<TDelegate>.Adapt(TDelegate handler) => Adapt(handler);
}
