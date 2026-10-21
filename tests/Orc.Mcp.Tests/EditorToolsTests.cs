using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Packages.Editing;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// S9「MCP 工具与门控」验收（编辑器侧）：<c>editor.*</c> 全组仅在 Authoring 档登记、与
/// <see cref="AssetEditor"/> 能力集一一对应、结构化过滤生效、**工具与进程内 API 等价**。
/// <para>
/// 等价性判据：工具返回的资产投影与同一会话内直接调用 <see cref="AssetEditor"/> 的结果**逐字段一致**
/// （同一后端组件、同一份过滤判定，不存在第二套实现）。
/// </para>
/// </summary>
public sealed class EditorToolsTests : IDisposable
{
    /// <summary>R9 能力集 → 工具名的期望映射（一一对应；总数不得漂移）。</summary>
    private static readonly string[] EditorTools =
    [
        "editor.pack_list",     // pack.list
        "editor.session_begin", // session.begin
        "editor.session_end",   // session.end
        "editor.asset_list",    // asset.list
        "editor.asset_get",     // asset.get
        "editor.asset_commit",  // asset.commit
        "editor.asset_create",  // asset.create
        "editor.asset_delete",  // asset.delete
        "editor.version_set",   // version.set
        "editor.dep_set",       // dep.set
        "editor.lock_preview",  // lock.preview
        "editor.promote",       // promote
        "editor.reload",        // reload
    ];

    private readonly SandboxFixture _fixture = new(McpServiceMode.Authoring);

    public void Dispose() => _fixture.Dispose();

    // ─────────────────────────────────────────────── 门控（R11）

    [Fact]
    public void Authoring_Registers_Every_Editor_Tool_In_Own_Group()
    {
        var names = _fixture.Service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in EditorTools)
        {
            Assert.Contains(name, names);
        }

        // 能力集与工具集**恰好**一致（多一条＝未映射能力，少一条＝能力缺失）
        var registered = _fixture.Service.Tools
            .Where(tool => tool.Name.StartsWith("editor.", StringComparison.Ordinal))
            .Select(tool => tool.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(EditorTools.OrderBy(name => name, StringComparer.Ordinal).ToArray(), registered);

        // 命名与分组：点后 snake_case、group＝editor
        foreach (var descriptor in _fixture.Service.Tools.Where(tool => tool.Name.StartsWith("editor.", StringComparison.Ordinal)))
        {
            Assert.Equal("editor", descriptor.Group);
            Assert.Equal(2, descriptor.Name.Split('.').Length); // 单点前缀：点后 snake_case
        }
    }

    [Fact]
    public async Task Player_Has_No_Editor_Tools_And_Calls_Are_Unknown()
    {
        using var player = new SandboxFixture(McpServiceMode.Player);
        var names = player.Service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in EditorTools)
        {
            Assert.DoesNotContain(name, names);
        }

