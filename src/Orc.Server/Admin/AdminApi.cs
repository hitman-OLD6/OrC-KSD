using System.Text.Json.Nodes;
using Orc.Game;
using Orc.Game.Decks;
using Orc.Mcp;
using Orc.Mcp.Match;
using Orc.Mcp.Pool;
using Orc.Core;
using Orc.Packages;
using Orc.Packages.Decks;
using Orc.Packages.Loading;
using Orc.Protocol;
using Orc.Server.Execution;
using Orc.Server.Pool;
using Orc.Server.Rooms;
using Orc.Server.Transport;

namespace Orc.Server.Admin;

/// <summary>
/// 管理面实现（S3；**进程内**，由父子私有通道调用，不注册进工具表）。
/// <para>
/// 执行上下文：凡改动引擎、房间/seat、卡池或开局装配输入的（开局、终止、绑定/解绑、撤销许可、沙盒重置）
/// **都在串行队列内执行**；纯只读查询在队列外。
/// </para>
/// <para>
/// 资产判定**不在这里做**：并集/冲突/缺口由管理器的资产库算，本层只接收"本局加载计划"
/// （包目录 ＋ 期望 lock）并执行；失败时把失败码原样上报，不另造码表。
/// </para>
/// </summary>
public sealed class AdminApi
{
    /// <summary>轮换 drain 窗口的建议重试间隔。</summary>
    public const int RetryAfterMs = 200;

    private readonly ServerHost _host;

    internal AdminApi(ServerHost host) => _host = host;

    /// <summary>调用一个管理 op（未知 op ⇒ <c>ok:false</c> 且带可读码，不静默丢弃）。</summary>
    public async Task<AdminOutcome> InvokeAsync(string op, JsonObject? args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);
        args ??= new JsonObject();

