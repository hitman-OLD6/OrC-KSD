using System.Text.Json.Nodes;
using Orc.Game;
using Orc.Game.Decks;
using Orc.Mcp;
using Orc.Mcp.Match;
using Orc.Core;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.Protocol;
using Orc.Server.Execution;
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

    /// <summary>最后一次**成功**装配的包目录（S10 回退用；失败时按它重试一次）。</summary>
    private IReadOnlyList<string>? _lastGoodPackDirectories;

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
            ["packDirectories"] = new JsonArray(_host.Options.PackDirectories
                .Select(path => (JsonNode)JsonValue.Create(path)!)
                .ToArray()),
        });
    }

    private AdminOutcome ConnectionsList()
        => AdminOutcome.Success(new JsonObject
        {
            ["connections"] = new JsonArray(_host.ListConnections().Select(node => (JsonNode)node).ToArray()),
        });

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
            var dirs = ResolvePackDirectories(args);
            var assembly = TryAssemble(dirs, _lastGoodPackDirectories, out var failure, out var fallback, out var used);
            if (assembly is null)
            {
                room.MarkLobby();
                var report = new MatchPoolReport(dirs, 0, Failure: failure);
                service.Pool = report;
                _host.PublishEvent("pool.assemble.failed", report.ToJson());
                return AdminOutcome.Fail(
                    ResultCodes.MissingAssets,
                    $"卡池装配失败：{failure}",
                    report.ToJson());
            }

            // 只取**本局真正可装载**的卡（中间层能定版的那些）：注册面可能登记了未进 lock 的卡
            // （例如沙盒包的脚手架卡），把它们算进生成池会让 AI 组出"解析不出来"的卡组。
            _lastGoodPackDirectories = used;

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
                if (deck.Count == 0 && room.IsAiSeat(seat))
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

                var config = declared.DeckConfig is { } deckConfig
                    ? new PlayerDeckConfiguration(deckConfig.MainFaction, deckConfig.AllyFaction)
                    : null;

                if (tier != DeckRuleTier.None)
                {
                    var violations = new List<DeckViolation>(DeckRules.Validate(deck, pool, requiredDeckSize, tier));
                    if (DeckRules.ValidateFaction(config, tier) is { } factionViolation)
                    {
                        violations.Add(factionViolation);
                    }

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
                _host.PublishEvent("pool.assemble.failed", report.ToJson());
                return AdminOutcome.Fail(
                    outcome.FailureCode ?? ResultCodes.EngineRejected,
                    outcome.FailureMessage ?? "开局失败。",
                    report.ToJson());
            }

            room.MarkInMatch();

            var poolReport = new MatchPoolReport(dirs, assembly.Definitions.Count, fallback);
            service.Pool = poolReport;
            _host.PublishEvent("pool.assembled", poolReport.ToJson());

            return AdminOutcome.Success(new JsonObject
            {
                ["roomId"] = room.RoomId,
                ["state"] = room.State.ToString(),
                ["definitions"] = assembly.Definitions.Count,
                ["fallback"] = fallback,
                ["packs"] = new JsonArray(assembly.PackPrefixes
                    .Select(prefix => (JsonNode)JsonValue.Create(prefix)!)
                    .ToArray()),
                ["match"] = outcome.Payload,
            });
        }
        catch (Exception ex)
        {
            room.MarkLobby();
            return AdminOutcome.Fail(ResultCodes.Internal, $"开局异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 按本局加载计划装配卡池：**沙盒包非法时先剔除它重试一次**（R3 降级），成功后告警；
    /// 仍失败才拒绝开局。
    /// </summary>
    private MatchCardAssembly? TryAssemble(
        IReadOnlyList<string> directories,
        IReadOnlyList<string>? lastGood,
        out string? failure,
        out bool fallback,
        out IReadOnlyList<string> used)
    {
        failure = null;
        fallback = false;
        used = directories;

        // 装配前先补齐沙盒包元数据（sidecar ＋ lock）：装配链是 lock 驱动的，
        // 调试面新写的卡若没有 sidecar/lock，会连累整个沙盒包被判非法、进而被下面的降级剔除。
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
                fallback = true;
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
            fallback = true;
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

    private IReadOnlyList<string> ResolvePackDirectories(JsonObject args)
    {
        var dirs = new List<string>();

        if (args["packDirectories"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node?.GetValue<string>() is { Length: > 0 } path)
                {
                    dirs.Add(path);
                }
            }
        }

        if (dirs.Count == 0)
        {
            dirs.AddRange(_host.Options.PackDirectories);
        }

        // 沙盒包＝**实例级固定包**（强制加入每局并集，不由玩家声明），保证调试面造出的卡在轮换后仍可用。
        var sandbox = SandboxPackDirectory();
        if (Directory.Exists(sandbox)
            && !dirs.Any(dir => string.Equals(Path.GetFullPath(dir), sandbox, StringComparison.OrdinalIgnoreCase)))
        {
            dirs.Add(sandbox);
        }

        return dirs;
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
