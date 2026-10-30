using Orc.Core;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.Packages.Loading;
using Orc.TestSupport;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S11 验收（R7／R11／D15／D36-D39／D51-D54）：后端编辑器的卡组能力。
/// 主入口是「直写 decks/x.deck.json → asset_commit」，create 只负责声明、且**不补骨架**。
/// </summary>
public sealed class DeckEditorTests : IDisposable
{
    private const string DeckPath = "decks/starter.deck.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "orc-deckedit-" + Guid.NewGuid().ToString("N"));
    private readonly string _packsRoot;
    private readonly string _packageRoot;
    private readonly DeckPackFixture _fixture;
    private readonly MatchCardAssembly _assembly;
    private readonly AssetEditor _editor;

    public DeckEditorTests()
    {
        _packsRoot = Path.Combine(_root, "packs");
        _packageRoot = Path.Combine(_packsRoot, "edit.pack");
        Directory.CreateDirectory(_packsRoot);

        _fixture = new DeckPackFixture(_packageRoot, "edit.pack");
        for (var index = 0; index < 10; index++)
        {
            _fixture.AddCard("c" + index);
        }

        _fixture.Build();

        // 会话卡池＝注册面快照（D36）：编辑器不扫盘，所以夹具必须真把它注册进来。
        _assembly = MatchCardAssembly.Register([_packageRoot], requiredDeckSize: 39);
        _editor = new AssetEditor(
            _assembly.Registry,
            new EditableRootPolicy(_root),
            new AssetEditorOptions { PackageRoots = [_packsRoot] });
    }

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
    public void 新建卡组_源文件不存在时拒绝且给出下一步()
    {
        Begin();

        var exception = Assert.Throws<AssetEditorException>(
            () => _editor.CreateAsset(AssetKind.Deck, "starter"));

        Assert.Equal(AssetEditorErrorCode.InvalidContent, exception.Code);
        Assert.Contains("请先写入 `decks/starter.deck.json`", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("loader")]
    public void 新建卡组_带anchor或loader在入口就被拒(string argument)
    {
        Begin();

        var exception = Assert.Throws<AssetEditorException>(() => _editor.CreateAsset(
            AssetKind.Deck,
            "starter",
            anchor: argument == "anchor" ? "stableKey.host" : null,
            loader: argument == "loader" ? "builtin.card" : null));

        Assert.Equal(AssetEditorErrorCode.InvalidContent, exception.Code);
        Assert.Contains("卡组不接受", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_editor.Session!.TemporaryRoot, DeckPath) + ".meta.json"));
    }

    [Fact]
    public void 新建卡组_文件存在即可声明且不做判定()
    {
        var session = Begin();
        WriteToTemporary(session, DeckPath, DeckJson("starter", DeckPackFixture.Deck39(_fixture.PackageId), "示例"));

        // 故意给一份**必然非法**的内容（空 cards）：create 只声明、不判定（D52）。
        WriteToTemporary(session, DeckPath, DeckJson("starter", Array.Empty<(string, int)>(), "示例"));

        var asset = _editor.CreateAsset(AssetKind.Deck, "starter");

        Assert.Equal(AssetKind.Deck, asset.Kind);
        Assert.Equal("decks/starter.deck.json", asset.SourcePath);
        Assert.Equal("1.0.0", asset.Version.ToString());
        Assert.Equal("pack/edit.pack/starter", asset.AppId);
    }

    [Fact]
    public void 会话开始回显卡组张数口径()
    {
        var session = Begin();

        Assert.Equal(39, session.RequiredDeckSize);
    }

    [Fact]
    public void 直写后提交_自动补声明并派生依赖()
    {
        var session = Begin();
        WriteToTemporary(session, DeckPath, DeckJson("starter", DeckPackFixture.Deck39(_fixture.PackageId), "示例"));

        // sidecar 不存在（作者直写）⇒ commit 自动补声明（D54）。
        var committed = _editor.Commit(DeckPath);

        Assert.NotNull(committed.Asset);
        Assert.Equal(AssetKind.Deck, committed.Asset!.Kind);
        Assert.Equal("1.0.0", committed.Asset.Version.ToString());
        Assert.Null(committed.Violations);

        var sidecarPath = Path.Combine(session.TemporaryRoot, DeckPath + ".meta.json");
        Assert.True(File.Exists(sidecarPath));

        var sidecar = AssetSidecarJson.ReadFile(sidecarPath);
        var metadata = Assert.Single(sidecar.Assets);
        Assert.Equal(10, metadata.DependsOn.Count); // 按卡去重
        Assert.All(metadata.DependsOn, reference =>
        {
            Assert.Equal(AssetKind.Card, reference.Kind);
            Assert.StartsWith("pack/edit.pack/", reference.AppId, StringComparison.Ordinal);
            Assert.Equal("*.*.*", reference.VersionConstraint);
        });
        Assert.Null(metadata.Anchor);
        Assert.Null(metadata.Loader);
        Assert.Null(metadata.ServiceKind);
    }

    [Fact]
    public void 提交非法卡组_拒绝写入且返回全部违规()
    {
        var session = Begin();

        // 内容通道：后端先落盘，再判定；被拒后文件必须恢复原样（本例原本不存在 → 删除）。
        var illegal = DeckJson("starter", new[] { ("pack/edit.pack/c0", 4) }, "示例");
        var committed = _editor.Commit(DeckPath, illegal);

        Assert.Null(committed.Asset);
        Assert.False(committed.VersionBumped);
        Assert.NotNull(committed.Violations);
        Assert.Contains(committed.Violations!, violation => violation.Kind == "size");

        Assert.False(File.Exists(Path.Combine(session.TemporaryRoot, DeckPath)));
        Assert.False(File.Exists(Path.Combine(session.TemporaryRoot, DeckPath + ".meta.json")));
    }

    [Fact]
    public void 删除被引用的卡后提交卡组_报unknown_card()
    {
        var session = Begin();
        WriteToTemporary(session, DeckPath, DeckJson("starter", DeckPackFixture.Deck39(_fixture.PackageId), "示例"));
        Assert.NotNull(_editor.Commit(DeckPath).Asset);

        // D39：删卡必须同时移出会话卡池。
        _editor.DeleteAsset(AssetKind.Card, "c0");

        var recommitted = _editor.Commit(DeckPath);

        Assert.Null(recommitted.Asset);
        Assert.Contains(
            recommitted.Violations!,
            violation => violation.Kind == "unknown-card" && violation.CardId == "pack/edit.pack/c0");
    }

    [Fact]
    public void 转正后不存在versions下的卡组归档()
    {
        var session = Begin();
        WriteToTemporary(session, DeckPath, DeckJson("starter", DeckPackFixture.Deck39(_fixture.PackageId), "示例"));
        Assert.NotNull(_editor.Commit(DeckPath).Asset);

        // 改一次内容再提交 ⇒ 版本递增（正式版会覆盖旧内容，而不是归档）。
        WriteToTemporary(session, DeckPath, DeckJson("starter", DeckPackFixture.Deck39(_fixture.PackageId), "示例二"));
        Assert.NotNull(_editor.Commit(DeckPath).Asset);

        _editor.EndSession(EditorSessionEndMode.Save);

        var archivedDecks = Directory.Exists(Path.Combine(_packageRoot, "versions"))
            ? Directory.EnumerateDirectories(Path.Combine(_packageRoot, "versions"), "decks", SearchOption.AllDirectories)
                .ToArray()
            : Array.Empty<string>();
        Assert.Empty(archivedDecks);

        // 同一卡组任一时刻只有一个版本：正式 lock 里只有一条 deck 条目。
        var formal = AssetLockJson.ReadFile(Path.Combine(_packageRoot, AssetLockJson.FileNameFor(AssetDomain.Formal)));
        Assert.Single(formal.Assets, entry => entry.Kind == AssetKind.Deck);
    }

    [Fact]
    public void 按种类deck可列举_已声明未提交也能列出()
    {
        var session = Begin();
        WriteToTemporary(session, DeckPath, DeckJson("starter", Array.Empty<(string, int)>(), "示例"));
        _editor.CreateAsset(AssetKind.Deck, "starter");

        var declared = _editor.ListAssets(new EditorAssetQuery { Kind = AssetKind.Deck });

        // 「已声明、未提交」＝sidecar 有条目但注册面没有 Deck DTO（该状态由既有字段天然表达，不另加状态位）。
        var asset = Assert.Single(declared);
        Assert.Equal("decks/starter.deck.json", asset.SourcePath);
        Assert.DoesNotContain(_assembly.Registry.All, registration => registration.Kind == AssetKind.Deck);
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private EditorSessionInfo Begin() => _editor.BeginSession(EditorSessionRequest.Edit(_packageRoot));

    private static void WriteToTemporary(EditorSessionInfo session, string relativePath, string content)
    {
        var path = Path.Combine(session.TemporaryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string DeckJson(string localId, IReadOnlyList<(string Id, int Count)> cards, string name)
    {
        var list = string.Join(
            ", ",
            cards.Select(card => "{ \"id\": \"" + card.Id + "\", \"count\": " + card.Count + " }"));
        return "{ \"schemaVersion\": 1, \"id\": \"" + localId + "\", \"name\": \"" + name + "\", "
               + "\"deckConfig\": { \"mainFaction\": \"germany\" }, \"cards\": [ " + list + " ] }";
    }
}
