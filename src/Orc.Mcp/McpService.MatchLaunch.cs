using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Orc.Packages;
using Orc.Packages.Decks;
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

        // ① 座位形状（互斥／都空）——放在最前：调用方的错误不该走到昂贵的包注册之后（D30）。
        var shapeError = ValidateSeatShape(options.SeatA) ?? ValidateSeatShape(options.SeatB);
        if (shapeError is not null)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, shapeError);
        }

        // 进程级资产（词条/组件注册面）由 **boot 段**注册（刻意变化第 10 条：不在每局重复调用，
        // 避免两处都调的双真源）。此处只留痕一次，供 sandbox.state 呈现。
        NoteProcessStateChange("builtin.process-defaults（进程级注册，boot 段一次，幂等）");

        // S7：卡组装载＝注册面 ＋ 两域 lock ＋ 资产加载器（分层列表）＋ 卡牌加载器；卡库在初始化期接入。
        // 管理面已按"本局加载计划"注册过时直接复用（同一份卡包只解析一遍）。
        MatchCardAssembly assembly;
        if (options.Assembly is { } prebuilt)
        {
            assembly = prebuilt;
        }
        else
        {
            try
            {
                assembly = MatchCardAssembly.Register(options.PackDirectories);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
                or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
            {
                return ActionOutcome.Failed(McpCodes.InvalidArguments, $"卡包装载失败：{ex.Message}");
            }
        }

        // ② 解析座位给的卡组（手写列表 / 卡组标识），得到本局实际使用的卡 id 与构筑配置。
        var seatA = ResolveSeat(options.SeatA, assembly);
        var seatB = seatA.Failure is null ? ResolveSeat(options.SeatB, assembly) : default;
        if (seatA.Failure is { } seatAFailure)
        {
            return ActionOutcome.Failed(seatAFailure.Code, seatAFailure.Message);
        }

        if (seatB.Failure is { } seatBFailure)
        {
            return ActionOutcome.Failed(seatBFailure.Code, seatBFailure.Message);
        }

        var deckA = seatA.Deck;
        var deckB = seatB.Deck;

        // ③ 张数／空白（形状级，与档位无关）
        var deckError = ValidateDeck(options.SeatA, deckA, options.RequiredDeckSize)
            ?? ValidateDeck(options.SeatB, deckB, options.RequiredDeckSize);
        if (deckError is not null)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, deckError);
        }

        // S7：卡组规则（唯一实现）——尺寸、卡池存在性、同名上限（按稀有度）、主国/盟国配对。
        if (options.EnforceDeckRules)
        {
            var pool = assembly.Definitions.ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
            var violations = new List<Orc.Game.Decks.DeckViolation>();
            foreach (var (deck, configuration) in new[] { (deckA, seatA.Configuration), (deckB, seatB.Configuration) })
            {
                // 手写卡列表通路：**继续吃档位**（R10 末条）。卡组资产通路不经过这里——它在注册期
                // 已按恒 strict 判定完毕，两套量尺互不影响（D10）。
                violations.AddRange(Orc.Game.Decks.DeckRules.Validate(deck, pool, options.RequiredDeckSize, options.DeckRuleTier));
                if (Orc.Game.Decks.DeckRules.ValidateFaction(configuration, options.DeckRuleTier) is { } factionViolation)
                {
                    violations.Add(factionViolation);
                }

                violations.AddRange(Orc.Game.Decks.DeckRules.ValidateFactionComposition(
                    deck, pool, configuration, options.DeckRuleTier));
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
                    // R2c：随机取样只在沙盒（Authoring）装配路径开启，play 路径关闭。
                    AllowRandomTargeting = Options.Mode == McpServiceMode.Authoring,
                },
                targeterBridge: Bridge,
                effectRegistry: effectRegistry,
                packageActivator: packageActivator,
                cardLoader: assembly,
                deckConfigForPlayerA: seatA.Configuration,
                deckConfigForPlayerB: seatB.Configuration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return ActionOutcome.Failed(McpCodes.InvalidArguments, $"建局失败：{ex.Message}");
        }

        EffectRegistry = effectRegistry;

        // §34：卡级装载失败（如效果 csx 在 v4 承载视图下编译不过）→ 回填卡牌加载报告。
        // 游戏层只把事实交出来（CardComponentFailureData），报告模型与映射都在 Orc.Packages 侧；
        // 时机＝Initialize 之前（牌堆装载发生在初始化内）。
        match.ComponentFailureSink = failures =>
            assembly.RecordComponentFailures(CardComponentFailureMapper.ToReportFailures(failures));

        // 内联预制体与效果库须在 Initialize 前注册（卡装载链在 Initialize 内解析效果引用）。
        foreach (var snapshot in assembly.InlinePrefabs)
        {
            match.Engine.Prefabs.RegisterPrefab(snapshot);
        }

        foreach (var directory in assembly.EffectDirectories)
        {
            // 旧格式效果库目录（*.prefab.json）随旧装载链删除——明确失败，不静默漏装。
            return ActionOutcome.Failed(
                McpCodes.InvalidArguments,
                $"旧格式效果库目录已不再支持（v4-only）：'{directory}'——请改用 effects-v4/*.component.json。");
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

    /// <summary>
    /// 座位形状校验（D26/D29）：两源都非空＝互斥；归一后都空＝必须提供。错误码只复用一个 <c>InvalidArguments</c>。
    /// </summary>
    private static string? ValidateSeatShape(MatchSeatConfig seat)
    {
        var deck = seat.NormalizedDeck;
        var deckId = seat.NormalizedDeckId;

        if (deck is not null && deckId is not null)
        {
            return $"座位 {seat.Seat} 同时给了卡组标识（DeckId）与手写卡列表——两者互斥，只能给一个。";
        }

        return deck is null && deckId is null
            ? $"座位 {seat.Seat} 未提供卡组（给卡组标识 DeckId 或手写卡列表 Deck 之一）。"
            : null;
    }

    /// <summary>座位解析结果：本局实际使用的卡 id ＋ 构筑配置（或失败码）。</summary>
    private readonly record struct SeatResolution(
        string[] Deck,
        Orc.Game.PlayerDeckConfiguration? Configuration,
        (string Code, string Message)? Failure);

    /// <summary>
    /// 解析一个座位的卡组（D30 第 ③ 步）：手写通路逐条经中间层解析；卡组资产通路查**可用集合**索引，
    /// 再按卡组展开并逐条经同一中间层解析（D35：卡组合法 ≠ 本局可用，lock 可能被裁剪）。
    /// </summary>
    private static SeatResolution ResolveSeat(MatchSeatConfig seat, MatchCardAssembly assembly)
    {
        if (seat.NormalizedDeck is { } handWritten)
        {
            try
            {
                return new SeatResolution(
                    handWritten.Select(id => ResolveDeckCardId(assembly, id)).ToArray(),
                    seat.DeckConfiguration,
                    Failure: null);
            }
            catch (InvalidOperationException ex)
            {
                return Failed(McpCodes.InvalidArguments, $"座位 {seat.Seat} 的卡组解析失败：{ex.Message}");
            }
        }

        var lookup = assembly.Decks.Resolve(seat.NormalizedDeckId);
        switch (lookup.Status)
        {
            case DeckLookupStatus.Resolved:
                break;
            case DeckLookupStatus.Ambiguous:
                return Failed(
                    McpCodes.DeckInvalid,
                    $"座位 {seat.Seat} 的卡组标识 '{seat.NormalizedDeckId}' 命中多份卡组"
                    + $"（{string.Join("、", lookup.Candidates)}）——请给全名（应用层 ID 或包内唯一写法）。");
            default:
                return Failed(
                    McpCodes.DeckInvalid,
                    $"座位 {seat.Seat} 的卡组标识 '{seat.NormalizedDeckId}' 不在本局可用集合（不存在／未入 lock／内容不合法）。");
        }

        var definition = lookup.Deck!;

        // 座位显式配置只与卡组内嵌的**最终值**比对（D47）：文件省略 allyFaction 时推导结果也算一致。
        if (seat.DeckConfiguration is { } requested)
        {
            var finalConfig = definition.DeckConfig;
            if (!string.Equals(requested.MainFaction, finalConfig.MainFaction, StringComparison.Ordinal)
                || !string.Equals(requested.AllyFaction, finalConfig.AllyFaction, StringComparison.Ordinal))
            {
                return Failed(
                    McpCodes.DeckInvalid,
                    $"座位 {seat.Seat} 的构筑配置与卡组 '{lookup.AppId}' 不一致"
                    + $"（卡组：主国 {finalConfig.MainFaction}／盟国 {finalConfig.AllyFaction}；"
                    + $"座位：主国 {requested.MainFaction ?? "<无>"}／盟国 {requested.AllyFaction ?? "<无>"}）。");
            }
        }

        try
        {
            return new SeatResolution(
                definition.ToCardIds().Select(id => ResolveDeckCardId(assembly, id)).ToArray(),
                seat.DeckConfiguration ?? definition.DeckConfig,
                Failure: null);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(
                McpCodes.DeckInvalid,
                $"座位 {seat.Seat} 的卡组 '{lookup.AppId}' 含本局不可用的卡：{ex.Message}");
        }
    }

    private static SeatResolution Failed(string code, string message)
        => new(Array.Empty<string>(), Configuration: null, (code, message));

    private static string? ValidateDeck(MatchSeatConfig seat, IReadOnlyList<string> deck, int requiredSize)
    {
        if (deck.Any(string.IsNullOrWhiteSpace))
        {
            return $"座位 {seat.Seat} 的卡组含空白卡 id。";
        }

        if (requiredSize > 0 && deck.Count != requiredSize)
        {
            return $"座位 {seat.Seat} 的卡组为 {deck.Count} 张，要求 {requiredSize} 张。";
        }

        return null;
    }
}
