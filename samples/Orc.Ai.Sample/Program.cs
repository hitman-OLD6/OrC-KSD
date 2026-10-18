// =============================================================================
// Orc.Ai.Sample —— 启发式 AI 玩家的可运行示例
//
// 演示：真实对局创建面（卡包目录 ＋ 显式 39 张卡组）→ AI 占 1 号座位 → 宿主单步驱动
//       （StepAsync 一个动作 ＋ 其待决应答）→ 脚本对手（0 号座位）只会结束回合 → 打到终局。
//
// 运行：
//   dotnet run --project samples/Orc.Ai.Sample
//   dotnet run --project samples/Orc.Ai.Sample -- --pack <卡包目录>
// =============================================================================

using System.Text;
using Orc.Ai;
using Orc.Game;
using Orc.Game.Ui;
using Orc.Mcp;
using Orc.Mcp.Hosting;
using Orc.Mcp.Match;

namespace Orc.Ai.Sample;

internal static class Program
{
    private const int AiSeat = 1;
    private const int ScriptedSeat = 0;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var packDirectory = ParsePack(args) ?? FindDefaultPack();
        if (packDirectory is null || !Directory.Exists(packDirectory))
        {
            Console.WriteLine("未找到 AI 示例卡包（samples/card-pack-ai）——请用 --pack <目录> 指定。");
            return 1;
        }

        Console.WriteLine($"=== Orc.Ai 示例 ===\n卡包：{packDirectory}");

        var sandboxRoot = Path.Combine(Path.GetTempPath(), "orc-ai-sample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);

        var mcp = new McpService(new McpServiceOptions
        {
            Mode = McpServiceMode.Player,
            SandboxRoot = sandboxRoot,
            Seed = 20261017,
            AgentSeat = AiSeat,
        });

        IUiProjectionProvider? projection = null;
        mcp.MatchChanged += (_, change) =>
        {
            projection = change.Change == MatchLifecycleChange.Created && change.Match is { } created
                ? created.UiProjections.CreateProvider(artLookup: null, matchId: "ai-sample")
                : null;
        };

        var launch = new MatchLaunchOptions
        {
            PackDirectories = new[] { packDirectory },
            SeatA = new MatchSeatConfig(ScriptedSeat, SampleDeck(), "脚本对手"),
            SeatB = new MatchSeatConfig(AiSeat, SampleDeck(), "启发式 AI"),
            Seed = 20261017,
        };

        var created = await mcp.CreateMatchAsync(launch);
        Console.WriteLine($"[建局] status={created.Status} {created.FailureMessage ?? ""}");
        if (created.Status != ActionOutcome.StatusCompleted || mcp.ActiveMatch is not { } match || projection is null)
        {
            return 1;
        }

        Console.WriteLine($"[建局] 回合={match.TurnNumber} 相位={match.Phase}；双方卡组={match.Players[0].Deck.Count}/{match.Players[1].Deck.Count}");

        var pilot = new AiPilot(mcp, projection, AiSeat);
        var opponentMulliganDone = false;
        var steps = 0;
        const int maxSteps = 4000;

        // 脚本对手（0 号座位）的待决应答器：模拟"人在场"——回调只认领，提交放在主循环（不在桥线程上阻塞）。
        // AI 的动作链已预占预期应答者，故此处认领会失败、不会抢 AI 的选择。
        PendingSelection? opponentPending = null;
        using var targeting = mcp.SubscribeTargetingRequests(pending =>
        {
            if (mcp.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out _))
            {
                opponentPending = pending;
            }
        });

        while (match.State != MatchState.Ended && steps++ < maxSteps)
        {
            if (opponentPending is { } pendingForOpponent)
            {
                opponentPending = null;
                IReadOnlyList<int> picks = pendingForOpponent.Min > 0 && pendingForOpponent.Candidates.Count > 0
                    ? new[] { pendingForOpponent.Candidates[0].Index }
                    : Array.Empty<int>();
                var submitted = mcp.TrySubmitTargeting(
                    pendingForOpponent.SessionId, picks, cancel: false, TargeterBridgeAdapter.ClaimantHost, out var reason);
                Console.WriteLine(submitted
                    ? $"[对手] 待决应答（{pendingForOpponent.SelectorName}）已提交"
                    : $"[对手] 待决应答失败：{reason}");
                continue;
            }

            var view = projection.Project(match, match.Players[AiSeat]);

            if (string.Equals(view.Phase, "mulligan", StringComparison.Ordinal))
            {
                if (!opponentMulliganDone)
                {
                    // 不换牌直接确认（换牌策略＝不换）。
                    var done = await mcp.MulliganDoneAsync(ScriptedSeat);
                    opponentMulliganDone = done.Status != ActionOutcome.StatusFailed;
                    Console.WriteLine($"[对手] 换牌确认 → {done.Status}");
                }

                var mulligan = await pilot.StepAsync();
                Log(mulligan);
                continue;
            }

            if (string.Equals(view.ActivePlayerSide, "self", StringComparison.Ordinal))
            {
                var step = await pilot.StepAsync();
                Log(step);
                continue;
            }

            var endTurn = await mcp.EndTurnAsync();
            Console.WriteLine($"[对手] 结束回合 → {endTurn.Status}");
        }

        Console.WriteLine();
        Console.WriteLine(steps >= maxSteps
            ? $"[终止] 达到步数上限 {maxSteps}（对局未结束）"
            : "[终止] 对局已结束");
        Console.WriteLine($"[终局] 状态={match.State} 回合={match.TurnNumber} 胜者={match.Players.FirstOrDefault(p => ReferenceEquals(p, match.Winner))?.Index.ToString() ?? "无"} 原因={match.EndReason}");
        Console.WriteLine($"[终局] 总部血量：脚本对手={match.Players[ScriptedSeat].HqHealth}，AI={match.Players[AiSeat].HqHealth}");

        mcp.DisposeMatch();

        try
        {
            Directory.Delete(sandboxRoot, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响示例结论。
        }

        return match.State == MatchState.Ended ? 0 : 2;
    }

    private static void Log(AiStepResult step)
    {
        if (!step.Performed)
        {
            return;
        }

        var outcome = step.Outcome is { } o
            ? o.Failed
                ? $"failed{(o.FailureMessage is null ? "" : "/" + o.FailureMessage)}"
                : o.IsTargeting ? "targeting" : "completed"
            : "n/a";
        Console.WriteLine($"[AI] {step.Kind} → {outcome}{(step.Note is null ? "" : "：" + step.Note)}");
    }

    /// <summary>示例卡组：39 张（KARDS 默认口径；同名不限）。</summary>
    private static IReadOnlyList<string> SampleDeck() =>
    [
        .. Enumerable.Repeat("ai.sample.infantry", 15),
        .. Enumerable.Repeat("ai.sample.armor", 10),
        .. Enumerable.Repeat("ai.sample.artillery", 5),
        .. Enumerable.Repeat("ai.sample.order", 5),
        .. Enumerable.Repeat("ai.sample.counter", 4),
    ];

    private static string? ParsePack(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--pack", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return null;
    }

    private static string? FindDefaultPack()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
