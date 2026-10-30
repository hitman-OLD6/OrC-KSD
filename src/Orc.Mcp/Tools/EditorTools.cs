using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Packages;
using Orc.Packages.Editing;

namespace Orc.Mcp.Tools;

/// <summary>
/// 后端编辑器工具组（S9；R11/R13）：<c>editor.*</c> 全组 <b>sandboxOnly</b>——
/// Authoring 档登记、Player 档清单中**不存在**（服务层「非沙盒不登记」双门控）。
/// <para>
/// **工具与进程内 API 等价**：每个处理器只做「入参解析 → 转调 <see cref="AssetEditor"/> 能力方法 → 投影为 JSON」，
/// 不复制任何版本／依赖／过滤判定（这些只在 <c>Orc.Packages.Editing</c> 存在一份）。
/// </para>
/// <para>
/// 命名：能力集（R9）写作 <c>pack.list</c>／<c>session.begin</c> 等；工具名沿用仓库既有「单点前缀 ＋ 点后 snake_case」，
/// 故一一映射为 <c>editor.pack_list</c>／<c>editor.session_begin</c>…（13 个，无一对多、无多对一）。
/// </para>
/// </summary>
internal static class EditorTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "editor.pack_list",
            "editor",
            "列出可编辑卡包（插件与 *.tmp 目录不列出）。对应能力 `pack.list`。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Guard(() => PackList(service))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "editor.session_begin",
            "editor",
            "开始编辑会话（同时只允许一个）：mode=edit 复制已有包到 <包目录>.tmp（packageRoot 缺省＝沙盒包）；"
            + "mode=create 新建包（packageId＋dependsOnPackages 必填，parentDirectory 缺省＝沙盒根）。对应能力 `session.begin`。",
            """
            {
              "type": "object",
              "properties": {
                "mode": { "type": "string", "enum": ["edit", "create"], "description": "edit＝编辑已有包；create＝新建包" },
                "packageRoot": { "type": "string", "description": "mode=edit 的包根绝对路径（缺省＝沙盒包 <沙盒根>/pack）" },
                "packageId": { "type": "string", "description": "mode=create 的新包 id" },
                "parentDirectory": { "type": "string", "description": "mode=create 的父目录绝对路径（缺省＝沙盒根）" },
                "name": { "type": "string", "description": "mode=create 的显示名（可选）" },
                "dependsOnPackages": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "mode=create 期望依赖的卡包 ID（R8：至少一项）"
                }
              },
              "required": ["mode"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => SessionBegin(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.session_end",
            "editor",
            "结束编辑会话：mode=save 转正（去测试段并递增、写两 lock、归档 versions/）；mode=discard 放弃（删除临时包）。"
            + "对应能力 `session.end`。",
            """
            {
              "type": "object",
              "properties": {
                "mode": { "type": "string", "enum": ["save", "discard"], "description": "save＝转正；discard＝放弃" }
              },
              "required": ["mode"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => SessionEnd(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.asset_list",
            "editor",
            "列出当前会话的资产（结构化过滤：kind／packageId／idPrefix／tags／minVersion／maxVersion）。"
            + "对应能力 `asset.list`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "packageId": { "type": "string" },
                "idPrefix": { "type": "string" },
                "tags": { "type": "array", "items": { "type": "string" }, "description": "全部命中（AND）" },
                "minVersion": { "type": "string", "description": "版本下界（含），如 1.2.0" },
                "maxVersion": { "type": "string", "description": "版本上界（含），如 2.0.0" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => AssetList(service, call))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "editor.asset_get",
            "editor",
            "读取一条资产（kind ＋ 局部 ID）。对应能力 `asset.get`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "id": { "type": "string" }
              },
              "required": ["kind", "id"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => AssetGet(service, call))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "editor.asset_commit",
            "editor",
            "提交资产：带 content＝内容通道（后端写盘并重解析）；不带 content＝文件指针通道（前端已直写，后端只读该文件）。"
            + "路径为包内相对路径（如 cards/x.card.json）。对应能力 `asset.commit`。",
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "临时包内相对路径（*.card.json／*.prefab.json）" },
                "content": { "type": "string", "description": "资产原文（缺省＝文件指针通道）" }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => AssetCommit(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.asset_create",
            "editor",
            "新建资产（版本从 1.0.0 起）：卡牌源文件缺失时后端补可解析骨架；"
            + "卡组（kind=deck）**必须先写源文件** `decks/<id>.deck.json`（含 id／name／deckConfig／cards），"
            + "后端不补骨架、也不接受 anchor／loader。对应能力 `asset.create`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "id": { "type": "string", "description": "包内局部 ID（手写，后端校验）" },
                "sourcePath": { "type": "string", "description": "源文件相对路径（缺省＝cards/<id>.card.json 或 effects/<id>.prefab.json）" },
                "anchor": { "type": "string", "description": "内含资产锚点（trigger/handler 必填）" },
                "loader": { "type": "string", "description": "加载器名（须为进程级已注册名）" }
              },
              "required": ["kind", "id"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => AssetCreate(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.asset_delete",
            "editor",
            "删除资产（当前版本移出声明与 lock；历史版本仍留在 versions/）。对应能力 `asset.delete`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "id": { "type": "string" }
              },
              "required": ["kind", "id"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => AssetDelete(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.version_set",
            "editor",
            "设定资产版本：缺省 version＝自动递增（尾段 +1）；手写须严格大于旧版（1..8 段纯数字）。"
            + "对应能力 `version.set`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "id": { "type": "string" },
                "version": { "type": "string", "description": "手写版本（缺省＝自动递增）" }
              },
              "required": ["kind", "id"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => VersionSet(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.dep_set",
            "editor",
            "设定资产依赖（测试 lock 随之重算）；裸局部 ID 归一到本包前缀。对应能力 `dep.set`。",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                "id": { "type": "string" },
                "dependencies": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "kind": { "type": "string", "enum": ["card", "effect", "trigger", "handler", "deck"] },
                      "appId": { "type": "string", "description": "应用层 ID 或裸局部 ID" },
                      "versionConstraint": { "type": "string", "description": "如 1.2.3／1.*.*／1.^.*" }
                    },
                    "required": ["kind", "appId", "versionConstraint"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["kind", "id", "dependencies"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Guard(() => DepSet(service, call))),
            sandboxOnly: true);

        service.AddTool(
            "editor.lock_preview",
            "editor",
            "预览 lock：转正将写出的正式 lock（预测）＋ 当前测试 lock（实况）。对应能力 `lock.preview`。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Guard(() => LockPreview(service))),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "editor.promote",
            "editor",
            "转正（等价于 session_end(mode=save)）。对应能力 `promote`。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Guard(() => Promote(service))),
            sandboxOnly: true);

        service.AddTool(
            "editor.reload",
            "editor",
            "重载：合并各包 lock ＋ 当前测试 lock，经**资产加载器**（AssetLoader）计算分层列表并返回结构化结果"
            + "（状态／首个变更层／条目数／问题明细／失败原因）。对应能力 `reload`。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Guard(() => Reload(service))),
            sandboxOnly: true);
    }

    // ── 处理器（每个＝一次能力调用 ＋ JSON 投影） ─────────────────────────────

    private static McpToolResult PackList(McpService service)
    {
        var packages = service.Editor.ListPackages();
        var items = new JsonArray();
        foreach (var package in packages)
        {
            items.Add(PackageJson(package));
        }

        return McpToolResult.Success($"{packages.Count} 个可编辑卡包。", details: new JsonObject
        {
            ["count"] = packages.Count,
            ["packages"] = items,
        });
    }

    private static McpToolResult SessionBegin(McpService service, McpToolCall call)
    {
        var mode = ToolArgs.RequireString(call.Arguments, "mode").Trim().ToLowerInvariant();
        EditorSessionRequest request;

        if (mode == "edit")
        {
            var packageRoot = ToolArgs.OptionalString(call.Arguments, "packageRoot");
            request = EditorSessionRequest.Edit(
                string.IsNullOrWhiteSpace(packageRoot) ? service.Sandbox.PackRoot : packageRoot);
        }
        else if (mode == "create")
        {
            var packageId = ToolArgs.RequireString(call.Arguments, "packageId");
            var parent = ToolArgs.OptionalString(call.Arguments, "parentDirectory");
            request = EditorSessionRequest.CreateNew(
                packageId,
                string.IsNullOrWhiteSpace(parent) ? service.Sandbox.Guard.Root : parent,
                ToolArgs.OptionalStringList(call.Arguments, "dependsOnPackages"),
                ToolArgs.OptionalString(call.Arguments, "name"));
        }
        else
        {
            return McpToolResult.Failure(
                McpCodes.InvalidArguments, $"mode '{mode}' 非法（期望 edit/create）。");
        }

        var session = service.Editor.BeginSession(request);
        return McpToolResult.Success($"编辑会话已开始（临时包：{session.TemporaryRoot}）。", details: SessionJson(session));
    }

    private static McpToolResult SessionEnd(McpService service, McpToolCall call)
    {
        var mode = ToolArgs.RequireString(call.Arguments, "mode").Trim().ToLowerInvariant();
        var endMode = mode switch
        {
            "save" => EditorSessionEndMode.Save,
            "discard" => EditorSessionEndMode.Discard,
            _ => (EditorSessionEndMode?)null,
        };

        if (endMode is null)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, $"mode '{mode}' 非法（期望 save/discard）。");
        }

        var result = service.Editor.EndSession(endMode.Value);
        var message = endMode == EditorSessionEndMode.Save
            ? $"会话已转正（包 '{result.PackageId}'，{result.Assets.Count} 资产）。"
            : $"会话已放弃（包 '{result.PackageId}' 的临时目录已删除）。";

        return McpToolResult.Success(message, details: PromotionJson(result));
    }

    private static McpToolResult AssetList(McpService service, McpToolCall call)
    {
        var query = ReadQuery(call.Arguments);
        var assets = service.Editor.ListAssets(query);
        var items = new JsonArray();
        foreach (var asset in assets)
        {
            items.Add(AssetJson(asset));
        }

        return McpToolResult.Success($"{assets.Count} 条资产。", details: new JsonObject
        {
            ["count"] = assets.Count,
            ["filter"] = FilterJson(call.Arguments),
            ["assets"] = items,
        });
    }

    private static JsonArray DeckViolationsJson(IReadOnlyList<Orc.Game.Decks.DeckViolation> violations)
        => new(violations
            .Select(violation => (JsonNode)new JsonObject
            {
                ["kind"] = violation.Kind,
                ["cardId"] = violation.CardId,
                ["message"] = violation.Message,
            })
            .ToArray());

    private static McpToolResult AssetGet(McpService service, McpToolCall call)
    {
        var asset = service.Editor.GetAsset(ReadKind(call.Arguments), ToolArgs.RequireString(call.Arguments, "id"));
        return McpToolResult.Success($"已读取资产 {asset.AppId}。", details: AssetJson(asset));
    }

    private static McpToolResult AssetCommit(McpService service, McpToolCall call)
    {
        var path = ToolArgs.RequireString(call.Arguments, "path");
        var content = ToolArgs.OptionalString(call.Arguments, "content");

        var result = content is null
            ? service.Editor.Commit(path)
            : service.Editor.Commit(path, content);

        if (result.Violations is { } violations)
        {
            // 非法卡组：写入被拒、文件保持原样、版本不递增——违规逐条交回（客户端按 Kind 分类）。
            return McpToolResult.Failure(
                McpCodes.DeckInvalid,
                $"卡组不合法，提交被拒（{violations.Count} 项）："
                + string.Join("；", violations.Select(violation => violation.Message)),
                new JsonObject
                {
                    ["path"] = path,
                    ["violations"] = DeckViolationsJson(violations),
                });
        }

        var asset = result.Asset!;
        return McpToolResult.Success(
            result.VersionBumped
                ? $"已提交 {path}（版本自动递增至 {asset.Version}）。"
                : $"已提交 {path}（版本保持 {asset.Version}）。",
            details: new JsonObject
            {
                ["asset"] = AssetJson(asset),
                ["versionBumped"] = result.VersionBumped,
                ["channel"] = content is null ? "path" : "content",
            });
    }

    private static McpToolResult AssetCreate(McpService service, McpToolCall call)
    {
        var asset = service.Editor.CreateAsset(
            ReadKind(call.Arguments),
            ToolArgs.RequireString(call.Arguments, "id"),
            ToolArgs.OptionalString(call.Arguments, "sourcePath"),
            ToolArgs.OptionalString(call.Arguments, "anchor"),
            ToolArgs.OptionalString(call.Arguments, "loader"));

        return McpToolResult.Success($"已新建资产 {asset.AppId}（版本 {asset.Version}）。", details: AssetJson(asset));
    }

    private static McpToolResult AssetDelete(McpService service, McpToolCall call)
    {
        var kind = ReadKind(call.Arguments);
        var localId = ToolArgs.RequireString(call.Arguments, "id");

        // 先取（应用层 ID 由后端组件给出），再删——工具不自行拼接 ID 形态。
        var asset = service.Editor.GetAsset(kind, localId);
        service.Editor.DeleteAsset(kind, localId);

        return McpToolResult.Success($"已删除资产 {asset.AppId}（当前版移出声明与 lock；历史版本仍在 versions/）。", details: new JsonObject
        {
            ["kind"] = AssetKindTokens.Token(kind),
            ["id"] = localId,
            ["appId"] = asset.AppId,
        });
    }

    private static McpToolResult VersionSet(McpService service, McpToolCall call)
    {
        var asset = service.Editor.SetVersion(
            ReadKind(call.Arguments),
            ToolArgs.RequireString(call.Arguments, "id"),
            ToolArgs.OptionalString(call.Arguments, "version"));

        return McpToolResult.Success($"版本已设定为 {asset.Version}（{asset.AppId}）。", details: AssetJson(asset));
    }

    private static McpToolResult DepSet(McpService service, McpToolCall call)
    {
        var asset = service.Editor.SetDependencies(
            ReadKind(call.Arguments),
            ToolArgs.RequireString(call.Arguments, "id"),
            ReadDependencies(call.Arguments));

        return McpToolResult.Success($"依赖已更新（{asset.AppId}）。", details: AssetJson(asset));
    }

    private static McpToolResult LockPreview(McpService service)
    {
        var preview = service.Editor.PreviewLocks();
        return McpToolResult.Success("lock 预览已生成。", details: new JsonObject
        {
            ["formal"] = LockJson(preview.Formal),
            ["test"] = LockJson(preview.Test),
        });
    }

    private static McpToolResult Promote(McpService service)
    {
        var result = service.Editor.Promote();
        return McpToolResult.Success(
            $"已转正（包 '{result.PackageId}'，{result.Assets.Count} 资产）。", details: PromotionJson(result));
    }

    private static McpToolResult Reload(McpService service)
    {
        var reload = service.Editor.Reload();
        var result = reload.Result;

        return McpToolResult.Success(
            $"重载计算完成（状态 {result.Status}）。",
            details: new JsonObject
            {
                ["configured"] = reload.Configured,
                ["status"] = result.Status.ToString(),
                ["ok"] = result.Ok,
                ["firstChangedLayer"] = result.FirstChangedLayer,
                ["itemCount"] = result.Plan?.Count ?? 0,
                ["formalLayers"] = result.Plan?.Formal.LayerCount ?? 0,
                ["testLayers"] = result.Plan?.Test.LayerCount ?? 0,
                ["failure"] = result.Failure,
                ["issues"] = IssuesJson(result.Issues),
            });
    }

    // ── 入参 ────────────────────────────────────────────────────────────────

    private static AssetKind ReadKind(JsonElement args)
    {
        var token = ToolArgs.RequireString(args, "kind");
        return AssetKindTokens.TryParse(token, out var kind)
            ? kind
            : throw new ToolArgumentException("kind", $"kind '{token}' 非法（期望 card/effect/trigger/handler/deck）。");
    }

    /// <summary>结构化过滤入参 → <see cref="EditorAssetQuery"/>（工具面无 lambda，跨进程不可传谓词）。</summary>
    private static EditorAssetQuery ReadQuery(JsonElement args)
        => new()
        {
            Kind = ToolArgs.OptionalString(args, "kind") is { Length: > 0 } kindToken
                ? (AssetKindTokens.TryParse(kindToken, out var kind)
                    ? kind
                    : throw new ToolArgumentException("kind", $"kind '{kindToken}' 非法（期望 card/effect/trigger/handler/deck）。"))
                : null,
            PackageId = ToolArgs.OptionalString(args, "packageId"),
            IdPrefix = ToolArgs.OptionalString(args, "idPrefix"),
            Tags = ToolArgs.OptionalStringList(args, "tags"),
            MinVersion = ReadVersion(args, "minVersion"),
            MaxVersion = ReadVersion(args, "maxVersion"),
        };

    private static AssetVersion? ReadVersion(JsonElement args, string name)
    {
        var text = ToolArgs.OptionalString(args, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return AssetVersion.TryParse(text, out var version)
            ? version
            : throw new ToolArgumentException(name, $"版本 '{text}' 非法（期望 1..8 段纯数字）。");
    }

    private static IReadOnlyList<AssetReference> ReadDependencies(JsonElement args)
    {
        if (!ToolArgs.TryGet(args, "dependencies", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException("dependencies", "缺少数组参数 'dependencies'。");
        }

        var result = new List<AssetReference>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ToolArgumentException("dependencies", $"dependencies[{index}] 须为对象。");
            }

            var kindToken = ToolArgs.RequireString(item, "kind");
            if (!AssetKindTokens.TryParse(kindToken, out var kind))
            {
                throw new ToolArgumentException("kind", $"dependencies[{index}].kind '{kindToken}' 非法。");
            }

            result.Add(new AssetReference(
                kind,
                ToolArgs.RequireString(item, "appId"),
                ToolArgs.RequireString(item, "versionConstraint")));
            index++;
        }

        return result;
    }

    // ── 投影 ────────────────────────────────────────────────────────────────

    private static JsonObject PackageJson(EditablePackageInfo package)
        => new()
        {
            ["packageId"] = package.PackageId,
            ["name"] = package.Name,
            ["rootDirectory"] = package.RootDirectory,
            ["version"] = package.Version.Text,
            ["hasTemporaryPackage"] = package.HasTemporaryPackage,
        };

    private static JsonObject SessionJson(EditorSessionInfo session)
        => new()
        {
            ["packageId"] = session.PackageId,
            ["packageRoot"] = session.PackageRoot,
            ["temporaryRoot"] = session.TemporaryRoot,
            ["isNewPackage"] = session.IsNewPackage,
            ["packageGuid"] = session.PackageGuid.ToString("D"),
            ["dependsOnPackages"] = SandboxTools.ToJsonArray(session.DependsOnPackages),
            ["assetCount"] = session.AssetCount,
            // D15：回显卡组判定口径，作者才知道自己按多少张在写。
            ["requiredDeckSize"] = session.RequiredDeckSize,
            ["warnings"] = SandboxTools.ToJsonArray(session.Warnings),
        };

    /// <summary>资产投影：键名与进程内 <see cref="EditorAssetInfo"/> 字段一一对应（工具与 API 等价可断言）。</summary>
    private static JsonObject AssetJson(EditorAssetInfo asset)
        => new()
        {
            ["kind"] = AssetKindTokens.Token(asset.Kind),
            ["packageId"] = asset.PackageId,
            ["id"] = asset.LocalId,
            ["appId"] = asset.AppId,
            ["version"] = asset.Version.ToString(),
            ["assetGuid"] = asset.AssetGuid.ToString("D"),
            ["sourcePath"] = asset.SourcePath,
            ["sidecarPath"] = asset.SidecarPath,
            ["isNew"] = asset.IsNew,
            ["tags"] = SandboxTools.ToJsonArray(asset.Tags),
        };

    private static JsonObject PromotionJson(EditorPromotionResult result)
    {
        var assets = new JsonArray();
        foreach (var asset in result.Assets)
        {
            assets.Add(AssetJson(asset));
        }

        return new JsonObject
        {
            ["packageId"] = result.PackageId,
            ["packageRoot"] = result.PackageRoot,
            ["isNewPackage"] = result.IsNewPackage,
            ["assets"] = assets,
            ["formalLock"] = LockJson(result.FormalLock),
            ["testLock"] = LockJson(result.TestLock),
            ["archivedVersions"] = SandboxTools.ToJsonArray(result.ArchivedVersions),
        };
    }

    private static JsonObject LockJson(AssetLock @lock)
    {
        var assets = new JsonArray();
        foreach (var entry in @lock.Assets)
        {
            assets.Add(new JsonObject
            {
                ["assetGuid"] = entry.AssetGuid.ToString("D"),
                ["kind"] = AssetKindTokens.Token(entry.Kind),
                ["appId"] = entry.AppId,
                ["version"] = entry.Version.ToString(),
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = @lock.SchemaVersion,
            ["packageId"] = @lock.PackageId,
            ["domain"] = AssetDomainTokens.Token(@lock.Domain),
            ["assets"] = assets,
        };
    }

    private static JsonArray IssuesJson(IReadOnlyList<AssetLoadIssue> issues)
    {
        var array = new JsonArray();
        foreach (var issue in issues)
        {
            array.Add(new JsonObject
            {
                ["code"] = issue.Code.ToString(),
                ["domain"] = AssetDomainTokens.Token(issue.Domain),
                ["appId"] = issue.AppId,
                ["message"] = issue.Message,
                ["path"] = SandboxTools.ToJsonArray(issue.Path),
                ["fatal"] = issue.IsFatal,
            });
        }

        return array;
    }

    /// <summary>回显生效的过滤条件（agent 可据此确认「过滤确实生效」）。</summary>
    private static JsonObject FilterJson(JsonElement args)
        => new()
        {
            ["kind"] = ToolArgs.OptionalString(args, "kind"),
            ["packageId"] = ToolArgs.OptionalString(args, "packageId"),
            ["idPrefix"] = ToolArgs.OptionalString(args, "idPrefix"),
            ["tags"] = SandboxTools.ToJsonArray(ToolArgs.OptionalStringList(args, "tags")),
            ["minVersion"] = ToolArgs.OptionalString(args, "minVersion"),
            ["maxVersion"] = ToolArgs.OptionalString(args, "maxVersion"),
        };

    // ── 错误映射 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 统一失败映射（<see cref="AssetEditorException"/> → 受控结果码）：结构化错误码原样带回
    /// <c>details.editorCode</c>，agent 与前端消费同一份判定。
    /// <para>
    /// F12：IO 类异常按语义分流为受控码（**不吞其它异常**——<c>NRE</c> 之类仍上抛）：
    /// <see cref="UnauthorizedAccessException"/>＝「没权限」⇒ <c>path-denied</c>；
    /// <see cref="IOException"/>＝「环境性暂时失败」（占用／磁盘）⇒ <c>validation-failed</c>。
    /// </para>
    /// </summary>
    private static McpToolResult Guard(Func<McpToolResult> action)
    {
        try
        {
            return action();
        }
        catch (AssetEditorException ex)
        {
            return McpToolResult.Failure(
                MapCode(ex.Code),
                ex.Message,
                new JsonObject
                {
                    ["editorCode"] = ex.Code.ToString(),
                    ["error"] = ex.Message,
                });
        }
        catch (UnauthorizedAccessException ex)
        {
            return McpToolResult.Failure(
                McpCodes.PathDenied,
                ex.Message,
                new JsonObject
                {
                    ["cause"] = ex.GetType().Name,
                    ["error"] = ex.Message,
                });
        }
        catch (IOException ex)
        {
            return McpToolResult.Failure(
                McpCodes.ValidationFailed,
                ex.Message,
                new JsonObject
                {
                    ["cause"] = ex.GetType().Name,
                    ["error"] = ex.Message,
                });
        }
    }

    private static string MapCode(AssetEditorErrorCode code) => code switch
    {
        AssetEditorErrorCode.NoSession => McpCodes.NotReady,
        AssetEditorErrorCode.PathDenied => McpCodes.PathDenied,
        AssetEditorErrorCode.PackageNotFound => McpCodes.InvalidArguments,
        AssetEditorErrorCode.DependenciesRequired => McpCodes.InvalidArguments,
        _ => McpCodes.ValidationFailed,
    };
}
