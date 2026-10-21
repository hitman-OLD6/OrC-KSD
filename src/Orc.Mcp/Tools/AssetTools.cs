using System.Text.Json.Nodes;
using Orc.Cards;
using Orc.Game.Cards.Data;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 资产管线工具（S6）：写路径白名单内的规范化写入、dry-run 校验、目录浏览，以及默认关闭的产物提升。
/// <para>
/// 写入语义（R1b）：agent 写**原文 JSON**，服务层负责①校验（解析失败＝拒绝落盘）②规范化写出（读宽写窄）。
/// 唯一可写根＝沙盒根（<see cref="Orc.Mcp.Sandbox.SandboxPathGuard"/>）；正式资产根仅经 <c>asset.promote</c>
/// 且默认关闭时方可写。
/// </para>
/// </summary>
internal static class AssetTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "card.write",
            "card",
            "写入/覆盖沙盒卡包内的一张卡数据体（*.card.json）。写入前先解析校验：JSON 非法或结构不符＝拒绝落盘；"
            + "成功则按读宽写窄规范化写出（固定 schemaVersion、2 空格缩进、LF）。",
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "相对沙盒根的路径（通常 pack/cards/<id>.card.json）" },
                "json": { "type": "string", "description": "卡数据体 JSON 原文" },
                "overwrite": { "type": "boolean", "description": "是否允许覆盖既有文件（缺省 false）" }
              },
              "required": ["path", "json"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(WriteCard(service, call)),
            sandboxOnly: true);

        service.AddTool(
            "effect.write",
            "card",
            "写入/覆盖沙盒卡包内的一份效果预制体（*.prefab.json）。写入前先反序列化校验，成功后规范化写出。",
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "相对沙盒根的路径（通常 pack/effects/<id>.prefab.json）" },
                "json": { "type": "string", "description": "效果预制体 JSON 原文" },
                "overwrite": { "type": "boolean", "description": "是否允许覆盖既有文件（缺省 false）" }
              },
              "required": ["path", "json"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(WritePrefab(service, call)),
            sandboxOnly: true);

        service.AddTool(
            "asset.validate",
            "card",
            "对沙盒卡包做 dry-run 校验（纯只读）：逐文件解析明细 + 引用完整性报告（错误/警告分列）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Validate(service)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "file.list",
            "pack",
            "列出沙盒根下的全部文件（相对路径 + 字节数）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(ListFiles(service)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "file.read",
            "pack",
            "读取沙盒根内的一个文本文件（上限 1 MiB）。",
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "相对沙盒根的路径" }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(ReadFile(service, call)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "asset.promote",
            "pack",
            "把沙盒内的产物提升到**已存在**的正式卡包目录（默认关闭；需 --promote；要求无活跃沙盒局；不隐式覆盖）。",
            """
            {
              "type": "object",
              "properties": {
                "source": { "type": "string", "description": "沙盒根内的产物相对路径（如 pack/cards/demo.x.card.json）" },
                "targetPack": { "type": "string", "description": "正式卡包目录（绝对路径，须位于 --packs 指定的根之下且已存在）" }
              },
              "required": ["source", "targetPack"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Promote(service, call)),
            sandboxOnly: true);
    }

    private static McpToolResult WriteCard(McpService service, McpToolCall call)
    {
        var path = ToolArgs.RequireString(call.Arguments, "path");
        var json = ToolArgs.RequireString(call.Arguments, "json");
        var overwrite = ToolArgs.OptionalBool(call.Arguments, "overwrite");

        var read = CardDataJson.Read(json);
        if (read.Body is null)
        {
            return McpToolResult.Failure(
                McpCodes.ValidationFailed,
                $"卡数据体校验失败（未落盘）：{read.Error}",
                new JsonObject { ["warnings"] = SandboxTools.ToJsonArray(read.Warnings) });
        }

        var text = CardDataJson.Serialize(read.Body, out var writeWarnings);
        var full = service.Sandbox.Guard.ResolveWrite(path);

        if (File.Exists(full) && !overwrite)
        {
            return McpToolResult.Failure(
                McpCodes.ValidationFailed,
                $"目标文件已存在：'{path}'（不隐式覆盖；如需覆盖请传 overwrite=true）。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        service.NoteProcessStateChange($"sandbox.write:{path}");

        var warnings = new JsonArray();
        foreach (var warning in read.Warnings)
        {
            warnings.Add(warning);
        }

        foreach (var warning in writeWarnings)
        {
            warnings.Add(warning);
        }

        return McpToolResult.Success($"已写入卡数据体：{path}", details: new JsonObject
        {
            ["path"] = path,
            ["bytes"] = text.Length,
            ["schemaVersion"] = CardDataJson.SchemaVersion,
            ["warnings"] = warnings,
        });
    }

    private static McpToolResult WritePrefab(McpService service, McpToolCall call)
    {
        var path = ToolArgs.RequireString(call.Arguments, "path");
        var json = ToolArgs.RequireString(call.Arguments, "json");
        var overwrite = ToolArgs.OptionalBool(call.Arguments, "overwrite");

        EffectSnapshot snapshot;
        try
        {
            snapshot = PrefabJson.Deserialize(json);
        }
        catch (Exception ex)
        {
            return McpToolResult.Failure(McpCodes.ValidationFailed, $"效果预制体校验失败（未落盘）：{ex.Message}");
        }

        var text = PrefabJson.SerializeForDisk(snapshot);
        var full = service.Sandbox.Guard.ResolveWrite(path);

        if (File.Exists(full) && !overwrite)
        {
            return McpToolResult.Failure(
                McpCodes.ValidationFailed,
                $"目标文件已存在：'{path}'（不隐式覆盖；如需覆盖请传 overwrite=true）。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        service.NoteProcessStateChange($"sandbox.write:{path}");

        return McpToolResult.Success($"已写入效果预制体：{path}", details: new JsonObject
        {
            ["path"] = path,
            ["bytes"] = text.Length,
            ["schemaVersion"] = EffectSnapshot.SchemaVersion,
        });
    }

    private static McpToolResult Validate(McpService service)
    {
        var cardFiles = service.Sandbox.ListCardFiles();
        var effectFiles = service.Sandbox.ListEffectFiles();

        var perFile = new JsonArray();
        var parseErrors = 0;

        foreach (var file in cardFiles)
        {
            var text = File.ReadAllText(file);
            var read = CardDataJson.Read(text);
            var relative = Relative(service, file);

            var entry = new JsonObject
            {
                ["file"] = relative,
                ["ok"] = read.Body is not null,
                ["cardId"] = read.Body?.Id,
                ["warnings"] = SandboxTools.ToJsonArray(read.Warnings),
            };

            if (read.Body is null)
            {
                parseErrors++;
                entry["error"] = read.Error;
            }

            perFile.Add(entry);
        }

        var report = CardDistributionValidator.Validate(
            new[] { service.Sandbox.CardsDirectory },
            new[] { service.Sandbox.EffectsDirectory });

        var errors = new JsonArray();
        foreach (var error in report.Errors)
        {
            errors.Add(new JsonObject
            {
                ["category"] = error.Category.ToString(),
                ["message"] = error.Message,
                ["file"] = error.File,
                ["cardId"] = error.CardId,
                ["prefabId"] = error.PrefabId,
            });
        }

        var warnings = new JsonArray();
        foreach (var warning in report.Warnings)
        {
            warnings.Add(new JsonObject
            {
                ["category"] = warning.Category.ToString(),
                ["message"] = warning.Message,
                ["file"] = warning.File,
                ["cardId"] = warning.CardId,
                ["prefabId"] = warning.PrefabId,
            });
        }

        var details = new JsonObject
        {
            ["cardFiles"] = cardFiles.Count,
            ["effectFiles"] = effectFiles.Count,
            ["parseErrors"] = parseErrors,
            ["perFile"] = perFile,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["errorCount"] = report.ErrorCount,
            ["warningCount"] = report.WarningCount,
            ["hasNoErrors"] = report.HasNoErrors,
        };

        var message = report.HasNoErrors
            ? "校验通过（无 Error）。"
            : $"校验发现 {report.ErrorCount} 个 Error（详见 details.errors）。";

        return report.HasNoErrors
            ? McpToolResult.Success(message, details: details)
            : McpToolResult.Failure(McpCodes.ValidationFailed, message, details);
    }

    private static McpToolResult ListFiles(McpService service)
    {
        var root = service.Sandbox.Guard.Root;
        var files = new JsonArray();

        if (Directory.Exists(root))
        {
            var all = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);

            foreach (var file in all)
            {
                files.Add(new JsonObject
                {
                    ["path"] = Relative(service, file),
                    ["bytes"] = new FileInfo(file).Length,
                });
            }
        }

        return McpToolResult.Success($"{files.Count} 个文件。", details: new JsonObject { ["files"] = files });
    }

    private static McpToolResult ReadFile(McpService service, McpToolCall call)
    {
        var path = ToolArgs.RequireString(call.Arguments, "path");
        var full = service.Sandbox.Guard.Resolve(path);

        if (!File.Exists(full))
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, $"文件不存在：{path}");
        }

        var info = new FileInfo(full);
        if (info.Length > 1024 * 1024)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, $"文件过大（{info.Length} 字节；上限 1 MiB）。");
        }

        return McpToolResult.Success($"已读取 {path}", details: new JsonObject
        {
            ["path"] = path,
            ["bytes"] = info.Length,
            ["content"] = File.ReadAllText(full),
        });
    }

    private static McpToolResult Promote(McpService service, McpToolCall call)
    {
        if (!service.Options.PromoteEnabled)
        {
            return McpToolResult.NotSupported("产物提升默认关闭（启动参数 --promote 显式开启后可用）。");
        }

        if (service.Executor.HasMatch)
        {
            return McpToolResult.Failure(McpCodes.EngineRejected, "存在活跃沙盒局：请先 sandbox.dispose 再提升（提升不会影响已冻结的对局快照）。");
        }

        var packsRoot = service.Options.PacksRoot;
        if (string.IsNullOrWhiteSpace(packsRoot))
        {
            return McpToolResult.NotSupported("未配置正式资产根（启动参数 --packs）——无提升目标。");
        }

        var source = ToolArgs.RequireString(call.Arguments, "source");
        var targetPack = ToolArgs.RequireString(call.Arguments, "targetPack");

        var sourceFull = service.Sandbox.Guard.Resolve(source);
        if (!File.Exists(sourceFull))
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, $"沙盒产物不存在：{source}");
        }

        var packsRootFull = Path.GetFullPath(packsRoot);
        var targetFull = Path.GetFullPath(targetPack);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var packsRootWithSeparator = packsRootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!targetFull.StartsWith(packsRootWithSeparator, comparison))
        {
            return McpToolResult.Failure(
                McpCodes.PathDenied,
                $"提升目标越出正式资产根（--packs 指定）：'{targetPack}'");
        }

        if (!Directory.Exists(targetFull))
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, $"目标卡包目录不存在（提升只写入已存在的卡包）：{targetPack}");
        }

        var destination = Path.Combine(targetFull, Path.GetFileName(sourceFull));
        if (File.Exists(destination))
        {
            return McpToolResult.Failure(
                McpCodes.ValidationFailed,
                $"目标已存在同名文件（不做隐式覆盖）：{Path.GetFileName(sourceFull)}");
        }

        File.Copy(sourceFull, destination);
        service.NoteProcessStateChange($"promote:{source} → {destination}");

        return McpToolResult.Success("产物已提升到正式卡包。", details: new JsonObject
        {
            ["source"] = source,
            ["target"] = destination,
            ["note"] = "提升后的资产对**新建对局**生效；已冻结的对局快照不受影响。",
        });
    }

    private static string Relative(McpService service, string fullPath)
    {
        var root = service.Sandbox.Guard.Root;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath[root.Length..].Replace('\\', '/')
            : fullPath;
    }
}
