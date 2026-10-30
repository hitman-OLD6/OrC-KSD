using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Packages;
using Orc.Packages.Loading;

namespace Orc.Mcp.Pool;

/// <summary>
/// 卡包集合响应的**唯一投影**：管理面 op 与工具面共用同一个函数，保证逐字段一致。
/// <para>
/// <see cref="PoolAudience.RemotePlay"/> 走**字段白名单**（只列允许出现的键）——以后新增任何自由文本字段
/// 默认不会泄露，漏的是"少给"而不是"多给"。这不是"删掉已知的路径字段"：盘符/UNC/正反斜杠/引号/大小写
/// 的正则替换永远会漏，故 play 模式既不输出 <c>directory</c>，也不输出任何自由文本（<c>error</c>/<c>message</c>/<c>path[]</c>/<c>warnings</c>）。
/// </para>
/// <para>
/// <see cref="PoolAudience.Sandbox"/> 与 <see cref="PoolAudience.Admin"/> 给完整模型，两者之间不再分档。
/// </para>
/// </summary>
public static class PoolProjection
{
    /// <summary>沙盒固定包被剔除后仍可装配（本局输入里少了它）。</summary>
    public const string SandboxDegradedCode = "sandbox-degraded";

    /// <summary>沙盒包缺 sidecar/lock（开局会自动补齐；本次判定可能比实际悲观）。</summary>
    public const string SandboxMetadataUnsyncedCode = "sandbox-metadata-unsynced";

    /// <summary>把一次集合操作投影成响应载荷。</summary>
    /// <param name="result">操作结果（含唯一判定模型）。</param>
    /// <param name="audience">响应受众。</param>
    public static JsonObject Render(PoolCallResult result, PoolAudience audience)
    {
        ArgumentNullException.ThrowIfNull(result);

        var whitelist = audience == PoolAudience.RemotePlay;
        var payload = new JsonObject
        {
            ["ok"] = result.Ok,
            ["code"] = result.Code,
            ["model"] = RenderModel(result.Model, audience),
        };

        if (result.Added is not null || result.Removed is not null)
        {
            payload["change"] = RenderChange(result);
        }

        if (!whitelist)
        {
            payload["message"] = result.Message;
            if (result.Details is not null)
            {
                payload["details"] = result.Details.DeepClone();
            }
        }

        return payload;
    }

    /// <summary>
    /// 面向调用方的一句话说明：play 模式用**固定短语**（不搬运自由文本），其余受众原样给出。
    /// <para>drain 窗口的说明是两侧必须逐字一致的固定串，故不受短语表改写。</para>
    /// </summary>
    /// <param name="result">操作结果。</param>
    /// <param name="audience">响应受众。</param>
    public static string Message(PoolCallResult result, PoolAudience audience)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (audience != PoolAudience.RemotePlay)
        {
            return result.Message ?? FixedPhrase(result.Code);
        }

