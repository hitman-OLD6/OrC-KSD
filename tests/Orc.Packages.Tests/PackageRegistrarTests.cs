using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Packages;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S3 验收：注册面门禁按「诊断分级」两表执行——包级只拒结构性非法（sidecar 不可解析／缺必填、
/// 包内同 GUID 重复）；资产级（无 sidecar／id 不一致／源文件缺失／单文件解析失败／GUID 不一致）只报告、
/// 不阻断包。并验证「无 sidecar 的卡走无元数据」与历史版本目录自动扫描。
/// </summary>
public sealed class PackageRegistrarTests : IDisposable
{
    private const string PackId = "sample.registry";
    private static readonly Guid PackGuid = Guid.Parse("b0070000-0000-4000-8000-0000000000c1");

    private readonly string _base = Path.Combine(Path.GetTempPath(), "orc-registrar-" + Guid.NewGuid().ToString("N"));
    private readonly AssetRegistry _registry = new();

    public PackageRegistrarTests()
    {
        Directory.CreateDirectory(_base);
        Orc.Plugins.BuiltIn.BuiltInPlugin.RegisterProcessDefaults();
        Orc.Foundation.Actions.FoundationActions.Register();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PackRoot => Path.Combine(_base, "packs", PackId);

    [Fact]
    public void 注册_sidecar声明正常_资产与DTO入库()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(AssetKind.Card, asset.Kind);
        Assert.Equal("pack/sample.registry/rifle", asset.AppId);
        Assert.Equal(AssetMetadataSource.Sidecar, asset.MetadataSource);
        Assert.NotNull(asset.Card);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Content.Definitions, entry => entry.Id == "pack/sample.registry/rifle");
        Assert.Same(asset, _registry.Find(asset.AssetGuid));
    }

    [Fact]
    public void 注册_无sidecar的卡_合成元数据且不阻断包()
    {
        WriteManifest();
        WriteCard("cards", "rifle");

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(AssetMetadataSource.Synthesized, asset.MetadataSource);
        Assert.Equal(new AssetVersion(1, 0, 0), asset.Version);
        Assert.Equal(
            AssetIdentity.DeriveAssetGuid(PackGuid, AssetKind.Card, "rifle", new AssetVersion(1, 0, 0)),
            asset.AssetGuid);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AssetDiagnosticCode.SidecarMissing, diagnostic.Code);
    }

    [Fact]
    public void 注册_sidecar与源文件id不一致_该资产非法但不阻断包()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "other", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Empty(result.Assets);
        Assert.Empty(result.Content.Definitions);
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.IdMismatch);
    }

    [Fact]
    public void 注册_孤儿meta_不注册并进报告()
    {
        WriteManifest("effects/missing.prefab.json.meta.json");
        WriteSidecar("effects/missing.prefab.json", Meta(AssetKind.Effect, "missing", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest("effects/missing.prefab.json.meta.json"), _registry);

        Assert.Empty(result.Assets);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AssetDiagnosticCode.SourceMissing, diagnostic.Code);
        Assert.Equal("pack/sample.registry/missing", diagnostic.AppId);
    }

    [Fact]
    public void 注册_单文件解析失败_资产级不阻断包()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.0.0"));
        File.WriteAllText(Path.Combine(PackRoot, "cards", "broken.card.json"), "{ 这不是合法 JSON");

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Single(result.Assets);
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.FailedFile);
    }

    // ── 双结算护栏：同一张卡的「显式声明」与「文本模板」不得叠加成两个效果 ──

    [Fact]
    public void 注册_卡已显式声明效果_文本模板不再叠加()
    {
        WriteManifest();
        WriteCardJson("cards/owned.card.json", CardWithExplicitEffect("owned", "hand.effect"));
        WritePrefab("effects/hand.effect.prefab.json", "hand.effect");
        File.WriteAllText(Path.Combine(PackRoot, "texts.json"), """
            { "owned": "部署：抽 1 张牌。" }
            """);

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        // 声明只有一条（卡数据体给出的手写预制体）：文本模板效果不再生成。
        // 叠加会让同一张卡同时持两个效果实例，一次 card.played 把同一段效果结算两次。
        var declaration = Assert.Single(result.Content.Declarations);
        Assert.Equal("pack/sample.registry/owned", declaration.CardId);
        Assert.Equal("hand.effect", Assert.Single(declaration.PrefabIds));
        Assert.Empty(result.Content.InlinePrefabs);
    }

    [Fact]
    public void 注册_卡无显式效果_文本模板照常生成一份()
    {
        WriteManifest();
        WriteCardJson("cards/plain.card.json", CardWithExplicitEffect("plain", prefabId: null));
        File.WriteAllText(Path.Combine(PackRoot, "texts.json"), """
            { "plain": "部署：抽 1 张牌。" }
            """);

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var declaration = Assert.Single(result.Content.Declarations);
        Assert.Equal("pack/sample.registry/plain", declaration.CardId);
        Assert.Equal("pack.sample.registry.plain.effect", Assert.Single(declaration.PrefabIds));
        Assert.Single(result.Content.InlinePrefabs);
    }

    // ── R5/D40：注册期宽松解析（字段级问题不隔离，JSON 语法错误仍不注册） ──

    [Fact]
    public void 注册_缺name的卡_正常注册且携带name缺失问题()
    {
        WriteManifest();
        WriteCardJson("cards/warn.card.json", Card(id: "warn", includeName: false));
        WriteSidecar("cards/warn.card.json", Meta(AssetKind.Card, "warn", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.NotNull(asset.Card); // 警报档：按类型默认值补全后照常生成
        Assert.Equal(string.Empty, asset.Card!.Name);

        var issue = Assert.Single(asset.LoadIssues, item => item.Path == "name");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal(CardIssueSeverity.Warning, issue.Severity);
        Assert.Contains(result.Content.Definitions, entry => entry.Id == "pack/sample.registry/warn");
    }

    [Fact]
    public void 注册_缺factionCost的卡_正常注册且携带字段问题但不产出DTO()
    {
        // D40 非法项：不再隔离（报告/卡位可见），但卡片本身不生成。
        WriteManifest();
        WriteCardJson("cards/broken.card.json", Card(id: "broken", includeFactionCost: false));
        WriteSidecar("cards/broken.card.json", Meta(AssetKind.Card, "broken", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Null(asset.Card);
        var issue = Assert.Single(asset.LoadIssues, item => item.Path == "components.factionCost");
        Assert.Equal(CardIssueReason.Missing, issue.Reason);
        Assert.Equal(CardIssueSeverity.Invalid, issue.Severity);
        Assert.Empty(result.Content.Definitions);
        Assert.Empty(result.Diagnostics); // 非法卡不再是「单文件解析失败」
    }

    [Fact]
    public void 注册_缺id的卡_按sidecar的唯一声明补位身份()
    {
        WriteManifest();
        WriteCardJson("cards/no-id.card.json", Card(id: "no-id", includeId: false));
        WriteSidecar("cards/no-id.card.json", Meta(AssetKind.Card, "no-id", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("pack/sample.registry/no-id", asset.AppId);
        Assert.Null(asset.Card); // id 缺失＝D40 非法项
        Assert.Contains(asset.LoadIssues, item => item.Path == "id" && item.Reason == CardIssueReason.Missing);
    }

    [Fact]
    public void 注册_JSON语法错误的文件_仍不注册()
    {
        WriteManifest();
        WriteCardJson("cards/broken.card.json", "{ 这不是合法 JSON");
        WriteSidecar("cards/broken.card.json", Meta(AssetKind.Card, "broken", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Empty(result.Assets); // 不可解析＝真无法处理（保持既有资产级失败）
        Assert.Equal(0, _registry.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.FailedFile);
    }

    [Fact]
    public async Task 端到端_注册期字段问题进入加载报告_三档均可达()
    {
        WriteManifest();
        WriteCardJson("cards/ok.card.json", Card(
            id: "ok", description: "合法卡", image: "cards/ok.png"));
        WriteSidecar("cards/ok.card.json", Meta(AssetKind.Card, "ok", "1.0.0"));
        WriteCardJson("cards/warn.card.json", Card(id: "warn", includeName: false, description: "警报卡"));
        WriteSidecar("cards/warn.card.json", Meta(AssetKind.Card, "warn", "1.0.0"));
        WriteCardJson("cards/broken.card.json", Card(id: "broken", includeFactionCost: false, description: "非法卡"));
        WriteSidecar("cards/broken.card.json", Meta(AssetKind.Card, "broken", "1.0.0"));
        File.WriteAllBytes(Path.Combine(PackRoot, "cards", "ok.png"), [1, 2, 3]);

        var registration = PackageRegistrar.Register(PackRoot, Manifest(), _registry);
        Assert.Equal(3, registration.Assets.Count); // 字段级问题不再导致资产缺席

        var bridge = new AssetIdBridge();
        var cards = registration.Assets.Where(asset => asset.Kind == AssetKind.Card).ToArray();
        foreach (var card in cards)
        {
            bridge.Bind(card.Domain, AssetKind.Card, card.AppId, card.AssetGuid);
        }

        var loader = new CardLoaderTrigger(_registry, bridge, new CardLibrary(new LogicEngine()));
        var items = cards.Select((card, index) => new CardLoadItem("p1", index, card.AppId)).ToArray();

        var loaded = await loader.LoadBatchAsync(items);

        var byAppId = loaded.Report.Items.ToDictionary(
            item => item.AppId!, item => item, StringComparer.Ordinal);
        Assert.Equal(CardLoadTier.Valid, byAppId["pack/sample.registry/ok"].Tier);
        Assert.Equal(CardLoadTier.Warning, byAppId["pack/sample.registry/warn"].Tier);
        Assert.Equal(CardLoadTier.Invalid, byAppId["pack/sample.registry/broken"].Tier);

        // D40 档位可达：非法卡带字段问题、可玩卡照常产出。
        Assert.Contains(
            byAppId["pack/sample.registry/broken"].Fields,
            field => field.Path == "components.factionCost" && field.Reason == CardIssueReason.Missing);
        Assert.Contains(
            byAppId["pack/sample.registry/warn"].Fields,
            field => field.Path == "name" && field.Reason == CardIssueReason.Missing);
        Assert.Equal(2, loaded.Cards.Count);

        // 卡位不再只显示 appId 未解析：非法卡槽位有 GUID ＋ 具体字段路径。
        var brokenSlot = loaded.Report.Slots.Single(slot => slot.AppId == "pack/sample.registry/broken");
        Assert.Equal(byAppId["pack/sample.registry/broken"].AssetGuid, brokenSlot.AssetGuid);
        Assert.Contains("components.factionCost", brokenSlot.FieldPaths);
    }

    [Fact]
    public async Task 端到端_源文件缺失时_注册期字段问题仍进入报告()
    {
        // 证明注册期问题清单是**装载期可用事实**（不只是随资产带一遍）：原文缺失时由它充当字段问题来源。
        WriteManifest();
        WriteCardJson("cards/broken.card.json", Card(id: "broken", includeFactionCost: false));
        WriteSidecar("cards/broken.card.json", Meta(AssetKind.Card, "broken", "1.0.0"));

        var registration = PackageRegistrar.Register(PackRoot, Manifest(), _registry);
        var asset = Assert.Single(registration.Assets);

        File.Delete(Path.Combine(PackRoot, "cards", "broken.card.json")); // 装载期已读不到原文

        var bridge = new AssetIdBridge();
        bridge.Bind(asset.Domain, AssetKind.Card, asset.AppId, asset.AssetGuid);
        var loader = new CardLoaderTrigger(_registry, bridge, new CardLibrary(new LogicEngine()));

        var loaded = await loader.LoadBatchAsync(new[] { new CardLoadItem("p1", 0, asset.AppId) });

        var item = Assert.Single(loaded.Report.Items);
        Assert.Equal(CardLoadTier.Invalid, item.Tier);
        Assert.Contains(item.Fields, field => field.Path == "source" && field.Reason == CardIssueReason.SourceMissing);
        Assert.Contains(
            item.Fields,
            field => field.Path == "components.factionCost" && field.Reason == CardIssueReason.Missing);
    }

    [Fact]
    public void 注册_sidecar不可解析_整包结构性拒绝()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        File.WriteAllText(
            Path.Combine(PackRoot, "cards", "rifle.card.json.meta.json"), "{ \"schemaVersion\": 1, ");

        Assert.Throws<InvalidDataException>(() => PackageRegistrar.Register(PackRoot, Manifest(), _registry));
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void 注册_包内同GUID重复声明_整包结构性拒绝()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteCard("cards", "smg");
        var duplicate = Meta(AssetKind.Card, "rifle", "1.0.0");
        WriteSidecar("cards/rifle.card.json", duplicate);
        WriteSidecar("cards/smg.card.json", duplicate with { Id = "smg" });

        Assert.Throws<InvalidDataException>(() => PackageRegistrar.Register(PackRoot, Manifest(), _registry));
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void 注册_GUID与身份派生不一致_资产级非法()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json",
            Meta(AssetKind.Card, "rifle", "1.0.0") with { Guid = Guid.NewGuid() });

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Empty(result.Assets);
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.GuidMismatch);
    }

    [Fact]
    public void 注册_版本号非法_该资产非法但不阻断包()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json",
            Meta(AssetKind.Card, "rifle", "1.0.0") with { InvalidVersion = "1.2.3.4.5.6.7.8.9" });

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Empty(result.Assets); // 不生成
        Assert.Contains(result.Diagnostics, d => d.Code == AssetDiagnosticCode.VersionInvalid);
    }

    [Fact]
    public void 注册_历史版本目录被自动扫描_同资产多版本并存()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.0.0"));
        WriteCard("versions/1.1.0/cards", "rifle");
        WriteSidecar("versions/1.1.0/cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.1.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Equal(2, result.Assets.Count);
        Assert.Single(result.Content.Definitions); // 历史版本只注册，不进当前版 DTO 内容
        var versions = _registry.VersionsOf(PackId, AssetKind.Card, "rifle");
        Assert.Equal(2, versions.Count);
        Assert.Equal(AssetVersion.Parse("1.0.0"), versions[0].Version);
        Assert.Equal(AssetVersion.Parse("1.1.0"), versions[1].Version);
        Assert.Equal(versions[0].AppId, versions[1].AppId); // 同一应用层 ID 多版本（非歧义）
    }

    [Fact]
    public void 包扫描器_只做目录与sidecar定位()
    {
        WriteManifest();
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.0.0"));
        Directory.CreateDirectory(Path.Combine(PackRoot, "effects"));

        var scan = PackageScanner.Scan(PackRoot, Manifest());

        var cardFile = Assert.Single(scan.CardFiles);
        Assert.Equal("cards/rifle.card.json", cardFile.RelativePath);
        Assert.True(cardFile.HasSidecar);
        Assert.Single(scan.CardDirectories);
        Assert.NotNull(scan.EffectsDirectory);
        Assert.Null(scan.TextsFile);
    }

    [Fact]
    public void 旧装载器已删除_包扫描归PackageScanner()
    {
        // 单轨：CardPackLoader（含其 Load）已删除，包发现/枚举职责只由 PackageScanner 承担。
        Assert.Null(typeof(PackageRegistrar).Assembly.GetType("Orc.Packages.CardPackLoader", throwOnError: false));

        WriteManifest();
        WriteCard("cards", "rifle");

        var scan = PackageScanner.Scan(PackRoot, Manifest());
        Assert.Equal(Path.GetFullPath(PackRoot), scan.RootDirectory);
        Assert.Single(scan.CardFiles);
    }

    [Fact]
    public void 注册_未列入manifest的effects_sidecar仍可被发现()
    {
        WriteManifest();
        WritePrefab("effects/x.prefab.json", "x");
        WriteSidecar("effects/x.prefab.json", Meta(AssetKind.Effect, "x", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Equal(AssetKind.Effect, asset.Kind);
        Assert.Equal("pack/sample.registry/x", asset.AppId);
        Assert.Same(asset, _registry.Find(asset.AssetGuid));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void 注册_未列入manifest且源文件缺失_报SourceMissing()
    {
        WriteManifest();
        WriteSidecar("effects/gone.prefab.json", Meta(AssetKind.Effect, "gone", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        Assert.Empty(result.Assets);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AssetDiagnosticCode.SourceMissing, diagnostic.Code);
        Assert.Equal("pack/sample.registry/gone", diagnostic.AppId);
    }

    [Fact]
    public void 注册_effects递归层的sidecar同样被发现()
    {
        WriteManifest();
        WritePrefab("effects/sub/y.prefab.json", "y");
        WriteSidecar("effects/sub/y.prefab.json", Meta(AssetKind.Effect, "y", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("pack/sample.registry/y", asset.AppId);
        Assert.Equal("effects/sub/y.prefab.json", asset.SourcePath is null ? null : Path.GetRelativePath(PackRoot, asset.SourcePath).Replace('\\', '/'));
    }

    [Fact]
    public void 注册_manifest声明但文件缺失_仍整包结构性拒绝()
    {
        const string declared = "effects/missing.prefab.json.meta.json";
        WriteManifest(declared);

        Assert.Throws<InvalidDataException>(
            () => PackageRegistrar.Register(PackRoot, Manifest(declared), _registry));
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public void 注册_并集的sidecar仍受source与packageId一致性约束()
    {
        WriteManifest();
        WritePrefab("effects/x.prefab.json", "x");
        var sidecarPath = Path.Combine(PackRoot, "effects", "x.prefab.json") + AssetSidecarJson.SidecarExtension;
        AssetSidecarJson.WriteFile(
            sidecarPath,
            new AssetSidecar(
                AssetSidecarJson.CurrentSchemaVersion, PackId, "effects/other.prefab.json",
                [Meta(AssetKind.Effect, "x", "1.0.0")]));

        Assert.Throws<InvalidDataException>(() => PackageRegistrar.Register(PackRoot, Manifest(), _registry));

        AssetSidecarJson.WriteFile(
            sidecarPath,
            new AssetSidecar(
                AssetSidecarJson.CurrentSchemaVersion, "other.package", "effects/x.prefab.json",
                [Meta(AssetKind.Effect, "x", "1.0.0")]));
        Assert.Throws<InvalidDataException>(() => PackageRegistrar.Register(PackRoot, Manifest(), _registry));
    }

    [Fact]
    public void 包扫描器_清单与发现面取并集且字段语义不合并()
    {
        WriteManifest("cards/rifle.card.json.meta.json");
        WriteCard("cards", "rifle");
        WriteSidecar("cards/rifle.card.json", Meta(AssetKind.Card, "rifle", "1.0.0"));
        WritePrefab("effects/extra.prefab.json", "extra");
        WriteSidecar("effects/extra.prefab.json", Meta(AssetKind.Effect, "extra", "1.0.0"));
        WriteSidecar("effects/deep/nested.prefab.json", Meta(AssetKind.Effect, "nested", "1.0.0"));

        // 插件清单镜像不是资产 sidecar：既不在发现面，也不该被读入。
        AssetSidecarJson.WriteFile(
            Path.Combine(PackRoot, PluginAssetRegistrar.ManifestFileName),
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, PackId, "T.Assembly", []));

        var scan = PackageScanner.Scan(PackRoot, Manifest("cards/rifle.card.json.meta.json"));

        Assert.Equal(["cards/rifle.card.json.meta.json"], scan.ManifestSidecars);
        Assert.Equal(
            [
                "cards/rifle.card.json.meta.json",
                "effects/deep/nested.prefab.json.meta.json",
                "effects/extra.prefab.json.meta.json",
            ],
            scan.SidecarFiles);
    }

    [Fact]
    public void 注册_非约定目录的sidecar不产生资产()
    {
        WriteManifest();
        WritePrefab("units/x.prefab.json", "x");
        WriteSidecar("units/x.prefab.json", Meta(AssetKind.Effect, "x", "1.0.0"));

        var result = PackageRegistrar.Register(PackRoot, Manifest(), _registry);

        // 指纹＝包内容（含该 sidecar），注册＝约定目录内的资产（I14／B-7）。
        Assert.Empty(result.Assets);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void 样例包_注册面零结构性错误且加载计划无孤儿内含()
    {
        var samples = Path.Combine(FindRepoRoot(), "samples");
        var packages = Directory.EnumerateDirectories(samples)
            .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(packages);

        foreach (var packageRoot in packages)
        {
            var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(packageRoot, "manifest.json")));
            if (manifest.Kind != PackageKind.CardPack)
            {
                continue;
            }

            var registry = new AssetRegistry();
            var registration = PackageRegistrar.Register(packageRoot, manifest, registry);

            Assert.DoesNotContain(registration.Diagnostics, diagnostic =>
                diagnostic.Code is AssetDiagnosticCode.SourceMissing
                    or AssetDiagnosticCode.IdMismatch
                    or AssetDiagnosticCode.GuidMismatch
                    or AssetDiagnosticCode.VersionInvalid);

            if (AssetLockJson.ReadPackage(packageRoot, AssetDomain.Formal) is not { } formal)
            {
                continue;
            }

            var plan = LoadListPlanner.Plan(registry, [formal]);
            Assert.True(plan.IsValid);
            Assert.DoesNotContain(plan.Issues, issue => issue.Code == AssetLoadIssueCode.OrphanEmbedded);
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

    // ─────────────────────────────────────────────────────────── 夹具

    private PackageManifest Manifest(params string[] metadata) => new(
        PackageKind.CardPack,
        PackId,
        PackGuid,
        PackageVersion.Parse("1.0.0"),
        Array.Empty<PackageDependency>(),
        Array.Empty<PackageAsset>(),
        Name: "注册面示例",
        Metadata: metadata);

    private void WriteManifest(params string[] metadata)
    {
        Directory.CreateDirectory(PackRoot);
        File.WriteAllText(Path.Combine(PackRoot, PackageDiscovery.ManifestFileName), Manifest(metadata).ToJson());
    }

    private void WriteCard(string relativeDirectory, string id)
    {
        var directory = Path.Combine(PackRoot, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".card.json"), Card(id));
    }

    /// <summary>写一份卡数据体（按开关裁剪字段，构造 D40 的各档输入）。</summary>
    private void WriteCardJson(string relativeSource, string json)
    {
        var path = Path.Combine(PackRoot, relativeSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static string Card(
        string id,
        bool includeId = true,
        bool includeName = true,
        bool includeFactionCost = true,
        string? description = null,
        string? image = null)
    {
        var lines = new List<string> { "{", " \"schemaVersion\": 1," };
        if (includeId)
        {
            lines.Add($" \"id\": \"{id}\",");
        }

        if (includeName)
        {
            lines.Add($" \"name\": \"示例卡 {id}\",");
        }

        if (description is not null)
        {
            lines.Add($" \"description\": \"{description}\",");
        }

        if (image is not null)
        {
            lines.Add($" \"image\": \"{image}\",");
        }

        var components = new List<string>
        {
            "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }",
            "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 3 }",
            "{ \"component\": \"tagData\", \"rarity\": \"standard\" }",
        };
        if (includeFactionCost)
        {
            components.Insert(1, "{ \"component\": \"factionCost\", \"faction\": \"germany\", \"kredits\": 1 }");
        }

        lines.Add(" \"components\": [");
        lines.Add("  " + string.Join(",\n  ", components));
        lines.Add(" ]");
        lines.Add("}");
        return string.Join("\n", lines) + "\n";
    }

    private static string CardWithExplicitEffect(string id, string? prefabId)
    {
        var components = new List<string>
        {
            "{ \"component\": \"typeCategory\", \"type\": [\"infantry\"] }",
            "{ \"component\": \"factionCost\", \"faction\": \"germany\", \"kredits\": 1 }",
            "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 3 }",
            "{ \"component\": \"tagData\", \"rarity\": \"standard\" }",
        };
        if (prefabId is not null)
        {
            components.Add($"{{ \"component\": \"effects\", \"prefabs\": [\"{prefabId}\"] }}");
        }

        return string.Join("\n", new[]
        {
            "{",
            " \"schemaVersion\": 1,",
            $" \"id\": \"{id}\",",
            $" \"name\": \"示例卡 {id}\",",
            " \"components\": [",
            "  " + string.Join(",\n  ", components),
            " ]",
            "}",
        }) + "\n";
    }

    /// <summary>写一份最小可解析的效果预制体（宿主单条目）。</summary>
    private void WritePrefab(string relativeSource, string id)
    {
        var path = Path.Combine(PackRoot, relativeSource);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 2,
              "root": {
                "id": "{{id}}",
                "version": 1,
                "mainTrigger": {
                  "id": "t.main",
                  "stableKey": "{{id}}",
                  "kind": "passive",
                  "hooks": [],
                  "events": [],
                  "version": 1
                },
                "otherTriggers": [],
                "wrappers": [],
                "injects": []
              }
            }
            """);
    }

    private void WriteSidecar(string relativeSource, params AssetMetadata[] assets)
    {
        var path = Path.Combine(PackRoot, relativeSource + AssetSidecarJson.SidecarExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssetSidecarJson.WriteFile(
            path,
            new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, PackId, relativeSource.Replace('\\', '/'), assets));
    }

    private static AssetMetadata Meta(AssetKind kind, string id, string version)
    {
        var assetVersion = AssetVersion.Parse(version);
        return new AssetMetadata(
            AssetIdentity.DeriveAssetGuid(PackGuid, kind, id, assetVersion),
            kind,
            id,
            assetVersion,
            Anchor: null,
            Loader: null,
            DependsOn: [],
            ConflictsWith: []);
    }
}
