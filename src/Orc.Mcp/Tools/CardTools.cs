using System.Text.Json.Nodes;
using Orc.Cards;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;

namespace Orc.Mcp.Tools;

/// <summary>
/// 卡牌资产工具：逐卡注册进当前沙盒对局（R0g 唯一加载路径）、卡文件与卡包清单读取。
/// </summary>
internal static class CardTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "card.load",
            "card",
            "把沙盒卡包内的卡数据体**逐卡注册进当前沙盒对局**（运行期卡注册；只追加不覆盖）。"
            + "缺省加载 cards/ 下全部 *.card.json；也可用 files 指定（相对沙盒根的路径）。",
            """
            {
              "type": "object",
              "properties": {
                "files": { "type": "array", "items": { "type": "string" }, "description": "相对沙盒根的卡文件路径（缺省＝cards/ 全量）" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => LoadAsync(service, call, ct),
            sandboxOnly: true);

        service.AddTool(
            "card.list",
            "card",
            "列出沙盒卡包内的卡文件与其解析摘要（id/名称/失败与告警），并列出当前对局已注册的卡定义 id。",
            """
            {
              "type": "object",
              "properties": {
                "files": { "type": "array", "items": { "type": "string" }, "description": "相对沙盒根的卡文件路径（缺省＝cards/ 全量）" }
              },
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(List(service, call)),
            sandboxOnly: true,
            readOnly: true);

        service.AddTool(
            "pack.info",
            "pack",
            "读取沙盒卡包清单（manifest.json 原文与目录摘要）；正式资产根（只读）若已配置则一并列出其包目录。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(PackInfo(service)),
            sandboxOnly: true,
            readOnly: true);
    }

    private static IReadOnlyList<string> ResolveFiles(McpService service, McpToolCall call)
    {
        var files = ToolArgs.OptionalStringList(call.Arguments, "files");
        if (files.Count == 0)
        {
            return service.Sandbox.ListCardFiles();
        }

        var resolved = new List<string>(files.Count);
        foreach (var file in files)
        {
            resolved.Add(service.Sandbox.Guard.Resolve(file));
        }

        return resolved;
    }

    private static async Task<McpToolResult> LoadAsync(McpService service, McpToolCall call, CancellationToken ct)
    {
        var match = service.Executor.Match;
        if (match is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "尚无活跃沙盒对局（请先调用 sandbox.create）。");
        }

        var paths = ResolveFiles(service, call);

        // 效果库（pack/effects/*.prefab.json）不随卡文件装载：card.load 必须一并注册，
        // 否则引用型效果（components.effects.prefabs）在运行期挂空——静默不触发、无任何告警。
        // 内联效果（components.effects.inline）随卡文件自带，不走这一步。
        var library = match.Engine.Prefabs.LoadDirectory(service.Sandbox.EffectsDirectory);
        var libraryFailures = library.Failures
            .Where(failure => !failure.Contains("已注册", StringComparison.Ordinal))
            .ToArray();

        if (paths.Count == 0)
        {
            return McpToolResult.Success("沙盒卡包内没有卡文件可加载。");
        }

        var load = CardDataLoader.LoadFiles(paths);

        foreach (var prefab in load.Prefabs)
        {
            try
            {
                match.Engine.Prefabs.RegisterPrefab(prefab);
            }
            catch (InvalidOperationException)
            {
                // 同 id 已注册（注册面为"注册即配置"、重复注册被拒绝）——视为已装载，忽略。
            }
        }

        var declarations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var declaration in load.EffectDeclarations)
        {
            declarations[declaration.CardId] = declaration.PrefabIds;
        }

        var outcome = await service.Executor
            .RunAsync(
                m =>
                {
                    var registered = new JsonArray();
                    var alreadyRegistered = new JsonArray();

                    foreach (var entry in load.Definitions)
                    {
                        if (m.CardService.RegisteredDefinitions.ContainsKey(entry.Id))
                        {
                            // 运行期卡注册"只追加不覆盖"：同 id 已在库＝跳过（改卡须换新 id）。
                            alreadyRegistered.Add(entry.Id);
                            continue;
                        }

                        var ids = declarations.TryGetValue(entry.Id, out var declared)
                            ? declared
                            : Array.Empty<string>();

                        m.CardService.RegisterCard(new RuntimeCardRegistration(
                            entry.Id,
                            entry.Definition,
                            ids,
                            Array.Empty<EffectSnapshot>()));

                        registered.Add(entry.Id);
                    }

                    var payload = new JsonObject
                    {
                        ["registered"] = registered,
                        ["alreadyRegistered"] = alreadyRegistered,
                        ["failures"] = SandboxTools.ToJsonArray(load.Failures.Select(f => $"{f.File}: {f.Error}")),
                        ["warnings"] = SandboxTools.ToJsonArray(load.Warnings),
                        ["effectsLoaded"] = library.Loaded,
                        ["effectsFailures"] = SandboxTools.ToJsonArray(libraryFailures),
                        ["files"] = SandboxTools.ToJsonArray(paths),
                    };

                    return Task.FromResult(ActionOutcome.Completed(payload));
                },
                ct)
            .ConfigureAwait(false);

        return ToolResults.FromOutcome(outcome, $"已逐卡注册 {load.Definitions.Count} 张卡（注册即对当前沙盒局生效）。");
    }

    private static McpToolResult List(McpService service, McpToolCall call)
    {
        var paths = ResolveFiles(service, call);
        var load = paths.Count > 0 ? CardDataLoader.LoadFiles(paths) : null;

        var cards = new JsonArray();
        if (load is not null)
        {
            foreach (var entry in load.Definitions)
            {
                cards.Add(new JsonObject
                {
                    ["id"] = entry.Id,
                    ["name"] = entry.Definition.Name,
                    ["category"] = entry.Definition.Category,
                });
            }
        }

        var registered = new JsonArray();
        var match = service.Executor.Match;
        if (match is not null)
        {
            foreach (var id in match.CardService.RegisteredDefinitions.Keys)
            {
                registered.Add(id);
            }
        }

        var details = new JsonObject
        {
            ["files"] = SandboxTools.ToJsonArray(paths),
            ["cards"] = cards,
            ["failures"] = load is null
                ? new JsonArray()
                : SandboxTools.ToJsonArray(load.Failures.Select(f => $"{f.File}: {f.Error}")),
            ["warnings"] = load is null ? new JsonArray() : SandboxTools.ToJsonArray(load.Warnings),
            ["registeredInMatch"] = registered,
        };

        return McpToolResult.Success($"{paths.Count} 个卡文件、{cards.Count} 张可解析卡。", details: details);
    }

    private static McpToolResult PackInfo(McpService service)
    {
        var details = new JsonObject
        {
            ["packRoot"] = service.Sandbox.PackRoot,
            ["cardsDirectory"] = service.Sandbox.CardsDirectory,
            ["effectsDirectory"] = service.Sandbox.EffectsDirectory,
            ["scaffolded"] = service.Sandbox.IsScaffolded,
            ["cardFiles"] = SandboxTools.ToJsonArray(service.Sandbox.ListCardFiles()),
            ["effectFiles"] = SandboxTools.ToJsonArray(service.Sandbox.ListEffectFiles()),
        };

        if (File.Exists(service.Sandbox.ManifestFile))
        {
            details["manifest"] = File.ReadAllText(service.Sandbox.ManifestFile);
        }

        var packsRoot = service.Options.PacksRoot;
        if (!string.IsNullOrWhiteSpace(packsRoot) && Directory.Exists(packsRoot))
        {
            var discovered = Directory.GetDirectories(packsRoot);
            Array.Sort(discovered, StringComparer.OrdinalIgnoreCase);
            details["formalPacksReadOnly"] = SandboxTools.ToJsonArray(discovered);
        }

        return McpToolResult.Success("沙盒卡包信息已读取（正式资产根本次只读呈现）。", details: details);
    }
}
