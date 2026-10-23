using System.Text.Json.Nodes;
using Orc.Packages.Loading;

namespace Orc.Mcp.Pool;

/// <summary>
/// 卡包集合响应的受众（**脱敏按受众，不按入口**）：只有白名单式裁剪与完整模型两档。
/// <para>
/// <see cref="RemotePlay"/>＝play 实例（工具与 op 均走字段白名单，绝不出现绝对目录与自由文本）；
/// <see cref="Sandbox"/>／<see cref="Admin"/>＝沙盒实例与管理面（完整模型，两者之间不再分档）。
/// </para>
/// </summary>
public enum PoolAudience
{
    /// <summary>远程 play 面：字段白名单。</summary>
    RemotePlay,

    /// <summary>沙盒实例：完整模型。</summary>
    Sandbox,

    /// <summary>管理面：完整模型。</summary>
    Admin,
}

/// <summary>集合条目的引用形态（事件与变更回执用；**不含目录**）。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="Version">包版本（内容指纹；未知＝null）。</param>
/// <param name="Cause">移除原因（仅对账移除使用；<c>io</c>／<c>duplicate-key</c>／失效码）。</param>
public sealed record PoolEntry(string PackageId, string? Version = null, string? Cause = null);

/// <summary>
/// 一次卡包集合操作的结果（**薄信封**）：判定结论一律复用 <see cref="AssemblyInspection"/>（唯一模型），
/// 不在这里另造第二套判定 DTO。
/// </summary>
/// <param name="Ok">集合语义上的成败（<c>pool.list</c>＝现在开局会不会成功；写操作＝变更是否生效）。</param>
/// <param name="Code">受控结果码（见 <see cref="Contracts.McpCodes"/>）。</param>
/// <param name="Message">人读说明（自由文本，仅管理面/sandbox 受众可见）。</param>
/// <param name="Model">判定结论（唯一模型）。</param>
/// <param name="Added">本次新增的条目（幂等成功＝空）。</param>
/// <param name="Removed">本次移除的条目。</param>
/// <param name="RemovedByReconcile">本次真对账移除的条目（独立提交；变更被拒也不回滚）。</param>
/// <param name="Existed">重复注册命中已存在条目。</param>
/// <param name="Details">失败明细（冲突、当前版本、已在册目录等）。</param>
public sealed record PoolCallResult(
    bool Ok,
    string Code,
    string? Message,
    AssemblyInspection Model,
    IReadOnlyList<PoolEntry>? Added = null,
    IReadOnlyList<PoolEntry>? Removed = null,
    IReadOnlyList<PoolEntry>? RemovedByReconcile = null,
    bool Existed = false,
    JsonObject? Details = null);

/// <summary>
/// 卡包集合控制面（服务层向宿主暴露的**唯一**集合入口）。
/// <para>
/// 约束（对齐 <see cref="Room.IRoomControl"/> 的"活对象隔离点"模式）：入参与出参一律纯 DTO/JSON，
/// **不得出现** <c>Match</c>/<c>Player</c>/<c>Card</c>/<c>Ref&lt;T&gt;</c>。
/// </para>
/// <para>
/// 硬约束：工具回调**不得**绕到 <c>AdminApi.InvokeAsync</c>（会在消费者上下文内重入串行队列 ⇒ 自死锁）；
/// 管理面与工具面必须调用**同一个**实现。
/// </para>
/// </summary>
public interface IPoolControl
{
    /// <summary>读当前集合（纯读；不入队、不写集合、不写盘）。</summary>
    /// <param name="audience">响应受众。</param>
    PoolCallResult Snapshot(PoolAudience audience);

    /// <summary>注册一个卡包目录（绝对路径）。</summary>
    /// <param name="directory">包目录。</param>
    /// <param name="audience">响应受众。</param>
    /// <param name="ct">取消令牌。</param>
    Task<PoolCallResult> RegisterAsync(string directory, PoolAudience audience, CancellationToken ct = default);

    /// <summary>注销一个包（<paramref name="packageId"/> ＋ <paramref name="version"/> 精确匹配当前快照）。</summary>
    /// <param name="packageId">包 id。</param>
    /// <param name="version">包版本（内容指纹）。</param>
    /// <param name="audience">响应受众。</param>
    /// <param name="ct">取消令牌。</param>
    Task<PoolCallResult> UnregisterAsync(string packageId, string version, PoolAudience audience, CancellationToken ct = default);
}
