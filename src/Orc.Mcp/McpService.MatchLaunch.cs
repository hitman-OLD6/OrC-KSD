using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Packages;
using Orc.Packages.Loading;
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

        // S7：卡组装载＝注册面 ＋ 两域 lock ＋ 资产加载器（分层列表）＋ 卡牌加载器；卡库在初始化期接入。
        MatchCardAssembly assembly;
        try
        {
            assembly = MatchCardAssembly.Register(options.PackDirectories);
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
            deckA = options.SeatA.Deck.Select(id => ResolveDeckCardId(assembly, id)).ToArray();
            deckB = options.SeatB.Deck.Select(id => ResolveDeckCardId(assembly, id)).ToArray();
        }
        catch (InvalidOperationException ex)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"卡组解析失败：{ex.Message}");
        }

        // S7：卡组规则（唯一实现）——尺寸、卡池存在性、同名上限（按稀有度）、主国/盟国配对。
        if (options.EnforceDeckRules)
        {
            var pool = assembly.Definitions.ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
            var violations = new List<Orc.Game.Decks.DeckViolation>();
            foreach (var (seat, deck) in new[] { (options.SeatA, deckA), (options.SeatB, deckB) })
            {
                violations.AddRange(Orc.Game.Decks.DeckRules.Validate(deck, pool, options.RequiredDeckSize, options.DeckRuleTier));
                if (Orc.Game.Decks.DeckRules.ValidateFaction(seat.DeckConfiguration, options.DeckRuleTier) is { } factionViolation)
                {
                    violations.Add(factionViolation);
                }
            }

            if (violations.Count > 0)
            {
                return ActionOutcome.Failed(
                    McpCodes.DeckInvalid,
                    "卡组不合法：" + string.Join("；", violations.Select(v => v.Message)));
            }
        }

        var effectRegistry = new CardEffectRegistry();
        foreach (var (cardId, prefabIds) in assembly.Declarations)
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
                assembly.Definitions,
                seed: options.Seed,
                options: new MatchOptions
                {
                    SkipMulligan = options.SkipMulligan,
                    OpeningHandSizeFirstPlayer = options.OpeningHandSizeFirstPlayer,
                    OpeningHandSizeSecondPlayer = options.OpeningHandSizeSecondPlayer,
                    // 沙盒授权（R2）：仅 Authoring（沙盒）装配路径开启；play 路径不开启。
                    SandboxAuthorized = Options.Mode == McpServiceMode.Authoring,
                },
                targeterBridge: Bridge,
                effectRegistry: effectRegistry,
                packageActivator: packageActivator,
                cardLoader: assembly,
                deckConfigForPlayerA: options.SeatA.DeckConfiguration,
                deckConfigForPlayerB: options.SeatB.DeckConfiguration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"建局失败：{ex.Message}");
        }

        EffectRegistry = effectRegistry;

        // 内联预制体与效果库须在 Initialize 前注册（卡装载链在 Initialize 内解析效果引用）。
        foreach (var snapshot in assembly.InlinePrefabs)
        {
            match.Engine.Prefabs.RegisterPrefab(snapshot);
        }

        foreach (var directory in assembly.EffectDirectories)
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
        CardAssembly = assembly; // S9：诊断面（diag.card_load）经此读取本局加载台账

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
            ["definitions"] = assembly.Definitions.Count,
            ["packs"] = new JsonArray(assembly.PackPrefixes.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
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

    /// <summary>
    /// 卡组条目解析（**唯一实现＝<see cref="AssetIdBridge"/>**，S3/S7）：应用层 ID 直配，裸局部 ID 在中间层内唯一才可解析。
    /// 解析失败＝调用方输入错误（卡组是不可静默删减的显式契约）；卡级**非法**（已注册但判定不通过）不在此拦——
    /// 那些卡位按 R5 留空、开局继续。
    /// </summary>
    /// <param name="assembly">卡组装载（中间层来源）。</param>
    /// <param name="reference">卡组条目（全名或裸局部 ID）。</param>
    /// <exception cref="InvalidOperationException">未注册／歧义（提示给出全名）。</exception>
    private static string ResolveDeckCardId(MatchCardAssembly assembly, string reference)
    {
        var resolution = assembly.Bridge.Resolve(AssetKind.Card, reference);
        return resolution.Status switch
        {
            IdResolutionStatus.Resolved => resolution.AppId!,
            IdResolutionStatus.Ambiguous => throw new InvalidOperationException(
                $"卡 id '{reference}' 在多个卡包中重名（{string.Join("、", resolution.Candidates)}），请给出全名。"),
            _ => throw new InvalidOperationException($"卡 id '{reference}' 不在已装载的卡定义中。"),
        };
    }

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
