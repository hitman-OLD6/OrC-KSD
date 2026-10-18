namespace Orc.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 统一服务机制（资产化 S2）：判定器与资产服务**共用**的"条目 + 包装器 + 句柄"机制基类。
// 背景：判定器（Judicator：「可被改写」的逻辑服务）与资产服务（AssetService：静态资产的数据/逻辑
// 储存服务）语义分立、**名字域独立**（判定器名＝受控常量集；资产名＝插件命名），但机制同构——
// 本文件把该机制抽出为泛型基类，二者各具其具体化实例，避免机制复制。
// 语义：注册即配置（装配期一次成型、重复注册拒绝、未注册引用 fail-fast）；
// 包装器＝管道式（前置 LIFO→核心或替换→后置 FIFO）；句柄即权限（无注册者校验）；
// 快照一致（一次调用＝一次快照）；机制静默（包装器面无读面、注册/注销/解析不留痕）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 统一服务面（判定器/资产服务共用）：载荷进、结果出（无类型统一签名）。
/// </summary>
public interface IUnifiedService
{
    /// <summary>统一主方法（调用侧唯一入口）；载荷/结果为调用方约定序列，可为 null。</summary>
    object[]? Invoke(object[]? args);
}

/// <summary>
/// 强类型替换包装器适配面：服务子类声明强类型 delegate 并实现本面时，replace wrapper 可经强类型 delegate 注入
/// （机制侧调用 <see cref="Adapt"/> 适配为统一签名后入栈——统一签名底层等效替换，同一条目同一组）。
/// </summary>
/// <typeparam name="TDelegate">服务子类声明的强类型 delegate（该服务的签名）。</typeparam>
public interface IStrongTypedService<in TDelegate> where TDelegate : Delegate
{
    /// <summary>把强类型 delegate 适配为统一签名；适配错误＝fail-fast 族。</summary>
    Func<object[]?, object[]?> Adapt(TDelegate handler);
}

/// <summary>
/// 服务签名描述（包装器严格校验用）：强类型判定器由 <see cref="Judicator{TDelegate}"/> 一处实现；
/// 非强类型服务回退为统一签名 <c>object[] → object[]</c>。
/// </summary>
public interface IServiceSignature
{
    /// <summary>目标核心逻辑的参数类型（按序、严格相等比较）。</summary>
    IReadOnlyList<Type> ParameterTypes { get; }

    /// <summary>目标核心逻辑的返回类型。</summary>
    Type ReturnType { get; }
}

/// <summary>
/// 服务条目：默认服务 ＋ 前置/后置/替换三组包装器。
/// 前置与后置按注册序追加（前置执行时反序取 LIFO、后置正序取 FIFO）；替换取尾项（最后注册者生效）。
/// 条目生命周期＝注册表生命周期（无条目注销通道——「注册即配置」）。
/// </summary>
/// <typeparam name="TService">服务类型。</typeparam>
public sealed class ServiceEntry<TService> where TService : class, IUnifiedService
{
    /// <summary>默认域键（无玩家语境；对外的 null 映射到此——Dictionary 不接受 null 键）。</summary>
    internal const int DefaultDomain = -1;

    private readonly Dictionary<int, WrapperBucket> _buckets = new();

    /// <summary>单域的包装器集合（三组；与「条目」分离，使同一服务可按玩家域并行持有）。</summary>
    private sealed class WrapperBucket
    {
        public readonly List<(Delegate Handler, long Seq)> Before = new();
        public readonly List<(Delegate Handler, long Seq)> After = new();
        public readonly List<(Delegate Handler, long Seq)> Replace = new();
    }

    internal ServiceEntry(object owner, string name, TService service)
    {
        Owner = owner;
        Name = name;
        Service = service;

        if (service is IServiceSignature signature)
        {
            SignatureParameterTypes = signature.ParameterTypes;
            SignatureReturnType = signature.ReturnType;
        }
        else
        {
            SignatureParameterTypes = new[] { typeof(object[]) };
            SignatureReturnType = typeof(object[]);
        }
    }

    /// <summary>所属注册表（跨注册表句柄判定用）。</summary>
    internal object Owner { get; }

    /// <summary>条目名（服务名）。</summary>
    public string Name { get; }

    /// <summary>默认服务实例。</summary>
    public TService Service { get; }

