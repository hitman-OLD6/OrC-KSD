using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Game.Targeting;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 选靶异常传播（F18）：桥的驱动循环异常必须让动作以 <c>engine-rejected</c> 收束，
/// 并且不得把执行器永久卡在 <c>busy</c>。
/// </summary>
public class TargetingFaultTests
{
    [Fact]
    public async Task TargetingFault_EndsActionAsEngineRejected_AndClearsBusy()
    {
        using var temp = new TestSupport.TempDirectory("orc-targeting-fault");
        await using var host = await StartHostAsync(temp.Path);
        await using var seat0 = await TestSupport.ConnectAsync(host.Port);
        await using var seat1 = await TestSupport.ConnectAsync(host.Port);

        var token = TestSupport.SeatToken(await seat0.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await seat1.CallAsync("room.claim", TestSupport.Claim(1, "remote")));
        Assert.True((await seat0.CallAsync("room.start", new JsonObject { ["seatToken"] = token })).Ok);
        Assert.True((await seat0.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
        Assert.True((await seat1.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);

        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var view = await seat0.CallAsync("observe.view");
                return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
            },
            phase => phase == "play",
            TimeSpan.FromSeconds(15),
            throwOnTimeout: true);

        // 让执行器进入"忙"：真实动作挂起在待决选择上。
        var sessionId = await EnterTargetingAsync(seat0);
        Assert.NotNull(sessionId);
        Assert.True(host.Service.Executor.IsBusy);

        // 注入故障：桥的驱动循环在 NextAsync 上抛异常。
        host.Service.Bridge.BeginTargeting(new FaultingSession());

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(host.Service.Bridge.Fault),
            fault => fault is not null,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        // 下一个动作：必须回 engine-rejected（而不是 busy），并把活跃动作清空。
        var rejected = await seat0.CallAsync("match.end_turn");
        Assert.False(rejected.Ok);
        Assert.Equal(ResultCodes.EngineRejected, rejected.Code);
        Assert.False(host.Service.Executor.IsBusy);

        // 队列已经解卡：后续动作不再 busy/pending-targeting。
        var next = await seat0.CallAsync("match.end_turn");
        Assert.NotEqual(ResultCodes.Busy, next.Code);
        Assert.NotEqual(ResultCodes.PendingTargeting, next.Code);
    }

    /// <summary>空卡组 → <c>deck-invalid</c>（F19；走真实 socket）。</summary>
    [Fact]
    public async Task Claim_EmptyDeck_IsDeckInvalid()
    {
        using var temp = new TestSupport.TempDirectory("orc-targeting-fault");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var claim = await client.CallAsync("room.claim", new JsonObject
        {
            ["seat"] = 0,
            ["kind"] = "local",
            ["deck"] = new JsonArray(),
        });

        Assert.False(claim.Ok);
        Assert.Equal(ResultCodes.DeckInvalid, claim.Code);
    }

    private static async Task<string?> EnterTargetingAsync(OrcClient client)
    {
        var view = await client.CallAsync("observe.view");
        foreach (var card in view.Details!["view"]!["selfHand"]!.AsArray())
        {
            var pre = await client.CallAsync("play.unit_pre", new JsonObject
            {
                ["ref"] = card!["uid"]!.GetValue<string>(),
            });

            if (pre.Ok && pre.Code == ResultCodes.Targeting)
            {
                return pre.Details!["pending"]!["sessionId"]!.GetValue<string>();
            }
        }

        return null;
    }

    private static Task<ServerHost> StartHostAsync(string sandboxRoot)
        => ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            EnforceDeckRules = false,
        });

    /// <summary>立刻在 <see cref="ITargeterSession.NextAsync"/> 上抛异常的会话（模拟选靶会话内部故障）。</summary>
    private sealed class FaultingSession : ITargeterSession
    {
        public Task<ISelectorInstance?> NextAsync()
            => Task.FromException<ISelectorInstance?>(new InvalidOperationException("注入的选靶会话故障。"));

        public TargeterResult? Result => null;
    }
}
