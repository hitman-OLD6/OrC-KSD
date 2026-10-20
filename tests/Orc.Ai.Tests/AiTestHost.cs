using Orc.Game;
using Orc.Game.Ui;
using Orc.Mcp;
using Orc.Mcp.Match;

namespace Orc.Ai.Tests;

/// <summary>测试宿主：建一局真实对局（AI 1 号 / 脚本 0 号）＋ 脚本对手的待决应答器。</summary>
internal sealed class AiTestHost : IDisposable
{
    private readonly IDisposable _targeting;
    private PendingSelection? _opponentPending;

    private AiTestHost(McpService service, Orc.Game.Match match, IUiProjectionProvider projection)
    {
        Service = service;
        Match = match;
        Projection = projection;
        Pilot = new AiPilot(service, projection, Seat);

        _targeting = service.SubscribeTargetingRequests(pending =>
        {
            // 模拟"人在场"：回调只认领；提交在主循环（不在桥线程上阻塞）。
            if (service.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out _))
            {
                _opponentPending = pending;
            }
        });
    }

    public const int Seat = 1;
    public const int OpponentSeat = 0;

    public McpService Service { get; }

    public Orc.Game.Match Match { get; }

    public IUiProjectionProvider Projection { get; }

    public AiPilot Pilot { get; }

    public bool MulliganConfirmed { get; private set; }

    public static async Task<AiTestHost> StartAsync(int seed = 20261017)
    {
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "orc-ai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);

        var service = new McpService(new McpServiceOptions
        {
            Mode = McpServiceMode.Player,
            SandboxRoot = sandboxRoot,
            Seed = seed,
            AgentSeat = Seat,
        });

        var launch = new MatchLaunchOptions
        {
            PackDirectories = new[] { PackDirectory() },
            SeatA = new MatchSeatConfig(OpponentSeat, SampleDeck(), "脚本对手"),
            SeatB = new MatchSeatConfig(Seat, SampleDeck(), "启发式 AI"),
            Seed = seed,
            // 示例卡包只有 5 张卡，无法组成 39 张合法构筑；AI 测试只验决策行为。
            EnforceDeckRules = false,
        };

        var created = await service.CreateMatchAsync(launch);
        if (created.Status != ActionOutcome.StatusCompleted || service.ActiveMatch is not { } match)
        {
            throw new InvalidOperationException($"建局失败：{created.Status} {created.FailureMessage}");
        }

        var projection = match.UiProjections.CreateProvider(artLookup: null, matchId: "ai-tests")
            ?? throw new InvalidOperationException("投影 provider 未注册（UI 包未激活）。");

        return new AiTestHost(service, match, projection);
    }

    /// <summary>示例卡包目录（仓库内 `samples/card-pack-ai`）。</summary>
    public static string PackDirectory()
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

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }

    /// <summary>示例卡组：39 张（同名不限）。</summary>
    public static IReadOnlyList<string> SampleDeck() =>
    [
        .. Enumerable.Repeat("ai.sample.infantry", 15),
        .. Enumerable.Repeat("ai.sample.armor", 10),
        .. Enumerable.Repeat("ai.sample.artillery", 5),
        .. Enumerable.Repeat("ai.sample.order", 5),
        .. Enumerable.Repeat("ai.sample.counter", 4),
    ];

    /// <summary>提交对手（0 号）的待决应答；无待决＝false。</summary>
    public bool TrySubmitOpponentPending()
    {
        if (_opponentPending is not { } pending)
        {
            return false;
        }

        _opponentPending = null;
        IReadOnlyList<int> picks = pending.Min > 0 && pending.Candidates.Count > 0
            ? new[] { pending.Candidates[0].Index }
            : Array.Empty<int>();

        Service.TrySubmitTargeting(pending.SessionId, picks, cancel: false, TargeterBridgeAdapter.ClaimantHost, out _);
        return true;
    }

    /// <summary>对手换牌确认（不换牌直接确认）。</summary>
    public async Task<bool> ConfirmOpponentMulliganAsync()
    {
        var done = await Service.MulliganDoneAsync(OpponentSeat);
        if (done.Status != ActionOutcome.StatusFailed)
        {
            MulliganConfirmed = true;
        }

        return MulliganConfirmed;
    }

    /// <summary>推进一整局（AI 单步循环 ＋ 脚本对手只结束回合）。返回执行步数。</summary>
    public async Task<int> RunAsync(int maxSteps = 2000)
    {
        var steps = 0;
        while (Match.State != MatchState.Ended && steps < maxSteps)
        {
            steps++;

            if (TrySubmitOpponentPending())
            {
                continue;
            }

            var view = Projection.Project(Match, Match.Players[Seat]);

            if (string.Equals(view.Phase, "mulligan", StringComparison.Ordinal))
            {
                if (!MulliganConfirmed)
                {
                    await ConfirmOpponentMulliganAsync();
                }

                await Pilot.StepAsync();
                continue;
            }

            if (string.Equals(view.ActivePlayerSide, "self", StringComparison.Ordinal))
            {
                await Pilot.StepAsync();
                continue;
            }

            await Service.EndTurnAsync();
        }

        return steps;
    }

    public void Dispose()
    {
        _targeting.Dispose();
        Service.DisposeMatch();
    }
}
