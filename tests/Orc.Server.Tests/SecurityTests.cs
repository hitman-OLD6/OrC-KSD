using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 安全负面用例（F1/F2/F3/F20）：会话座位绑定、跨座位隔离、动作归属、房主身份、推送不外泄。
/// <para>
/// 全部走真 socket ＋ 完整启动链路；断言只看 <c>ok/code</c> 与推送，不看人读 message。
/// </para>
/// </summary>
public class SecurityTests
{
    // ---------------------------------------------------------------- S1 分类表与默认拒绝

    /// <summary>守卫 1：对外宣告的每个工具都必须有座位作用域归属（新工具漏登记即失败并打印工具名）。</summary>
    [Fact]
    public async Task EveryAdvertisedTool_HasSeatScopeRegistry()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var advertised = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        var unregistered = advertised.Where(name => !ServerHost.IsToolClassified(name)).ToArray();

        Assert.True(
            unregistered.Length == 0,
            "以下工具未登记座位作用域（新工具必须显式登记到四类或专门分支）：" + string.Join(", ", unregistered));
    }

    /// <summary>守卫 2：分类表里不得有过期/拼错的工具名（否则"默认拒绝"会被死条目掩盖）。</summary>
    [Fact]
    public async Task SeatScopeRegistry_HasNoDeadEntries()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var advertised = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var dead = ServerHost.RegisteredToolNames.Where(name => !advertised.Contains(name)).ToArray();

        Assert.True(dead.Length == 0, "分类表含未宣告的工具名（死条目）：" + string.Join(", ", dead));
    }

    /// <summary>默认拒绝行为守卫：临时登记一个假工具 → 未登记即 unauthorized（不解析参数、不入队）。</summary>
    [Fact]
    public async Task SeatScope_DefaultsToDeny_ForUnregisteredTool()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        host.Service.AddTool(
            "test.unregistered_tool",
            "test",
            "测试专用：故意未登记座位作用域。",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            (_, _) => Task.FromResult(McpToolResult.Success("不应被调用。")),
            readOnly: true);

        // 假工具确实在目录里（证明拒绝来自门禁，而不是"工具不存在"）。
        var advertised = (await client.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        Assert.Contains("test.unregistered_tool", advertised);

        var result = await client.CallAsync("test.unregistered_tool");

        Assert.False(result.Ok);
        Assert.Equal(ResultCodes.Unauthorized, result.Code);
        Assert.False(host.Service.Executor.IsBusy);
    }

    /// <summary>未绑定会话：动作类一律 unauthorized（原本会落到引擎的 not-ready）。</summary>
    [Fact]
    public async Task UnboundSession_ActionTools_AreUnauthorized()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var endTurn = await client.CallAsync("match.end_turn");
        Assert.False(endTurn.Ok);
        Assert.Equal(ResultCodes.Unauthorized, endTurn.Code);

        var mulligan = await client.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        Assert.False(mulligan.Ok);
        Assert.Equal(ResultCodes.Unauthorized, mulligan.Code);

        var play = await client.CallAsync("play.unit_pre", new JsonObject { ["ref"] = "deadbeef" });
        Assert.False(play.Ok);
        Assert.Equal(ResultCodes.Unauthorized, play.Code);
    }

    /// <summary>房间工具成功返回带 seat，但不得把调用方会话绑到该座位（释放/生成 AI 两处回归）。</summary>
    [Fact]
    public async Task RoomReleaseAndAddAi_DoNotBindCaller()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);
        await using var bystander = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var release = await bystander.CallAsync("room.release", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        });
        Assert.True(release.Ok, release.Message);
        Assert.Equal(1, release.Details!["seat"]!.GetValue<int>());

        // 旁观者若被误绑到座位 1，这里会变成 not-ready 而不是 unauthorized。
        var afterRelease = await bystander.CallAsync("match.end_turn");
        Assert.False(afterRelease.Ok);
        Assert.Equal(ResultCodes.Unauthorized, afterRelease.Code);

        var addAi = await bystander.CallAsync("room.add_ai", new JsonObject { ["seat"] = 1 });
        Assert.True(addAi.Ok, addAi.Message);
        Assert.Equal(1, addAi.Details!["seat"]!.GetValue<int>());

        var afterAddAi = await bystander.CallAsync("match.end_turn");
        Assert.False(afterAddAi.Ok);
        Assert.Equal(ResultCodes.Unauthorized, afterAddAi.Code);
    }

    /// <summary>踢人成功返回 seat，但不得把操作者会话绑到被踢座位。</summary>
    [Fact]
    public async Task RoomKick_DoesNotBindCaller()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);
        await using var bystander = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var kick = await bystander.CallAsync("room.kick", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        });
        Assert.True(kick.Ok, kick.Message);

        var afterKick = await bystander.CallAsync("match.end_turn");
        Assert.False(afterKick.Ok);
        Assert.Equal(ResultCodes.Unauthorized, afterKick.Code);
    }

    /// <summary>开局成功返回不带 seat，且不得把发起者会话绑到房主座位。</summary>
    [Fact]
    public async Task RoomStart_DoesNotBindStarter()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);
        await using var bystander = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var start = await bystander.CallAsync("room.start", new JsonObject { ["seatToken"] = hostToken });
        Assert.True(start.Ok, start.Message);

        var afterStart = await bystander.CallAsync("match.end_turn");
        Assert.False(afterStart.Ok);
        Assert.Equal(ResultCodes.Unauthorized, afterStart.Code);
    }

    /// <summary>UnbindSeat 调用点①：room.release 成功后，原持有该座位的会话立即失去动作权。</summary>
    [Fact]
    public async Task Release_UnbindsReleasedSeat()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        // 释放前：已绑定（无对局 ⇒ not-ready，而不是 unauthorized）。
        var before = await guestClient.CallAsync("match.end_turn");
        Assert.False(before.Ok);
        Assert.Equal(ResultCodes.NotReady, before.Code);

        var release = await hostClient.CallAsync("room.release", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        });
        Assert.True(release.Ok, release.Message);

        var after = await guestClient.CallAsync("match.end_turn");
        Assert.False(after.Ok);
        Assert.Equal(ResultCodes.Unauthorized, after.Code);
    }

    /// <summary>UnbindSeat 调用点②：room.kick 成功后，被踢会话立即失去动作权。</summary>
    [Fact]
    public async Task Kick_UnbindsKickedSeat()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var hostClient = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);

        var hostToken = TestSupport.SeatToken(await hostClient.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var kick = await hostClient.CallAsync("room.kick", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = hostToken,
        });
        Assert.True(kick.Ok, kick.Message);

        var after = await guestClient.CallAsync("match.end_turn");
        Assert.False(after.Ok);
        Assert.Equal(ResultCodes.Unauthorized, after.Code);
    }

    /// <summary>UnbindSeat 调用点⑤：同一座位令牌在新连接上 resume → 旧连接的绑定被拆除。</summary>
    [Fact]
    public async Task ResumeFromSecondConnection_StealsBinding()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var first = await TestSupport.ConnectAsync(host.Port);
        await using var guestClient = await TestSupport.ConnectAsync(host.Port);
        await using var second = await TestSupport.ConnectAsync(host.Port);

        var token = TestSupport.SeatToken(await first.CallAsync("room.claim", TestSupport.Claim(0)));
        TestSupport.SeatToken(await guestClient.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var start = await first.CallAsync("room.start", new JsonObject { ["seatToken"] = token });
        Assert.True(start.Ok, start.Message);

        await first.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
        await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });

        var resume = await second.CallAsync("room.resume", new JsonObject { ["seatToken"] = token });
        Assert.True(resume.Ok, resume.Message);
        Assert.Equal(0, resume.Details!["seat"]!.GetValue<int>());

        var stolen = await first.CallAsync("match.end_turn");
        Assert.False(stolen.Ok);
        Assert.Equal(ResultCodes.Unauthorized, stolen.Code);

        var moved = await second.CallAsync("match.end_turn");
        Assert.True(moved.Ok, moved.Message);
    }

    // ---------------------------------------------------------------- S2 观察面与选靶

    /// <summary>跨座位读视图 → unauthorized，且不会因此改绑（推送仍是自己的座位）。</summary>
    [Fact]
    public async Task CrossSeatView_IsRejected_AndNoRebind()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var rejected = await table.Seat0.CallAsync("observe.view", new JsonObject { ["seat"] = 1 });
        Assert.False(rejected.Ok);
        Assert.Equal(ResultCodes.Unauthorized, rejected.Code);

        // 自己的视图仍然可用，且座位号是自己的。
        var own = await table.Seat0.CallAsync("observe.view");
        Assert.True(own.Ok, own.Message);
        Assert.Equal(0, own.Details!["seat"]!.GetValue<int>());
        Assert.NotNull(own.Details!["view"]!["selfHand"]);

        // 推送不得被劫持到座位 1（原 P0-1 的二次伤害路径）。
        var pushed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        table.Seat0.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView && notification.Params is { } payload)
            {
                pushed.TrySetResult(payload["seat"]!.GetValue<int>());
            }
        };

        var endTurn = await table.Seat0.CallAsync("match.end_turn");
        Assert.True(endTurn.Ok, endTurn.Message);

        Assert.Equal(0, await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>已绑定会话省略 seat → 取自己的座位（不回显、不越权）。</summary>
    [Fact]
    public async Task ObserveView_WithoutSeat_UsesSessionSeat()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var view = await table.Seat0.CallAsync("observe.view");

        Assert.True(view.Ok, view.Message);
        Assert.Equal(0, view.Details!["seat"]!.GetValue<int>());
        Assert.NotNull(view.Details!["view"]!["selfHand"]);
    }

    /// <summary>未绑定会话读视图仍允许（单机诊断），但**不得**因此绑定座位。</summary>
    [Fact]
    public async Task ObserveView_UnboundSession_IsAllowed_AndKeepsUnbound()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);
        await using var bystander = await TestSupport.ConnectAsync(table.Host.Port);

        var view = await bystander.CallAsync("observe.view", new JsonObject { ["seat"] = 1 });
        Assert.True(view.Ok, view.Message);
        Assert.Equal(1, view.Details!["seat"]!.GetValue<int>());

        // 若 observe.view 误绑，这里会变成 not-ready。
        var action = await bystander.CallAsync("match.end_turn");
        Assert.False(action.Ok);
        Assert.Equal(ResultCodes.Unauthorized, action.Code);
    }

    /// <summary>座位 0 应答座位 1 的待决请求 → unauthorized，且 pending 仍归座位 1。</summary>
    [Fact]
    public async Task TargetSubmit_ForOtherSeatPending_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        // 座位 1 的行动才会产出归属座位 1 的待决请求：先把回合交给它。
        Assert.True((await table.Seat0.CallAsync("match.end_turn")).Ok);
        var sessionId = await EnterTargetingAsync(table.Seat1);

        var stolen = await table.Seat0.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.False(stolen.Ok);
        Assert.Equal(ResultCodes.Unauthorized, stolen.Code);

        // pending 保持存活：归属座位仍可应答。
        Assert.True(table.Host.Service.Bridge.HasPending);

        var owner = await table.Seat1.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.True(owner.Ok, owner.Message);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(table.Host.Service.Bridge.HasPending),
            alive => !alive,
            TimeSpan.FromSeconds(5));
    }

    /// <summary>未绑定会话应答任意 pending → unauthorized（pending 保持存活）。</summary>
    [Fact]
    public async Task TargetSubmit_UnboundSession_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);
        await using var bystander = await TestSupport.ConnectAsync(table.Host.Port);

        var sessionId = await EnterTargetingAsync(table.Seat0);

        var denied = await bystander.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.False(denied.Ok);
        Assert.Equal(ResultCodes.Unauthorized, denied.Code);
        Assert.True(table.Host.Service.Bridge.HasPending);

        var accepted = await table.Seat0.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.True(accepted.Ok, accepted.Message);
    }

    /// <summary>
    /// <c>pending.Seat == null</c> 的两行裁决：未绑定 → unauthorized；已绑定 → 放行（错误码由服务层给）。
    /// <para>示例卡包无法产出"无归属"的真实选靶，故直接驱动 <c>TargeterManager</c> 造 pending；
    /// 此时没有执行器动作在等，放行的会话会拿到 <c>invalid-arguments</c>（这正是"门禁不抢业务错误码"）。</para>
    /// </summary>
    [Fact]
    public async Task TargetSubmit_NullSeatPending_UnboundRejected_BoundAllowed()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);
        await using var bystander = await TestSupport.ConnectAsync(table.Host.Port);

        var sessionId = await RunTargetingAsync(table.Host, ownerSeat: null);

        var denied = await bystander.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.False(denied.Ok);
        Assert.Equal(ResultCodes.Unauthorized, denied.Code);

        var allowed = await table.Seat0.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.NotEqual(ResultCodes.Unauthorized, allowed.Code);
    }

    /// <summary>后到者应答已完成的请求 → invalid-arguments（先到先得未被本层改写）。</summary>
    [Fact]
    public async Task TargetSubmit_AfterCompletion_IsInvalidArguments()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var sessionId = await EnterTargetingAsync(table.Seat0);

        var first = await table.Seat0.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.True(first.Ok, first.Message);

        await TestSupport.WaitUntilAsync(
            () => Task.FromResult(table.Host.Service.Bridge.HasPending),
            alive => !alive,
            TimeSpan.FromSeconds(5));

        var second = await table.Seat1.CallAsync("target.cancel", new JsonObject { ["sessionId"] = sessionId });
        Assert.False(second.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, second.Code);
    }

    // ---------------------------------------------------------------- S3 动作归属

    /// <summary>座位 0 代座位 1 认输 → unauthorized，且对局未结束。</summary>
    [Fact]
    public async Task Concede_ForOtherSeat_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var concede = await table.Seat0.CallAsync("match.concede", new JsonObject { ["owner"] = 1 });
        Assert.False(concede.Ok);
        Assert.Equal(ResultCodes.Unauthorized, concede.Code);

        var view = await table.Seat0.CallAsync("observe.view");
        Assert.True(view.Ok, view.Message);
        Assert.Equal("play", view.Details!["view"]!["phase"]!.GetValue<string>());
    }

    /// <summary>座位 0 代座位 1 确认换牌 → unauthorized，相位不变。</summary>
    [Fact]
    public async Task MulliganDone_ForOtherSeat_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path, completeMulligan: false);

        var spoofed = await table.Seat0.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });
        Assert.False(spoofed.Ok);
        Assert.Equal(ResultCodes.Unauthorized, spoofed.Code);

        var view = await table.Seat0.CallAsync("observe.view");
        Assert.Equal("mulligan", view.Details!["view"]!["phase"]!.GetValue<string>());

        // 真正的座位 1 确认仍然有效。
        var real = await table.Seat1.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });
        Assert.True(real.Ok, real.Message);
    }

    /// <summary>座位 0 用 owner=1 出牌 → unauthorized（手牌不变）。</summary>
    [Fact]
    public async Task PlayUnit_ForOtherSeat_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var view = await table.Seat0.CallAsync("observe.view");
        var uid = view.Details!["view"]!["selfHand"]!.AsArray()[0]!["uid"]!.GetValue<string>();

        var spoofed = await table.Seat0.CallAsync("play.unit", new JsonObject
        {
            ["ref"] = uid,
            ["owner"] = 1,
            ["slot"] = 0,
        });

        Assert.False(spoofed.Ok);
        Assert.Equal(ResultCodes.Unauthorized, spoofed.Code);

        var after = await table.Seat0.CallAsync("observe.view");
        Assert.Equal(
            view.Details!["view"]!["selfHand"]!.AsArray().Count,
            after.Details!["view"]!["selfHand"]!.AsArray().Count);
    }

    /// <summary>非当前方 end_turn 不由门禁拒绝（错误码仍是引擎/服务层的那一套，本层不改写）。</summary>
    [Fact]
    public async Task EndTurn_ByNonActiveSeat_IsNotGateRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var table = await StartMatchAsync(temp.Path);

        var result = await table.Seat1.CallAsync("match.end_turn");

        // 引擎是否接受"非当前方结束回合"由引擎决定；本层只保证不是 unauthorized。
        Assert.NotEqual(ResultCodes.Unauthorized, result.Code);
    }

    // ---------------------------------------------------------------- S4 房主身份

    /// <summary>回环判定口径：只有 <c>127.0.0.0/8</c> 与 <c>::1</c> 算"本机房主来源"。</summary>
    [Fact]
    public void SeatScopePolicy_TrustsOnlyLoopback()
    {
        Assert.True(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.True(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("127.5.6.7")));
        Assert.True(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.IPv6Loopback));

        Assert.False(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("10.130.35.183")));
        Assert.False(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("192.168.1.20")));
        Assert.False(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("169.254.10.20")));
        Assert.False(SeatScopePolicy.IsTrustedLocal(null));
    }

    /// <summary>回环来源 claim local 成功（房主能力的正向前置）。</summary>
    [Fact]
    public async Task ClaimLocal_FromLoopback_IsAccepted()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);
        await using var client = await TestSupport.ConnectAsync(host.Port);

        var claim = await client.CallAsync("room.claim", TestSupport.Claim(0, "local"));

        Assert.True(claim.Ok, claim.Message);
        Assert.False(string.IsNullOrWhiteSpace(claim.Details!["seatToken"]!.GetValue<string>()));
    }

    /// <summary>非回环来源 claim local → unauthorized（房主身份不可被远程抢占）。</summary>
    [Fact]
    public async Task ClaimLocal_FromNonLoopback_IsRejected()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        var remote = NonLoopbackIPv4();
        if (remote is null)
        {
            // 本机没有可用的非回环 IPv4：退化为策略单测（无法构造真实非回环连接，见执行记录）。
            Assert.False(SeatScopePolicy.IsTrustedLocal(System.Net.IPAddress.Parse("10.1.2.3")));
            return;
        }

        await using var client = await TestSupport.ConnectAsync(host.Port, remote);

        var claim = await client.CallAsync("room.claim", TestSupport.Claim(0, "local"));

        Assert.False(claim.Ok);
        Assert.Equal(ResultCodes.Unauthorized, claim.Code);

        // 座位未被占用（拒绝发生在进入房间之前）。
        var info = await client.CallAsync("room.info");
        Assert.Equal(string.Empty, info.Details!["seats"]!.AsArray()[0]!["controllerKind"]!.GetValue<string>());
    }

    /// <summary>无房主座位时 start / release / kick 一律 unauthorized（不许免令牌开局/清座）。</summary>
    [Fact]
    public async Task RoomStartReleaseKick_WithoutHost_AreUnauthorized()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var first = await TestSupport.ConnectAsync(host.Port);
        await using var second = await TestSupport.ConnectAsync(host.Port);

        var tokenFirst = TestSupport.SeatToken(await first.CallAsync("room.claim", TestSupport.Claim(0, "remote")));
        TestSupport.SeatToken(await second.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        var start = await first.CallAsync("room.start", new JsonObject { ["seatToken"] = tokenFirst });
        Assert.False(start.Ok);
        Assert.Equal(ResultCodes.Unauthorized, start.Code);

        var release = await first.CallAsync("room.release", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = tokenFirst,
        });
        Assert.False(release.Ok);
        Assert.Equal(ResultCodes.Unauthorized, release.Code);

        var kick = await first.CallAsync("room.kick", new JsonObject
        {
            ["seat"] = 1,
            ["seatToken"] = tokenFirst,
        });
        Assert.False(kick.Ok);
        Assert.Equal(ResultCodes.Unauthorized, kick.Code);

        // 座位都还在（拒绝不是"清空后拒绝"）。
        var info = await first.CallAsync("room.info");
        Assert.Equal("remote", info.Details!["seats"]!.AsArray()[1]!["controllerKind"]!.GetValue<string>());
    }

    /// <summary>房主断线释放座位 → 不可开局；新房主 claim local 后即可开局（同一进程）。</summary>
    [Fact]
    public async Task HostDisconnect_ReleasesSeat_AndNewHostCanStart()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var guest = await TestSupport.ConnectAsync(host.Port);
        var oldHost = await TestSupport.ConnectAsync(host.Port);

        TestSupport.SeatToken(await oldHost.CallAsync("room.claim", TestSupport.Claim(0, "local")));
        var guestToken = TestSupport.SeatToken(await guest.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

        await oldHost.DisposeAsync();

        await TestSupport.WaitUntilAsync(
            async () =>
            {
                var info = await guest.CallAsync("room.info");
                return info.Details!["seats"]!.AsArray()[0]!["controllerKind"]!.GetValue<string>();
            },
            kind => kind.Length == 0,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        // 缺一个座位：不可开局（且释放后房间也没有房主了）。
        var blocked = await guest.CallAsync("room.start", new JsonObject { ["seatToken"] = guestToken });
        Assert.False(blocked.Ok);

        // 新房主 claim local 后可以照常开局。
        await using var newHost = await TestSupport.ConnectAsync(host.Port);
        var newToken = TestSupport.SeatToken(await newHost.CallAsync("room.claim", TestSupport.Claim(0, "local")));

        var start = await newHost.CallAsync("room.start", new JsonObject { ["seatToken"] = newToken });
        Assert.True(start.Ok, start.Message);
    }

    /// <summary>
    /// 悬空凭证（一等公民）：A 释放座位后 B 接手同一座位并开局 —— A 必须彻底失去动作权与推送。
    /// <para>这是"只绑不拆"漏洞的完整复现路径（释放后换人 → 旧会话仍能代新座位行动）。</para>
    /// </summary>
    [Fact]
    public async Task StaleBinding_AfterRelease_CannotAct()
    {
        using var temp = new TestSupport.TempDirectory("orc-security");
        await using var host = await StartHostAsync(temp.Path);

        await using var a = await TestSupport.ConnectAsync(host.Port);
        var tokenA = TestSupport.SeatToken(await a.CallAsync("room.claim", TestSupport.Claim(0)));

        Assert.True((await a.CallAsync("room.release", new JsonObject
        {
            ["seat"] = 0,
            ["seatToken"] = tokenA,
        })).Ok);

        // B 接手同一座位、补 AI、开局。
        await using var b = await TestSupport.ConnectAsync(host.Port);
        var tokenB = TestSupport.SeatToken(await b.CallAsync("room.claim", TestSupport.Claim(0)));
        Assert.True((await b.CallAsync("room.add_ai", new JsonObject { ["seat"] = 1, ["deck"] = TestSupport.Deck() })).Ok);
        var started = await b.CallAsync("room.start", new JsonObject { ["seatToken"] = tokenB });
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");

        // A 的绑定已被拆除：动作一律 unauthorized（不是 invalid-arguments，也不是 engine-rejected）。
        var concede = await a.CallAsync("match.concede", new JsonObject { ["owner"] = 0 });
        Assert.False(concede.Ok);
        Assert.Equal(ResultCodes.Unauthorized, concede.Code);

        // 对局没有被 A 结束（B 的视角仍在换牌相位）。
        var view = await b.CallAsync("observe.view");
        Assert.True(view.Ok, view.Message);
        Assert.Equal("mulligan", view.Details!["view"]!["phase"]!.GetValue<string>());

        // A 也不再收座位 0 的推送。
        var pushedToA = 0;
        a.NotificationReceived += notification =>
        {
            if (notification.Method == ProtocolMethods.NotifyView)
            {
                Interlocked.Increment(ref pushedToA);
            }
        };

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref pushedToA));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>本机可用的非回环 IPv4（无＝null；用于构造真实的"远程来源"连接）。</summary>
    private static string? NonLoopbackIPv4()
    {
        foreach (var network in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                || network.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(unicast.Address))
                {
                    return unicast.Address.ToString();
                }
            }
        }

        return null;
    }

    /// <summary>一盘已开局的桌面（座位 0 ＝房主、座位 1 ＝远端；默认换牌完成进入 play）。</summary>
    private sealed class Table : IAsyncDisposable
    {
        private Table(ServerHost host, OrcClient seat0, OrcClient seat1)
        {
            Host = host;
            Seat0 = seat0;
            Seat1 = seat1;
        }

        public ServerHost Host { get; }

        public OrcClient Seat0 { get; }

        public OrcClient Seat1 { get; }

        public static async Task<Table> OpenAsync(string sandboxRoot, bool completeMulligan)
        {
            var host = await StartHostAsync(sandboxRoot);
            var seat0 = await TestSupport.ConnectAsync(host.Port);
            var seat1 = await TestSupport.ConnectAsync(host.Port);

            var token = TestSupport.SeatToken(await seat0.CallAsync("room.claim", TestSupport.Claim(0)));
            TestSupport.SeatToken(await seat1.CallAsync("room.claim", TestSupport.Claim(1, "remote")));

            var start = await seat0.CallAsync("room.start", new JsonObject { ["seatToken"] = token });
            Assert.True(start.Ok, start.Message);

            if (completeMulligan)
            {
                Assert.True((await seat0.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 })).Ok);
                Assert.True((await seat1.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 })).Ok);
                await TestSupport.WaitUntilAsync(
                    async () =>
                    {
                        var view = await seat0.CallAsync("observe.view");
                        return view.Ok ? view.Details!["view"]!["phase"]!.GetValue<string>() : null;
                    },
                    phase => phase == "play",
                    TimeSpan.FromSeconds(10),
                    throwOnTimeout: true);
            }

            return new Table(host, seat0, seat1);
        }

        public async ValueTask DisposeAsync()
        {
            await Seat0.DisposeAsync();
            await Seat1.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static Task<Table> StartMatchAsync(string sandboxRoot, bool completeMulligan = true)
        => Table.OpenAsync(sandboxRoot, completeMulligan);

    /// <summary>
    /// 让该会话用自己手牌里能进入选靶的卡发起一次真实动作，返回 pending 的 sessionId。
    /// （真实动作才有执行器 <c>_active</c>，才可能被 <c>target.submit/cancel</c> 推进。）
    /// </summary>
    private static async Task<string> EnterTargetingAsync(OrcClient client)
    {
        var view = await client.CallAsync("observe.view");
        Assert.True(view.Ok, view.Message);

        foreach (var card in view.Details!["view"]!["selfHand"]!.AsArray())
        {
            var uid = card!["uid"]!.GetValue<string>();
            var pre = await client.CallAsync("play.unit_pre", new JsonObject { ["ref"] = uid });
            if (pre.Ok && pre.Code == ResultCodes.Targeting && pre.Details?["pending"] is { } pending)
            {
                return pending["sessionId"]!.GetValue<string>();
            }
        }

        throw new InvalidOperationException("手牌里没有能进入选靶的单位卡（夹具失效）。");
    }

    /// <summary>
    /// 经引擎发起一次待决选靶（示例卡包没有"让对手选择"的效果，只能直接驱动 <c>TargeterManager</c>），
    /// 返回 sessionId。<paramref name="ownerSeat"/>＝null 时 pending.Seat 亦为 null。
    /// <para>注意：这条路径没有执行器动作在等，<c>target.*</c> 只能验门禁，不能验推进。</para>
    /// </summary>
    private static async Task<string> RunTargetingAsync(ServerHost host, int? ownerSeat)
    {
        var match = host.Service.ActiveMatch ?? throw new InvalidOperationException("无活跃对局。");
        var parameter = new Orc.Game.Targeting.ReferenceSetParameter(
            Array.Empty<Orc.Core.Ref<Orc.Core.Entity>>(),
            0,
            1)
        {
            Owner = ownerSeat is { } seat ? match.Players[seat] : null,
        };

        _ = match.TargeterManager.RunAsync(async flow =>
        {
            await flow.Step(Orc.Game.Targeting.SelectorTemplates.FieldUnit, parameter);
            return Orc.Game.Targeting.TargeterResult.Ok();
        });

        var sessionId = await TestSupport.WaitUntilAsync(
            () => Task.FromResult(host.Service.Bridge.CurrentPending?.SessionId),
            id => id is not null,
            TimeSpan.FromSeconds(5),
            throwOnTimeout: true);

        return sessionId!;
    }

    internal static Task<ServerHost> StartHostAsync(
        string sandboxRoot,
        TimeSpan? disconnectGrace = null,
        TimeSpan? targetSelectionTimeout = null,
        TimeSpan? heartbeatTimeout = null)
        => ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [TestSupport.SamplePackDirectory()],
            SandboxRoot = sandboxRoot,
            Port = 0,
            EnforceDeckRules = false,
            DisconnectGrace = disconnectGrace ?? TimeSpan.FromSeconds(60),
            TargetSelectionTimeout = targetSelectionTimeout ?? TimeSpan.FromMinutes(10),
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(45),
        });
}
