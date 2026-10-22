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

namespace Orc.Mcp;

/// <summary>
/// **进程内**沙盒建局面（非工具、不进工具表；S11 退役了 <c>sandbox.create</c> / <c>sandbox.dispose</c>）。
/// <para>
/// 线上建局/毁局只走管理面（<c>match.start</c> / <c>room.abort</c>）；本入口只服务宿主、示例与测试——
/// 它构造的是一个"只有脚手架占位卡"的空场对局，用于调试面塑造局面。
/// </para>
/// </summary>
public sealed partial class McpService
{
    /// <summary>
    /// 创建沙盒对局（脚手架 ＋ 占位卡 ＋ 初始化）。沙盒局默认无卡：仅含脚手架占位卡以满足
    /// 引擎"卡组名单非空"的构造契约（起手张数＝0，故占位卡不进手牌）。
    /// </summary>
    public async Task<McpToolResult> CreateSandboxMatchAsync(CancellationToken ct = default)
    {
        if (Options.Mode != McpServiceMode.Authoring)
        {
            // play 模式没有调试面：建局只走管理面（match.start），不提供进程内空场。
            return McpToolResult.ModeDenied("沙盒建局只在 Authoring（sandbox）模式下可用。");
        }

        if (Executor.HasMatch)
        {
            return McpToolResult.Failure(McpCodes.EngineRejected, "已存在活跃沙盒对局（请先销毁）。");
        }

        if (Executor.IsBusy)
        {
            return McpToolResult.Failure(McpCodes.PendingTargeting, "存在未完成的选择请求。", PendingJson(Bridge.CurrentPending));
        }

        var scaffold = Sandbox.EnsureScaffold();

        // 进程级注册由宿主 boot 段完成（刻意变化第 10 条）；这里再调一次是幂等的，
        // 保证"独立引用本服务的宿主/测试"也能直接用（无副作用、不会产生第二真源）。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();

        if (!_processStateNotes.Any(note => note.StartsWith("builtin.process-defaults", StringComparison.Ordinal)))
        {
            NoteProcessStateChange("builtin.process-defaults（内置词条/组件进程级注册，幂等）");
        }

        var load = CardDataLoader.LoadDirectory(Sandbox.CardsDirectory);

        var definitions = new List<CardDefinitionEntry>(load.Definitions);
        var placeholderId = Options.PlaceholderCardId;

        if (!definitions.Any(entry => string.Equals(entry.Id, placeholderId, StringComparison.Ordinal)))
        {
            // 兜底：占位卡被删除/解析失败时用代码构造的最小定义（保证建局不因占位卡缺失而失败）。
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

        var effectRegistry = new CardEffectRegistry();
        var matchOptions = new MatchOptions
        {
            SkipMulligan = true,                 // 沙盒直接进入"进行"态（不经过换牌相位）
            OpeningHandSizeFirstPlayer = 0,      // 起手 0 张（占位卡不进手牌）
            OpeningHandSizeSecondPlayer = 0,
            SandboxAuthorized = Options.Mode == McpServiceMode.Authoring,
        };

        // 资产快照须**同时**含内置核心包与 UI 包：前者提供判定器/词条/组件注册面，
        // 后者在 match/preCommand 相注册投影提供者工厂（缺它则 Match.UiProjections 无 provider）。
        var pluginsRoot = Options.PluginsRoot;
        var snapshot = AssetCatalogSnapshot.FromPackages(new[]
        {
            BuiltInPluginPackage.Load(pluginsRoot),
            FoundationActionsPluginPackage.Load(pluginsRoot),
            Orc.UI.UiPluginPackage.Load(pluginsRoot),
        });

        var match = new Orc.Game.Match(
            new CardList(new[] { placeholderId }),
            new CardList(new[] { placeholderId }),
            definitions,
            seed: Options.Seed,
            options: matchOptions,
            targeterBridge: Bridge,
            effectRegistry: effectRegistry,
            packageActivator: new SnapshotPackageActivator(snapshot));

        EffectRegistry = effectRegistry;
        AttachMatch(match);

        var outcome = await Executor
            .RunAsync(
                async m =>
                {
                    await m.Initialize(ct).ConfigureAwait(false);
                    return ActionOutcome.Completed();
                },
                ct)
            .ConfigureAwait(false);

        if (outcome.Status is ActionOutcome.StatusCompleted or ActionOutcome.StatusTargeting)
        {
            NotifyMatchReady();
        }

        var summary = new JsonObject
        {
            ["matchId"] = Options.MatchId,
            ["state"] = match.State.ToString(),
            ["phase"] = match.Phase.ToString(),
            ["turn"] = match.TurnNumber,
            ["seed"] = match.Seed,
            ["registeredCards"] = definitions.Count,
            ["packRoot"] = scaffold.PackRoot,
            ["placeholderCardId"] = placeholderId,
        };

        if (outcome.IsTargeting)
        {
            summary["pending"] = PendingJson(outcome.Pending);
            return McpToolResult.SuccessWith(
                McpCodes.Targeting,
                "沙盒对局已创建，初始化过程中出现待决选择请求（请用 target.submit / target.cancel 应答）。",
                summary);
        }

        if (outcome.Status == ActionOutcome.StatusFailed)
        {
            return McpToolResult.Failure(
                outcome.FailureCode ?? McpCodes.EngineRejected,
                $"沙盒对局创建失败：{outcome.FailureMessage}",
                summary);
        }

        return McpToolResult.Success("沙盒对局已创建并进入进行态。", summary);
    }

    /// <summary>销毁活跃沙盒对局（进程内入口；对局不可序列化，销毁即丢弃）。</summary>
    public McpToolResult DisposeSandboxMatch()
    {
        if (Executor.IsBusy)
        {
            return McpToolResult.Failure(
                McpCodes.PendingTargeting,
                "存在未完成的选择请求（请先应答或取消）：不能销毁对局。",
                PendingJson(Bridge.CurrentPending));
        }

        if (DetachMatch() is null)
        {
            return McpToolResult.Failure(McpCodes.NotReady, "当前没有活跃沙盒对局。");
        }

        EffectRegistry = null;
        return McpToolResult.Success("沙盒对局已销毁。");
    }
}
