using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.Protocol;
using Orc.Server.Admin;
using Orc.Server.Pool;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S12 守卫与不变量：启动期初值口径、对账的三种磁盘漂移（版本／重复键／IO）、变更被拒后的集合状态、
/// 两个集合事件、drain 窗口的两面一致性、开局输入快照与降级报告、沙盒固定包的边界。
/// </summary>
public sealed class PackPoolGuardTests
{
    // ─────────────────────────────────────────────── 启动期（S1）

    [Fact]
    public async Task SpawnList_Containing_Sandbox_Pack_Is_Filtered_From_Initial_Pool()
    {
        // <sandboxRoot>/pack 在启动时还不存在：过滤必须发生在"可索引校验"之前，否则会拒绝启动。
        await using var fixture = await HostFixture.StartAsync(
            mode: ServerMode.Sandbox,
            includeSandboxPackInPacks: true);

        var info = await fixture.AdminAsync(AdminOps.InstanceInfo);
        var initial = info.Payload!["initialPackDirectories"]!.AsArray()
            .Select(node => Path.GetFullPath(node!.GetValue<string>()))
            .ToArray();

        Assert.Equal(new[] { Path.GetFullPath(TestSupport.SamplePackDirectory()) }, initial);
        Assert.Single((await fixture.AdminAsync(AdminOps.PoolList)).Payload!["model"]!["packages"]!.AsArray());
    }

    [Fact]
    public async Task SpawnList_With_Unindexable_Directory_Rejects_Startup()
    {
        var missing = Path.Combine(Path.GetTempPath(), "orc-no-such-pack-" + Guid.NewGuid().ToString("N"));

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => HostFixture.StartAsync(packs: [missing]));

        Assert.Contains("卡包目录不存在", error.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────── 两面一致性（S1/S8）

    [Fact]
    public async Task PlayMode_Admin_Op_And_Tool_Return_Same_Payload()
    {
        await using var fixture = await HostFixture.StartAsync();
        var admin = await fixture.AdminAsync(AdminOps.PoolList);

        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);
        var tool = await debug.CallAsync("pool.list");

        Assert.True(tool.Ok, $"{tool.Code}: {tool.Message}");
        Assert.Equal(admin.Payload!.ToJsonString(), tool.Details!.ToJsonString());
        Assert.DoesNotContain(fixture.Root, tool.Details!.ToJsonString());

        await debug.DisposeAsync();
    }

    [Fact]
    public async Task PoolList_Tool_Description_Warns_About_Heavy_Operation()
    {
        await using var fixture = await HostFixture.StartAsync();
        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);

        var descriptor = Assert.Single(await debug.ListToolsAsync(), tool => tool.Name == "pool.list");

        Assert.Contains("重操作", descriptor.Description, StringComparison.Ordinal);
        Assert.Contains("下一局开局会用到", descriptor.Description, StringComparison.Ordinal);
        Assert.Contains("轮询", descriptor.Description, StringComparison.Ordinal);

