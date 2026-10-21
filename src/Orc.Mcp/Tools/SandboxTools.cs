using System.Text.Json.Nodes;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Packages;
using Orc.Plugins.BuiltIn;

namespace Orc.Mcp.Tools;

/// <summary>
/// 沙盒对局生命周期工具（建局/销毁/状态）。**沙盒专属**。
/// </summary>
internal static class SandboxTools
{
    public static void Register(McpService service)
    {
        service.AddTool(
            "sandbox.create",
            "sandbox",
            "创建沙盒对局（脚手架 + 沙盒卡包卡定义装载 + 初始化）。沙盒局默认无卡：仅含脚手架占位卡以满"
            + "足引擎'卡组名单非空'的构造契约（起手张数＝0，故占位卡不进手牌）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => CreateAsync(service, ct),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.dispose",
            "sandbox",
            "销毁当前沙盒对局（释放活跃对局位；对局不可序列化，销毁即丢弃）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(Dispose(service)),
            sandboxOnly: true);

        service.AddTool(
            "sandbox.state",
            "sandbox",
            "读取沙盒状态：是否已建局、执行器是否忙（存在未完成的选择请求）、本进程已发生的进程级变更留痕。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(State(service)),
            sandboxOnly: true,
            readOnly: true);
    }

    private static async Task<McpToolResult> CreateAsync(McpService service, CancellationToken ct)
    {
        if (service.Executor.HasMatch)
        {
            return McpToolResult.Failure(McpCodes.EngineRejected, "已存在活跃沙盒对局（请先调用 sandbox.dispose）。");
        }

        if (service.Executor.IsBusy)
        {
            return McpToolResult.Failure(McpCodes.PendingTargeting, "存在未完成的选择请求。", McpService.PendingJson(service.Bridge.CurrentPending));
        }

        var scaffold = service.Sandbox.EnsureScaffold();

        // 进程级资产（词条等静态注册面）：幂等，须早于任何对局。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();
        service.NoteProcessStateChange("builtin.process-defaults（内置词条/组件进程级注册，幂等）");

        var load = CardDataLoader.LoadDirectory(service.Sandbox.CardsDirectory);

        var definitions = new List<CardDefinitionEntry>(load.Definitions);
        var placeholderId = service.Options.PlaceholderCardId;

        if (!definitions.Any(entry => string.Equals(entry.Id, placeholderId, StringComparison.Ordinal)))
        {
            // 兜底：脚手架占位卡被删除/解析失败时，用代码构造的最小定义（保证建局不因占位卡缺失而失败）。
            var fallback = new CardDefinition(
                "沙盒占位卡（引擎要求卡组名单非空）",
                deployCost: 1,
                operateCost: 0,
                attack: 0,
                defense: 1,
                category: CardCategory.Unit,
                faction: Faction.Neutral,
                rarity: Rarity.Standard);
            definitions.Add(new CardDefinitionEntry(placeholderId, fallback));
        }

        var deckA = new CardList(new[] { placeholderId });
        var deckB = new CardList(new[] { placeholderId });

        var effectRegistry = new CardEffectRegistry();
        var matchOptions = new MatchOptions
        {
            SkipMulligan = true,                 // 沙盒直接进入"进行"态（不经过换牌相位）
            OpeningHandSizeFirstPlayer = 0,      // 起手 0 张（占位卡不进手牌）
            OpeningHandSizeSecondPlayer = 0,
            // 沙盒授权（R2）：仅 Authoring（沙盒）装配路径开启。
            SandboxAuthorized = service.Options.Mode == McpServiceMode.Authoring,
        };

        // 资产快照须**同时**含内置核心包与 UI 包：前者提供判定器/词条/组件注册面，
        // 后者在 match/preCommand 相注册投影提供者工厂（缺它则 Match.UiProjections 无 provider ⇒ UI 无法投影）。
        var pluginsRoot = service.Options.PluginsRoot;
        var snapshot = AssetCatalogSnapshot.FromPackages(new[]
        {
            BuiltInPluginPackage.Load(pluginsRoot),
            FoundationActionsPluginPackage.Load(pluginsRoot),
            Orc.UI.UiPluginPackage.Load(pluginsRoot),
        });

        var match = new Orc.Game.Match(
            deckA,
            deckB,
            definitions,
            seed: service.Options.Seed,
            options: matchOptions,
            targeterBridge: service.Bridge,
            effectRegistry: effectRegistry,
            packageActivator: new SnapshotPackageActivator(snapshot));

        service.EffectRegistry = effectRegistry;
        service.AttachMatch(match);

        var outcome = await service.Executor
            .RunAsync(
                async m =>
                {
                    await m.Initialize(ct).ConfigureAwait(false);
                    return ActionOutcome.Completed();
                },
                ct)
            .ConfigureAwait(false);

        // 对局就绪（Initialize 完成、UI 包激活相已跑）后才广播生命周期事件——宿主此刻才能建投影。
        if (outcome.Status is ActionOutcome.StatusCompleted or ActionOutcome.StatusTargeting)
        {
            service.NotifyMatchReady();
        }

        var summary = new JsonObject
        {
            ["matchId"] = service.Options.MatchId,
            ["state"] = match.State.ToString(),
            ["phase"] = match.Phase.ToString(),
            ["turn"] = match.TurnNumber,
            ["seed"] = match.Seed,
            ["players"] = PlayersJson(match),
            ["registeredCards"] = definitions.Count,
        };

        var details = new JsonObject
        {
            ["packRoot"] = scaffold.PackRoot,
            ["cardsDirectory"] = scaffold.CardsDirectory,
            ["effectsDirectory"] = scaffold.EffectsDirectory,
            ["createdManifest"] = scaffold.CreatedManifest,
            ["createdPlaceholder"] = scaffold.CreatedPlaceholder,
            ["loadFailures"] = ToJsonArray(load.Failures.Select(f => $"{f.File}: {f.Error}")),
            ["loadWarnings"] = ToJsonArray(load.Warnings),
            ["placeholderCardId"] = placeholderId,
        };

        if (outcome.IsTargeting)
        {
            details["pending"] = McpService.PendingJson(outcome.Pending);
            return McpToolResult.SuccessWith(
                McpCodes.Targeting,
                "沙盒对局已创建，初始化过程中出现待决选择请求（请用 target.submit / target.cancel 应答）。",
                summary,
                details);
        }

        if (outcome.Status == ActionOutcome.StatusFailed)
        {
            return McpToolResult.Failure(
                outcome.FailureCode ?? McpCodes.EngineRejected,
                $"沙盒对局创建失败：{outcome.FailureMessage}",
                details);
        }

        return McpToolResult.Success("沙盒对局已创建并进入进行态。", summary, details);
    }

