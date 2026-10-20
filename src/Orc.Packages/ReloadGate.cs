using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 重载门禁的判定请求（D22）：本次要从**首个变更层**起重装的资产集合 ＋ 要摘除的旧条目。
/// </summary>
/// <param name="Domain">首个变更所在的域（正式优先）。</param>
/// <param name="FirstChangedLayer">首个变更层（-1＝无变更；本次装载不会发生）。</param>
/// <param name="Reloaded">本次（重）装载的条目（层序）。</param>
/// <param name="Retired">本次摘除的旧条目（变化层起的旧段 ＋ 新列表中已不存在的条目）。</param>
public sealed record ReloadRequest(
    AssetDomain Domain,
    int FirstChangedLayer,
    IReadOnlyList<LoadListItem> Reloaded,
    IReadOnlyList<LoadListItem> Retired);

/// <summary>
/// 重载门禁的判定结果（D22）：**结构化**返回（允许／拒绝 ＋ 原因），加载器不抛错。
/// </summary>
/// <param name="Allowed">是否允许重载。</param>
/// <param name="Reason">拒绝原因（允许＝null）。</param>
public sealed record ReloadVerdict(bool Allowed, string? Reason)
{
    /// <summary>允许重载。</summary>
    public static ReloadVerdict Allow { get; } = new(true, null);

    /// <summary>拒绝重载（结构化原因）。</summary>
    /// <exception cref="ArgumentException">reason 为 null/空白。</exception>
    public static ReloadVerdict Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ReloadVerdict(false, reason);
    }
}

/// <summary>重载门禁逻辑（强类型面）：请求 → 判定。</summary>
/// <param name="request">判定请求。</param>
/// <returns>判定结果。</returns>
public delegate ReloadVerdict ReloadGateLogic(ReloadRequest request);

/// <summary>
/// **重载门禁判定器**（D22；判定器名固定为 <see cref="Name"/>）：单一判定器承担全部门禁清单
/// （是否正在等待 targeter／动作执行中／待决交互…），返回「允许 ＋ 原因」。
/// <para>
/// 沿用判定器机制：可被 wrapper 改写（改写的后果由宿主承担）；**被拒返回结构化结果、不抛错**。
/// 默认实现无条件放行——宿主（对局装配期）按需注册自定义实现。
/// </para>
/// </summary>
public class ReloadGateJudicator : Judicator<ReloadGateLogic>
{
    /// <summary>判定器名（受控常量；加载器按此名解析）。</summary>
    public const string Name = "reloadGate";

    /// <summary>默认门禁：无条件放行。</summary>
    public static ReloadGateJudicator AllowAll { get; } = new();

    /// <summary>门禁逻辑（默认放行；moding 生效时被栈顶替换逻辑取代）。</summary>
    /// <param name="request">判定请求。</param>
    protected virtual ReloadVerdict Evaluate(ReloadRequest request) => ReloadVerdict.Allow;

    /// <inheritdoc />
    protected sealed override Func<object[]?, object[]?> Adapt(ReloadGateLogic handler)
        => args => new object[] { handler(Unpack<ReloadRequest>(args, 0)) };

    /// <inheritdoc />
    public sealed override object[]? Invoke(object[]? args) => Adapt(Evaluate)(args);
}
