using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 沙盒包必须真正进入开局装配（R3「实例级固定包强制入并集」）。
/// <para>
/// 回归背景：装配链由 lock 驱动，而脚手架原先只产出 manifest ＋ 占位卡，导致沙盒包被判非法、
/// 被开局装配的降级路径**整包剔除**——表现是 <c>match.start</c> 成功但 <c>fallback=true</c>，
/// 且调试面造出的卡永远进不了对局。本用例锁死 <c>fallback=false</c> 与「包前缀里含沙盒包」。
/// </para>
/// </summary>
public sealed class SandboxPoolTests
{
    [Fact]
    public async Task MatchStart_IncludesSandboxPack_WithoutFallback()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        var first = await fixture.ConnectAsync("seat0");
        var second = await fixture.ConnectAsync("seat1");
        await HostFixture.AttachControllerAsync(first, seat: 0);
        await HostFixture.AttachControllerAsync(second, seat: 1);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = first.ConnectionId })).Ok);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = second.ConnectionId })).Ok);

        // 调试面造一张卡：这会让沙盒包多一个源文件，必须同步出 sidecar ＋ lock 才能装配。
        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);

        var written = await debug.CallAsync("card.write", new JsonObject
        {
            ["path"] = "pack/cards/pool-probe.card.json",
            ["json"] = ProbeCardJson("pool-probe"),
        });
        Assert.True(written.Ok, $"{written.Code}: {written.Message}");

        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");

        Assert.False(
            started.Payload!["fallback"]!.GetValue<bool>(),
            "沙盒包被降级剔除 ⇒ 元数据（sidecar/lock）没有同步进装配链。");

        var packs = started.Payload!["packs"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Contains(
            packs,
            prefix => prefix.Contains("/" + fixture.Host.Service.Options.SandboxPackId + "/", StringComparison.Ordinal));

        await debug.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private static string ProbeCardJson(string id) => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "沙盒装配探针卡",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "neutral", "kredits": 1 },
    { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
    { "component": "tagData", "rarity": "standard", "tags": ["sandbox"] }
  ]
}
""";
}
