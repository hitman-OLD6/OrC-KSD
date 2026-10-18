namespace Orc.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 资产服务（资产化 S2）：与「判定器」并列的数据/逻辑储存服务——承载静态资产（阵营/单位类型等
// 枚举值域及其属性/关系），由插件提供、装配期加载；提供列举/取条目/校验等查询与可改写裁决。
// 机制＝与判定器**共用** <see cref="ServiceRegistry{TService,THandle,TModingHandle}"/>（条目+moding栈+句柄），
// 但**名字域独立**（资产名非受控常量集——保住"判定器名＝受控常量集"不变量）。
// 调用契约：底层沿用无类型 <see cref="IUnifiedService.Invoke"/>(与判定器对称)；上层经
// <see cref="AssetServiceRegistration"/> 提供薄强类型包装（列举/取条目）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 静态资产条目：<c>{ id, 名称, 类型, 属性, 关系 }</c>。
/// 例：阵营＝属性{是否主国}＋关系{可盟列表}；单位类型＝关系{类型组}。
/// </summary>
/// <param name="Id">资产 id（如 <c>germany</c>/<c>infantry</c>；小写规范）。</param>
/// <param name="Name">显示名（如「德国」）。</param>
/// <param name="Category">资产种类（如 <c>faction</c>/<c>unitType</c>）。</param>
/// <param name="Attributes">标量属性（键→值）。</param>
/// <param name="Relations">多值关系（键→值集）。</param>
public sealed record AssetEntry(
    string Id,
    string Name,
    string Category,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Relations)
{
    /// <summary>取标量属性（缺失＝null）。</summary>
    public string? Attribute(string key)
        => Attributes.TryGetValue(key, out var value) ? value : null;

    /// <summary>取多值关系（缺失＝空）。</summary>
    public IReadOnlyList<string> Relation(string key)
        => Relations.TryGetValue(key, out var value) ? value : Array.Empty<string>();
}

/// <summary>资产服务（与判定器并列的数据/逻辑储存服务基类）。</summary>
public abstract class AssetService : IUnifiedService
{
    /// <summary>操作：列举某类资产 → 载荷 <c>[OpList, category]</c>；结果 <c>[IReadOnlyList&lt;AssetEntry&gt;]</c>。</summary>
    public const string OpList = "list";

    /// <summary>操作：取单个资产 → 载荷 <c>[OpGet, category, id]</c>；结果 <c>[AssetEntry]</c> 或 null。</summary>
    public const string OpGet = "get";

    /// <summary>操作：存在性 → 载荷 <c>[OpHas, category, id]</c>；结果 <c>[bool]</c>。</summary>
    public const string OpHas = "has";

    /// <summary>统一主方法（调用侧唯一入口）；子类按上述操作契约实现。</summary>
    public abstract object[]? Invoke(object[]? args);
}

/// <summary>
/// 强类型适配型资产服务：子类声明强类型 delegate 并实现 <see cref="Adapt"/>，
/// 使 moding 可经强类型 delegate 注入（由 <see cref="ServiceRegistry{TService,THandle,TModingHandle}"/> 适配）。
/// </summary>
/// <typeparam name="TDelegate">子类声明的强类型 delegate（该服务的签名）。</typeparam>
public abstract class AssetService<TDelegate> : AssetService, IStrongTypedService<TDelegate>
    where TDelegate : Delegate
{
    /// <summary>把强类型 delegate 适配为统一签名；适配错误＝fail-fast 族。</summary>
    protected internal abstract Func<object[]?, object[]?> Adapt(TDelegate handler);

    /// <inheritdoc />
    Func<object[]?, object[]?> IStrongTypedService<TDelegate>.Adapt(TDelegate handler) => Adapt(handler);
}

/// <summary>
/// 表式资产服务（默认实现）：内存持有某类/多类资产条目，按操作契约应答。
/// 用途：插件以数据（JSON）声明资产后经本类承载；亦作内置默认。moding 可整体替换其应答。
/// </summary>
public sealed class AssetTableService : AssetService
{
    private readonly Dictionary<string, Dictionary<string, AssetEntry>> _byCategory;

    private AssetTableService(Dictionary<string, Dictionary<string, AssetEntry>> byCategory)
    {
        _byCategory = byCategory;
    }

    /// <summary>由条目集构建（同 种类＋id 重复＝拒绝——注册即配置口径）。</summary>
    /// <exception cref="ArgumentException">id/category 为 null/空白。</exception>
    /// <exception cref="InvalidOperationException">同 种类＋id 重复。</exception>
    public static AssetTableService FromEntries(IEnumerable<AssetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new Dictionary<string, Dictionary<string, AssetEntry>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Category);