        // 非沙盒不登记 ⇒ 调用即 unknown-tool（连"模式拒绝"的面都不存在）
        var result = await player.InvokeAsync("editor.pack_list");
        Assert.False(result.Ok);
        Assert.Equal(McpCodes.UnknownTool, result.Code);
    }

    // ─────────────────────────────────────────────── 会话与资产生命周期

    [Fact]
    public async Task PackList_Lists_Sandbox_Pack_After_Scaffold()
    {
        _fixture.Service.Sandbox.EnsureScaffold();

        var result = await _fixture.InvokeAsync("editor.pack_list");

        Assert.True(result.Ok, result.Message);
        var packages = result.Details!["packages"]!.AsArray();
        var package = Assert.Single(packages.Select(node => node!.AsObject()));
        Assert.Equal("sandbox", (string)package["packageId"]!);
        Assert.Equal("pack", Path.GetFileName((string)package["rootDirectory"]!));
        Assert.True(Directory.Exists((string)package["rootDirectory"]!));
        Assert.False((bool)package["hasTemporaryPackage"]!);
    }

    [Fact]
    public async Task SessionBegin_Edit_Defaults_To_Sandbox_Pack_And_Copies_To_Tmp()
    {
        _fixture.Service.Sandbox.EnsureScaffold();

        var result = await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""");

        Assert.True(result.Ok, result.Message);
        var temporaryRoot = (string)result.Details!["temporaryRoot"]!;
        Assert.Equal("pack", Path.GetFileName((string)result.Details["packageRoot"]!));
        Assert.Equal("pack.tmp", Path.GetFileName(temporaryRoot));
        Assert.True(Directory.Exists(temporaryRoot));
        Assert.True(File.Exists(Path.Combine(temporaryRoot, "manifest.json")));

        // 单会话：第二次 begin 被拒绝
        var again = await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""");
        Assert.False(again.Ok);
        Assert.Equal(McpCodes.ValidationFailed, again.Code);
        Assert.Equal("SessionAlreadyActive", (string)again.Details!["editorCode"]!);
    }

    [Fact]
    public async Task Promote_Conflict_Reports_PromoteFailed_As_ValidationFailed()
    {
        var packageRoot = Path.Combine(_fixture.Root, "demo");
        CopyDirectory(Path.Combine(FindRepoRoot(), "samples", "card-pack-demo"), packageRoot);

        var begun = await _fixture.InvokeAsync(
            "editor.session_begin",
            JsonSerializer.Serialize(new { mode = "edit", packageRoot }));
        Assert.True(begun.Ok, begun.Message);
        var committed = await _fixture.InvokeAsync(
            "editor.asset_commit", """{ "path": "cards/demo.rifleman.card.json" }""");
        Assert.True(committed.Ok, committed.Message);
        Assert.True((bool)committed.Details!["versionBumped"]!);

        var conflict = Path.Combine(packageRoot, "versions", "1.0.0", "cards", "demo.rifleman.card.json");
        Directory.CreateDirectory(Path.GetDirectoryName(conflict)!);
        await File.WriteAllTextAsync(conflict, """{ "conflict": true }""");

        var result = await _fixture.InvokeAsync("editor.promote");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.ValidationFailed, result.Code);
        Assert.Equal("PromoteFailed", (string)result.Details!["editorCode"]!);
        Assert.Contains("versions/1.0.0/cards/demo.rifleman.card.json", result.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(packageRoot + ".tmp")); // 临时包保留
    }

    [Fact]
    public async Task Commit_On_Blocked_Parent_Maps_IoException_To_ValidationFailed()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);

        var temporaryRoot = Path.Combine(_fixture.Root, "pack.tmp");
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "cards"));
        File.WriteAllText(Path.Combine(temporaryRoot, "cards", "blocked"), "占用该名字的文件");

        var result = await _fixture.InvokeAsync(
            "editor.asset_commit",
            """{ "path": "cards/blocked/x.card.json", "content": "{}" }""");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.ValidationFailed, result.Code);
        Assert.Equal(nameof(IOException), (string)result.Details!["cause"]!);
        Assert.NotNull(result.Details["error"]);
        Assert.DoesNotContain("   at ", result.Message, StringComparison.Ordinal); // 不泄露堆栈
    }

    [Fact]
    public async Task Commit_On_ReadOnly_File_Maps_UnauthorizedAccess_To_PathDenied()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);

        var temporaryRoot = Path.Combine(_fixture.Root, "pack.tmp");
        var target = Path.Combine(temporaryRoot, "cards", "readonly.card.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "{}");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            var result = await _fixture.InvokeAsync(
                "editor.asset_commit",
                """{ "path": "cards/readonly.card.json", "content": "{}" }""");

            Assert.False(result.Ok);
            Assert.Equal(McpCodes.PathDenied, result.Code);
            Assert.Equal(nameof(UnauthorizedAccessException), (string)result.Details!["cause"]!);
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Guard_Maps_Only_Io_And_Unauthorized_And_Has_No_CatchAll()
    {
        var text = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Orc.Mcp", "Tools", "EditorTools.cs"));
        var start = text.IndexOf("private static McpToolResult Guard", StringComparison.Ordinal);
        var end = text.IndexOf("private static string MapCode", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var guard = text[start..end];
        Assert.Contains("catch (UnauthorizedAccessException ex)", guard, StringComparison.Ordinal);
        Assert.Contains("catch (IOException ex)", guard, StringComparison.Ordinal);
        Assert.Contains("[\"cause\"]", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (System.Exception", guard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSession_Tool_Returns_NotReady()
    {
        var result = await _fixture.InvokeAsync("editor.asset_list");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.NotReady, result.Code);
        Assert.Equal("NoSession", (string)result.Details!["editorCode"]!);
    }

    [Fact]
    public async Task AssetLifecycle_Create_Commit_List_Get_Version_LockPreview_Discard()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);

        // 新建（版本从 1.0.0 起）
        var created = await _fixture.InvokeAsync(
            "editor.asset_create", """{ "kind": "card", "id": "editor.alpha" }""");
        Assert.True(created.Ok, created.Message);
        Assert.Equal("1.0.0", (string)created.Details!["version"]!);
        Assert.Equal("pack/sandbox/editor.alpha", (string)created.Details["appId"]!);

        // 内容通道提交（首次提交不递增：新建即 1.0.0）
        var content = CardJson("editor.alpha", "alpha 卡", "fire");
        var committed = await _fixture.InvokeAsync(
            "editor.asset_commit",
            JsonSerializer.Serialize(new { path = "cards/editor.alpha.card.json", content }));
        Assert.True(committed.Ok, committed.Message);
        Assert.Equal("content", (string)committed.Details!["channel"]!);
        Assert.False((bool)committed.Details["versionBumped"]!);
        Assert.Equal("1.0.0", (string)committed.Details["asset"]!["version"]!);

        // 文件指针通道提交（前端已直写 ⇒ 后端只读该文件）；第二次提交＝尾段 +1
        var pointer = await _fixture.InvokeAsync(
            "editor.asset_commit", """{ "path": "cards/editor.alpha.card.json" }""");
        Assert.True(pointer.Ok, pointer.Message);
        Assert.Equal("path", (string)pointer.Details!["channel"]!);
        Assert.True((bool)pointer.Details["versionBumped"]!);
        Assert.Equal("1.0.1", (string)pointer.Details["asset"]!["version"]!);

        // get / list
        var got = await _fixture.InvokeAsync("editor.asset_get", """{ "kind": "card", "id": "editor.alpha" }""");
        Assert.True(got.Ok, got.Message);
        Assert.Equal("1.0.1", (string)got.Details!["version"]!);
        Assert.Equal(["fire"], got.Details["tags"]!.AsArray().Select(node => (string)node!).ToArray());

        // version.set（缺省＝自动递增）
        var bumped = await _fixture.InvokeAsync("editor.version_set", """{ "kind": "card", "id": "editor.alpha" }""");
        Assert.True(bumped.Ok, bumped.Message);
        Assert.Equal("1.0.2", (string)bumped.Details!["version"]!);

        // lock.preview：正式 lock（预测）＋ 测试 lock（实况）
        var preview = await _fixture.InvokeAsync("editor.lock_preview");
        Assert.True(preview.Ok, preview.Message);
        Assert.Single(preview.Details!["formal"]!["assets"]!.AsArray());
        Assert.Single(preview.Details["test"]!["assets"]!.AsArray());
        Assert.Equal("formal", (string)preview.Details["formal"]!["domain"]!);

        // 放弃：临时包不残留
        var discarded = await _fixture.InvokeAsync("editor.session_end", """{ "mode": "discard" }""");
        Assert.True(discarded.Ok, discarded.Message);
        Assert.False(Directory.Exists(Path.Combine(_fixture.Root, "pack.tmp")));
        Assert.False(_fixture.Service.Editor.HasActiveSession);
    }

    [Fact]
    public async Task SessionEnd_Save_Promotes_Package_And_Writes_Formal_Lock()
    {
        var scaffold = _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);
        Assert.True((await _fixture.InvokeAsync(
            "editor.asset_create", """{ "kind": "card", "id": "editor.promoted" }""")).Ok);

        var saved = await _fixture.InvokeAsync("editor.session_end", """{ "mode": "save" }""");

        Assert.True(saved.Ok, saved.Message);
        Assert.False(Directory.Exists(Path.Combine(_fixture.Root, "pack.tmp")));
        Assert.Single(saved.Details!["formalLock"]!["assets"]!.AsArray());
        Assert.Empty(saved.Details["testLock"]!["assets"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(scaffold.PackRoot, "lock.formal.json")));
        Assert.True(File.Exists(Path.Combine(scaffold.PackRoot, "cards", "editor.promoted.card.json")));
    }

    [Fact]
    public async Task Reload_Returns_Structured_Loader_Result()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);

        var result = await _fixture.InvokeAsync("editor.reload");

        Assert.True(result.Ok, result.Message);
        // 已注入资产加载器（editor.reload 走 AssetLoader，而不是工具内自算）
        Assert.True((bool)result.Details!["configured"]!);
        Assert.Contains(
            (string)result.Details["status"]!,
            new[] { "Loaded", "Reloaded", "Unchanged", "Rejected", "Failed", "Invalid" });
        Assert.NotNull(result.Details["issues"]);
        Assert.NotNull(result.Details["firstChangedLayer"]);
        Assert.NotNull(result.Details["ok"]);
    }

    [Fact]
    public async Task SessionBegin_Outside_Sandbox_Root_Is_Path_Denied()
    {
        // 沙盒根之外的合法卡包：策略是**单根**且会话期不扩权 ⇒ 临时包目录（`<包目录>.tmp`）的
        // 直接父目录不在授权根内，一律拒绝（越权不因"包看起来合法"而放行）。
        var outsidePackage = Path.Combine(Path.GetTempPath(), "orc-mcp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePackage);
        await File.WriteAllTextAsync(
            Path.Combine(outsidePackage, "manifest.json"),
            $$"""
            {
              "schemaVersion": 2,
              "id": "outside",
              "guid": "{{Guid.NewGuid()}}",
              "kind": "cardpack",
              "version": "outside-tmp",
              "metadata": []
            }
            """);

        try
        {
            var result = await _fixture.InvokeAsync(
                "editor.session_begin",
                JsonSerializer.Serialize(new { mode = "edit", packageRoot = outsidePackage }));

            Assert.False(result.Ok);
            Assert.Equal(McpCodes.PathDenied, result.Code);
            Assert.Equal("PathDenied", (string)result.Details!["editorCode"]!);
        }
        finally
        {
            Directory.Delete(outsidePackage, recursive: true);
        }
    }

    // ─────────────────────────────────────────────── 结构化过滤（R9）

    [Fact]
    public async Task AssetList_Structured_Filters_Take_Effect()
    {
        await ArrangeThreeAssetsAsync();

        Assert.Equal(3, await CountAsync("{}"));
        Assert.Equal(3, await CountAsync("""{ "kind": "card" }"""));
        Assert.Equal(0, await CountAsync("""{ "kind": "effect" }"""));
        Assert.Equal(3, await CountAsync("""{ "packageId": "sandbox" }"""));
        Assert.Equal(0, await CountAsync("""{ "packageId": "other" }"""));

        // 前缀过滤
        var alpha = await ListAsync("""{ "idPrefix": "alpha" }""");
        Assert.Equal(new[] { "alpha.one", "alpha.two" }, alpha.Select(asset => (string)asset["id"]!).ToArray());

        // 标签过滤（全部命中）
        var fire = await ListAsync("""{ "tags": ["fire"] }""");
        Assert.Equal(new[] { "alpha.one" }, fire.Select(asset => (string)asset["id"]!).ToArray());
        Assert.Empty(await ListAsync("""{ "tags": ["fire", "fast"] }"""));

        // 版本范围（alpha.one 已提交两次 ⇒ 1.0.1；其余仍为 1.0.0）
        var upper = await ListAsync("""{ "minVersion": "1.0.1", "maxVersion": "1.0.1" }""");
        Assert.Equal(new[] { "alpha.one" }, upper.Select(asset => (string)asset["id"]!).ToArray());

        var lower = await ListAsync("""{ "minVersion": "1.0.0", "maxVersion": "1.0.0" }""");
        Assert.Equal(new[] { "alpha.two", "beta.one" }, lower.Select(asset => (string)asset["id"]!).ToArray());

        // 组合过滤
        var combined = await ListAsync("""{ "kind": "card", "idPrefix": "alpha", "tags": ["fire"] }""");
        Assert.Single(combined);
        Assert.Equal("alpha.one", (string)combined[0]["id"]!);
    }

    [Fact]
    public async Task AssetList_Tool_And_InProcess_Api_Are_Equivalent()
    {
        await ArrangeThreeAssetsAsync();
        var editor = _fixture.Service.Editor;

        // 同一后端组件：结构化过滤入参与 lambda 谓词走同一求值路径
        var viaTool = await ListAsync("""{ "kind": "card", "idPrefix": "alpha", "tags": ["fire"] }""");
        var viaLambda = editor.ListAssets(asset =>
            asset.Kind == AssetKind.Card
            && asset.PackageId == "sandbox"
            && asset.LocalId.StartsWith("alpha", StringComparison.Ordinal)
            && asset.Tags.Contains("fire", StringComparer.Ordinal));
        var viaQuery = editor.ListAssets(new EditorAssetQuery
        {
            Kind = AssetKind.Card,
            PackageId = "sandbox",
            IdPrefix = "alpha",
            Tags = ["fire"],
        });

        Assert.Equal(Project(viaLambda), Project(viaQuery));
        Assert.Equal(Project(viaLambda), ProjectTool(viaTool));

        // 「全部」路径同样等价（无过滤＝null 谓词）
        var allTool = await ListAsync("{}");
        Assert.Equal(Project(editor.ListAssets((Func<EditorAssetInfo, bool>?)null)), ProjectTool(allTool));
    }

    [Fact]
    public async Task VersionSet_Rejects_NonIncreasing_With_Structured_Code()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);
        Assert.True((await _fixture.InvokeAsync(
            "editor.asset_create", """{ "kind": "card", "id": "editor.versioned" }""")).Ok);

        // 与旧版相等（补 0 后相等）⇒ 拒绝
        var same = await _fixture.InvokeAsync(
            "editor.version_set", """{ "kind": "card", "id": "editor.versioned", "version": "1.0.0.0" }""");
        Assert.False(same.Ok);
        Assert.Equal(McpCodes.ValidationFailed, same.Code);
        Assert.Equal("VersionNotIncreasing", (string)same.Details!["editorCode"]!);

        // 格式非法 ⇒ 拒绝
        var malformed = await _fixture.InvokeAsync(
            "editor.version_set", """{ "kind": "card", "id": "editor.versioned", "version": "1.x" }""");
        Assert.False(malformed.Ok);
        Assert.Equal("InvalidVersion", (string)malformed.Details!["editorCode"]!);

        // 严格大于 ⇒ 通过
        var ok = await _fixture.InvokeAsync(
            "editor.version_set", """{ "kind": "card", "id": "editor.versioned", "version": "1.2" }""");
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("1.2", (string)ok.Details!["version"]!);
    }

    [Fact]
    public async Task DepSet_Writes_Dependencies_Into_Test_Lock()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);
        Assert.True((await _fixture.InvokeAsync(
            "editor.asset_create", """{ "kind": "card", "id": "editor.host" }""")).Ok);

        var result = await _fixture.InvokeAsync(
            "editor.dep_set",
            """
            {
              "kind": "card",
              "id": "editor.host",
              "dependencies": [ { "kind": "effect", "appId": "shared.effect", "versionConstraint": "1.*.*" } ]
            }
            """);

        Assert.True(result.Ok, result.Message);

        // 裸局部 ID 归一到本包前缀（应用层 ID 形态）
        var preview = await _fixture.InvokeAsync("editor.lock_preview");
        Assert.True(preview.Ok, preview.Message);
        var sidecar = Directory
            .EnumerateFiles(Path.Combine(_fixture.Root, "pack.tmp"), "*.meta.json", SearchOption.AllDirectories)
            .Single(path => path.Contains("editor.host", StringComparison.Ordinal));
        var text = await File.ReadAllTextAsync(sidecar);
        Assert.Contains("pack/sandbox/shared.effect", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetDelete_Removes_Current_Version_From_Declaration_And_Locks()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);
        Assert.True((await _fixture.InvokeAsync(
            "editor.asset_create", """{ "kind": "card", "id": "editor.doomed" }""")).Ok);
        Assert.Single(await ListAsync("{}"));

        var deleted = await _fixture.InvokeAsync(
            "editor.asset_delete", """{ "kind": "card", "id": "editor.doomed" }""");

        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal("pack/sandbox/editor.doomed", (string)deleted.Details!["appId"]!);
        Assert.Empty(await ListAsync("{}"));

        // 正式 lock 预测随之不再含该资产
        var preview = await _fixture.InvokeAsync("editor.lock_preview");
        Assert.True(preview.Ok, preview.Message);
        Assert.Empty(preview.Details!["formal"]!["assets"]!.AsArray());

        // 再次删除＝资产未声明（结构化错误码）
        var again = await _fixture.InvokeAsync(
            "editor.asset_delete", """{ "kind": "card", "id": "editor.doomed" }""");
        Assert.False(again.Ok);
        Assert.Equal("AssetNotFound", (string)again.Details!["editorCode"]!);
    }

    // ─────────────────────────────────────────────── 夹具

    /// <summary>三张卡（两张带 <c>fire</c> 标签，其中一张提交两次 ⇒ 版本 1.0.1）。</summary>
    private async Task ArrangeThreeAssetsAsync()
    {
        _fixture.Service.Sandbox.EnsureScaffold();
        Assert.True((await _fixture.InvokeAsync("editor.session_begin", """{ "mode": "edit" }""")).Ok);

        await CreateAndCommitAsync("alpha.one", "alpha 一", "fire");
        await CreateAndCommitAsync("alpha.two", "alpha 二", "water");
        await CreateAndCommitAsync("beta.one", "beta 一");

        // alpha.one 追加一次提交 ⇒ 1.0.1（其余保持 1.0.0）
        var again = await _fixture.InvokeAsync(
            "editor.asset_commit", """{ "path": "cards/alpha.one.card.json" }""");
        Assert.True(again.Ok, again.Message);
        Assert.Equal("1.0.1", (string)again.Details!["asset"]!["version"]!);
    }

    private async Task CreateAndCommitAsync(string localId, string name, params string[] tags)
    {
        var created = await _fixture.InvokeAsync(
            "editor.asset_create", JsonSerializer.Serialize(new { kind = "card", id = localId }));
        Assert.True(created.Ok, created.Message);

        var committed = await _fixture.InvokeAsync(
            "editor.asset_commit",
            JsonSerializer.Serialize(new { path = $"cards/{localId}.card.json", content = CardJson(localId, name, tags) }));
        Assert.True(committed.Ok, committed.Message);
    }

    private async Task<int> CountAsync(string argsJson)
        => (await ListAsync(argsJson)).Count;

    private async Task<IReadOnlyList<JsonObject>> ListAsync(string argsJson)
    {
        var result = await _fixture.InvokeAsync("editor.asset_list", argsJson);
        Assert.True(result.Ok, result.Message);
        return result.Details!["assets"]!.AsArray().Select(node => node!.AsObject()).ToArray();
    }

    private static string[] Project(IEnumerable<EditorAssetInfo> assets)
        => assets.Select(ProjectOne).ToArray();

    private static string[] ProjectTool(IReadOnlyList<JsonObject> assets)
        => assets.Select(ProjectToolOne).ToArray();

    /// <summary>进程内 API 的规范化投影（与工具投影逐字段可比）。</summary>
    private static string ProjectOne(EditorAssetInfo asset)
        => string.Join(
            "|",
            AssetKindTokens.Token(asset.Kind),
            asset.PackageId,
            asset.LocalId,
            asset.AppId,
            asset.Version.ToString(),
            asset.AssetGuid.ToString("D"),
            asset.SourcePath,
            asset.SidecarPath,
            asset.IsNew ? "new" : "existing",
            string.Join(",", asset.Tags));

    private static string ProjectToolOne(JsonObject asset)
        => string.Join(
            "|",
            (string)asset["kind"]!,
            (string)asset["packageId"]!,
            (string)asset["id"]!,
            (string)asset["appId"]!,
            (string)asset["version"]!,
            (string)asset["assetGuid"]!,
            (string)asset["sourcePath"]!,
            (string)asset["sidecarPath"]!,
            (bool)asset["isNew"]! ? "new" : "existing",
            string.Join(",", asset["tags"]!.AsArray().Select(node => (string)node!)));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static string CardJson(string localId, string name, params string[] tags)    {
        var tagData = tags.Length == 0
            ? """{ "component": "tagData", "rarity": "Standard" }"""
            : $$"""{ "component": "tagData", "rarity": "Standard", "tags": [{{string.Join(",", tags.Select(tag => $"\"{tag}\""))}}] }""";

        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{localId}}",
          "name": "{{name}}",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 2 },
            {{tagData}}
          ]
        }
        """;
    }
}
