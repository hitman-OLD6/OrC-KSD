using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Match;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S9「MCP 工具与门控」验收（诊断侧）：<c>diag.card_load</c> 的档位（<c>sandboxOnly + readOnly</c>）、
/// 命名风格（点后 snake_case）、以及**两部分**返回——本批报告（冻结 schema）与本局累积视图
/// （按 GUID 合并、<c>refCount</c> 累加）。
/// <para>
/// 累积视图的持有者是**对局侧的加载台账**（<see cref="Orc.Packages.Loading.CardLoadLedger"/>，
/// 由 <see cref="Orc.Packages.Loading.MatchCardAssembly"/> 每批记录）；工具只读，不复制报告模型。
/// </para>
/// </summary>
public sealed class CardLoadDiagTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-diag-cards-" + Guid.NewGuid().ToString("N"));

    public CardLoadDiagTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Name_Follows_Diag_Convention_And_Is_Authoring_Only()
    {
        using var authoring = new SandboxFixture(McpServiceMode.Authoring);
        using var player = new SandboxFixture(McpServiceMode.Player);

        var descriptor = Assert.Single(authoring.Service.Tools, tool => tool.Name == "diag.card_load");
        Assert.Equal("diag", descriptor.Group);

        // 点后 snake_case（与 diag.audit_chain / diag.entry_points 同风格）
        Assert.Equal(2, descriptor.Name.Split('.').Length);
        Assert.Equal("card_load", descriptor.Name.Split('.')[1]);

        // 档位：sandboxOnly（非沙盒不登记）
        Assert.DoesNotContain(player.Service.Tools, tool => tool.Name == "diag.card_load");
    }

    [Fact]
    public async Task ReadOnly_Passes_Pending_Gate_While_Write_Tools_Are_Rejected()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Authoring);
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        // 制造「挂起期」（执行器忙）：动作挂在可控的 TaskCompletionSource 上。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = fixture.Service.Executor.RunAsync(
            async _ =>
            {
                await gate.Task;
                return ActionOutcome.Completed();
            },
            CancellationToken.None);

        try
        {
            // readOnly ⇒ 穿透挂起期门禁（诊断面在待决期间仍可读）
            var read = await fixture.InvokeAsync("diag.card_load");
            Assert.True(read.Ok, read.Message);

            // 非只读 ⇒ 被挂起期门禁拒绝（同一门禁的另一侧，证明 readOnly 档位真实生效）
            var write = await fixture.InvokeAsync("sandbox.spawn", """{ "cardId": "any" }""");
            Assert.False(write.Ok);
            Assert.Equal(McpCodes.PendingTargeting, write.Code);

            // 编辑器只读工具同理（editor.asset_list 标了 readOnly；无会话＝not-ready 而非 pending-targeting）
            var editorRead = await fixture.InvokeAsync("editor.asset_list");
            Assert.Equal(McpCodes.NotReady, editorRead.Code);
        }
        finally
        {
            gate.SetResult();
            await active;
        }
    }

    [Fact]
    public async Task Real_Match_Returns_Batch_Report_And_Cumulative_View()
    {
        var service = NewService();
        var created = await service.CreateMatchAsync(LaunchOptions());
        Assert.Equal(ActionOutcome.StatusCompleted, created.Status);
        Assert.NotNull(service.CardAssembly);

        var result = await service.InvokeAsync("diag.card_load", SandboxFixture.Args("{}"));

        Assert.True(result.Ok, result.Message);
        var details = result.Details!;
        Assert.True((bool)details["hasMatch"]!);
        Assert.Equal("card-assembly", (string)details["source"]!);
        Assert.Equal(1, (int)details["batchCount"]!);

        // ① 本批报告（I10 冻结 schema 原样嵌入）
        var batch = details["batch"]!.AsObject();
        Assert.Equal(1, (int)batch["schemaVersion"]!);
        Assert.Equal("batch-1", (string)batch["batchId"]!);
        Assert.Equal(5, batch["items"]!.AsArray().Count);   // 样例包 5 张卡
        Assert.Equal(78, batch["slots"]!.AsArray().Count);  // 双方牌堆各 39 卡位
        Assert.Equal(78, (int)batch["summary"]!["totalRefs"]!);
        // 样例卡缺卡图 ⇒ 档位为 warning（可玩、不阻断）；报告里不应出现非法
        Assert.Equal(0, (int)batch["summary"]!["invalid"]!);
        Assert.All(batch["items"]!.AsArray(), item => Assert.Contains(
            (string)item!["tier"]!, new[] { "valid", "warning" }));

        // ② 本局累积视图（单批：同样的 GUID、同样的 refCount；且不含 slots——卡位只对单批有意义）
        var cumulative = details["cumulative"]!.AsObject();
        Assert.Equal(1, (int)cumulative["schemaVersion"]!);
        Assert.Equal(1, (int)cumulative["batchCount"]!);
        Assert.Equal("batch-1", (string)cumulative["lastBatchId"]!);
        Assert.Null(cumulative["slots"]);

        var batchByGuid = batch["items"]!.AsArray()
            .ToDictionary(item => (string)item!["assetGuid"]!, item => item!.AsObject());
        var cumulativeItems = cumulative["items"]!.AsArray();
        Assert.Equal(5, cumulativeItems.Count);

        foreach (var node in cumulativeItems)
        {
            var item = node!.AsObject();
            var source = batchByGuid[(string)item["assetGuid"]!];
            Assert.Equal((string)source["appId"]!, (string)item["appId"]!);
            Assert.Equal((int)source["refCount"]!, (int)item["refCount"]!);
            Assert.Equal((string)source["tier"]!, (string)item["tier"]!);
        }

        // 双方牌堆同名卡 ⇒ 引用计数＝两份牌堆之和
        Assert.Equal(
            batch["items"]!.AsArray().Sum(item => (int)item!["refCount"]!),
            (int)cumulative["summary"]!["totalRefs"]!);

        service.DisposeMatch();
        Assert.Null(service.CardAssembly); // 累积视图是「本局」语义：对局丢弃即失效
    }

    [Fact]
    public async Task Cumulative_View_Merges_By_Guid_And_Accumulates_RefCount()
    {
        var service = NewService();
        var created = await service.CreateMatchAsync(LaunchOptions());
        Assert.Equal(ActionOutcome.StatusCompleted, created.Status);

        var first = await service.InvokeAsync("diag.card_load", SandboxFixture.Args("{}"));
        Assert.True(first.Ok, first.Message);
        var firstCumulative = first.Details!["cumulative"]!.AsObject();
        var beforeByGuid = ToRefCounts(firstCumulative);

        // 第二批（同一批报告再记一次＝"再装一批同内容"）：GUID 合并不新增条目，refCount 逐条累加。
        service.CardAssembly!.Ledger.Record(service.CardAssembly.LastReport!);

        var second = await service.InvokeAsync("diag.card_load", SandboxFixture.Args("{}"));
        Assert.True(second.Ok, second.Message);
        var secondCumulative = second.Details!["cumulative"]!.AsObject();
        var afterByGuid = ToRefCounts(secondCumulative);

        Assert.Equal(2, (int)second.Details["batchCount"]!);
        Assert.Equal(2, (int)secondCumulative["batchCount"]!);
        Assert.Equal(beforeByGuid.Keys.OrderBy(key => key, StringComparer.Ordinal), afterByGuid.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (guid, count) in beforeByGuid)
        {
            Assert.Equal(count * 2, afterByGuid[guid]);
        }

        // 汇总随之累加（totalRefs 翻倍）
        Assert.Equal((int)firstCumulative["summary"]!["totalRefs"]! * 2, (int)secondCumulative["summary"]!["totalRefs"]!);

        service.DisposeMatch();
    }

    [Fact]
    public async Task Sandbox_Match_Reports_Direct_Registration_Path_With_Empty_Views()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Authoring);

        var created = await fixture.InvokeAsync("sandbox.create");
        Assert.True(created.Ok, created.Message);

        var result = await fixture.InvokeAsync("diag.card_load");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("none", (string)result.Details!["source"]!);
        Assert.Null(result.Details["batch"]);
        Assert.NotNull(result.Details["note"]);

        var cumulative = result.Details["cumulative"]!.AsObject();
        Assert.Equal(0, (int)cumulative["batchCount"]!);
        Assert.Empty(cumulative["items"]!.AsArray());
        Assert.Equal(0, (int)cumulative["summary"]!["totalRefs"]!);
    }

    private McpService NewService()
        => new(new McpServiceOptions
        {
            Mode = McpServiceMode.Authoring,
            SandboxRoot = _root,
            Seed = 20261020,
        });

    private static MatchLaunchOptions LaunchOptions()
        => new()
        {
            PackDirectories = new[] { SamplePackDirectory() },
            SeatA = new MatchSeatConfig(0, SampleDeck()),
            SeatB = new MatchSeatConfig(1, SampleDeck()),
            SkipMulligan = true,
            // 示例卡包只有 5 张卡，无法组成 39 张合法构筑；本测试只验诊断面。
            EnforceDeckRules = false,
        };

    /// <summary>示例卡包目录（仓库内 `samples/card-pack-ai`）。</summary>
    private static string SamplePackDirectory()
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

    /// <summary>示例卡组：39 张（与 samples/card-pack-ai 的 5 张卡一致）。</summary>
    private static IReadOnlyList<string> SampleDeck() =>
    [
        .. Enumerable.Repeat("ai.sample.infantry", 15),
        .. Enumerable.Repeat("ai.sample.armor", 10),
        .. Enumerable.Repeat("ai.sample.artillery", 5),
        .. Enumerable.Repeat("ai.sample.order", 5),
        .. Enumerable.Repeat("ai.sample.counter", 4),
    ];

    private static Dictionary<string, int> ToRefCounts(JsonObject cumulative)
        => cumulative["items"]!.AsArray()
            .ToDictionary(item => (string)item!["assetGuid"]!, item => (int)item!["refCount"]!);
}
