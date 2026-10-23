using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Orc.Packages;
using Orc.Protocol;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 「卡池运行期注册与注销」验收（R1/R2/R4/R5/R7）：已注册包集合的读面／写面、对账事件、
/// play 与 sandbox 的脱敏差异、开局唯一真源。
/// </summary>
public sealed class PackPoolTests
{
    // ─────────────────────────────────────────────── 读面

    [Fact]
    public async Task PoolList_InPlayMode_Hides_Directories_And_FreeText()
    {
        await using var fixture = await HostFixture.StartAsync();

        var listed = await fixture.AdminAsync(AdminOps.PoolList);

        Assert.True(listed.Ok, listed.Message);
        var model = listed.Payload!["model"]!.AsObject();
        Assert.True(model["ok"]!.GetValue<bool>());

        var row = Assert.Single(model["packages"]!.AsArray())!.AsObject();
        Assert.Equal(TestSupport.SamplePackage().PackageId, row["packageId"]!.GetValue<string>());
        Assert.Null(row["directory"]);
        Assert.Null(row["guid"]);
        Assert.Null(row["error"]);
        Assert.Equal("ok", listed.Payload!["code"]!.GetValue<string>());
        Assert.Null(listed.Payload!["message"]);

        // 白名单式投影：绝对路径不可能出现在响应任何位置。
        Assert.DoesNotContain(TestSupport.SamplePackDirectory(), listed.Payload!["model"]!.ToJsonString());
        Assert.DoesNotContain(fixture.Root, listed.Payload!["model"]!.ToJsonString());
    }

    [Fact]
    public async Task PoolList_InSandboxMode_Returns_Directories_And_Warnings()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        var listed = await fixture.AdminAsync(AdminOps.PoolList);

