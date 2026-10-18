namespace Orc.Core;

/// <summary>
/// 前置包装器（强类型工厂产物）：统一签名 <c>Action&lt;object[]?&gt;</c> ＋ 原始签名描述（供严格校验）。
/// </summary>
public readonly struct BeforeWrapper
{
    internal BeforeWrapper(Action<object[]?> handler, IReadOnlyList<Type> parameterTypes)
    {
        Handler = handler;
        ParameterTypes = parameterTypes;
    }

    /// <summary>统一签名执行体（可就地改写 args 元素）。</summary>
    public Action<object[]?> Handler { get; }

    /// <summary>强类型参数类型（按序；与目标签名严格比较）。</summary>
    public IReadOnlyList<Type> ParameterTypes { get; }

    /// <summary>前置包装器无业务返回值。</summary>
    public Type ReturnType => typeof(void);
}

/// <summary>
/// 后置包装器（强类型工厂产物）：统一签名 <c>Func&lt;object[]?, object[]?, object[]?&gt;</c> ＋ 原始签名描述。
/// </summary>
public readonly struct AfterWrapper
{
    internal AfterWrapper(
        Func<object[]?, object[]?, object[]?> handler,
        IReadOnlyList<Type> parameterTypes,
        Type returnType)
    {
        Handler = handler;
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    /// <summary>统一签名执行体（参数＋核心结果 → 新结果）。</summary>
    public Func<object[]?, object[]?, object[]?> Handler { get; }

    /// <summary>强类型参数类型（目标参数 + 结果类型；按序严格比较）。</summary>
    public IReadOnlyList<Type> ParameterTypes { get; }

    /// <summary>结果类型（须与目标返回类型相等）。</summary>
    public Type ReturnType { get; }
}

/// <summary>
/// 包装器强类型工厂：把 <c>Action&lt;T1..T4&gt;</c> / <c>Func&lt;T1..T4,TResult,TResult&gt;</c>
/// 适配为统一签名，并携带原始签名描述；判定器文件不改，调用方保留编译期类型约束。
/// 约定：<c>After</c> 的**最后一个泛型参数为结果类型**。
/// </summary>
public static class Wrapper
{
    // ---------- 前置：Action<T...> ----------

    /// <summary>0 参前置包装器。</summary>
    public static BeforeWrapper Before(Action wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new BeforeWrapper(_ => wrapper(), Array.Empty<Type>());
    }

    /// <summary>1 参前置包装器。</summary>
    public static BeforeWrapper Before<T1>(Action<T1> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new BeforeWrapper(
            args => wrapper(Unpack<T1>(args, 0)),
            new[] { typeof(T1) });
    }

    /// <summary>2 参前置包装器。</summary>
    public static BeforeWrapper Before<T1, T2>(Action<T1, T2> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new BeforeWrapper(
            args => wrapper(Unpack<T1>(args, 0), Unpack<T2>(args, 1)),
            new[] { typeof(T1), typeof(T2) });
    }

    /// <summary>3 参前置包装器。</summary>
    public static BeforeWrapper Before<T1, T2, T3>(Action<T1, T2, T3> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new BeforeWrapper(
            args => wrapper(Unpack<T1>(args, 0), Unpack<T2>(args, 1), Unpack<T3>(args, 2)),
            new[] { typeof(T1), typeof(T2), typeof(T3) });
    }

    /// <summary>4 参前置包装器。</summary>
    public static BeforeWrapper Before<T1, T2, T3, T4>(Action<T1, T2, T3, T4> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new BeforeWrapper(
            args => wrapper(Unpack<T1>(args, 0), Unpack<T2>(args, 1), Unpack<T3>(args, 2), Unpack<T4>(args, 3)),
            new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) });
    }

    // ---------- 后置：Func<..., TResult, TResult> ----------

    /// <summary>0 参后置包装器（仅结果）。</summary>
    public static AfterWrapper After<TResult>(Func<TResult, TResult> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new AfterWrapper(
            (_, result) => new object[] { wrapper(UnpackResult<TResult>(result))! },
            new[] { typeof(TResult) },
            typeof(TResult));
    }

    /// <summary>1 参后置包装器。</summary>
    public static AfterWrapper After<T1, TResult>(Func<T1, TResult, TResult> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new AfterWrapper(
            (args, result) => new object[] { wrapper(Unpack<T1>(args, 0), UnpackResult<TResult>(result))! },
            new[] { typeof(T1), typeof(TResult) },
            typeof(TResult));
    }

    /// <summary>2 参后置包装器。</summary>
    public static AfterWrapper After<T1, T2, TResult>(Func<T1, T2, TResult, TResult> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new AfterWrapper(
            (args, result) => new object[]
            {
                wrapper(Unpack<T1>(args, 0), Unpack<T2>(args, 1), UnpackResult<TResult>(result))!,
            },
            new[] { typeof(T1), typeof(T2), typeof(TResult) },
            typeof(TResult));
    }

    /// <summary>3 参后置包装器。</summary>
    public static AfterWrapper After<T1, T2, T3, TResult>(Func<T1, T2, T3, TResult, TResult> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new AfterWrapper(
            (args, result) => new object[]
            {
                wrapper(Unpack<T1>(args, 0), Unpack<T2>(args, 1), Unpack<T3>(args, 2), UnpackResult<TResult>(result))!,
            },
            new[] { typeof(T1), typeof(T2), typeof(T3), typeof(TResult) },
            typeof(TResult));
    }

    /// <summary>4 参后置包装器。</summary>
    public static AfterWrapper After<T1, T2, T3, T4, TResult>(Func<T1, T2, T3, T4, TResult, TResult> wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new AfterWrapper(
            (args, result) => new object[]
            {
                wrapper(
                    Unpack<T1>(args, 0), Unpack<T2>(args, 1), Unpack<T3>(args, 2), Unpack<T4>(args, 3),
                    UnpackResult<TResult>(result))!,
            },
            new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(TResult) },
            typeof(TResult));
    }

    private static T Unpack<T>(object[]? args, int index)
    {
        if (args is null || index < 0 || index >= args.Length)
        {
            throw new ArgumentException(
                $"包装器载荷缺少第 {index} 个参数（期望 {typeof(T).Name}）——参数契约不符（fail-fast）。", nameof(args));
        }

        var value = args[index];
        if (value is T typed)
        {
            return typed;
        }

        if (value is null && default(T) is null)
        {
            return default!;
        }

        throw new ArgumentException(
            $"包装器载荷第 {index} 个参数类型不符：期望 {typeof(T).Name}，实际 {(value is null ? "null" : value.GetType().Name)}——参数契约不符（fail-fast）。",
            nameof(args));
    }

    private static TResult UnpackResult<TResult>(object[]? result)
    {
        if (result is null || result.Length == 0)
        {
            return default!;
        }

        var value = result[0];
        if (value is TResult typed)
        {
            return typed;
        }

        if (value is null && default(TResult) is null)
        {
            return default!;
        }

        throw new ArgumentException(
            $"核心结果类型不符：期望 {typeof(TResult).Name}，实际 {(value is null ? "null" : value.GetType().Name)}——参数契约不符（fail-fast）。",
            nameof(result));
    }
}
