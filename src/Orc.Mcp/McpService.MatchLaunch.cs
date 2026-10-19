using System.Text.Json.Nodes;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Packages;
using Orc.Plugins.BuiltIn;

namespace Orc.Mcp;

/// <summary>
/// 服务层的**真实对局创建面**（进程内；不注册为 MCP 工具）。
/// <para>
/// 与 <c>sandbox.create</c> 的分工：沙盒＝编辑者空场（占位卡、跳过换牌）；本入口＝正式对局
/// （真实卡包 ＋ 显式卡组 ＋ 换牌相位）。卡组必须显式给出，不做自动构筑。
/// </para>
/// </summary>
public sealed partial class McpService
{
    /// <summary>
    /// 创建并装配一局真实对局（装载卡包 → 建局 → 初始化 → 广播 <c>MatchChanged(Created)</c>）。
    /// </summary>
    /// <param name="options">创建输入（包目录 ＋ 两个座位）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功＝完成并携带对局摘要；失败＝可读原因（不留半装载态）。</returns>
    public async Task<ActionOutcome> CreateMatchAsync(MatchLaunchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Executor.HasMatch)
        {
            return ActionOutcome.Failed(McpCodes.EngineRejected, "已存在活跃对局（请先销毁后再创建）。");
        }

        if (Executor.IsBusy)
        {
            return ActionOutcome.Failed(McpCodes.PendingTargeting, "存在未完成的选择请求。", PendingJson(Bridge.CurrentPending));
        }

        if (options.SeatA.Seat != 0 || options.SeatB.Seat != 1)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, "座位配置须恰为 0 与 1（SeatA＝0、SeatB＝1）。");
        }

        var deckError = ValidateDeck(options.SeatA, options.RequiredDeckSize)
            ?? ValidateDeck(options.SeatB, options.RequiredDeckSize);
        if (deckError is not null)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, deckError);
        }

        // 进程级资产（词条/组件注册面）：预检与卡数据装载都依赖它，须早于装载。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();
        NoteProcessStateChange("builtin.process-defaults（内置词条/组件进程级注册，幂等）");

        MatchContentLoadResult content;
        try
        {
            content = MatchContentLoader.Load(options.PackDirectories);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
            or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"卡包装载失败：{ex.Message}");
        }

        string[] deckA;
        string[] deckB;
        try
        {
            deckA = options.SeatA.Deck.Select(content.ResolveCardId).ToArray();
            deckB = options.SeatB.Deck.Select(content.ResolveCardId).ToArray();
        }
        catch (InvalidOperationException ex)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"卡组解析失败：{ex.Message}");
        }

        var effectRegistry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in content.Declarations)
        {
            effectRegistry.DeclarePrefab(cardId, prefabIds);
        }

        // P5b：底层逻辑集包随核心包同批装载（依赖 orc.builtin.core ⇒ 解析/装配序在前）。
        var packageActivator = new SnapshotPackageActivator(AssetCatalogSnapshot.FromPackages(new[]
        {
            BuiltInPluginPackage.Load(Options.PluginsRoot),
            FoundationActionsPluginPackage.Load(Options.PluginsRoot),
            Orc.UI.UiPluginPackage.Load(Options.PluginsRoot),
        }));

        Orc.Game.Match match;
        try
        {
            match = new Orc.Game.Match(
                new CardList(deckA),
                new CardList(deckB),
                content.Definitions,
                seed: options.Seed,
                options: new MatchOptions
                {
                    SkipMulligan = options.SkipMulligan,
                    OpeningHandSizeFirstPlayer = options.OpeningHandSizeFirstPlayer,
                    OpeningHandSizeSecondPlayer = options.OpeningHandSizeSecondPlayer,
                },
                targeterBridge: Bridge,
                effectRegistry: effectRegistry,
                packageActivator: packageActivator);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"建局失败：{ex.Message}");
        }

        EffectRegistry = effectRegistry;

        // 内联预制体与效果库须在 Initialize 前注册（卡装载链在 Initialize 内解析效果引用）。
        foreach (var snapshot in content.InlinePrefabs)
        {
            match.Engine.Prefabs.RegisterPrefab(snapshot);
        }

        foreach (var directory in content.EffectDirectories)
        {
            var report = match.Engine.Prefabs.LoadDirectory(directory);
            if (report.Failures.Count > 0)
            {
                return ActionOutcome.Failed(
                    McpCodes.InvalidArguments,
                    $"效果库装载失败：{string.Join("；", report.Failures)}");
            }
        }

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

        if (outcome.Status == ActionOutcome.StatusFailed)
        {
            DetachMatch();
            return outcome;
        }

        NotifyMatchReady();

        var players = new JsonArray();
        foreach (var player in match.Players)
        {
            players.Add(new JsonObject
            {
                ["index"] = player.Index,
                ["ref"] = player.ReferenceKey,
                ["deck"] = player.Deck.Count,
                ["hand"] = player.Hand.Count,
                ["hqHealth"] = player.HqHealth,
            });
        }

        return ActionOutcome.Completed(new JsonObject
        {
            ["kind"] = "match.created",
            ["state"] = match.State.ToString(),
            ["phase"] = match.Phase.ToString(),
            ["turn"] = match.TurnNumber,
            ["definitions"] = content.Definitions.Count,
            ["packs"] = new JsonArray(content.PackPrefixes.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["players"] = players,
        });
    }

    /// <summary>
    /// 声明**预期应答者**：在下一段动作链内发布的待决请求预占给该应答者（供进程内 AI 使用）。
    /// 该动作链结束（非 targeting 结局）后自动清除；null＝恢复默认先到先服务。
    /// </summary>
    public void ExpectClaimant(string? claimant) => Executor.ExpectClaimant(claimant);

    /// <summary>销毁活跃对局（进程内宿主入口；对局不可序列化，销毁即丢弃）。</summary>
    /// <returns>被销毁的对局（无＝null）。</returns>
    public Orc.Game.Match? DisposeMatch() => DetachMatch();

    private static string? ValidateDeck(MatchSeatConfig seat, int requiredSize)
    {
        if (seat.Deck is null)
        {
            return $"座位 {seat.Seat} 未提供卡组（卡组必须显式指定）。";
        }

        if (seat.Deck.Count == 0)
        {
            return $"座位 {seat.Seat} 的卡组为空（卡组必须显式指定）。";
        }

        if (seat.Deck.Any(string.IsNullOrWhiteSpace))
        {
            return $"座位 {seat.Seat} 的卡组含空白卡 id。";
        }

        if (requiredSize > 0 && seat.Deck.Count != requiredSize)
        {
            return $"座位 {seat.Seat} 的卡组为 {seat.Deck.Count} 张，要求 {requiredSize} 张。";
        }

        return null;
    }
}
