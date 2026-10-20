using System.Text.Json;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Loading;
using Orc.Game.Triggers;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S6 卡牌加载器（R5/D33/D34/D36/D41/I5/I10）验收：
/// 触发通道（直调、整批一次、专用视图）、两层注册面（进程级声明白名单／对局级 LowLevel 记账）、
/// 三档产出的装载语义（非法不生成、牌堆非法位留空）、报告冻结 schema（键名／枚举／slots 完整性／batchId）、
/// 报告不落盘，以及装载段把资产域写进卡库条目与实例。
/// </summary>
public class CardLoaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public CardLoaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "orc-s6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论（临时目录由系统回收）。
        }
    }

    // ── 装配夹具 ──

    private static CardDefinition Definition(string name = "甲")
        => new(name, deployCost: 1, operateCost: 1, attack: 2, defense: 3,
            faction: Faction.Germany, rarity: Rarity.Standard);

    private static AssetRegistration RegisterCard(
        AssetRegistry registry,
        string packageId = "demo",
        string localId = "demo.a",
        AssetDomain domain = AssetDomain.Formal,
        string? loader = null,
        CardDefinition? card = null,
        string? sourcePath = null,
        string version = "1.0.0")
    {
        var parsed = AssetVersion.Parse(version);
        var packageGuid = AssetLoadTestKit.PackageGuidOf(packageId);
        var guid = AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, localId, parsed);

        var registration = new AssetRegistration(
            new AssetMetadata(guid, AssetKind.Card, localId, parsed, null, loader, [], []),
            domain,
            packageId,
            AssetAppId.Compose(packageId, localId),
            AssetMetadataSource.Sidecar,
            card ?? Definition(),
            Prefab: null,
            SourcePath: sourcePath)
        {
            PackageGuid = packageGuid,
        };

        registry.Register(registration);
        return registration;
    }

    private static CardLoaderTrigger CreateLoader(
        out AssetRegistry registry,
        out AssetIdBridge bridge,
        out CardLibrary library,
        Action<AssetRegistry>? configure = null)
    {
        registry = new AssetRegistry();
        bridge = new AssetIdBridge();
        library = new CardLibrary(new LogicEngine());
        configure?.Invoke(registry);

        return new CardLoaderTrigger(registry, bridge, library);
    }

    private static void Bind(AssetIdBridge bridge, AssetRegistration registration)
        => bridge.Bind(registration.Domain, AssetKind.Card, registration.AppId, registration.AssetGuid);

    private string WriteCardFile(string json, string name = "demo.a.card.json")
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, json);
        return path;
    }

    // ── 触发通道与产出 ──

    [Fact]
    public async Task Batch_Returns_Report_And_Card_References_Deduped_By_Asset()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var a = RegisterCard(registry, localId: "demo.a");
        var b = RegisterCard(registry, localId: "demo.b", card: Definition("乙"));
        Bind(bridge, a);
        Bind(bridge, b);

        var result = await loader.LoadBatchAsync(new[]
        {
            new CardLoadItem("p1", 0, a.AppId),
            new CardLoadItem("p1", 1, a.AppId),
            new CardLoadItem("p2", 0, b.AppId),
        });

        Assert.Equal(2, result.Report.Items.Count); // 按资产去重
        Assert.Equal(3, result.Report.Slots.Count); // 每卡位一条
        Assert.Equal(2, result.Cards.Count);

        var first = result.Report.Items[0];
        Assert.Equal(a.AssetGuid, first.AssetGuid);
        Assert.Equal(2, first.RefCount);
        Assert.Equal(CardLoadTier.Valid, first.Tier);
        Assert.Equal(AssetKind.Card, first.Kind);
        Assert.Equal(AssetDomain.Formal, first.Domain);
        Assert.Equal($"card：demo.a@{a.PackageGuid.ToString("N")[..8]}#1.0.0", first.AssetName);

        var second = result.Report.Items[1];
        Assert.Equal(b.AssetGuid, second.AssetGuid);
        Assert.Equal(1, second.RefCount);

        Assert.Equal(new CardLoadSummary(Total: 2, Valid: 2, Warning: 0, Invalid: 0, TotalRefs: 3), result.Report.Summary);
        Assert.Equal(1, result.Report.SchemaVersion);
    }

    [Fact]
    public async Task Every_Deck_Slot_Has_Exactly_One_Slot_Entry_Including_Empty_Ones()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var a = RegisterCard(registry);
        Bind(bridge, a);

        var items = new[]
        {
            new CardLoadItem("p1", 0, a.AppId),
            new CardLoadItem("p1", 1, "pack/demo/ghost"), // 未注册
            new CardLoadItem("p1", 2, ""),                // 空白
            new CardLoadItem("p2", 0, a.AppId),
        };

        var result = await loader.LoadBatchAsync(items);

        Assert.Equal(items.Length, result.Report.Slots.Count);
        Assert.Equal(
            items.Select(item => (item.PlayerId, item.Index, item.AppId)),
            result.Report.Slots.Select(slot => (slot.PlayerId, slot.Index, slot.AppId)));

        var ghost = result.Report.Slots[1];
        Assert.Null(ghost.AssetGuid);            // 解析失败＝留空位
        Assert.Equal(CardLoadTier.Invalid, ghost.Tier);
        Assert.Equal(new[] { "appId" }, ghost.FieldPaths);

        var blank = result.Report.Slots[2];
        Assert.Null(blank.AssetGuid);
        Assert.Equal(CardLoadTier.Invalid, blank.Tier);

        var resolved = result.Report.Slots[0];
        Assert.Equal(a.AssetGuid, resolved.AssetGuid);
        Assert.Equal(new[] { 2 }, result.Report.Items.Select(item => item.RefCount).ToArray());
        Assert.Equal(2, result.Report.Summary.TotalRefs); // 只有已解析资产的引用计入
        Assert.Single(result.Cards);
    }

    [Fact]
    public async Task Invalid_Card_Is_Not_Generated_And_Slot_Stays_Empty_But_Batch_Continues()
    {
        // 缺 factionCost ⇒ 非法（D40）；同批另一张卡照常装载（开局继续）。
        var broken = WriteCardFile("""
        {
          "schemaVersion": 1,
          "id": "demo.broken",
          "name": "坏卡",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """);

        var loader = CreateLoader(out var registry, out var bridge, out _);
        var valid = RegisterCard(registry, localId: "demo.a");
        var invalid = RegisterCard(registry, localId: "demo.broken", sourcePath: broken);
        Bind(bridge, valid);
        Bind(bridge, invalid);

        var result = await loader.LoadBatchAsync(new[]
        {
            new CardLoadItem("p1", 0, valid.AppId),
            new CardLoadItem("p1", 1, invalid.AppId),
        });

        Assert.Equal(CardLoadTier.Valid, result.Report.Items[0].Tier);
        Assert.Equal(CardLoadTier.Invalid, result.Report.Items[1].Tier);
        Assert.Contains(
            result.Report.Items[1].Fields,
            field => field.Path == "components.factionCost" && field.Reason == CardIssueReason.Missing);

        // 非法不生成：只有一张卡被产出。
        Assert.Single(result.Cards);
        Assert.Equal(valid.AppId, result.Cards[0].AppId);

        // 牌堆非法位留空（有 GUID 但无卡）。
        var slot = result.Report.Slots[1];
        Assert.Equal(invalid.AssetGuid, slot.AssetGuid);
        Assert.Equal(CardLoadTier.Invalid, slot.Tier);
        Assert.Contains("components.factionCost", slot.FieldPaths);

        Assert.Equal(new CardLoadSummary(2, 1, 0, 1, 2), result.Report.Summary);
    }

    [Fact]
    public async Task Warning_Card_Is_Generated_With_Type_Defaults()
    {
        // 缺 tagData（警报）+ 缺 name（警报）⇒ 照常生成、名称取空串。
        var file = WriteCardFile("""
        {
          "schemaVersion": 1,
          "id": "demo.warn",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 }
          ]
        }
        """);

        var loader = CreateLoader(out var registry, out var bridge, out _);
        var warning = RegisterCard(registry, localId: "demo.warn", sourcePath: file);
        Bind(bridge, warning);

        var result = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, warning.AppId) });

        Assert.Equal(CardLoadTier.Warning, result.Report.Items[0].Tier);
        Assert.Single(result.Cards);
        Assert.Equal("", result.Cards[0].Definition.Name);
        Assert.Equal(Rarity.Standard, result.Cards[0].Definition.Rarity);
        Assert.Contains(result.Report.Items[0].Fields, field => field.Path == "name");
        Assert.Contains(result.Report.Items[0].Fields, field => field.Path == "components.tagData");
    }

    [Fact]
    public async Task Empty_Batch_Produces_Empty_Report()
    {
        var loader = CreateLoader(out _, out _, out _);

        var result = await loader.LoadBatchAsync(Array.Empty<CardLoadItem>());

        Assert.Empty(result.Report.Items);
        Assert.Empty(result.Report.Slots);
        Assert.Empty(result.Cards);
        Assert.Equal(new CardLoadSummary(0, 0, 0, 0, 0), result.Report.Summary);
    }

    // ── 报告 schema（I10 冻结） ──

    [Fact]
    public async Task Report_Json_Matches_Frozen_Schema_Keys_And_Value_Vocabulary()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var valid = RegisterCard(registry, localId: "demo.a");
        Bind(bridge, valid);

        var result = await loader.LoadBatchAsync(new[]
        {
            new CardLoadItem("p1", 0, valid.AppId),
            new CardLoadItem("p1", 1, "pack/demo/ghost"),
        });

        using var document = JsonDocument.Parse(result.Report.ToJson());
        var root = document.RootElement;

        Assert.Equal(
            new[] { "schemaVersion", "batchId", "items", "slots", "summary" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var item = root.GetProperty("items")[0];
        Assert.Equal(
            new[] { "assetGuid", "assetName", "appId", "kind", "domain", "tier", "fields", "refCount" },
            item.EnumerateObject().Select(property => property.Name));
        Assert.Equal("card", item.GetProperty("kind").GetString());
        Assert.Equal("formal", item.GetProperty("domain").GetString());
        Assert.Equal("valid", item.GetProperty("tier").GetString());

        var slot = root.GetProperty("slots")[1];
        Assert.Equal(
            new[] { "playerId", "index", "appId", "assetGuid", "tier", "fieldPaths" },
            slot.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("assetGuid").ValueKind);
        Assert.Equal("invalid", slot.GetProperty("tier").GetString());

        Assert.Equal(
            new[] { "total", "valid", "warning", "invalid", "totalRefs" },
            root.GetProperty("summary").EnumerateObject().Select(property => property.Name));

        // reason 只取枚举 token（此处该批无字段问题；用非法卡的批次另测 reason，见下）。
        Assert.Empty(item.GetProperty("fields").EnumerateArray());

        var withIssues = await loader.LoadBatchAsync(new[]
        {
            new CardLoadItem("p1", 0, "pack/demo/ghost"),
        });
        Assert.Empty(withIssues.Report.Items);
    }

    [Fact]
    public async Task Report_Field_Entry_Uses_Frozen_Keys_And_Reason_Enum()
    {
        var file = WriteCardFile("""
        {
          "schemaVersion": 1,
          "id": "demo.warn",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 }
          ]
        }
        """, "demo.warn.card.json");

        var loader = CreateLoader(out var registry, out var bridge, out _);
        var warning = RegisterCard(registry, localId: "demo.warn", sourcePath: file);
        Bind(bridge, warning);

        var result = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, warning.AppId) });

        using var document = JsonDocument.Parse(result.Report.ToJson());
        var fields = document.RootElement.GetProperty("items")[0].GetProperty("fields");
        Assert.NotEqual(0, fields.GetArrayLength());

        var allowed = Enum.GetValues<CardIssueReason>()
            .Select(CardIssueReasonTokens.Token)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in fields.EnumerateArray())
        {
            Assert.Equal(
                new[] { "path", "expectedType", "defaultValue", "reason" },
                field.EnumerateObject().Select(property => property.Name));
            Assert.Contains(field.GetProperty("reason").GetString()!, allowed);
            Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("path").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("expectedType").GetString()));
        }
    }

    [Fact]
    public async Task BatchId_Is_Monotonic_And_Carries_No_Timestamp()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var card = RegisterCard(registry);
        Bind(bridge, card);

        var first = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) });
        var second = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) });

        Assert.Equal("batch-1", first.Report.BatchId);
        Assert.Equal("batch-2", second.Report.BatchId);
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(), first.Report.BatchId, StringComparison.Ordinal);
        Assert.Matches("^batch-[0-9]+$", first.Report.BatchId);
    }

    // ── 两层注册面 ──

    [Fact]
    public async Task Unregistered_Loader_Reference_Is_Structural_Refusal()
    {
        var libraryEngine = new LogicEngine();
        var registry = new AssetRegistry();
        var bridge = new AssetIdBridge();
        var library = new CardLibrary(libraryEngine);
        var loader = new CardLoaderTrigger(registry, bridge, library);

        var card = RegisterCard(registry, loader: "ghost.loader");
        Bind(bridge, card);

        var refusal = await Assert.ThrowsAsync<CardLoaderRefusedException>(
            () => loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) }));

        Assert.Equal("ghost.loader", refusal.LoaderName);
        Assert.Equal(card.AppId, refusal.AppId);
        Assert.Equal(CardIssueReason.LoaderUnregistered, refusal.Reason);
        Assert.False(library.Contains(card.AppId)); // 结构性拒绝＝整批不装载
    }

    [Fact]
    public async Task Registered_Loader_Reference_Is_Accepted()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        registry.CardLoaders.Register(new CardLoaderDescriptor(CardLoaderTrigger.DefaultLoaderName, "plugin.core", CardLoaderSource.Plugin));

        var card = RegisterCard(registry, loader: CardLoaderTrigger.DefaultLoaderName);
        Bind(bridge, card);

        var result = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) });

        Assert.Single(result.Cards);
    }

    [Fact]
    public async Task Pack_Self_Shipped_Loader_Is_Refused_At_The_Process_Registry()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);

        var rejected = Assert.Throws<InvalidOperationException>(() => registry.CardLoaders.Register(
            new CardLoaderDescriptor("demo.self-loader", "demo", CardLoaderSource.CardPack)));
        Assert.Contains("卡包不得自带加载器", rejected.Message);
        Assert.Empty(registry.CardLoaders.Entries);

        // 卡包自带（同名）加载器无法进入白名单 ⇒ sidecar 引用它时整批结构性拒绝。
        var card = RegisterCard(registry, loader: "demo.self-loader");
        Bind(bridge, card);

        await Assert.ThrowsAsync<CardLoaderRefusedException>(
            () => loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) }));
    }

    [Fact]
    public async Task Duplicate_Loader_Declaration_Is_Rejected()
    {
        var loader = CreateLoader(out var registry, out _, out _);
        registry.CardLoaders.Register(new CardLoaderDescriptor("core.a", "plugin.core", CardLoaderSource.Plugin));

        var duplicate = Assert.Throws<InvalidOperationException>(() => registry.CardLoaders.Register(
            new CardLoaderDescriptor("core.a", "plugin.other", CardLoaderSource.Plugin)));
        Assert.Contains("已由插件", duplicate.Message);

        Assert.True(registry.CardLoaders.Contains("core.a"));
        Assert.Same(loader.LoaderRegistry, registry.CardLoaders);
    }

    [Fact]
    public void Loader_Is_Registered_Into_Match_Registry_As_LowLevel_Audit_Only()
    {
        var loader = CreateLoader(out _, out _, out _);
        var triggers = new TriggerRegistry();

        loader.RegisterIn(triggers);

        var lowLevel = triggers.GetByLayer(TriggerLayer.LowLevel);
        var entry = Assert.Single(lowLevel);
        Assert.Same(loader, entry.Trigger);
        Assert.Empty(triggers.GetByLayer(TriggerLayer.External));

        // 只记账、不挂载：加载器是主动触发器（主动不得声明 hooks ⇒ 结构上不可能被总线挂载），
        // 唯一入口是直调 LoadBatchAsync。
        Assert.Equal(TriggerKind.Active, loader.Kind);
        Assert.Equal(CardLoaderTrigger.DefaultLoaderName, loader.Name);
    }

    [Fact]
    public async Task Loader_Is_Usable_Without_Any_Bus_Mount()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var card = RegisterCard(registry);
        Bind(bridge, card);

        // 无 TriggerRegistry、无总线：直调即用（注册纯记账）。
        var result = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, card.AppId) });

        Assert.Single(result.Cards);
    }

    // ── 装载段写入域（IAssetLoadSink） ──

    [Fact]
    public void Apply_Writes_Domain_Into_Library_Entry_And_Instances()
    {
        var engine = new LogicEngine();
        var registry = new AssetRegistry();
        var bridge = new AssetIdBridge();
        var library = new CardLibrary(engine);
        var loader = new CardLoaderTrigger(registry, bridge, library);

        var test = RegisterCard(registry, localId: "demo.test", domain: AssetDomain.Test);
        var formal = RegisterCard(registry, localId: "demo.formal");

        loader.Apply(new[]
        {
            new LoadListItem(test, Array.Empty<AssetRegistration>()),
            new LoadListItem(formal, Array.Empty<AssetRegistration>()),
        });

        Assert.Equal(AssetDomain.Test, library.DomainOf(test.AppId));
        Assert.Equal(AssetDomain.Formal, library.DomainOf(formal.AppId));
        Assert.True(library.Instantiate(test.AppId).IsTestAsset);
        Assert.False(library.Instantiate(formal.AppId).IsTestAsset);

        // 重复 Apply 幂等（已存在＝替换定义与域）。
        loader.Apply(new[] { new LoadListItem(test, Array.Empty<AssetRegistration>()) });
        Assert.True(library.Instantiate(test.AppId).IsTestAsset);

        loader.Revert(new[] { new LoadListItem(test, Array.Empty<AssetRegistration>()) });
        Assert.False(library.Contains(test.AppId));
        Assert.True(library.Contains(formal.AppId));
    }

    [Fact]
    public async Task Report_Domain_Token_Follows_Asset_Domain()
    {
        var loader = CreateLoader(out var registry, out var bridge, out _);
        var test = RegisterCard(registry, localId: "demo.test", domain: AssetDomain.Test);
        Bind(bridge, test);

        var result = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, test.AppId) });

        using var document = JsonDocument.Parse(result.Report.ToJson());
        Assert.Equal("test", document.RootElement.GetProperty("items")[0].GetProperty("domain").GetString());
        Assert.Equal(AssetDomain.Test, result.Cards[0].Domain);
    }

    // ── 真实样例包（端到端读取原文 + 效果引用解析） ──

    [Fact]
    public async Task Sample_Pack_Cards_Load_Without_Any_Invalid_Item()
    {
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();

        var packRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../samples/card-pack-demo"));
        Assert.True(Directory.Exists(packRoot), $"样例包目录缺失：{packRoot}");

        var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(packRoot, "manifest.json")));
        var registry = new AssetRegistry();
        var registration = PackageRegistrar.Register(packRoot, manifest, registry);

        var bridge = new AssetIdBridge();
        var cards = registration.Assets.Where(asset => asset.Kind == AssetKind.Card).ToArray();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            bridge.Bind(card.Domain, AssetKind.Card, card.AppId, card.AssetGuid);
        }

        var loader = new CardLoaderTrigger(registry, bridge, new CardLibrary(new LogicEngine()));
        var items = cards.Select((card, index) => new CardLoadItem("p1", index, card.AppId)).ToArray();

        var result = await loader.LoadBatchAsync(items);

        Assert.Equal(items.Length, result.Report.Slots.Count);
        Assert.DoesNotContain(result.Report.Items, item => item.Tier == CardLoadTier.Invalid);
        Assert.Equal(items.Length, result.Cards.Count);
        Assert.All(result.Report.Items, item => Assert.Equal(AssetDomain.Formal, item.Domain));
        Assert.All(result.Cards, card => Assert.StartsWith("card：", card.AssetName, StringComparison.Ordinal));
    }

    // ── 静态约束（不落盘 / 专用视图 / 旧报告不动） ──

    [Fact]
    public void CardLoadReport_Is_Not_Persisted()
    {
        var directory = Path.Combine(RepositoryRoot(), "src/Orc.Packages/Loading");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("WriteAllText", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteAllBytes", text, StringComparison.Ordinal);
            Assert.DoesNotContain("StreamWriter", text, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Create", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Batch_View_Is_Dedicated_And_Not_Reused_By_Any_Event()
    {
        var root = RepositoryRoot();
        var allowed = new[]
        {
            "src/Orc.Packages/Loading/CardLoadBatchView.cs",
            "src/Orc.Packages/Loading/CardLoaderTrigger.cs",
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("CardLoadBatchView", StringComparison.Ordinal)
                && !allowed.Contains(relative, StringComparer.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "CardLoadBatchView 只服务卡牌加载批次（命令视图），不得被复用为事件视图：" + string.Join("、", offenders));
    }

    [Fact]
    public void CardDistribution_Validation_Report_Keeps_Binary_Shape()
    {
        var properties = typeof(CardDistributionValidationReport)
            .GetProperties()
            .Where(property => property.DeclaringType == typeof(CardDistributionValidationReport))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "CardDirectories", "CardDirectoryCount", "ErrorCount", "Errors", "HasNoErrors",
                "PrefabLibraryDirectories", "PrefabLibraryDirectoryCount", "SuccessfulCardCount",
                "SuccessfulInlineSnapshotCount", "SuccessfulLibrarySnapshotCount",
                "SuccessfulSnapshotCount", "UnresolvedReferenceCount", "WarningCount", "Warnings",
            },
            properties);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（OrcEngine.slnx）。");
    }
}