        Assert.True(listed.Ok, listed.Message);
        var model = listed.Payload!["model"]!.AsObject();
        var row = Assert.Single(model["packages"]!.AsArray())!.AsObject();
        Assert.Equal(
            Path.GetFullPath(TestSupport.SamplePackDirectory()),
            Path.GetFullPath(row["directory"]!.GetValue<string>()));
        Assert.NotNull(row["guid"]);
        Assert.NotNull(model["warnings"]);
    }

    [Fact]
    public async Task InstanceInfo_Reports_InitialPackDirectories_Without_Sandbox_Pack()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox);

        var info = await fixture.AdminAsync(AdminOps.InstanceInfo);

        var initial = info.Payload!["initialPackDirectories"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Equal(
            new[] { Path.GetFullPath(TestSupport.SamplePackDirectory()) },
            initial.Select(Path.GetFullPath));
        Assert.Equal("initialPackDirectories", info.Payload!["packDirectoriesRenamedTo"]!.GetValue<string>());
        Assert.Null(info.Payload!["packDirectories"]);
    }

    // ─────────────────────────────────────────────── 写面：注册

    [Fact]
    public async Task Register_Appends_Pack_And_List_Shows_Registration_Order()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var extra = CreatePack(fixture.Root, "extra");

        var registered = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra });

        Assert.True(registered.Ok, registered.Message);
        var change = registered.Payload!["change"]!.AsObject();
        var added = Assert.Single(change["added"]!.AsArray())!.AsObject();
        Assert.Equal("extra", added["id"]!.GetValue<string>());
        Assert.False(change["existed"]!.GetValue<bool>());

        var packages = registered.Payload!["model"]!["packages"]!.AsArray()
            .Select(node => node!["packageId"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(new[] { TestSupport.SamplePackage().PackageId, "extra" }, packages);
    }

    [Fact]
    public async Task Register_Relative_Path_Is_Rejected()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        var reply = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = "packs/relative" });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Contains("绝对目录路径", reply.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_Sandbox_Fixed_Pack_Directory_Is_Rejected()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");

        var reply = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = sandbox });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Contains("每局强制加入", reply.Message!, StringComparison.Ordinal);
        Assert.Contains("sandbox.reset", reply.Message!, StringComparison.Ordinal);

        // 集合不变。
        var packages = reply.Payload!["model"]!["packages"]!.AsArray();
        Assert.Single(packages);
    }

    [Fact]
    public async Task Register_Same_Directory_Is_Idempotent()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        var reply = await fixture.AdminAsync(
            AdminOps.PoolRegister,
            new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });

        Assert.True(reply.Ok, reply.Message);
        var change = reply.Payload!["change"]!.AsObject();
        Assert.True(change["existed"]!.GetValue<bool>());
        Assert.Empty(change["added"]!.AsArray());
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    [Fact]
    public async Task Register_Same_Key_Different_Path_Is_Rejected_With_Existing_Directory()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var copy = CopyPack(TestSupport.SamplePackDirectory(), Path.Combine(fixture.Root, "copy"));

        var reply = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = copy });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Equal(
            Path.GetFullPath(TestSupport.SamplePackDirectory()),
            Path.GetFullPath(reply.Payload!["details"]!["existingDirectory"]!.GetValue<string>()));
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    // ─────────────────────────────────────────────── 写面：注销

    [Fact]
    public async Task Unregister_With_Stale_Version_Reports_Current_Version()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var sample = TestSupport.SamplePackage();

        var reply = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = "0.0.0",
        });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.VersionMismatch, reply.Code);
        Assert.Equal(sample.Version, reply.Payload!["details"]!["currentVersion"]!.GetValue<string>());
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    [Fact]
    public async Task Unregister_Last_Pack_Leaves_Empty_Pool_And_MatchStart_Reports_It()
    {
        // AI 座位＝配置即批准：开局前置通过，才能走到"空集合"这条判据。
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);
        DeleteSandboxPack(fixture);

        var sample = TestSupport.SamplePackage();
        var removed = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = sample.Version,
        });

        Assert.True(removed.Ok, removed.Message);
        Assert.Empty(removed.Payload!["model"]!["packages"]!.AsArray());

        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.False(listed.Ok);
        var issue = Assert.Single(listed.Payload!["model"]!["issues"]!.AsArray())!.AsObject();
        Assert.Equal("EmptyPool", issue["code"]!.GetValue<string>());
        Assert.False(issue["fatal"]!.GetValue<bool>());
        Assert.Equal(0, listed.Payload!["model"]!["plan"]!["cardCount"]!.GetValue<int>());

        var start = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.False(start.Ok);
        Assert.Equal(ResultCodes.MissingAssets, start.Code);
        Assert.Contains("卡包集合为空", start.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unregister_Last_Pack_With_Sandbox_Pack_Still_Assembles_And_Warns_Registry_Empty()
    {
        // 保留沙盒包（占位卡仍在）：空集合不是"不可装配"，ok 由「集合 ＋ 沙盒包」共同决定。
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);

        var sample = TestSupport.SamplePackage();
        var removed = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = sample.Version,
        });
        Assert.True(removed.Ok, removed.Message);

        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.True(listed.Ok, listed.Message);
        var model = listed.Payload!["model"]!;
        Assert.Empty(model["packages"]!.AsArray());
        Assert.True(model["ok"]!.GetValue<bool>());
        Assert.Contains(
            model["warnings"]!.AsArray(),
            warning => warning!.GetValue<string>().Contains("registry-empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sample_Pack_Has_No_Domain_Mismatch_Noise()
    {
        // 正式包的 lock.test.json 必须为空：否则每条 formal 资产都会报 DomainMismatch（噪音），
        // 真实冲突被淹掉。样例包数据由迁移工具生成，这里锁死该结果。
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);

        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.True(listed.Ok, listed.Message);
        var issues = listed.Payload!["model"]!["issues"]!.AsArray();
        Assert.DoesNotContain(issues, issue => issue!["code"]!.GetValue<string>() == "DomainMismatch");
    }

    // ─────────────────────────────────────────────── match.start 唯一真源
    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("string")]
    public async Task MatchStart_Rejects_PackDirectories_Key(string shape)
    {
        await using var fixture = await HostFixture.StartAsync();

        var args = new JsonObject();
        args["packDirectories"] = shape switch
        {
            "empty" => new JsonArray(),
            "null" => null,
            _ => JsonValue.Create("not-an-array"),
        };

        var start = await fixture.AdminAsync(AdminOps.MatchStart, args);

        Assert.False(start.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, start.Code);
        Assert.Contains("pool.register", start.Message!, StringComparison.Ordinal);
        Assert.Contains("已移除", start.Message!, StringComparison.Ordinal);

        var info = await fixture.AdminAsync(AdminOps.InstanceInfo);
        Assert.Equal("Lobby", info.Payload!["roomState"]!.GetValue<string>());
    }

    // ─────────────────────────────────────────────── 对账

    [Fact]
    public async Task Write_Surface_Reconciles_Missing_Directory_And_Emits_Event_Without_Paths()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var events = new ConcurrentQueue<JsonObject>();
        fixture.Host.ChannelEvent += message => events.Enqueue((JsonObject)message.DeepClone());

        var extra = CreatePack(fixture.Root, "extra");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra })).Ok);
        Directory.Delete(extra, recursive: true);

        // 写面（幂等注册）触发真对账：目录消失的条目被移除并独立提交。
        var reply = await fixture.AdminAsync(
            AdminOps.PoolRegister,
            new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });

        Assert.True(reply.Ok, reply.Message);
        var reconciled = reply.Payload!["change"]!["removedByReconcile"]!.AsObject();
        Assert.Equal(1, reconciled["count"]!.GetValue<int>());
        var entry = reconciled["entries"]!.AsArray()[0]!.AsObject();
        Assert.Equal("extra", entry["id"]!.GetValue<string>());
        Assert.Equal("directory-missing", entry["cause"]!.GetValue<string>());
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());

        var evt = Assert.Single(events, message => message["event"]!.GetValue<string>() == "packset.reconciled");
        var removed = evt["payload"]!["removed"]!.AsArray()[0]!.AsObject();
        Assert.Equal("extra", removed["packageId"]!.GetValue<string>());
        Assert.Equal("directory-missing", removed["cause"]!.GetValue<string>());

        // 事件不带绝对目录（通道事件可能落到非管理连接）。
        Assert.DoesNotContain(fixture.Root, evt.ToJsonString());
    }

    [Fact]
    public async Task PoolList_Reports_Id_Change_As_WillRemove_Without_Changing_Collection()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var extra = CreatePack(fixture.Root, "extra");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra })).Ok);

        ReplaceManifestId(extra, "hijacked");

        // 读面＝预演：标 willRemove，但集合不变（仍两条）。
        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.True(listed.Ok, "排除失效条目后其余仍可装配 ⇒ 顶层 ok 应为 true。");
        var rows = listed.Payload!["model"]!["packages"]!.AsArray();
        Assert.Equal(2, rows.Count);
        var broken = Assert.Single(rows, node => node!["ok"]!.GetValue<bool>() == false)!.AsObject();
        Assert.Equal("id-changed", broken["errorCode"]!.GetValue<string>());
        Assert.True(broken["willRemove"]!.GetValue<bool>());

        var warnings = listed.Payload!["model"]!["warnings"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Contains(warnings, warning => warning.Contains("失效条目", StringComparison.Ordinal));

        // 下一次写面才真正移除。
        var reply = await fixture.AdminAsync(
            AdminOps.PoolRegister,
            new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });
        Assert.Equal(1, reply.Payload!["change"]!["removedByReconcile"]!["count"]!.GetValue<int>());
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    [Fact]
    public async Task PoolList_Does_Not_Write_Sandbox_Metadata()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        var lockPath = Path.Combine(sandbox, AssetLockJson.FormalFileName);

        // 制造「缺 lock」：判定必须只读探测（sandbox-metadata-unsynced），绝不能顺手补齐。
        File.Delete(lockPath);
        Assert.False(File.Exists(lockPath));

        var listed = await fixture.AdminAsync(AdminOps.PoolList);

        Assert.False(File.Exists(lockPath), "pool.list 不得写盘（试算零副作用）。");
        var warnings = listed.Payload!["model"]!["warnings"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Contains(warnings, warning => warning.Contains("sandbox-metadata-unsynced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Broken_Sandbox_Pack_Degrades_But_Registration_Still_Succeeds()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        File.WriteAllText(Path.Combine(sandbox, "manifest.json"), "{ \"kind\": \"plugin\" }"); // 沙盒包损坏

        var extra = CreatePack(fixture.Root, "extra");
        var reply = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra });

        Assert.True(reply.Ok, "沙盒包坏掉不该让管理员无法登记其他包。");
        var warnings = reply.Payload!["model"]!["warnings"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Contains(warnings, warning => warning.Contains("sandbox-degraded", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────── 工具面

    [Fact]
    public async Task PlayMode_Tools_Have_No_Pool_Writes_But_Sandbox_Does()
    {
        await using var play = await HostFixture.StartAsync();
        var playClient = await play.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(playClient)).State);
        var playTools = await playClient.ListToolsAsync();
        Assert.Contains(playTools, tool => tool.Name == "pool.list");
        Assert.DoesNotContain(playTools, tool => tool.Name == "pool.register");
        Assert.DoesNotContain(playTools, tool => tool.Name == "pool.unregister");

        await using var sandbox = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var sandboxClient = await sandbox.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(sandboxClient)).State);
        var sandboxTools = await sandboxClient.ListToolsAsync();
        Assert.Contains(sandboxTools, tool => tool.Name == "pool.list");
        Assert.Contains(sandboxTools, tool => tool.Name == "pool.register");
        Assert.Contains(sandboxTools, tool => tool.Name == "pool.unregister");

        await playClient.DisposeAsync();
        await sandboxClient.DisposeAsync();
    }

    [Fact]
    public async Task Viewer_Can_Call_PoolList()
    {
        await using var fixture = await HostFixture.StartAsync();
        var viewer = await fixture.ConnectAsync("viewer");
        Assert.Equal(SessionStates.Pending, (await HostFixture.AttachViewerAsync(viewer, seat: null)).State);
        Assert.True((await fixture.AdminAsync(
            AdminOps.ConnectionAdmit,
            new JsonObject { ["connectionId"] = viewer.ConnectionId })).Ok);

        var listed = await viewer.CallAsync("pool.list");

        Assert.True(listed.Ok, $"{listed.Code}: {listed.Message}");
        await viewer.DisposeAsync();
    }

    [Fact]
    public async Task Sandbox_Tool_Register_And_Unregister_Match_Admin_Op()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);

        var extra = CreatePack(fixture.Root, "extra");
        var toolRegistered = await debug.CallAsync("pool.register", new JsonObject { ["directory"] = extra });
        Assert.True(toolRegistered.Ok, $"{toolRegistered.Code}: {toolRegistered.Message}");

        var adminListed = await fixture.AdminAsync(AdminOps.PoolList);
        var toolListed = await debug.CallAsync("pool.list");
        Assert.Equal(
            adminListed.Payload!["model"]!.ToJsonString(),
            toolListed.Details!["model"]!.ToJsonString());

        var sample = TestSupport.SamplePackage();
        var toolRemoved = await debug.CallAsync("pool.unregister", new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = sample.Version,
        });
        Assert.True(toolRemoved.Ok, $"{toolRemoved.Code}: {toolRemoved.Message}");

        await debug.DisposeAsync();
    }

    // ─────────────────────────────────────────────── 夹具

    private static string CardJson(string cardId) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{cardId}}",
          "name": "pool probe {{cardId}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "neutral", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
            { "component": "tagData", "rarity": "standard" }
          ]
        }
        """;

    /// <summary>在夹具根下造一个最小卡包（id 由目录名 <c>card-pack-{id}</c> 派生）。</summary>
    private static string CreatePack(string root, string packageId)
    {
        var pack = Path.Combine(root, "packs", "card-pack-" + packageId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));
        File.WriteAllText(Path.Combine(pack, "cards", packageId + ".card.json"), CardJson(packageId));
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));
        return pack;
    }

    private static string CopyPack(string source, string parent)
    {
        var target = Path.Combine(parent, Path.GetFileName(source));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    /// <summary>把 manifest.id 换成另一个（模拟「目录被替换成另一个包」）。</summary>
    private static void ReplaceManifestId(string pack, string packageId)
    {
        var path = Path.Combine(pack, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest["id"] = packageId;
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private static void DeleteSandboxPack(HostFixture fixture)
    {
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