    /// <summary>目标核心逻辑的参数类型（强类型判定器经 <see cref="IServiceSignature"/> 提供）。</summary>
    internal IReadOnlyList<Type> SignatureParameterTypes { get; }

    /// <summary>目标核心逻辑的返回类型。</summary>
    internal Type SignatureReturnType { get; }

    /// <summary>取指定域的包装器集合（不存在＝创建）。</summary>
    private WrapperBucket Bucket(int domain)
    {
        if (!_buckets.TryGetValue(domain, out var bucket))
        {
            bucket = new WrapperBucket();
            _buckets.Add(domain, bucket);
        }

        return bucket;
    }

    /// <summary>取指定域的包装器集合（不存在＝null）。</summary>
    private WrapperBucket? ExistingBucket(int domain)
        => _buckets.TryGetValue(domain, out var bucket) ? bucket : null;

    /// <summary>指定域当前生效的替换包装器：最后注册且未注销者；无＝null（回退默认）。机制静默：无公共读面。</summary>
    private static Func<object[]?, object[]?>? LastReplace(WrapperBucket? bucket)
        => bucket is null || bucket.Replace.Count == 0 ? null : (Func<object[]?, object[]?>)bucket.Replace[^1].Handler;

    /// <summary>挂载一条包装器（统一签名；类别不符＝fail-fast）。domain 缺省＝默认域。</summary>
    internal void AddWrapper(WrapperPhase phase, Delegate handler, long seq, int domain = DefaultDomain)
    {
        var bucket = Bucket(domain);
        switch (phase)
        {
            case WrapperPhase.Before:
                if (handler is not Action<object[]?> before)
                {
                    throw new ArgumentException("前置包装器签名须为 Action<object[]?>。", nameof(handler));
                }

                bucket.Before.Add((before, seq));
                break;

            case WrapperPhase.After:
                if (handler is not Func<object[]?, object[]?, object[]?> after)
                {
                    throw new ArgumentException(
                        "后置包装器签名须为 Func<object[]?, object[]?, object[]?>。", nameof(handler));
                }

                bucket.After.Add((after, seq));
                break;

            case WrapperPhase.Replace:
                if (handler is not Func<object[]?, object[]?> replace)
                {
                    throw new ArgumentException("替换包装器签名须为 Func<object[]?, object[]?>。", nameof(handler));
                }

                bucket.Replace.Add((replace, seq));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "未定义的包装器类别。");
        }
    }

    /// <summary>按注册序移除一项（跨全部域查找）；不存在＝false（幂等）。</summary>
    internal bool RemoveWrapper(long seq)
    {
        foreach (var bucket in _buckets.Values)
        {
            if (RemoveFrom(bucket.Before, seq) || RemoveFrom(bucket.After, seq) || RemoveFrom(bucket.Replace, seq))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>清空全部包装器（条目撤销时调用：包装器随之失效、避免悬空）。</summary>
    internal void ClearWrappers() => _buckets.Clear();

    /// <summary>
    /// 统一解析点：一次调用＝一次快照。管道＝前置（LIFO）→ 替换或默认 → 后置（FIFO，逐层改写 result）。
    /// 异常对称性：不捕获、不包装——默认与包装器逻辑异常可见性一致。
    /// </summary>
    public object[]? Invoke(object[]? args) => Invoke(args, DefaultDomain);

    /// <summary>
    /// 统一解析点（指定域）：**全局层（默认域）→ 玩家层** 两层管道。
    /// 全局层＝插件/装配期注册（对所有玩家域生效）；玩家层＝卡效果按来源注入（仅该域生效）。
    /// 替换包装器：玩家层优先、否则全局层；后置：玩家层先、全局层后（全局为基线改写，玩家为叠加）。
    /// </summary>
    public object[]? Invoke(object[]? args, int domain)
    {
        var global = ExistingBucket(DefaultDomain);
        var local = domain == DefaultDomain ? null : ExistingBucket(domain);

        foreach (var wrapper in SnapshotBefore(global))
        {
            wrapper(args);
        }

        if (local is not null)
        {
            foreach (var wrapper in SnapshotBefore(local))
            {
                wrapper(args);
            }
        }

        var replace = LastReplace(local) ?? LastReplace(global);
        var result = replace is { } replacement ? replacement(args) : Service.Invoke(args);

        if (local is not null)
        {
            foreach (var wrapper in SnapshotAfter(local))
            {
                result = wrapper(args, result);
            }
        }

        foreach (var wrapper in SnapshotAfter(global))
        {
            result = wrapper(args, result);
        }

        return result;
    }

    private static Action<object[]?>[] SnapshotBefore(WrapperBucket? bucket)
    {
        if (bucket is null)
        {
            return Array.Empty<Action<object[]?>>();
        }

        var snapshot = bucket.Before.ToArray();
        Array.Reverse(snapshot);
        return Array.ConvertAll(snapshot, static e => (Action<object[]?>)e.Handler);
    }

    private static Func<object[]?, object[]?, object[]?>[] SnapshotAfter(WrapperBucket? bucket)
        => bucket is null
            ? Array.Empty<Func<object[]?, object[]?, object[]?>>()
            : Array.ConvertAll(bucket.After.ToArray(), static e => (Func<object[]?, object[]?, object[]?>)e.Handler);

    private static bool RemoveFrom(List<(Delegate Handler, long Seq)> list, long seq)
    {
        var index = list.FindIndex(e => e.Seq == seq);
        if (index < 0)
        {
            return false;
        }

        list.RemoveAt(index);
        return true;
    }
}

/// <summary>服务条目等价句柄基类（注册所得＝解析所得——同一可寻址锚）。仅由注册面创建；不含可变状态。</summary>
public class ServiceRegistration<TService> where TService : class, IUnifiedService
{
    internal ServiceRegistration(ServiceEntry<TService> entry)
    {
        Entry = entry;
    }

    /// <summary>服务名（可读标识）。</summary>
    public string Name => Entry.Name;

    /// <summary>条目内部件（机制内部使用）。</summary>
    internal ServiceEntry<TService> Entry { get; }

    /// <summary>调用（持有句柄的调用；经统一解析点——默认域）。</summary>
    public object[]? Invoke(object[]? args) => Entry.Invoke(args);

    /// <summary>调用（指定玩家域；<paramref name="playerIndex"/> 为 null＝默认域）。</summary>
    public object[]? Invoke(object[]? args, int? playerIndex)
        => Entry.Invoke(args, playerIndex ?? ServiceEntry<TService>.DefaultDomain);
}

/// <summary>包装器注册句柄基类（注销凭据；句柄即权限）。仅由注册面创建；不含可变状态。</summary>
public class ServiceWrapperRegistration<TService> where TService : class, IUnifiedService
{
    internal ServiceWrapperRegistration(ServiceEntry<TService> target, long wrapperSeq)
    {
        Target = target;
        WrapperSeq = wrapperSeq;
    }

    /// <summary>目标条目（机制内部使用）。</summary>
    internal ServiceEntry<TService> Target { get; }

    /// <summary>包装器注册序（注册表内唯一；机制内部使用）。</summary>
    internal long WrapperSeq { get; }
}

/// <summary>
/// 通用服务注册表基类（机制）：字符串名（ordinal）→ 条目（默认服务 ＋ 三组包装器）。
/// 语义见文件头；具体注册表（<c>JudicatorRegistry</c> / <c>AssetServiceRegistry</c>）经本类具体化，
/// 各自持有独立实例（名字域隔离），并各自声明其句柄类型。
/// </summary>
/// <typeparam name="TService">服务类型。</typeparam>
/// <typeparam name="THandle">条目句柄类型（派生自 <see cref="ServiceRegistration{TService}"/>）。</typeparam>
/// <typeparam name="TWrapperHandle">包装器句柄类型（派生自 <see cref="ServiceWrapperRegistration{TService}"/>）。</typeparam>
public abstract class ServiceRegistry<TService, THandle, TWrapperHandle>
    where TService : class, IUnifiedService
    where THandle : ServiceRegistration<TService>
    where TWrapperHandle : ServiceWrapperRegistration<TService>
{
    private readonly Dictionary<string, ServiceEntry<TService>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<ServiceEntry<TService>, THandle> _handles = new();
    private long _wrapperSeq;

    /// <summary>创建条目句柄（派生类提供；每条目恰一次——注册所得＝解析所得）。</summary>
    protected abstract THandle CreateHandle(ServiceEntry<TService> entry);

    /// <summary>创建包装器句柄（派生类提供）。</summary>
    protected abstract TWrapperHandle CreateWrapperHandle(ServiceEntry<TService> entry, long wrapperSeq);

    /// <summary>注册一枚服务条目（注册即配置——重复注册拒绝）。返回条目等价句柄。</summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">service 为 null。</exception>
    /// <exception cref="InvalidOperationException">同名条目重复注册。</exception>
    protected THandle RegisterCore(string name, TService service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(service);

        if (_entries.ContainsKey(name))
        {
            throw new InvalidOperationException($"服务名 '{name}' 已注册（重复注册被拒绝——注册即配置）。");
        }

        var entry = new ServiceEntry<TService>(this, name, service);
        var handle = CreateHandle(entry);
        _entries.Add(name, entry);
        _handles.Add(entry, handle);
        return handle;
    }

    /// <summary>按名解析条目句柄（与注册所得为同一实例）。</summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="KeyNotFoundException">未注册引用（配置错误——fail-fast）。</exception>
    protected THandle ResolveCore(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_entries.TryGetValue(name, out var entry))
        {
            throw new KeyNotFoundException($"服务 '{name}' 未注册（未注册引用＝配置错误——明确失败）。");
        }

        return _handles[entry];
    }

    /// <summary>条目是否存在（诊断面）。</summary>
    public bool Contains(string name)
        => !string.IsNullOrWhiteSpace(name) && _entries.ContainsKey(name);

    /// <summary>
    /// 已注册条目名（只读诊断/工具面；顺序不保证）。
    /// 注册面语义不变（仍为"注册即配置"——重复注册拒绝）。
    /// </summary>
    public IReadOnlyCollection<string> Names => _entries.Keys;

    /// <summary>按名调用（一次调用＝一次快照；执行管道）。<paramref name="playerIndex"/> 为 null＝默认域。</summary>
    public object[]? Invoke(string name, object[]? args, int? playerIndex = null)
        => ResolveCore(name).Invoke(args, playerIndex);

    /// <summary>
    /// 注册一条**前置包装器**（统一签名）：核心之前 LIFO 执行；可就地改写 <c>object[]</c> 元素。
    /// 目标句柄无效（不属本注册表）＝fail-fast。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 或 wrapper 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表。</exception>
    public TWrapperHandle RegisterBeforeWrapper(THandle target, Action<object[]?> wrapper, int? playerIndex = null)
        => RegisterWrapperCore(target, WrapperPhase.Before, wrapper, playerIndex);

    /// <summary>
    /// 注册一条**前置包装器**（强类型工厂产物）：严格校验参数类型与目标一致、返回 <c>void</c>；
    /// 不符＝挂载期 fail-fast。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表，或签名不符。</exception>
    public TWrapperHandle RegisterBeforeWrapper(THandle target, BeforeWrapper wrapper, int? playerIndex = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);
        ValidateBeforeSignature(target.Entry, wrapper);
        return RegisterWrapperCore(target, WrapperPhase.Before, wrapper.Handler, playerIndex);
    }

    /// <summary>
    /// 注册一条**后置包装器**（统一签名）：核心之后 FIFO 执行；接收「原参数＋核心结果」并返回新结果。
    /// 目标句柄无效（不属本注册表）＝fail-fast。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 或 wrapper 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表。</exception>
    public TWrapperHandle RegisterAfterWrapper(THandle target, Func<object[]?, object[]?, object[]?> wrapper, int? playerIndex = null)
        => RegisterWrapperCore(target, WrapperPhase.After, wrapper, playerIndex);

    /// <summary>
    /// 注册一条**后置包装器**（强类型工厂产物）：严格校验参数＝目标参数＋结果、返回＝目标返回；
    /// 不符＝挂载期 fail-fast。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表，或签名不符。</exception>
    public TWrapperHandle RegisterAfterWrapper(THandle target, AfterWrapper wrapper, int? playerIndex = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);
        ValidateAfterSignature(target.Entry, wrapper);
        return RegisterWrapperCore(target, WrapperPhase.After, wrapper.Handler, playerIndex);
    }

    /// <summary>
    /// 注册一条**替换包装器**（统一签名）：只替换核心段；最后一个未注销者生效。
    /// 目标句柄无效（不属本注册表）＝fail-fast。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 或 wrapper 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表。</exception>
    public TWrapperHandle RegisterReplaceWrapper(THandle target, Func<object[]?, object[]?> wrapper, int? playerIndex = null)
        => RegisterWrapperCore(target, WrapperPhase.Replace, wrapper, playerIndex);

    /// <summary>
    /// 注册一条**替换包装器**（强类型便捷面）：目标服务实现 <see cref="IStrongTypedService{TDelegate}"/> 时经其适配；
    /// 恰为统一签名 delegate＝直接采用；否则＝适配错误（fail-fast）。
    /// </summary>
    /// <exception cref="ArgumentNullException">target 或 wrapper 为 null。</exception>
    /// <exception cref="ArgumentException">句柄不属本注册表。</exception>
    /// <exception cref="InvalidOperationException">目标服务不支持该强类型 delegate（适配错误——fail-fast）。</exception>
    public TWrapperHandle RegisterReplaceWrapper<TDelegate>(THandle target, TDelegate wrapper, int? playerIndex = null)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(wrapper);

        if (!ReferenceEquals(target.Entry.Owner, this))
        {
            throw new ArgumentException("包装器目标句柄不属于本注册表（跨注册表挂载被拒绝）。", nameof(target));
        }

        Func<object[]?, object[]?> unified;
        if (target.Entry.Service is IStrongTypedService<TDelegate> typed)
        {
            unified = typed.Adapt(wrapper);
        }
        else if (wrapper is Func<object[]?, object[]?> direct)
        {
            unified = direct;
        }
        else
        {
            throw new InvalidOperationException(
                $"服务 '{target.Name}' 不支持强类型 delegate '{typeof(TDelegate).Name}' 的注入（适配错误——fail-fast）。");
        }

        var seq = _wrapperSeq++;
        target.Entry.AddWrapper(WrapperPhase.Replace, unified, seq, playerIndex ?? ServiceEntry<TService>.DefaultDomain);
        return CreateWrapperHandle(target.Entry, seq);
    }

    /// <summary>注销一条包装器（句柄即权限）。句柄不属本注册表／已注销＝幂等无操作、返回 false。</summary>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    public bool UnregisterWrapper(TWrapperHandle registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!ReferenceEquals(registration.Target.Owner, this))
        {
            return false;
        }

        return registration.Target.RemoveWrapper(registration.WrapperSeq);
    }

    private TWrapperHandle RegisterWrapperCore(THandle target, WrapperPhase phase, Delegate wrapper, int? playerIndex)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(wrapper);

        ValidateTarget(target);

        var seq = _wrapperSeq++;
        target.Entry.AddWrapper(phase, wrapper, seq, playerIndex ?? ServiceEntry<TService>.DefaultDomain);
        return CreateWrapperHandle(target.Entry, seq);
    }

    private void ValidateTarget(THandle target)
    {
        if (!ReferenceEquals(target.Entry.Owner, this))
        {
            throw new ArgumentException("包装器目标句柄不属于本注册表（跨注册表挂载被拒绝）。", nameof(target));
        }
    }

    private static void ValidateBeforeSignature(ServiceEntry<TService> entry, BeforeWrapper wrapper)
    {
        if (wrapper.ReturnType != typeof(void))
        {
            throw new ArgumentException("前置包装器返回类型必须为 void。", nameof(wrapper));
        }

        if (!SameSequence(wrapper.ParameterTypes, entry.SignatureParameterTypes))
        {
            throw new ArgumentException(
                $"前置包装器签名不符：目标 '{entry.Name}' 期望 ({(FormatTypes(entry.SignatureParameterTypes))})，"
                + $"实际 ({(FormatTypes(wrapper.ParameterTypes))})。", nameof(wrapper));
        }
    }

    private static void ValidateAfterSignature(ServiceEntry<TService> entry, AfterWrapper wrapper)
    {
        var expected = new Type[entry.SignatureParameterTypes.Count + 1];
        for (var i = 0; i < entry.SignatureParameterTypes.Count; i++)
        {
            expected[i] = entry.SignatureParameterTypes[i];
        }

        expected[^1] = entry.SignatureReturnType;

        if (!SameSequence(wrapper.ParameterTypes, expected) || wrapper.ReturnType != entry.SignatureReturnType)
        {
            throw new ArgumentException(
                $"后置包装器签名不符：目标 '{entry.Name}' 期望 ({(FormatTypes(expected))}) -> {entry.SignatureReturnType.Name}，"
                + $"实际 ({(FormatTypes(wrapper.ParameterTypes))}) -> {wrapper.ReturnType.Name}。", nameof(wrapper));
        }
    }

    private static bool SameSequence(IReadOnlyList<Type> left, IReadOnlyList<Type> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatTypes(IReadOnlyList<Type> types) => string.Join(", ", types);


}