        return op switch
        {
            AdminOps.InstanceInfo => InstanceInfo(),
            AdminOps.ConnectionsList => ConnectionsList(),
            AdminOps.ConnectionAdmit => await InQueueAsync(token => Task.FromResult(Admit(args, AdminOps.ConnectionAdmit)), ct)
                .ConfigureAwait(false),
            AdminOps.SeatBind => await InQueueAsync(token => Task.FromResult(Admit(args, AdminOps.SeatBind)), ct)
                .ConfigureAwait(false),
            AdminOps.ConnectionReject => await InQueueAsync(
                token => Task.FromResult(_host.Reject(RequireConnectionId(args), OptionalString(args, "reason"))), ct)
                .ConfigureAwait(false),
            AdminOps.SeatUnbind => await InQueueAsync(
                token => Task.FromResult(_host.Unbind(RequireConnectionId(args))), ct)
                .ConfigureAwait(false),
            AdminOps.ConnectionRevoke => await RevokeAsync(args, ct).ConfigureAwait(false),
            AdminOps.MatchStart => await StartMatchAsync(args, ct).ConfigureAwait(false),
            AdminOps.RoomAbort => await AbortAsync(ct).ConfigureAwait(false),
            AdminOps.SandboxReset => await ResetSandboxAsync(ct).ConfigureAwait(false),
            AdminOps.PoolList => PoolList(),
            AdminOps.PoolRegister => await PoolRegisterAsync(args, ct).ConfigureAwait(false),
            AdminOps.PoolUnregister => await PoolUnregisterAsync(args, ct).ConfigureAwait(false),
            _ => AdminOutcome.Fail(ResultCodes.InvalidArguments, $"未知管理操作 '{op}'。"),
        };
    }

    // ---------------------------------------------------------------- 只读

    private AdminOutcome InstanceInfo()
    {
        var room = _host.Room;
        var match = _host.Service.Executor.Match;

        return AdminOutcome.Success(new JsonObject
        {
            ["port"] = _host.Port,
            ["label"] = _host.Options.Label,
            ["mode"] = _host.Options.Mode == ServerMode.Sandbox ? "sandbox" : "play",
            ["roomId"] = room.RoomId,
            ["roomState"] = room.State.ToString(),
            ["epoch"] = _host.CurrentEpoch,
            ["seatCount"] = room.SeatCount,
            ["sessions"] = _host.SessionCount,
            ["draining"] = _host.IsDraining,
            ["hasMatch"] = match is not null,
            ["matchState"] = match?.State.ToString(),
            ["endReason"] = match?.EndReason?.ToString(),
            ["winner"] = match?.Winner?.Index,

            // S10：对外可见的破坏性改名——语义＝spawn 初值（历史信息），且已剔除沙盒固定包。
            ["initialPackDirectories"] = new JsonArray(_host.Packs.InitialDirectories
                .Select(path => (JsonNode)JsonValue.Create(path)!)
                .ToArray()),
            ["packDirectoriesRenamedTo"] = "initialPackDirectories",
        });
    }

    private AdminOutcome ConnectionsList()
        => AdminOutcome.Success(new JsonObject
        {
            ["connections"] = new JsonArray(_host.ListConnections().Select(node => (JsonNode)node).ToArray()),
        });

    // ---------------------------------------------------------------- 已注册包集合（pool.*）

    /// <summary>
    /// 管理面受众＝**实例模式**（R4：脱敏按实例模式、不按入口）——play 实例的 op 与工具走同一白名单。
    /// </summary>
    private PoolAudience PoolAudience
        => _host.Options.Mode == ServerMode.Sandbox ? PoolAudience.Sandbox : PoolAudience.RemotePlay;

    /// <summary>
    /// <c>pool.list</c>：**不入队**（lock 内取快照＋预演），因此 drain 窗口不影响它（S5）。
    /// </summary>
    private AdminOutcome PoolList()
    {
        var audience = PoolAudience;
        var result = _host.Packs.Snapshot(audience);
        return Outcome(result, audience);
    }

    /// <summary><c>pool.register</c>：写操作（drain 内拒绝；队列边界由 <c>PackPool</c> 的自适应入口负责）。</summary>
    private async Task<AdminOutcome> PoolRegisterAsync(JsonObject args, CancellationToken ct)
    {
        var directory = OptionalString(args, "directory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return AdminOutcome.Fail(ResultCodes.InvalidArguments, "缺少必填字符串参数 'directory'（卡包绝对目录路径）。");
        }

        var audience = PoolAudience;
        var result = await _host.Packs.RegisterAsync(directory, audience, ct).ConfigureAwait(false);
        return Outcome(result, audience);
    }

    /// <summary><c>pool.unregister</c>：写操作（入参＝id ＋ version，与当前快照精确匹配）。</summary>
    private async Task<AdminOutcome> PoolUnregisterAsync(JsonObject args, CancellationToken ct)
    {
        var packageId = OptionalString(args, "packageId");
        var version = OptionalString(args, "version");
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return AdminOutcome.Fail(
                ResultCodes.InvalidArguments,
                "pool.unregister 需要 packageId 与 version 两项（只用 id 不足以移除）。");
        }

        var audience = PoolAudience;
        var result = await _host.Packs.UnregisterAsync(packageId, version, audience, ct).ConfigureAwait(false);
        return Outcome(result, audience);
    }

    /// <summary>集合操作结果 → 管理面回执（载荷走**唯一投影**，与工具面逐字段一致）。</summary>
    private static AdminOutcome Outcome(PoolCallResult result, PoolAudience audience)
    {
        var payload = PoolProjection.Render(result, audience);
        var message = PoolProjection.Message(result, audience);
        return new AdminOutcome(result.Ok, result.Code, message, payload);
    }

    // ---------------------------------------------------------------- 绑定拓扑

    private AdminOutcome Admit(JsonObject args, string op)
    {
        var connectionId = RequireConnectionId(args);
        var seat = OptionalInt(args, "seat");
        var roles = args["roles"] is JsonArray array
            ? array.Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : null;

        _ = op;
        return _host.Admit(connectionId, seat, roles);
    }

    private async Task<AdminOutcome> RevokeAsync(JsonObject args, CancellationToken ct)
    {
        var connectionId = RequireConnectionId(args);

        // 撤销**允许在 drain 窗口内**：只动会话、不动旧栈（绑定释放留待新房间建立后清理）。
        return await InQueueAsync(
            token => _host.RevokeAsync(connectionId, OptionalString(args, "reason"), token),
            ct,
            allowWhileDraining: true).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 开局 / 终止 / 沙盒

    private async Task<AdminOutcome> StartMatchAsync(JsonObject args, CancellationToken ct)
    {
        // R5：`packDirectories` 的**单局覆盖语义已移除**——判据是"键是否存在"（`[]`／`null`／非数组都算携带），
        // 不再回落 spawn 列表（旧实现 `dirs.Count == 0` 的回落正是被禁止的静默歧义）。
        if (args.ContainsKey("packDirectories"))
        {
            return AdminOutcome.Fail(
                ResultCodes.InvalidArguments,
                "match.start 不再接受 packDirectories：卡池集合改由 pool.register / pool.unregister 管理；本参数已移除。");
        }

        if (_host.IsDraining)
        {
            // drain 窗口内开局＝失败，不延后（窗口结束由客户端重试）。
            return AdminOutcome.Fail(
                ResultCodes.RoomRotating,
                "房间正在轮换，本次开局未被接受（请稍后重试）。",
                new JsonObject { ["retryAfterMs"] = RetryAfterMs });
        }

        return await InQueueAsync(token => StartCoreAsync(args, token), ct).ConfigureAwait(false);
    }

    private async Task<AdminOutcome> StartCoreAsync(JsonObject args, CancellationToken ct)
    {
        var room = _host.Room;
        var service = _host.Service;

        if (room.State != RoomState.Lobby)
        {
            return AdminOutcome.Fail(ResultCodes.RoomStarted, $"房间状态为 {room.State}，不能开局。");
        }

        if (service.Executor.HasMatch)
        {
            return AdminOutcome.Fail(ResultCodes.RoomStarted, "已存在活跃对局。");
        }

        if (room.HasEmptySeat)
        {
            return AdminOutcome.Fail(ResultCodes.RoomNotReady, "仍有空座或未批准的座位，不能开局（引擎 1v1 需要两个 controller）。");
        }

        if (!room.TryBeginStart())
        {
            return AdminOutcome.Fail(ResultCodes.RoomStarted, "房间正在开局中。");
        }

        try
        {
            // 开局输入＝**集合全部条目 ＋ 沙盒固定包**（R5 唯一真源）；装配前先做一次真对账（独立提交）。
            var input = _host.Packs.PrepareAssembly();
            var dirs = input.Directories;

            if (dirs.Count == 0)
            {
                // 空集合是合法状态，但开局必然失败：必须特化说明（不能输出笼统的"卡池装配失败"）。
                room.MarkLobby();
                const string emptyMessage = "卡包集合为空（请先 pool.register）：没有任何可装载的卡包目录。";
                var emptyReport = new MatchPoolReport(dirs, 0, Failure: emptyMessage);
                service.Pool = emptyReport;
                _host.PublishEvent("pool.assemble.failed", emptyReport.ToJson());
                return AdminOutcome.Fail(ResultCodes.MissingAssets, emptyMessage, emptyReport.ToJson());
            }

            var assembly = TryAssemble(
                dirs,
                _host.Packs.LastGood,
                out var failure,
                out var usedLastGood,
                out var sandboxDropped,
                out var used);
            var fallback = usedLastGood || sandboxDropped;
            var degradation = new JsonObject
            {
                ["usedLastGood"] = usedLastGood,
                ["sandboxDropped"] = sandboxDropped,
                ["reconciled"] = new JsonArray(input.Reconciled
                    .Select(entry => (JsonNode)new JsonObject
                    {
                        ["packageId"] = entry.PackageId,
                        ["version"] = entry.Version,
                        ["cause"] = entry.Cause,
                    })
                    .ToArray()),
            };

            if (assembly is null)
            {
                room.MarkLobby();
                var report = new MatchPoolReport(dirs, 0, Failure: failure);
                service.Pool = report;
                _host.PublishEvent("pool.assemble.failed", WithDegradation(report, input, degradation));
                return AdminOutcome.Fail(
                    ResultCodes.MissingAssets,
                    $"卡池装配失败：{failure}",
                    WithAssemblyInput(report.ToJson(), input, degradation));
            }

            // 只取**本局真正可装载**的卡（中间层能定版的那些）：注册面可能登记了未进 lock 的卡
            // （例如沙盒包的脚手架卡），把它们算进生成池会让 AI 组出"解析不出来"的卡组。
            _host.Packs.MarkLastGood(used);

            var pool = assembly.Definitions
                .Where(entry => assembly.Bridge.Resolve(AssetKind.Card, entry.Id).Status == IdResolutionStatus.Resolved)
                .ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
            var tier = _host.Options.EffectiveDeckTier;
            var requiredDeckSize = tier == DeckRuleTier.Lenient ? 0 : _host.Options.RequiredDeckSize;

            var seatDecks = new IReadOnlyList<string>?[2];
            var seatConfigs = new PlayerDeckConfiguration?[2];
            JsonObject? seatFailure = null;

            for (var seat = 0; seat < 2; seat++)
            {
                if (room.SeatAt(seat) is not { } declared)
                {
                    room.MarkLobby();
                    return AdminOutcome.Fail(ResultCodes.RoomNotReady, $"座位 {seat} 没有声明。");
                }

                var deck = declared.Deck;
                PlayerDeckConfiguration? config = null;

                if (!string.IsNullOrWhiteSpace(declared.DeckId))
                {
                    // 卡组资产通路（R6/D27/D28）：显式标识优先于自动构筑——AI 座位也不例外。
                    var lookup = assembly.Decks.Resolve(declared.DeckId);
                    if (lookup.Status == DeckLookupStatus.Ambiguous)
                    {
                        room.MarkLobby();
                        return AdminOutcome.Fail(
                            ResultCodes.DeckInvalid,
                            $"座位 {seat} 的卡组标识 '{declared.DeckId}' 命中多份卡组"
                            + $"（{string.Join("、", lookup.Candidates)}）——请给全名。");
                    }

                    if (lookup.Status != DeckLookupStatus.Resolved)
                    {
                        room.MarkLobby();
                        return AdminOutcome.Fail(
                            ResultCodes.DeckInvalid,
                            $"座位 {seat} 的卡组标识 '{declared.DeckId}' 不在本局可用集合"
                            + "（不存在／未入 lock／内容不合法）。");
                    }

                    var definition = lookup.Deck!;
                    var seatConfig = declared.DeckConfig is { } requested
                        ? new PlayerDeckConfiguration(requested.MainFaction, requested.AllyFaction)
                        : null;

                    // 座位显式配置只与卡组内嵌的**最终值**比对（D47）。
                    if (seatConfig is not null
                        && (!string.Equals(seatConfig.MainFaction, definition.DeckConfig.MainFaction, StringComparison.Ordinal)
                            || !string.Equals(seatConfig.AllyFaction, definition.DeckConfig.AllyFaction, StringComparison.Ordinal)))
                    {
                        room.MarkLobby();
                        return AdminOutcome.Fail(
                            ResultCodes.DeckInvalid,
                            $"座位 {seat} 的构筑配置与卡组 '{lookup.AppId}' 不一致"
                            + $"（卡组：主国 {definition.DeckConfig.MainFaction}／盟国 {definition.DeckConfig.AllyFaction}）。");
                    }

                    config = seatConfig ?? definition.DeckConfig;
                    deck = definition.ToCardIds();
                }
                else if (deck.Count == 0 && room.IsAiSeat(seat))
                {
                    // AI 卡组在**装配卡池之后**由该池生成（不再依赖进程级预装池）。
                    // 目标张数：strict/lenient 用配置值；none 档没有张数口径，退回引擎默认（否则生成器只返回空)。
                    var aiTarget = requiredDeckSize > 0 ? requiredDeckSize : DeckRules.DefaultRequiredSize;
                    var generator = new AiDeckGenerator(pool, _host.Options.Seed is { } seed ? new Random(seed + seat) : null);
                    deck = generator.Generate(
                        aiTarget,
                        bestEffort: tier != DeckRuleTier.Strict,
                        relaxCopyLimit: tier != DeckRuleTier.Strict);
                    if (deck.Count == 0)
                    {
                        room.MarkLobby();
                        return AdminOutcome.Fail(
                            ResultCodes.DeckInvalid,
                            $"座位 {seat}（AI）无法从本局卡池生成卡组（卡种不足）。");
                    }
                }

                config ??= declared.DeckConfig is { } deckConfig
                    ? new PlayerDeckConfiguration(deckConfig.MainFaction, deckConfig.AllyFaction)
                    : null;

                if (tier != DeckRuleTier.None)
                {
                    var violations = new List<DeckViolation>(DeckRules.Validate(deck, pool, requiredDeckSize, tier));
                    if (DeckRules.ValidateFaction(config, tier) is { } factionViolation)
                    {
                        violations.Add(factionViolation);
                    }

                    violations.AddRange(DeckRules.ValidateFactionComposition(deck, pool, config, tier));

                    if (violations.Count > 0)
                    {
                        seatFailure = new JsonObject
                        {
                            ["seat"] = seat,
                            ["reason"] = "卡组不合法：" + string.Join("；", violations.Select(v => v.Message)),
                        };
                        break;
                    }
                }

                seatDecks[seat] = deck;
                seatConfigs[seat] = config;
                room.SetDeck(seat, deck);
            }

            if (seatFailure is not null)
            {
                room.MarkLobby();
                return AdminOutcome.Fail(
                    ResultCodes.DeckInvalid,
                    seatFailure["reason"]!.GetValue<string>(),
                    seatFailure);
            }

            var options = new MatchLaunchOptions
            {
                PackDirectories = dirs,
                Assembly = assembly,
                SeatA = new MatchSeatConfig(0, seatDecks[0]!, Name: room.SeatAt(0)?.DisplayName, DeckConfiguration: seatConfigs[0]),
                SeatB = new MatchSeatConfig(1, seatDecks[1]!, Name: room.SeatAt(1)?.DisplayName, DeckConfiguration: seatConfigs[1]),
                EnforceDeckRules = false, // 已在上面用本局卡池校验过（同一份规则实现）。
                RequiredDeckSize = requiredDeckSize,
                DeckRuleTier = tier,
                Seed = _host.Options.Seed,
            };

            var outcome = await service.CreateMatchAsync(options, ct).ConfigureAwait(false);
            if (outcome.Status == ActionOutcome.StatusFailed)
            {
                room.MarkLobby();
                var report = new MatchPoolReport(dirs, assembly.Definitions.Count, fallback, outcome.FailureMessage);
                service.Pool = report;
                _host.PublishEvent("pool.assemble.failed", WithDegradation(report, input, degradation));
                return AdminOutcome.Fail(
                    outcome.FailureCode ?? ResultCodes.EngineRejected,
                    outcome.FailureMessage ?? "开局失败。",
                    WithAssemblyInput(report.ToJson(), input, degradation));
            }

            room.MarkInMatch();

            var poolReport = new MatchPoolReport(dirs, assembly.Definitions.Count, fallback);
            service.Pool = poolReport;
            _host.PublishEvent("pool.assembled", WithDegradation(poolReport, input, degradation));

            var payload = WithAssemblyInput(poolReport.ToJson(), input, degradation);
            payload["roomId"] = room.RoomId;
            payload["state"] = room.State.ToString();
            payload["definitions"] = assembly.Definitions.Count;
            payload["fallback"] = fallback;
            payload["packs"] = new JsonArray(assembly.PackPrefixes
                .Select(prefix => (JsonNode)JsonValue.Create(prefix)!)
                .ToArray());
            payload["match"] = outcome.Payload;
            return AdminOutcome.Success(payload);
        }
        catch (Exception ex)
        {
            room.MarkLobby();
            return AdminOutcome.Fail(ResultCodes.Internal, $"开局异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 降级/装配输入的**管理面**形态（S7）：带目录，供人定位；通道事件用 <see cref="WithDegradation"/>（不含目录）。
    /// </summary>
    private static JsonObject WithAssemblyInput(JsonNode node, PackAssemblyInput input, JsonObject degradation)
    {
        var payload = (JsonObject)node.DeepClone();
        payload["assemblyInput"] = new JsonObject
        {
            // 集合条目（按注册序；沙盒固定包不在这份列表里——它由 sandboxIncluded 表达）。
            ["packages"] = new JsonArray(input.Packages
                .Select((entry, index) => (JsonNode)new JsonObject
                {
                    ["id"] = entry.PackageId,
                    ["version"] = entry.Version,
                    ["directory"] = index < input.Directories.Count ? input.Directories[index] : null,
                })
                .ToArray()),
            ["sandboxIncluded"] = input.SandboxIncluded,
            ["usedLastGood"] = degradation["usedLastGood"]!.GetValue<bool>(),
            ["sandboxDropped"] = degradation["sandboxDropped"]!.GetValue<bool>(),
            ["reconciled"] = degradation["reconciled"]!.DeepClone(),
        };

        return payload;
    }

    /// <summary>
    /// 降级/装配输入的**事件**形态：**不含任何绝对目录**（通道事件经管理器原样转发，可能落到非管理连接）。
    /// </summary>
    private static JsonObject WithDegradation(MatchPoolReport report, PackAssemblyInput input, JsonObject degradation)
    {
        var payload = (JsonObject)report.ToJson();
        payload["degradation"] = degradation.DeepClone();
        payload["assembly"] = new JsonObject
        {
            ["packages"] = new JsonArray(input.Packages
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["id"] = entry.PackageId,
                    ["version"] = entry.Version,
                })
                .ToArray()),
            ["sandboxIncluded"] = input.SandboxIncluded,
        };

        return payload;
    }

    /// <summary>
    /// 按本局加载计划装配卡池：**沙盒包非法时先剔除它重试一次**（R3 降级），成功后告警；
    /// 仍失败才拒绝开局。
    /// </summary>
    private MatchCardAssembly? TryAssemble(
        IReadOnlyList<string> directories,
        IReadOnlyList<string>? lastGood,
        out string? failure,
        out bool usedLastGood,
        out bool sandboxDropped,
        out IReadOnlyList<string> used)
    {
        failure = null;
        usedLastGood = false;
        sandboxDropped = false;
        used = directories;

        // 装配前先补齐沙盒包元数据（sidecar ＋ lock）：装配链是 lock 驱动的，
        // 调试面新写的卡若没有 sidecar/lock，会连累整个沙盒包被判非法、进而被下面的降级剔除。
        // **只发生在开局装配路径**（试算绝不写盘）。
        SyncSandboxMetadata(directories);

        try
        {
            return MatchCardAssembly.Register(directories);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
            or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            failure = ex.Message;
        }

        // S10 回退：用**最后一次成功的包集合**重试一次（磁盘内容坏了不该让实例失去开局能力）。
        if (lastGood is { Count: > 0 }
            && !lastGood.SequenceEqual(directories, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var cached = MatchCardAssembly.Register(lastGood);
                usedLastGood = true;
                failure = null;
                used = lastGood;
                return cached;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
                or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
            {
                failure = $"{failure}；按最后一次成功的包集合重试后仍失败：{ex.Message}";
            }
        }

        var sandbox = SandboxPackDirectory();
        var without = directories
            .Where(dir => !string.Equals(Path.GetFullPath(dir), sandbox, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (without.Length == directories.Count)
        {
            return null; // 没有沙盒包可剔除：原样失败。
        }

        try
        {
            var assembly = MatchCardAssembly.Register(without);
            sandboxDropped = true;
            failure = null;
            used = without;
            return assembly;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
            or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            failure = $"{failure}；剔除沙盒包重试后仍失败：{ex.Message}";
            return null;
        }
    }

    private string SandboxPackDirectory()
        => Path.GetFullPath(Path.Combine(_host.Options.SandboxRoot, "pack"));

    /// <summary>
    /// 若本次装配包含沙盒包，先补齐其元数据（sidecar ＋ lock）。
    /// <para>
    /// 幂等且**不抛**：失败只记日志——装配随后仍会尝试，最坏落到底部的剔除降级。
    /// </para>
    /// </summary>
    private void SyncSandboxMetadata(IReadOnlyList<string> directories)
    {
        var sandbox = SandboxPackDirectory();
        var included = directories.Any(
            dir => string.Equals(Path.GetFullPath(dir), sandbox, StringComparison.OrdinalIgnoreCase));
        if (!included)
        {
            return;
        }

        try
        {
            var result = _host.Service.Sandbox.EnsurePackageMetadata();
            if (result.Obstacles.Count > 0)
            {
                Console.Error.WriteLine($"[pool] 沙盒包元数据存在障碍：{string.Join("；", result.Obstacles)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pool] 沙盒包元数据同步失败（不阻断装配）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<AdminOutcome> AbortAsync(CancellationToken ct)
    {
        if (_host.IsDraining)
        {
            // drain 窗口内已无活跃对局：幂等成功（空操作，不报错）。
            return AdminOutcome.Success(new JsonObject
            {
                ["state"] = "ended",
                ["winner"] = null,
                ["code"] = ResultCodes.AlreadyEnded,
            }, "房间正在轮换，对局已结束。");
        }

        return await InQueueAsync(
            token =>
            {
                var match = _host.Service.Executor.Match;
                if (match is null || match.State == Orc.Game.MatchState.Ended)
                {
                    return Task.FromResult(AdminOutcome.Success(new JsonObject
                    {
                        ["state"] = "ended",
                        ["winner"] = null,
                        ["code"] = ResultCodes.AlreadyEnded,
                    }, "对局已结束（幂等成功）。"));
                }

                var aborted = match.Abort();
                if (!aborted && match.State != Orc.Game.MatchState.Ended)
                {
                    // 引擎在准备/换牌相位没有"无胜者终局"的合法出口（E2/E3 保留相位门禁，见 PROGRESS 的偏差记录）。
                    // 服务器仍然终结房间并轮换——否则"管理员终止"在换牌期会变成空操作。
                }

                _host.AfterMatchAborted();

                return Task.FromResult(AdminOutcome.Success(new JsonObject
                {
                    ["roomId"] = _host.Room.RoomId,
                    ["state"] = match.State.ToString(),
                    ["winner"] = match.Winner?.Index,
                    ["endReason"] = match.EndReason?.ToString(),
                    ["aborted"] = aborted,
                }, "对局已终止（无胜者）。"));
            },
            ct).ConfigureAwait(false);
    }

    private async Task<AdminOutcome> ResetSandboxAsync(CancellationToken ct)
    {
        if (_host.IsDraining)
        {
            return AdminOutcome.Fail(
                ResultCodes.RoomRotating,
                "房间正在轮换，请稍后重试。",
                new JsonObject { ["retryAfterMs"] = RetryAfterMs });
        }

        return await InQueueAsync(
            token =>
            {
                var packRoot = SandboxPackDirectory();
                if (Directory.Exists(packRoot))
                {
                    Directory.Delete(packRoot, recursive: true);
                }

                var scaffold = _host.Service.Sandbox.EnsureScaffold();

                // 重建后必须补齐元数据（sidecar ＋ lock）：否则新建的沙盒包无 lock，下一局装配会被判非法。
                var metadata = _host.Service.Sandbox.EnsurePackageMetadata();
                _host.PublishEvent("sandbox.reset", new JsonObject
                {
                    ["packRoot"] = scaffold.PackRoot,
                    ["createdManifest"] = scaffold.CreatedManifest,
                    ["createdPlaceholder"] = scaffold.CreatedPlaceholder,
                    ["metadataWritten"] = metadata.WrittenFiles.Count,
                });

                return Task.FromResult(AdminOutcome.Success(new JsonObject
                {
                    ["packRoot"] = scaffold.PackRoot,
                    ["createdManifest"] = scaffold.CreatedManifest,
                    ["createdPlaceholder"] = scaffold.CreatedPlaceholder,
                    ["metadataWritten"] = metadata.WrittenFiles.Count,
                }, "沙盒包已重置（服务器未重启）。"));
            },
            ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 队列与参数

    private async Task<AdminOutcome> InQueueAsync(
        Func<CancellationToken, Task<AdminOutcome>> work,
        CancellationToken ct,
        bool allowWhileDraining = false)
    {
        if (_host.IsDraining && !allowWhileDraining)
        {
            return AdminOutcome.Fail(
                ResultCodes.RoomRotating,
                "房间正在轮换，请稍后重试。",
                new JsonObject { ["retryAfterMs"] = RetryAfterMs });
        }

        try
        {
            return await _host.Queue.EnqueueAsync(WorkPriority.Command, work, ct).ConfigureAwait(false);
        }
        catch (WorkQueueFullException)
        {
            return AdminOutcome.Fail(ResultCodes.Busy, "串行队列已满，请稍后重试。");
        }
        catch (ObjectDisposedException)
        {
            return AdminOutcome.Fail(ResultCodes.RoomRotating, "服务端正在关闭。");
        }
    }

    private static string RequireConnectionId(JsonObject args)
        => OptionalString(args, "connectionId") is { Length: > 0 } id
            ? id
            : throw new ArgumentException("缺少必填字符串参数 'connectionId'。");

    private static string? OptionalString(JsonObject args, string name)
        => args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? OptionalInt(JsonObject args, string name)
        => args[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
