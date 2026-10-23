using Orc.Mcp.Contracts;
using Orc.Mcp.Pool;

namespace Orc.Mcp.Tools;

/// <summary>
/// 卡包集合工具面（S6）：<c>pool.list</c>（两模式、只读、角色不限）＋
/// <c>pool.register</c>/<c>pool.unregister</c>（**仅 sandbox**：<c>sandboxOnly</c> ＋ 写类门控 ＋ debug 角色 ＋ 回环来源）。
/// <para>
/// 名字边界（避免"pool 一词两义"）：<c>pool.*</c>＝**已注册包集合**（下一局开局会用到的输入）；
/// <c>sandbox.state.pool</c>/<c>pool.assembled</c>＝本局装配结果，两者不混。
/// </para>
/// <para>
/// 硬约束：handler **直接调 <see cref="IPoolControl"/>**，绝不绕 <c>AdminApi.InvokeAsync</c>
/// （会在消费者上下文内重入串行队列 ⇒ 自死锁）。
/// </para>
/// </summary>
internal static class PoolTools
{
    private const string EmptySchema = """
        {"type":"object","properties":{},"additionalProperties":false}
        """;

    private const string RegisterSchema = """
        {"type":"object","properties":{"directory":{"type":"string","description":"卡包目录（绝对路径）"}},"required":["directory"],"additionalProperties":false}
        """;

    private const string UnregisterSchema = """
        {"type":"object","properties":{"packageId":{"type":"string","description":"包 id（manifest.id）"},"version":{"type":"string","description":"包版本（内容指纹；须与 pool.list 当前快照一致）"}},"required":["packageId","version"],"additionalProperties":false}
        """;

    /// <summary>登记卡包集合工具组（未注入集合控制面时整组跳过）。</summary>
    public static void Register(McpService service)
    {
        if (service.PoolControl is not { } pool)
        {
            return;
        }

        var audience = AudienceOf(service);

        service.AddTool(
            "pool.list",
            "pool",
            "读取「下一局开局会用到的卡包集合」与试算结论（ok／逐包状态／冲突明细／装载摘要）。"
            + "**重操作**：完整走一遍资产注册与分层计划，不缓存——变更后查一次、开局前查一次即可，请勿高频轮询。"
            + "空集合是合法状态（ok=false ＋ EmptyPool），此时开局必然失败。",
            EmptySchema,
            (_, _) => Task.FromResult(ToResult(pool.Snapshot(audience), audience)),
            readOnly: true,
            requiredRole: ToolRole.None);

        service.AddTool(
            "pool.register",
            "pool",
            "把一个卡包目录加入本实例的已注册包集合（入参＝绝对目录路径；读该目录的 manifest.json 登记 id/version）。"
            + "只影响**下一局**开局（当前对局不受影响）；重算合并 lock 冲突，冲突则整体拒绝、集合不变。"
            + "本工具**不受可写根约束**（登记的是'要加载哪些包'，与可写根无关）。",
            RegisterSchema,
            async (call, ct) =>
            {
                var directory = ToolArgs.RequireString(call.Arguments, "directory");
                return ToResult(await pool.RegisterAsync(directory, audience, ct).ConfigureAwait(false), audience);
            },
            sandboxOnly: true);

        service.AddTool(
            "pool.unregister",
            "pool",
            "按 包 id ＋ 版本 从已注册包集合移除一个包（版本须与当前快照精确一致）。"
            + "只影响**下一局**开局（当前对局照常打完）；允许注销到空集合。"
            + "沙盒固定包（<sandboxRoot>/pack）不在集合内，注销不动它——清空请用 sandbox.reset。",
            UnregisterSchema,
            async (call, ct) =>
            {
                var packageId = ToolArgs.RequireString(call.Arguments, "packageId");
                var version = ToolArgs.RequireString(call.Arguments, "version");
                return ToResult(await pool.UnregisterAsync(packageId, version, audience, ct).ConfigureAwait(false), audience);
            },
            sandboxOnly: true);
    }

    /// <summary>受众＝**实例模式**（不按入口）：play 实例的 op 与工具都走白名单（R4）。</summary>
    private static PoolAudience AudienceOf(McpService service)
        => service.Options.Mode == McpServiceMode.Authoring ? PoolAudience.Sandbox : PoolAudience.RemotePlay;

    /// <summary>结果 → 工具外壳（载荷走唯一投影，管理面 op 用同一投影）。</summary>
    private static McpToolResult ToResult(PoolCallResult result, PoolAudience audience)
    {
        var payload = PoolProjection.Render(result, audience);
        var message = PoolProjection.Message(result, audience);

        return result.Ok
            ? McpToolResult.Success(message, details: payload)
            : McpToolResult.Failure(result.Code, message, payload);
    }
}