        return result.Code == McpCodes.RoomRotating
            ? result.Message ?? FixedPhrase(result.Code)
            : FixedPhrase(result.Code);
    }

    /// <summary>固定短语表（play 模式专用：天然不含路径）。</summary>
    /// <param name="code">受控结果码。</param>
    private static string FixedPhrase(string code) => code switch
    {
        McpCodes.Ok => "ok",
        McpCodes.MissingAssets => "卡包集合当前不可装配。",
        McpCodes.LockConflict => "卡包集合存在 lock 冲突（同一资产被锁定为不同版本）。",
        McpCodes.InvalidArguments => "参数非法。",
        McpCodes.VersionMismatch => "包版本与当前快照不符（请重新 pool.list）。",
        McpCodes.PathDenied => "路径不可读或无权限。",
        McpCodes.Busy => "服务繁忙，请重试。",
        McpCodes.RoomRotating => "房间正在轮换，请稍后重试。",
        McpCodes.Internal => "内部错误。",
        _ => "池操作失败。",
    };

    /// <summary>判定模型（两档：白名单／完整）。</summary>
    private static JsonObject RenderModel(AssemblyInspection model, PoolAudience audience)
    {
        ArgumentNullException.ThrowIfNull(model);

        var whitelist = audience == PoolAudience.RemotePlay;
        var node = new JsonObject
        {
            ["ok"] = model.Ok,
            ["packages"] = new JsonArray(model.Packages.Select(package => (JsonNode)RenderPackage(package, whitelist)).ToArray()),
            ["issues"] = new JsonArray(model.Issues.Select(issue => (JsonNode)RenderIssue(issue, whitelist)).ToArray()),
            ["plan"] = new JsonObject
            {
                ["definitionCount"] = model.Plan.DefinitionCount,
                ["formalLayers"] = model.Plan.FormalLayers,
                ["testLayers"] = model.Plan.TestLayers,
                ["itemCount"] = model.Plan.ItemCount,
                ["cardCount"] = model.Plan.CardCount,
            },
        };

        if (!whitelist)
        {
            node["warnings"] = new JsonArray(model.Warnings.Select(warning => (JsonNode)JsonValue.Create(warning)!).ToArray());
        }

        return node;
    }

    /// <summary>逐包行（白名单：不含 <c>directory</c>/<c>guid</c>/自由文本）。</summary>
    private static JsonObject RenderPackage(PackageInspection package, bool whitelist)
    {
        var node = new JsonObject
        {
            ["packageId"] = package.PackageId,
            ["version"] = package.Version,
            ["ok"] = package.Ok,
            ["willRemove"] = package.WillRemove,
            ["errorCode"] = package.ErrorCode,
        };

        if (!whitelist)
        {
            node["directory"] = package.Directory;
            node["guid"] = package.Guid;
            node["error"] = package.Error;
        }

        // 卡组诊断（R5/D42）：**显式进白名单**——这是「作者的包坏了」的告知，不是敏感信息；
        // 不加白名单的话，play 受众永远看不到自己卡组为什么不可用。
        if (package.DeckDiagnostics.Count > 0)
        {
            node["deckDiagnostics"] = new JsonArray(
                package.DeckDiagnostics.Select(diagnostic => (JsonNode)RenderDeckDiagnostic(diagnostic, whitelist)).ToArray());
        }

        return node;
    }

    /// <summary>
    /// 卡组诊断行：<c>code/appId/message/violations</c>（违规按 <c>kind</c> 分类、按 <c>cardId</c> 点名）。
    /// 完整的 file 段只在非白名单受众出现。
    /// </summary>
    private static JsonObject RenderDeckDiagnostic(AssetDiagnostic diagnostic, bool whitelist)
    {
        var node = new JsonObject
        {
            ["code"] = diagnostic.Code.ToString(),
            ["appId"] = diagnostic.AppId,
            ["message"] = diagnostic.Message,
        };

        if (diagnostic.Violations is { } violations)
        {
            node["violations"] = new JsonArray(violations
                .Select(violation => (JsonNode)new JsonObject
                {
                    ["kind"] = violation.Kind,
                    ["cardId"] = violation.CardId,
                    ["message"] = violation.Message,
                })
                .ToArray());
        }

        if (!whitelist)
        {
            node["file"] = diagnostic.File;
        }

        return node;
    }

    /// <summary>问题行（白名单：<c>{code, domain, appId, fatal, conflict?}</c>）。</summary>
    private static JsonObject RenderIssue(AssetLoadIssue issue, bool whitelist)
    {
        var node = new JsonObject
        {
            ["code"] = issue.Code.ToString(),
            ["domain"] = AssetDomainTokens.Token(issue.Domain),
            ["appId"] = issue.AppId,
            ["fatal"] = issue.IsFatal,
        };

        if (issue.Conflict is { } conflict)
        {
            node["conflict"] = RenderConflict(conflict);
        }

        if (!whitelist)
        {
            node["message"] = issue.Message;
            node["path"] = new JsonArray(issue.Path.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray());
        }

        return node;
    }

    /// <summary>lock 冲突明细（结构化字段来自 <see cref="AssetLoadIssue.Conflict"/>，不是字符串解析）。</summary>
    private static JsonObject RenderConflict(LockConflict conflict) => new()
    {
        ["kind"] = AssetKindTokens.Token(conflict.Kind),
        ["appId"] = conflict.AppId,
        ["firstPackageId"] = conflict.FirstPackageId,
        ["firstVersion"] = conflict.FirstVersion.ToString(),
        ["secondPackageId"] = conflict.SecondPackageId,
        ["secondVersion"] = conflict.SecondVersion.ToString(),
    };

    /// <summary>变更回执（added/removed 只给 id 与版本；对账移除另给 cause）。</summary>
    private static JsonObject RenderChange(PoolCallResult result)
    {
        var reconciled = result.RemovedByReconcile ?? Array.Empty<PoolEntry>();
        return new JsonObject
        {
            ["added"] = new JsonArray((result.Added ?? Array.Empty<PoolEntry>())
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["id"] = entry.PackageId,
                    ["version"] = entry.Version,
                })
                .ToArray()),
            ["removed"] = new JsonArray((result.Removed ?? Array.Empty<PoolEntry>())
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["id"] = entry.PackageId,
                    ["version"] = entry.Version,
                })
                .ToArray()),
            ["existed"] = result.Existed,
            ["removedByReconcile"] = new JsonObject
            {
                ["count"] = reconciled.Count,
                ["entries"] = new JsonArray(reconciled
                    .Select(entry => (JsonNode)new JsonObject
                    {
                        ["id"] = entry.PackageId,
                        ["version"] = entry.Version,
                        ["cause"] = entry.Cause,
                    })
                    .ToArray()),
            },
        };
    }
}