    private static McpToolResult Dispose(McpService service)
    {
        if (service.Executor.IsBusy)
        {
            return McpToolResult.Failure(
                McpCodes.PendingTargeting,
                "存在未完成的选择请求（请先应答或取消）：不能销毁对局。",
                McpService.PendingJson(service.Bridge.CurrentPending));
        }

        var match = service.DetachMatch();
        service.EffectRegistry = null;

        if (match is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "当前没有活跃沙盒对局。");
        }

        return McpToolResult.Success("沙盒对局已销毁。");
    }

    private static McpToolResult State(McpService service)
    {
        var match = service.Executor.Match;

        var details = new JsonObject
        {
            ["mode"] = service.Options.Mode.ToString(),
            ["hasMatch"] = match is not null,
            ["busy"] = service.Executor.IsBusy,
            ["pending"] = McpService.PendingJson(service.Bridge.CurrentPending),
            ["sandboxRoot"] = service.Sandbox.Guard.Root,
            ["scaffolded"] = service.Sandbox.IsScaffolded,
            ["processStateNotes"] = ToJsonArray(service.ProcessStateNotes),
            ["promoteEnabled"] = service.Options.PromoteEnabled,
            ["csxEnabled"] = service.Options.CsxEnabled,
        };

        if (match is not null)
        {
            details["state"] = match.State.ToString();
            details["phase"] = match.Phase.ToString();
            details["turn"] = match.TurnNumber;
        }

        return McpToolResult.Success("沙盒状态已读取。", details: details);
    }

    private static JsonArray PlayersJson(Orc.Game.Match match)
    {
        var players = new JsonArray();
        foreach (var player in match.Players)
        {
            players.Add(new JsonObject
            {
                ["index"] = player.Index,
                ["ref"] = player.ReferenceKey,
                ["handCount"] = player.Hand.Count,
                ["deckCount"] = player.Deck.Count,
                ["points"] = player.Points,
                ["pointSlots"] = player.PointSlots,
                ["hqHealth"] = player.HqHealth,
                ["hqRef"] = player.Hq.Id.ToString("N"),
            });
        }

        return players;
    }

    internal static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