        await debug.DisposeAsync();
    }

    // ─────────────────────────────────────────────── 活跃对局与入参（R3/S4）

    [Fact]
    public async Task Register_And_Unregister_During_Match_Do_Not_Disturb_Current_Match()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);

        Assert.True((await fixture.AdminAsync(AdminOps.MatchStart)).Ok);
        var match = fixture.Host.Service.Executor.Match;
        Assert.NotNull(match);
        var loadedBefore = fixture.Host.Service.Pool!.Packages.ToArray();

        var extra = CreatePack(fixture.Root, "extra");
        var registered = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra });
        Assert.True(registered.Ok, registered.Message);

        // 只影响下一局：本局持有的是开局时装配好的卡库。
        Assert.Same(match, fixture.Host.Service.Executor.Match);
        Assert.Equal(loadedBefore, fixture.Host.Service.Pool!.Packages);
        Assert.Equal("InMatch", fixture.Host.Room.State.ToString());

        // 注销本局正在用的包也不被拒绝（下一局才不再加载它）。
        var sample = TestSupport.SamplePackage();
        var removed = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = sample.Version,
        });
        Assert.True(removed.Ok, removed.Message);
        Assert.Same(match, fixture.Host.Service.Executor.Match);
        Assert.Equal(loadedBefore, fixture.Host.Service.Pool!.Packages);
    }

    [Theory]
    [InlineData("missing-directory")]
    [InlineData("without-manifest")]
    [InlineData("not-cardpack")]
    public async Task Register_Input_Failures_Return_InvalidArguments_Without_Changing_Pool(string shape)
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var directory = Path.Combine(fixture.Root, "bad-" + shape);
        Directory.CreateDirectory(directory);
        if (shape == "not-cardpack")
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"), """{ "kind": "plugin" }""");
        }
        else if (shape == "missing-directory")
        {
            Directory.Delete(directory);
        }

        var reply = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = directory });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    [Fact]
    public async Task Unregister_Unknown_Id_Returns_InvalidArguments()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        var reply = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = "no-such-pack",
            ["version"] = "1.0.0",
        });

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Single(reply.Payload!["model"]!["packages"]!.AsArray());
    }

    // ─────────────────────────────────────────────── 对账的磁盘漂移（S2）

    [Fact]
    public async Task Version_Drift_Is_Visible_Without_ReRegister()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);
        var extra = CreatePack(fixture.Root, "extra");
        var registered = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra });
        var before = registered.Payload!["change"]!["added"]![0]!["version"]!.GetValue<string>();

        // 编辑器 promote 的等价物：改内容 → 重算指纹（同一目录、不重新注册）。
        AddCard(extra, "extra2");

        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        var row = Assert.Single(
            listed.Payload!["model"]!["packages"]!.AsArray(),
            node => node!["packageId"]!.GetValue<string>() == "extra")!;

        var after = row["version"]!.GetValue<string>();
        Assert.NotEqual(before, after);
        Assert.True(row["ok"]!.GetValue<bool>());

        // 下一局直接装载新版本（无需重新注册）。
        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        var loaded = Assert.Single(
            started.Payload!["assemblyInput"]!["packages"]!.AsArray(),
            node => node!["id"]!.GetValue<string>() == "extra")!;
        Assert.Equal(after, loaded["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task PoolList_Reports_Ok_False_When_Remaining_Entries_Cannot_Assemble()
    {
        using var temp = new TestSupport.TempDirectory("orc-pool-guard");
        var only = CreatePack(temp.Path, "only");

        await using var fixture = await HostFixture.StartAsync(packs: [only]);

        // 唯一集合条目目录消失 + 沙盒固定包也不存在 ⇒ 预演没有可装配输入（ok=false，但变更本身仍合法）。
        Directory.Delete(only, recursive: true);
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }

        var listed = await fixture.AdminAsync(AdminOps.PoolList);

        Assert.False(listed.Ok);
        var row = Assert.Single(listed.Payload!["model"]!["packages"]!.AsArray())!;
        Assert.False(row["ok"]!.GetValue<bool>());
        Assert.True(row["willRemove"]!.GetValue<bool>());
        Assert.Equal(PackageProbeCodes.DirectoryMissing, row["errorCode"]!.GetValue<string>());
        Assert.Contains(
            listed.Payload!["model"]!["issues"]!.AsArray(),
            issue => issue!["code"]!.GetValue<string>() == "EmptyPool");
    }

    [Fact]
    public async Task Duplicate_Key_Drift_Keeps_First_And_Reconciles_Later()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);

        // A 先注册（alpha@v1）；B 是 A 的副本，加了一张卡变成 alpha@v2 ⇒ 两个不同的键，可并存。
        var first = CreatePack(fixture.Root, "alpha");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = first })).Ok);
        var second = CopyPack(first, fixture.Root);
        AddCard(second, "alpha-second");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = second })).Ok);

        // 磁盘漂移：A 也被改成 v2（同 id、同内容指纹）⇒ 两条目键相同，保留先注册者 A。
        AddCard(first, "alpha-second");

        var reply = await fixture.AdminAsync(
            AdminOps.PoolRegister,
            new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });

        Assert.True(reply.Ok, reply.Message);
        var reconciled = reply.Payload!["change"]!["removedByReconcile"]!;
        Assert.Equal(1, reconciled["count"]!.GetValue<int>());
        var entry = reconciled["entries"]!.AsArray()[0]!.AsObject();
        Assert.Equal("alpha", entry["id"]!.GetValue<string>());
        Assert.Equal("duplicate-key", entry["cause"]!.GetValue<string>());

        var kept = reply.Payload!["model"]!["packages"]!.AsArray()
            .Select(node => node!["packageId"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(new[] { TestSupport.SamplePackage().PackageId, "alpha" }, kept);
    }

    [Fact]
    public async Task Io_Failure_Marks_Entry_Invalid_And_Reconciles_With_Cause_Io()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var extra = CreatePack(fixture.Root, "extra");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra })).Ok);

        // 占住 manifest（独占）⇒ 读盘抛 IOException：读面按"失效条目"报告，绝不让整个对账失败。
        using (new FileStream(
                   Path.Combine(extra, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var listed = await fixture.AdminAsync(AdminOps.PoolList);

            Assert.True(listed.Ok, "坏盘符/被占用只让该条目失效，其余条目仍可装配。");
            var broken = Assert.Single(
                listed.Payload!["model"]!["packages"]!.AsArray(),
                node => node!["ok"]!.GetValue<bool>() == false)!;
            Assert.Equal(PackageProbeCodes.IoError, broken["errorCode"]!.GetValue<string>());
            Assert.True(broken["willRemove"]!.GetValue<bool>());

            // 写面触发的真对账：removed[] 的 cause 用 S2 的 `io` 口径。
            var reply = await fixture.AdminAsync(
                AdminOps.PoolRegister,
                new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });

            Assert.True(reply.Ok, reply.Message);
            var entry = reply.Payload!["change"]!["removedByReconcile"]!["entries"]!.AsArray()[0]!.AsObject();
            Assert.Equal("io", entry["cause"]!.GetValue<string>());
        }
    }

    // ─────────────────────────────────────────────── 事务边界与不变量（S4/S12）

    [Fact]
    public async Task Rejected_Change_Keeps_Post_Reconcile_State_And_Match_Still_Starts()
    {
        // AI 座位＝配置即批准：开局前置能通过，才能验证"对账之后必然可装配"。
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);

        var victim = CreatePack(fixture.Root, "victim");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = victim })).Ok);
        Directory.Delete(victim, recursive: true); // 失效条目：下一次写面才对账移除

        var broken = CreatePack(fixture.Root, "broken");
        CorruptSidecar(broken); // 包级结构性非法 ⇒ 变更被拒

        var rejected = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = broken });

        Assert.False(rejected.Ok);
        Assert.Equal(ResultCodes.MissingAssets, rejected.Code);

        // 变更被拒，但对账已独立提交：集合＝对账后状态（只剩样例包）。
        var reconciled = rejected.Payload!["change"]!["removedByReconcile"]!;
        Assert.Equal(1, reconciled["count"]!.GetValue<int>());
        Assert.Equal("victim", reconciled["entries"]![0]!["id"]!.GetValue<string>());

        // 被拒响应的 model 是"被拒候选的试算结论"（含冲突/不可装载行）；集合真身看 pool.list。
        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        var kept = Assert.Single(listed.Payload!["model"]!["packages"]!.AsArray())!;
        Assert.Equal(TestSupport.SamplePackage().PackageId, kept["packageId"]!.GetValue<string>());

        // 不变量：对账之后按集合开局必然可装配。
        var started = await fixture.AdminAsync(AdminOps.MatchStart);
        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
    }

    // ─────────────────────────────────────────────── 变更事件（S10）

    [Fact]
    public async Task Changed_Event_Is_Emitted_On_Success_Only_And_Has_No_Paths()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var events = new ConcurrentQueue<JsonObject>();
        fixture.Host.ChannelEvent += message => events.Enqueue((JsonObject)message.DeepClone());

        var extra = CreatePack(fixture.Root, "extra");
        Assert.True((await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = extra })).Ok);

        var changed = Assert.Single(events, message => message["event"]!.GetValue<string>() == "packset.changed");
        Assert.Equal("extra", changed["payload"]!["added"]![0]!["id"]!.GetValue<string>());
        Assert.True(changed["payload"]!["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Root, changed.ToJsonString());

        // 没有移除 ⇒ 不发 packset.reconciled（避免噪音）。
        Assert.DoesNotContain(events, message => message["event"]!.GetValue<string>() == "packset.reconciled");

        // 失败不发（失败信息只在响应里）。
        var failed = await fixture.AdminAsync(AdminOps.PoolRegister, new JsonObject { ["directory"] = "packs/relative" });
        Assert.False(failed.Ok);
        Assert.Single(events, message => message["event"]!.GetValue<string>() == "packset.changed");

        // `sandbox.state` 仍只报"本局装配结果"，不出现集合段（S10：集合只经 pool.list 读）。
        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);
        var state = await debug.CallAsync("sandbox.state");
        Assert.True(state.Ok, $"{state.Code}: {state.Message}");
        var stateDetails = state.Details!.AsObject();
        Assert.True(stateDetails.ContainsKey("pool")); // 字段语义不变（未开局＝null）
        Assert.False(stateDetails.ContainsKey("registered"));
        Assert.False(stateDetails.ContainsKey("poolList"));
        Assert.False(stateDetails.ContainsKey("packages"));
        await debug.DisposeAsync();
    }

    // ─────────────────────────────────────────────── drain 窗口（S5/S6）

    [Fact]
    public async Task Drain_Rejects_Pool_Writes_With_Same_Message_On_Both_Faces_And_Allows_List()
    {
        await using var fixture = await HostFixture.StartAsync(mode: ServerMode.Sandbox, allowDebugWrites: true);
        var debug = await fixture.ConnectAsync("debug");
        Assert.Equal(SessionStates.Debug, (await HostFixture.AttachDebugAsync(debug)).State);

        var drainField = typeof(Orc.Server.Transport.ServerHost).GetField(
            "_draining", BindingFlags.Instance | BindingFlags.NonPublic)!;
        drainField.SetValue(fixture.Host, true);

        try
        {
            var adminWrite = await fixture.AdminAsync(
                AdminOps.PoolRegister,
                new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });
            Assert.False(adminWrite.Ok);
            Assert.Equal(ResultCodes.RoomRotating, adminWrite.Code);
            Assert.Equal(PackPool.DrainMessage, adminWrite.Message);

            var adminList = await fixture.AdminAsync(AdminOps.PoolList);
            Assert.True(adminList.Ok, adminList.Message);

            var toolWrite = await debug.CallAsync(
                "pool.register",
                new JsonObject { ["directory"] = TestSupport.SamplePackDirectory() });
            Assert.False(toolWrite.Ok);
            Assert.Equal(ResultCodes.RoomRotating, toolWrite.Code);
            Assert.Equal(PackPool.DrainMessage, toolWrite.Message);

            var toolUnregister = await debug.CallAsync(
                "pool.unregister",
                new JsonObject { ["packageId"] = TestSupport.SamplePackage().PackageId, ["version"] = "0.0.0" });
            Assert.False(toolUnregister.Ok);
            Assert.Equal(ResultCodes.RoomRotating, toolUnregister.Code);
            Assert.Equal(PackPool.DrainMessage, toolUnregister.Message);

            var toolList = await debug.CallAsync("pool.list");
            Assert.True(toolList.Ok, $"{toolList.Code}: {toolList.Message}");

            // 守卫：drain 白名单**只**含 pool.list（其余只读工具照旧被拒）。
            var otherRead = await debug.CallAsync("service.info");
            Assert.False(otherRead.Ok);
            Assert.Equal(ResultCodes.RoomRotating, otherRead.Code);
        }
        finally
        {
            drainField.SetValue(fixture.Host, false);
        }

        await debug.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_Admin_Op_Returns_Readable_Failure_And_Ops_Are_Enumerated()
    {
        await using var fixture = await HostFixture.StartAsync();

        var reply = await fixture.AdminAsync("pool.nope");

        Assert.False(reply.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, reply.Code);
        Assert.Contains("未知管理操作", reply.Message!, StringComparison.Ordinal);

        // AdminOps.All 是"业务端口不得路由"守卫测试的输入：三项必须在其中。
        Assert.Contains(AdminOps.PoolList, AdminOps.All);
        Assert.Contains(AdminOps.PoolRegister, AdminOps.All);
        Assert.Contains(AdminOps.PoolUnregister, AdminOps.All);
    }

    [Fact]
    public async Task Connected_Client_Cannot_Call_PoolList()
    {
        await using var fixture = await HostFixture.StartAsync();
        var client = await fixture.ConnectAsync(); // 握手但未 attach ⇒ Connected

        var listed = await client.CallAsync("pool.list");

        Assert.False(listed.Ok);
        Assert.Equal(ResultCodes.NotAdmitted, listed.Code);

        await client.DisposeAsync();
    }

    // ─────────────────────────────────────────────── 开局输入与降级（S7）

    [Fact]
    public async Task MatchStart_Reports_AssemblyInput_And_Events_Have_No_Directories()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)]);

        var events = new ConcurrentQueue<JsonObject>();
        fixture.Host.ChannelEvent += message => events.Enqueue((JsonObject)message.DeepClone());

        var started = await fixture.AdminAsync(AdminOps.MatchStart);

        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        var input = started.Payload!["assemblyInput"]!.AsObject();
        var package = Assert.Single(input["packages"]!.AsArray())!.AsObject();
        Assert.Equal(TestSupport.SamplePackage().PackageId, package["id"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(TestSupport.SamplePackDirectory()), Path.GetFullPath(package["directory"]!.GetValue<string>()));
        Assert.False(input["usedLastGood"]!.GetValue<bool>());
        Assert.False(input["sandboxDropped"]!.GetValue<bool>());
        Assert.Empty(input["reconciled"]!.AsArray());

        var assembled = Assert.Single(events, message => message["event"]!.GetValue<string>() == "pool.assembled");
        Assert.DoesNotContain(fixture.Root, assembled["payload"]!["assembly"]!.ToJsonString());
        Assert.DoesNotContain(fixture.Root, assembled["payload"]!["degradation"]!.ToJsonString());
    }

    [Fact]
    public async Task Broken_Sandbox_Pack_Reports_SandboxDropped_On_Both_Faces()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox);

        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        File.WriteAllText(Path.Combine(sandbox, "manifest.json"), "{ \"kind\": \"plugin\" }");

        // 读面：剔除沙盒包后仍可装配 ⇒ ok=true，且告警与注册时同一口径。
        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.True(listed.Ok, listed.Message);
        Assert.Contains(
            listed.Payload!["model"]!["warnings"]!.AsArray(),
            warning => warning!.GetValue<string>().Contains("sandbox-degraded", StringComparison.Ordinal));

        var events = new ConcurrentQueue<JsonObject>();
        fixture.Host.ChannelEvent += message => events.Enqueue((JsonObject)message.DeepClone());

        var started = await fixture.AdminAsync(AdminOps.MatchStart);

        Assert.True(started.Ok, $"{started.Code}: {started.Message}");
        Assert.True(started.Payload!["assemblyInput"]!["sandboxDropped"]!.GetValue<bool>());
        Assert.True(started.Payload!["fallback"]!.GetValue<bool>());

        var assembled = Assert.Single(events, message => message["event"]!.GetValue<string>() == "pool.assembled");
        Assert.True(assembled["payload"]!["degradation"]!["sandboxDropped"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Root, assembled["payload"]!["degradation"]!.ToJsonString());
    }

    // ─────────────────────────────────────────────── 沙盒固定包边界（S9）

    [Fact]
    public async Task Unregister_And_SandboxReset_Do_Not_Touch_Sandbox_Pack()
    {
        await using var fixture = await HostFixture.StartAsync(
            seats: [new SeatConfig(SeatKind.Ai), new SeatConfig(SeatKind.Ai)],
            mode: ServerMode.Sandbox,
            allowDebugWrites: true);
        var sandbox = Path.Combine(fixture.Options.SandboxRoot, "pack");
        Assert.True(Directory.Exists(sandbox), "boot 必须已生成沙盒固定包。");

        var sample = TestSupport.SamplePackage();
        var removed = await fixture.AdminAsync(AdminOps.PoolUnregister, new JsonObject
        {
            ["packageId"] = sample.PackageId,
            ["version"] = sample.Version,
        });
        Assert.True(removed.Ok, removed.Message);
        Assert.True(Directory.Exists(sandbox), "注销集合条目不动物理目录（沙盒包更不在集合里）。");

        // 集合已空 + 沙盒包合法 ⇒ 仍可装配（ok=true），但逐包行里没有沙盒包（它不属于集合）。
        var listed = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.True(listed.Ok, "只剩沙盒固定包时仍可装配。");
        Assert.Empty(listed.Payload!["model"]!["packages"]!.AsArray());

        var reset = await fixture.AdminAsync(AdminOps.SandboxReset);
        Assert.True(reset.Ok, reset.Message);

        var afterReset = await fixture.AdminAsync(AdminOps.PoolList);
        Assert.Empty(afterReset.Payload!["model"]!["packages"]!.AsArray());
        Assert.True(Directory.Exists(sandbox));
    }

    // ─────────────────────────────────────────────── 夹具

    private static string CardJson(string cardId) => $$"""
        {
          "schemaVersion": 1,
          "id": "{{cardId}}",
          "name": "pool guard {{cardId}}",
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
        var pack = Path.Combine(root, "pools", "card-pack-" + packageId);
        Directory.CreateDirectory(Path.Combine(pack, "cards"));
        File.WriteAllText(Path.Combine(pack, "cards", packageId + ".card.json"), CardJson(packageId));
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));
        return pack;
    }

    /// <summary>给已有卡包加一张卡并重算元数据（等价于编辑器 promote 后的版本漂移）。</summary>
    private static void AddCard(string pack, string cardId)
    {
        File.WriteAllText(Path.Combine(pack, "cards", cardId + ".card.json"), CardJson(cardId));
        AssetMigrationTool.Apply(AssetMigrationTool.Plan(pack));
    }

    private static string CopyPack(string source, string parent)
    {
        var target = Path.Combine(parent, "pools", Path.GetFileName(source) + "-copy");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        return target;
    }

    /// <summary>把 sidecar 写成不可解析的内容 ⇒ 包级结构性非法（manifest 本身仍合法）。</summary>
    private static void CorruptSidecar(string pack)
    {
        var card = Directory.GetFiles(Path.Combine(pack, "cards"), "*.card.json").Single();
        File.WriteAllText(card + ".meta.json", "{ not-json");
    }
}