            if (!map.TryGetValue(entry.Category, out var bucket))
            {
                bucket = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
                map.Add(entry.Category, bucket);
            }

            if (!bucket.TryAdd(entry.Id, entry))
            {
                throw new InvalidOperationException(
                    $"资产 '{entry.Category}:{entry.Id}' 重复声明（拒绝——注册即配置）。");
            }
        }

        return new AssetTableService(map);
    }

    /// <summary>已声明的资产种类（稳定序＝字典序）。</summary>
    public IReadOnlyList<string> Categories
        => _byCategory.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public override object[]? Invoke(object[]? args)
    {
        if (args is null || args.Length == 0 || args[0] is not string op)
        {
            throw new ArgumentException("资产服务载荷缺少操作名（fail-fast）。", nameof(args));
        }

        return op switch
        {
            OpList => [List(Require(args, 1))],
            // 注：Get 缺失返回 null——null 为合法结果（数组元素运行时承载 null），`!` 仅抑制可空标注告警。
            OpGet => [Get(Require(args, 1), Require(args, 2))!],
            OpHas => [Has(Require(args, 1), Require(args, 2))],
            _ => throw new ArgumentException($"未知资产服务操作 '{op}'（fail-fast）。", nameof(args)),
        };
    }

    /// <summary>列举某类资产（未声明＝空）。</summary>
    public IReadOnlyList<AssetEntry> List(string category)
        => !_byCategory.TryGetValue(category, out var bucket)
            ? Array.Empty<AssetEntry>()
            : bucket.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();

    /// <summary>取单个资产（缺失＝null）。</summary>
    public AssetEntry? Get(string category, string id)
        => _byCategory.TryGetValue(category, out var bucket) && bucket.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>存在性。</summary>
    public bool Has(string category, string id) => Get(category, id) is not null;

    private static string Require(object[] args, int index)
    {
        if (index >= args.Length || args[index] is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"资产服务载荷缺少第 {index} 个参数（期望非空字符串）。", nameof(args));
        }

        return text;
    }
}

/// <summary>资产服务条目等价句柄（对局路径）：注册所得＝解析所得；含薄强类型包装面。</summary>
public sealed class AssetServiceRegistration : ServiceRegistration<AssetService>
{
    internal AssetServiceRegistration(ServiceEntry<AssetService> entry)
        : base(entry)
    {
    }

    /// <summary>列举某类资产（经统一解析点——moding 生效时＝替换逻辑的应答）。</summary>
    public IReadOnlyList<AssetEntry> List(string category)
        => Invoke([AssetService.OpList, category]) is { Length: 1 } raw && raw[0] is IReadOnlyList<AssetEntry> list
            ? list
            : Array.Empty<AssetEntry>();

    /// <summary>取单个资产（缺失＝null）。</summary>
    public AssetEntry? Get(string category, string id)
        => Invoke([AssetService.OpGet, category, id]) is { Length: 1 } raw ? raw[0] as AssetEntry : null;

    /// <summary>存在性。</summary>
    public bool Has(string category, string id)
        => Invoke([AssetService.OpHas, category, id]) is { Length: 1 } raw && raw[0] is true;
}

/// <summary>资产服务 moding 注册句柄（注销凭据）。</summary>
public sealed class AssetServiceWrapperRegistration : ServiceWrapperRegistration<AssetService>
{
    internal AssetServiceWrapperRegistration(ServiceEntry<AssetService> target, long wrapperSeq)
        : base(target, wrapperSeq)
    {
    }
}

/// <summary>
/// 资产服务注册表（对局级；与 <c>JudicatorRegistry</c> **并列、机制共用、名字域独立**）。
/// </summary>
public sealed class AssetServiceRegistry : ServiceRegistry<AssetService, AssetServiceRegistration, AssetServiceWrapperRegistration>
{
    /// <summary>注册一枚资产服务条目（重复注册拒绝）。</summary>
    public AssetServiceRegistration Register(string name, AssetService service) => RegisterCore(name, service);

    /// <summary>按名解析条目句柄（与注册所得为同一实例）。</summary>
    public AssetServiceRegistration Resolve(string name) => ResolveCore(name);

    /// <inheritdoc />
    protected override AssetServiceRegistration CreateHandle(ServiceEntry<AssetService> entry) => new(entry);

    /// <inheritdoc />
    protected override AssetServiceWrapperRegistration CreateWrapperHandle(ServiceEntry<AssetService> entry, long wrapperSeq)
        => new(entry, wrapperSeq);
}
