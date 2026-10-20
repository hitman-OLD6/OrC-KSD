using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 应用层 ID 文法（I6）：<c>pack/{包id}/{局部ID}</c>，**不含种类**（种类是并列字段，
/// 跨类型引用的一等键＝<c>(kind, appId)</c> 二元组；把种类压进字符串会破坏 UI 冻结的前缀契约）。
/// </summary>
public static class AssetAppId
{
    /// <summary>应用层 ID 前缀（UI 冻结契约）。</summary>
    public const string Prefix = "pack/";

    /// <summary>拼定应用层 ID。</summary>
    /// <exception cref="ArgumentException">packageId 或 localId 为 null/空白或含非法分隔。</exception>
    public static string Compose(string packageId, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        if (packageId.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"包 id '{packageId}' 不得含 '/'。", nameof(packageId));
        }

        return $"{Prefix}{packageId}/{localId}";
    }

    /// <summary>是否为应用层 ID 形态（<c>pack/{包id}/{局部ID}</c>）。</summary>
    public static bool IsAppId(string? value)
        => !string.IsNullOrWhiteSpace(value) && TrySplit(value, out _, out _);

    /// <summary>拆分应用层 ID（局部 ID 可含 <c>/</c>；非法＝false）。</summary>
    public static bool TrySplit(string? appId, out string packageId, out string localId)
    {
        packageId = "";
        localId = "";
        if (string.IsNullOrWhiteSpace(appId) || !appId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = appId[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        packageId = rest[..slash];
        localId = rest[(slash + 1)..];
        return true;
    }
}

/// <summary>应用层 ID 的解析结果。</summary>
public enum IdResolutionStatus
{
    /// <summary>唯一解析到一个资产版本。</summary>
    Resolved = 0,

    /// <summary>未注册。</summary>
    NotFound = 1,

    /// <summary>歧义：裸局部 ID 对应 **≥2 个不同应用层 ID**（同一应用层 ID 的多版本**不算**歧义）。</summary>
    Ambiguous = 2,
}

/// <summary>应用层 ID → 资产层 ID（GUID）的解析结果（结构化，不抛错）。</summary>
/// <param name="Status">结果。</param>
/// <param name="AppId">解析到的应用层 ID（非 Resolved＝null）。</param>
/// <param name="AssetGuid">解析到的资产版本 GUID（非 Resolved＝null）。</param>
/// <param name="Candidates">歧义候选（应用层 ID 升序；非 Ambiguous＝空）。</param>
public sealed record IdResolution(
    IdResolutionStatus Status,
    string? AppId,
    Guid? AssetGuid,
    IReadOnlyList<string> Candidates)
{
    /// <summary>成功解析的 GUID。</summary>
    /// <exception cref="InvalidOperationException">未解析成功。</exception>
    public Guid ResolvedGuid => AssetGuid
        ?? throw new InvalidOperationException($"应用层 ID '{AppId}' 未解析到资产版本（{Status}）。");
}

/// <summary>一条中间层映射（域 ＋ 种类 ＋ 应用层 ID → GUID）。</summary>
/// <param name="Domain">资产域。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="AppId">应用层 ID。</param>
/// <param name="AssetGuid">资产版本 GUID。</param>
public sealed record AssetBinding(AssetDomain Domain, AssetKind Kind, string AppId, Guid AssetGuid);

/// <summary>
/// **ID 中间层**（R14/D20；I2/D10）：应用层 ID → 资产层 GUID 的唯一转接实现。
/// <para>
/// **不持有 DTO**——只持有 <c>(域, 种类, 应用层 ID) → GUID</c> 映射；正式域／测试域各一张表，
/// 测试域同名映射**遮蔽**正式域（只影响合并视图，不污染正式域解析）。
/// </para>
/// <para>
/// 定版不在此层：调用方按 lock／遮蔽规则**先**向 <see cref="AssetRegistry.VersionsOf"/> 取候选定版，
/// 再把结果 <see cref="Bind"/> 进来。
/// </para>
/// </summary>
public sealed class AssetIdBridge
{
    private readonly Dictionary<BindingKey, Guid> _bindings = new();

    /// <summary>绑定一条"定版结果"（同域同键重绑＝覆盖，用于重新定版）。</summary>
    /// <exception cref="ArgumentNullException">appId 为 null。</exception>
    /// <exception cref="ArgumentException">appId 非应用层 ID 形态。</exception>
    public void Bind(AssetDomain domain, AssetKind kind, string appId, Guid assetGuid)
    {
        ArgumentNullException.ThrowIfNull(appId);
        if (!AssetAppId.IsAppId(appId))
        {
            throw new ArgumentException($"'{appId}' 不是应用层 ID（期望 pack/{{包id}}/{{局部ID}}）。", nameof(appId));
        }

        ArgumentOutOfRangeException.ThrowIfEqual(assetGuid, Guid.Empty, nameof(assetGuid));
        _bindings[new BindingKey(domain, kind, appId)] = assetGuid;
    }

    /// <summary>单域解析（**不做跨域遮蔽**：正式域永不引用测试定义）。</summary>
    /// <param name="domain">域。</param>
    /// <param name="kind">种类。</param>
    /// <param name="reference">应用层 ID 或裸局部 ID（裸 ID 须在本域内唯一）。</param>
    public IdResolution Resolve(AssetDomain domain, AssetKind kind, string reference)
        => ResolveCore(Table(domain, kind), reference);

    /// <summary>
    /// 合并视图解析（先装正式、再装测试 ⇒ **测试遮蔽同应用层 ID 的正式映射**）。
    /// </summary>
    /// <param name="kind">种类。</param>
    /// <param name="reference">应用层 ID 或裸局部 ID（裸 ID 须在合并视图内唯一）。</param>
    public IdResolution Resolve(AssetKind kind, string reference) => ResolveCore(Table(null, kind), reference);

    /// <summary>某域的一条映射（未绑定＝false）。</summary>
    public bool TryGet(AssetDomain domain, AssetKind kind, string appId, out Guid assetGuid)
        => _bindings.TryGetValue(new BindingKey(domain, kind, appId), out assetGuid);

    /// <summary>枚举已绑定映射（域为 null＝全部；稳定序：域、种类、应用层 ID）。</summary>
    public IReadOnlyList<AssetBinding> Bindings(AssetDomain? domain = null)
        => _bindings
            .Where(pair => domain is null || pair.Key.Domain == domain)
            .OrderBy(pair => pair.Key.Domain)
            .ThenBy(pair => pair.Key.Kind)
            .ThenBy(pair => pair.Key.AppId, StringComparer.Ordinal)
            .Select(pair => new AssetBinding(pair.Key.Domain, pair.Key.Kind, pair.Key.AppId, pair.Value))
            .ToArray();

    /// <summary>清空全部映射（新一次加载前重置）。</summary>
    public void Clear() => _bindings.Clear();

    /// <summary>
    /// 构建可见映射表：域为 null＝合并视图（**先正式后测试**，测试覆盖同键）；
    /// 指定域＝只该域（正式域解析永不看到测试映射）。
    /// </summary>
    private Dictionary<string, Guid> Table(AssetDomain? domain, AssetKind kind)
    {
        var table = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var pair in _bindings)
        {
            if (pair.Key.Kind == kind && (domain is null || pair.Key.Domain == domain) && pair.Key.Domain == AssetDomain.Formal)
            {
                table[pair.Key.AppId] = pair.Value;
            }
        }

        if (domain is null or AssetDomain.Test)
        {
            foreach (var pair in _bindings)
            {
                if (pair.Key.Kind == kind && pair.Key.Domain == AssetDomain.Test)
                {
                    table[pair.Key.AppId] = pair.Value;
                }
            }
        }

        return table;
    }

    private static IdResolution ResolveCore(Dictionary<string, Guid> table, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var trimmed = reference.Trim();
        if (trimmed.Length == 0)
        {
            return NotFound();
        }

        if (AssetAppId.IsAppId(trimmed))
        {
            return table.TryGetValue(trimmed, out var guid)
                ? new IdResolution(IdResolutionStatus.Resolved, trimmed, guid, Array.Empty<string>())
                : NotFound();
        }

        // 裸局部 ID（无包上下文入口）：候选去重键＝**应用层 ID 字符串**（不是 GUID）——
        // 同 ID 多版本不是歧义（交 lock／中间层定版），≥2 个不同应用层 ID 才是。
        var candidates = table.Keys
            .Where(appId => AssetAppId.TrySplit(appId, out _, out var localId)
                && string.Equals(localId, trimmed, StringComparison.Ordinal))
            .OrderBy(appId => appId, StringComparer.Ordinal)
            .ToArray();

        return candidates.Length switch
        {
            0 => NotFound(),
            1 => new IdResolution(IdResolutionStatus.Resolved, candidates[0], table[candidates[0]], candidates),
            _ => new IdResolution(IdResolutionStatus.Ambiguous, null, null, candidates),
        };
    }

    private static IdResolution NotFound()
        => new(IdResolutionStatus.NotFound, null, null, Array.Empty<string>());

    private readonly record struct BindingKey(AssetDomain Domain, AssetKind Kind, string AppId);
}
