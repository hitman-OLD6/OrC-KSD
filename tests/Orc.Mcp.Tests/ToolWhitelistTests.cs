using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 工具白名单护栏（R9d / U5b）：
/// ① player 模式的工具清单必须**恰好**等于"玩家可达入口"白名单——多一条＝越权、少一条＝玩家能力缺失；
/// ② authoring 模式 = player 清单 ＋ 调试类工具（差集必须全部是已知调试工具）。
/// </summary>
public class ToolWhitelistTests
{
    /// <summary>玩家可达工具白名单（与引擎公开入口一一对应；player 模式必须恰好提供这些）。</summary>
    private static readonly string[] PlayerTools =
    [
        "service.info",
        "service.tools",
        "room.info",
        "room.claim",
        "room.release",
        "room.add_ai",
        "room.start",
        "room.kick",
        "room.resume",
        "match.end_turn",
        "match.concede",
        "mulligan.begin",
        "mulligan.done",
        "play.unit_pre",
        "play.unit",
        "play.join",
        "play.command_pre",
        "play.command",
        "play.counter",
        "play.can_play",
        "command.begin",
        "command.move",
        "command.attack",
        "command.leave",
        "command.availability",
        "target.submit",
        "target.cancel",
        "target.claim",
        "observe.view",
        "docs.list",
        "docs.read",
    ];

    /// <summary>调试类工具（沙盒专属；不得出现在 player 模式）。</summary>
    private static readonly string[] DebugTools =
    [
        "sandbox.create",
        "sandbox.dispose",
        "sandbox.state",
        "sandbox.spawn",
        "sandbox.kill",
        "sandbox.damage",
        "sandbox.set_points",
        "sandbox.emit",
        "sandbox.instantiate",
        "sandbox.unitize",
        "sandbox.move",
        "sandbox.remove",
        "sandbox.points",
        "sandbox.draw",
        "card.load",
        "card.list",
        "card.write",
        "effect.write",
        "asset.validate",
        "pack.info",
        "file.list",
        "file.read",
        "asset.promote",
        // S9：后端编辑器全组（R9 能力集一一对应；非沙盒不登记）
        "editor.pack_list",
        "editor.session_begin",
        "editor.session_end",
        "editor.asset_list",
        "editor.asset_get",
        "editor.asset_commit",
        "editor.asset_create",
        "editor.asset_delete",
        "editor.version_set",
        "editor.dep_set",
        "editor.lock_preview",
        "editor.promote",
        "editor.reload",
        "observe.snapshot",
        "observe.segments",
        "diag.report",
        "diag.audit_chain",
        "diag.hooks",
        "diag.entry_points",
        "diag.card_load",
    ];

    [Fact]
    public void PlayerMode_ExposesExactlyThePlayerWhitelist()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: new FakeRoomControl());

        var actual = fixture.Service.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = PlayerTools.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlayerMode_ContainsNoDebugTools()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: new FakeRoomControl());
        var actual = fixture.Service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var debug in DebugTools)
        {
            Assert.DoesNotContain(debug, actual);
        }
    }

    [Fact]
    public void AuthoringMode_EqualsPlayerToolsPlusDebugTools()
    {
        using var player = new SandboxFixture(McpServiceMode.Player, room: new FakeRoomControl());
        using var authoring = new SandboxFixture(McpServiceMode.Authoring, room: new FakeRoomControl());

        var playerSet = player.Service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var authoringSet = authoring.Service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        // authoring 是 player 的超集
        Assert.Empty(playerSet.Except(authoringSet));

        // 差集必须**恰好**等于调试类工具清单（防"新工具忘了标 sandboxOnly"）
        var diff = authoringSet.Except(playerSet).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = DebugTools.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, diff);
    }

    [Fact]
    public async Task PlayerMode_SandboxOnlyToolCall_IsUnknown()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: new FakeRoomControl());

        var result = await fixture.InvokeAsync("sandbox.create");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.UnknownTool, result.Code);
    }

    [Fact]
    public async Task PlayerMode_ObservationWithoutSeat_IsModeDenied()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player, room: new FakeRoomControl());

        // 未指定座位（且无活跃对局）时，观察面给出可判别拒绝而非静默失败。
        var result = await fixture.InvokeAsync("observe.view");

        Assert.False(result.Ok);
        Assert.True(result.Code is McpCodes.NotReady or McpCodes.ModeDenied);
    }
}
