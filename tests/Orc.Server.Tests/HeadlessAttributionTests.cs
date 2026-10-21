using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 调用流水与摘要的归因口径（R6/R7）：
/// 应答型动作在 <c>calls.jsonl</c> 可唯一归因；<c>summary.errorCount</c> 只统计基础设施错误。
/// </summary>
public class HeadlessAttributionTests
{
    /// <summary>
    /// R6：一次「发起 → 应答」可仅凭 calls.jsonl 建立对应关系——
    /// 发起记录带 <c>pendingSessionId</c>，应答记录带 <c>answers</c>/<c>action</c>/<c>picks</c>/<c>pickedRefs</c> 与段区间。
    /// </summary>
    [Fact]
    public async Task CallsJsonl_Attributes_Answering_Action()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await HeadlessTests.StartAsync(temp.Path);

        var seat = host.Headless!.AgentSeat!.Value;
        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = HeadlessTests.AgentToken(host) })).Ok);
        await HeadlessTests.WaitForStateAsync(host, "in-match");

        var sessionId = await StartDeploymentTargetingAsync(agent, seat);
        Assert.False(string.IsNullOrWhiteSpace(sessionId), "若干回合内没有拿到部署选位请求。");

        var submit = await agent.CallAsync("target.submit", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["picks"] = new JsonArray(1),
        });
        Assert.True(submit.Ok, $"{submit.Code}: {submit.Message}");

        await ConcedeAsync(agent, seat);
        await WaitForSummaryAsync(host);

        var calls = HeadlessTests.ReadJsonLines(host.Headless.CallsPath);

        var initiating = calls.Single(call =>
            string.Equals(call["tool"]!.GetValue<string>(), "play.unit_pre", StringComparison.Ordinal)
            && string.Equals(call["pendingSessionId"]?.GetValue<string>(), sessionId, StringComparison.Ordinal));

        var answering = calls.Single(call =>
            string.Equals(call["tool"]!.GetValue<string>(), "target.submit", StringComparison.Ordinal)
            && string.Equals(call["answers"]?.GetValue<string>(), sessionId, StringComparison.Ordinal));

        // 回答的是哪个动作、选了哪个候选——两个方向都能从记录里读出来。
        Assert.Equal("deploy", answering["action"]!.GetValue<string>());
        Assert.Equal(1, answering["picks"]!.AsArray().Count);
        Assert.Single(answering["pickedRefs"]!.AsArray());

        // 段区间记录在**真实的产段者**（submit）上，不靠回填。
        Assert.NotNull(answering["segmentsFrom"]);
        Assert.NotNull(answering["segmentsTo"]);

        // 发起记录只做配对锚点：pre-play 只挂起、不产段，故它自己没有段区间（段在应答记录上）。
        Assert.Null(initiating["segmentsFrom"]);
    }

    /// <summary>R7：业务否定（<c>pending-targeting</c>）只计入 <c>failedCalls</c>，不污染 <c>errorCount</c>。</summary>
    [Fact]
    public async Task Summary_BusinessRejection_DoesNotCountAsError()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await HeadlessTests.StartAsync(temp.Path);

        var seat = host.Headless!.AgentSeat!.Value;
        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = HeadlessTests.AgentToken(host) })).Ok);
        await HeadlessTests.WaitForStateAsync(host, "in-match");

        // 无待决请求时认领＝业务否定（不是服务器故障）。
        var claim = await agent.CallAsync("target.claim", new JsonObject { ["sessionId"] = "no-such-session" });
        Assert.False(claim.Ok);
        Assert.Equal(ResultCodes.PendingTargeting, claim.Code);

        await ConcedeAsync(agent, seat);
        await WaitForSummaryAsync(host);

        var summary = JsonNode.Parse(File.ReadAllText(host.Headless.SummaryPath))!;
        Assert.Equal(0, summary["errorCount"]!.GetValue<int>());
        Assert.True(summary["failedCalls"]!.GetValue<int>() >= 1);
    }

    /// <summary>R7：门禁拒绝（<c>unauthorized</c>）属基础设施错误口径，仍计入 <c>errorCount</c>。</summary>
    [Fact]
    public async Task Summary_GateDenial_StillCountsAsError()
    {
        using var temp = new TestSupport.TempDirectory("orc-headless");
        await using var host = await HeadlessTests.StartAsync(temp.Path);

        var seat = host.Headless!.AgentSeat!.Value;
        await using var agent = await TestSupport.ConnectAsync(host.Port);
        Assert.True((await agent.CallAsync("room.resume", new JsonObject { ["seatToken"] = HeadlessTests.AgentToken(host) })).Ok);
        await HeadlessTests.WaitForStateAsync(host, "in-match");

        // 未绑定座位的旁观者调用动作类工具＝门禁拒绝（unauthorized）。
        await using var bystander = await TestSupport.ConnectAsync(host.Port);
        var denied = await bystander.CallAsync("match.end_turn");
        Assert.Equal(ResultCodes.Unauthorized, denied.Code);

        await ConcedeAsync(agent, seat);
        await WaitForSummaryAsync(host);

        var summary = JsonNode.Parse(File.ReadAllText(host.Headless.SummaryPath))!;
        Assert.Equal(1, summary["errorCount"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// 反复尝试把手里第一张单位卡「预打出」，直到进入待决（拿到 sessionId）；
    /// 期间完成换牌、必要时结束回合换点。返回 null＝若干回合内没有可预打出的单位。
    /// </summary>
    private static async Task<string?> StartDeploymentTargetingAsync(OrcClient agent, int seat)
    {
        for (var round = 0; round < 12; round++)
        {
            var view = await agent.CallAsync("observe.view");
            if (!view.Ok)
            {
                await Task.Delay(200);
                continue;
            }

            var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
            if (string.Equals(phase, "mulligan", StringComparison.Ordinal))
            {
                await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                continue;
            }

            foreach (var node in view.Details!["view"]!["selfHand"]!.AsArray())
            {
                var reference = node!["uid"]!.GetValue<string>();
                var prePlay = await agent.CallAsync("play.unit_pre", new JsonObject { ["ref"] = reference });
                if (prePlay.Details?["pending"]?["sessionId"]?.GetValue<string>() is { } sessionId)
                {
                    return sessionId;
                }
            }

            await agent.CallAsync("match.end_turn", new JsonObject { ["owner"] = seat });
            await Task.Delay(300);
        }

        return null;
    }

    /// <summary>确认双方换牌、进入 play 相位（换牌相位下不推进局面，也不写终局摘要）。</summary>
    private static async Task ReachPlayPhaseAsync(OrcClient agent, int seat)
    {
        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await agent.CallAsync("observe.view");
                if (!view.Ok)
                {
                    return false;
                }

                var phase = view.Details!["view"]!["phase"]!.GetValue<string>();
                if (string.Equals(phase, "mulligan", StringComparison.Ordinal))
                {
                    await agent.CallAsync("mulligan.done", new JsonObject { ["owner"] = seat });
                    return false;
                }

                return string.Equals(phase, "play", StringComparison.Ordinal);
            },
            ready => ready,
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);
    }

    private static async Task ConcedeAsync(OrcClient agent, int seat)
    {
        await ReachPlayPhaseAsync(agent, seat);

        var concede = await agent.CallAsync("match.concede", new JsonObject { ["owner"] = seat });
        Assert.True(concede.Ok, $"{concede.Code}: {concede.Message}");
    }

    private static Task WaitForSummaryAsync(ServerHost host) => TestSupport.WaitUntilAsync(
        () => Task.FromResult(File.Exists(host.Headless!.SummaryPath)),
        exists => exists,
        TimeSpan.FromSeconds(15),
        throwOnTimeout: true);
}
