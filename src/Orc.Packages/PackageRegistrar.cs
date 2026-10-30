using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Decks;
using Orc.Game.EffectParsing;
using Orc.Packages.Decks;

namespace Orc.Packages;

/// <summary>
/// 资产级诊断码（**不阻断包**）：与包级结构性拒绝严格分离（见「诊断分级」表）。
/// 结构性拒绝（manifest／sidecar 不可解析、缺必填字段、包内 GUID 重复）一律抛
/// <see cref="InvalidDataException"/>，**不进本枚举**。
/// </summary>
public enum AssetDiagnosticCode
{
    /// <summary>源文件解析失败（该文件资产不生成，包照常注册）。</summary>
    FailedFile = 0,

    /// <summary>sidecar 与源文件 <c>id</c> 不一致（该资产非法，不生成）。</summary>
    IdMismatch = 1,

    /// <summary>源文件在而 sidecar 缺（按「无元数据」走三档判定）。</summary>
    SidecarMissing = 2,

    /// <summary>源文件缺失（孤儿 meta；不注册、进报告）。</summary>
    SourceMissing = 3,

    /// <summary>sidecar 声明的 GUID 与身份派生不一致（该资产非法，不生成）。</summary>
    GuidMismatch = 4,

    /// <summary>版本号非法（该资产非法，不生成；不阻断包）。</summary>
    VersionInvalid = 5,

    /// <summary>
    /// 卡组内容不成立（R5/D41）：**一条诊断装一份卡组的全部违规**——「列全」由
    /// <see cref="AssetDiagnostic.Violations"/> 表达，不按违规条数拆成 N 条诊断。
    /// </summary>
    DeckInvalid = 6,

    /// <summary>
    /// 卡组声明了不允许的 sidecar 字段（D59：<c>anchor</c>／<c>loader</c>／<c>serviceKind</c>）。
    /// 只对 deck 生效——其他种类的既有宽口径不收紧。
    /// </summary>
    FieldNotAllowed = 7,
}

/// <summary>资产级诊断条目。</summary>
/// <param name="Code">诊断码。</param>
/// <param name="PackageId">所属包 id。</param>
/// <param name="AppId">应用层 ID（未知＝null）。</param>
/// <param name="File">相关文件（相对包根；未知＝null）。</param>
/// <param name="Message">人读说明。</param>
public sealed record AssetDiagnostic(
    AssetDiagnosticCode Code,
    string PackageId,
    string? AppId,
    string? File,
    string Message)
{
    /// <summary>
    /// 卡组的**全部**违规（R5/D41/D44）：仅 <see cref="AssetDiagnosticCode.DeckInvalid"/> 非空。
    /// <para>
    /// 卡牌诊断恒为 null——消费方容忍空值即可，不为「有没有违规」引入包装类型。
    /// 客户端按 <c>Kind</c> 分类、按 <c>CardId</c> 点名，**不解析 <see cref="Message"/>**。
    /// </para>
    /// </summary>
    public IReadOnlyList<DeckViolation>? Violations { get; init; }
}

/// <summary>一次包注册的产出：DTO 内容 ＋ 已注册资产 ＋ 资产级诊断。</summary>
/// <param name="Manifest">包清单。</param>
/// <param name="Content">注册面生成的 DTO 内容（供组装链使用）。</param>
/// <param name="Assets">本次注册进注册表的资产版本（注册序）。</param>
/// <param name="Diagnostics">资产级诊断（不阻断包）。</param>
public sealed record PackageRegistrationResult(
    PackageManifest Manifest,
    CardPackContent Content,
    IReadOnlyList<AssetRegistration> Assets,
    IReadOnlyList<AssetDiagnostic> Diagnostics)
{
    /// <summary>
    /// 第一段**不注册**、留给卡组阶段（R3 第二段）处理的卡组声明。
    /// <para>
    /// 卡组必须等全部包的第一段跑完才能判定（跨包引用），所以第一段只解析元数据、不产出 DTO；
    /// 注册与判定统一在 <see cref="DeckRegistrar.Register"/>。这也保证 planner 永远看不到
    /// 「半成品卡组」条目（D60）。
    /// </para>
    /// </summary>
    public IReadOnlyList<DeferredDeckAsset> Decks { get; init; } = Array.Empty<DeferredDeckAsset>();
}

/// <summary>
/// **注册面**（R3/D10）：元数据解析 ＋ 包内全部资产 DTO 生成（不实例化、不进对局）——
/// 注册即把资产版本写进 <see cref="AssetRegistry"/>。
/// <para>
/// 门禁按「诊断分级」两表执行：**包级结构性非法**（manifest／sidecar 不可解析、缺必填字段、
/// 包内同 GUID 重复声明）⇒ 抛 <see cref="InvalidDataException"/>、整包不注册；
/// **资产级问题**（id 不一致／源文件缺失／无 sidecar／单文件解析失败）⇒ 只进
/// <see cref="PackageRegistrationResult.Diagnostics"/>，**不阻断包**（不存在 <c>Warnings≠0 即拒包</c> 路径）。
/// </para>
/// </summary>
public static class PackageRegistrar
{
    /// <summary>注册一个卡包目录。</summary>
    /// <param name="packageRoot">包根目录。</param>
    /// <param name="manifest">已解析清单（须为 <see cref="PackageKind.CardPack"/>）。</param>
    /// <param name="registry">目标注册表（写入资产版本）。</param>
    /// <param name="domain">资产域（默认正式域）。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidOperationException">清单类型不符。</exception>
    /// <exception cref="InvalidDataException">包级结构性非法（整包不注册）。</exception>
    public static PackageRegistrationResult Register(
        string packageRoot,
        PackageManifest manifest,
        AssetRegistry registry,
        AssetDomain domain = AssetDomain.Formal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        if (manifest.Kind != PackageKind.CardPack)
        {
            throw new InvalidOperationException(
                $"注册面只接受 kind=cardpack 的清单（实际 {manifest.Kind}）。");
        }

        var scan = PackageScanner.Scan(packageRoot, manifest);
        var diagnostics = new List<AssetDiagnostic>();
        var sidecars = ReadSidecars(scan);
        RejectDuplicateGuids(manifest, sidecars);

        var definitions = new List<CardDefinitionEntry>();
        var inlinePrefabs = new List<EffectSnapshot>();
        var declarations = new List<(string CardId, IReadOnlyList<string> PrefabIds)>();
        var consumed = new HashSet<Guid>();
        var registered = new List<AssetRegistration>();
        var deferredDecks = new List<DeferredDeckAsset>();

        RegisterCards(scan, manifest, registry, domain, sidecars, diagnostics, consumed,
            registered, definitions, inlinePrefabs, declarations, deferredDecks);

        RegisterDeclaredAssets(
            scan, manifest, registry, domain, sidecars, diagnostics, consumed, registered, deferredDecks);

        var texts = PackageScanner.ReadTexts(scan.RootDirectory);
        AppendTextEffects(manifest, definitions, texts, inlinePrefabs, declarations);

        var content = new CardPackContent(
            manifest,
            scan.RootDirectory,
            definitions,
            inlinePrefabs,
            declarations,
            scan.EffectsDirectory,
            PackageScanner.ReadKeywords(scan.RootDirectory),
            texts,
            scan.ComponentsDirectory);

        return new PackageRegistrationResult(manifest, content, registered, diagnostics)
        {
            Decks = deferredDecks,
        };
    }

    /// <summary>
    /// 读取全部 sidecar（两段式，顺序不可换）：
    /// ① 先按 <see cref="PackageScan.ManifestSidecars"/> 校验「声明即落地」（缺失＝包级结构性非法，B4）；
    /// ② 再把并集（<see cref="PackageScan.SidecarFiles"/> ＋ 卡文件同目录 ＋ 卡组文件同目录）读入并按源文件相对路径索引。
    /// <para>
    /// 并集每一项**一视同仁**：source／packageId 一致性与重复 GUID 校验对全量生效。
    /// 卡组那条腿必须显式加（D56）——卡的 sidecar 靠「同目录」自动进字典，卡组若漏掉就是
    /// 「文件在、sidecar 不在字典、卡组永不注册且无任何报错」的静默坑。
    /// </para>
    /// </summary>
    private static Dictionary<string, AssetSidecar> ReadSidecars(PackageScan scan)
    {
        var sidecars = new Dictionary<string, AssetSidecar>(StringComparer.Ordinal);
        var paths = new List<string>();

        RejectVersionedDeckSidecars(scan);

        foreach (var relative in scan.ManifestSidecars)
        {
            var declared = Path.GetFullPath(Path.Combine(scan.RootDirectory, relative));
            if (!File.Exists(declared))
            {
                throw new InvalidDataException(MigrationGuidance.ForPackage(
                    scan.RootDirectory,
                    $"manifest 元数据列表指向的 sidecar 缺失：{relative}（包级结构性非法）"));
            }
        }

        foreach (var relative in scan.SidecarFiles)
        {
            paths.Add(Path.GetFullPath(Path.Combine(scan.RootDirectory, relative)));
        }

        foreach (var cardFile in scan.CardFiles)
        {
            if (cardFile.SidecarPath is { } sidecarPath)
            {
                paths.Add(sidecarPath);
            }
        }

        foreach (var deckFile in scan.DeckFiles)
        {
            if (deckFile.SidecarPath is { } sidecarPath)
            {
                paths.Add(sidecarPath);
            }
        }

        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            AssetSidecar sidecar;
            try
            {
                sidecar = AssetSidecarJson.ReadFile(path);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(MigrationGuidance.ForPackage(
                    scan.RootDirectory,
                    $"sidecar 不可解析（包级结构性非法）：{Relative(scan.RootDirectory, path)}：{ex.Message}"), ex);
            }

            if (!string.Equals(sidecar.PackageId, scan.Manifest.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"sidecar 的 packageId '{sidecar.PackageId}' 与包 id '{scan.Manifest.Id}' 不符"
                    + $"（包级结构性非法）：{Relative(scan.RootDirectory, path)}。");
            }

            var expectedSource = Relative(scan.RootDirectory, path[..^AssetSidecarJson.SidecarExtension.Length]);
            if (!string.Equals(sidecar.Source.Replace('\\', '/'), expectedSource, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"sidecar 的 source '{sidecar.Source}' 与文件名不符（期望 '{expectedSource}'；包级结构性非法）。");
            }

            sidecars[expectedSource] = sidecar;
        }

        return sidecars;
    }

    /// <summary>
    /// 卡组不保留历史版本（R11/D57）：<c>manifest.metadata</c> 显式声明 <c>versions/</c> 下的卡组 sidecar＝包级结构性非法。
    /// <para>
    /// 手工丢进 <c>versions/*/decks/</c> 且未声明＝**静默忽略**——扫描面根本不看那里，
    /// 因此这里只需要拒绝「作者明确声明了一份不存在的通道」这一种情况。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidDataException">声明了 versions/ 下的卡组 sidecar。</exception>
    private static void RejectVersionedDeckSidecars(PackageScan scan)
    {
        var deckSidecarSuffix = PackageScanner.DeckFilePattern[1..] + AssetSidecarJson.SidecarExtension;

        foreach (var relative in scan.ManifestSidecars)
        {
            var normalized = relative.Replace('\\', '/');
            if (normalized.StartsWith(PackageScanner.VersionsDirectoryName + "/", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith(deckSidecarSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"manifest 元数据声明了 versions/ 下的卡组 sidecar：{normalized}——"
                    + "卡组不保留历史版本，versions/ 下的卡组声明无效，请删除该声明（包级结构性非法）。");
            }
        }
    }

    private static void RejectDuplicateGuids(PackageManifest manifest, Dictionary<string, AssetSidecar> sidecars)
    {
        var seen = new Dictionary<Guid, string>();
        foreach (var (source, sidecar) in sidecars)
        {
            foreach (var asset in sidecar.Assets)
            {
                if (seen.TryGetValue(asset.Guid, out var owner) && !string.Equals(owner, source, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"资产 GUID '{asset.Guid}' 在包 '{manifest.Id}' 内重复声明（{owner} 与 {source}）"
                        + "——包级结构性非法。");
                }

                seen[asset.Guid] = source;
            }
        }
    }

    private static void RegisterCards(
        PackageScan scan,
        PackageManifest manifest,
        AssetRegistry registry,
        AssetDomain domain,
        Dictionary<string, AssetSidecar> sidecars,
        List<AssetDiagnostic> diagnostics,
        HashSet<Guid> consumed,
        List<AssetRegistration> registered,
        List<CardDefinitionEntry> definitions,
        List<EffectSnapshot> inlinePrefabs,
        List<(string CardId, IReadOnlyList<string> PrefabIds)> declarations,
        List<DeferredDeckAsset> deferredDecks)
    {
        var prefix = $"pack/{manifest.Id}/";

        foreach (var cardFile in scan.CardFiles)
        {
            // 宽松解析（R5/D40）：字段级问题＝字段问题清单（随资产注册），只有「真无法处理」才判单文件失败。
            var load = CardDataLoader.LoadFileLenient(cardFile.AbsolutePath);
            if (load.Failure is not null)
            {
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.FailedFile,
                    manifest.Id,
                    AppId: null,
                    cardFile.RelativePath,
                    load.Failure));
                continue;
            }

            sidecars.TryGetValue(cardFile.RelativePath, out var sidecar);
            var declaredCards = sidecar is null
                ? new List<AssetMetadata>()
                : sidecar.Assets.Where(asset => asset.Kind == AssetKind.Card).ToList();
            var matchedCards = new HashSet<string>(StringComparer.Ordinal);

            // 数据体缺 id（D40 非法项）：身份退到 sidecar 的唯一声明（无歧义才补位；否则文件无法注册）。
            var cardId = load.Id;
            if (string.IsNullOrWhiteSpace(cardId) && declaredCards.Count == 1)
            {
                cardId = declaredCards[0].Id;
            }

            if (string.IsNullOrWhiteSpace(cardId))
            {
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.FailedFile,
                    manifest.Id,
                    AppId: null,
                    cardFile.RelativePath,
                    "卡数据体缺少 id，且 sidecar 无法唯一确定身份（该文件不注册）。"));
                continue;
            }

            RegisterOneCard(cardId);

            if (sidecar is null)
            {
                continue;
            }

            foreach (var declared in declaredCards.Where(asset => !matchedCards.Contains(asset.Id)))
            {
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.IdMismatch,
                    manifest.Id,
                    prefix + declared.Id,
                    cardFile.RelativePath,
                    $"sidecar 声明了源文件中不存在的卡 id '{declared.Id}'（该资产非法，不生成）。"));
                consumed.Add(declared.Guid);
            }

            // 内含资产（effect／trigger／handler）：随宿主文件注册，DTO 取宿主内联预制体。
            foreach (var declared in sidecar.Assets.Where(asset => asset.Kind != AssetKind.Card))
            {
                var appId = prefix + declared.Id;
                if (!ValidateGuid(manifest, declared, appId, cardFile.RelativePath, diagnostics))
                {
                    consumed.Add(declared.Guid);
                    continue;
                }

                if (declared.Kind == AssetKind.Deck)
                {
                    // 卡组一律延后到第二段（R3/D9）——这里只登记声明，不产出 DTO。
                    consumed.Add(declared.Guid);
                    deferredDecks.Add(Defer(manifest, declared, appId, cardFile.AbsolutePath, cardFile.RelativePath));
                    continue;
                }

                var prefab = load.Prefabs.FirstOrDefault(snapshot =>
                                 string.Equals(snapshot.Root.Id, declared.Id, StringComparison.Ordinal))
                             ?? (load.Prefabs.Count == 1 ? load.Prefabs[0] : null);

                consumed.Add(declared.Guid);
                var registration = new AssetRegistration(
                    declared, domain, manifest.Id, appId, AssetMetadataSource.Sidecar,
                    Card: null, prefab, cardFile.AbsolutePath)
                {
                    PackageGuid = manifest.Guid,
                };
                Register(registry, registration, appId, cardFile.RelativePath, diagnostics, registered);
            }

            // 单张卡的注册（源文件一文件一卡）：字段级问题随资产携带，非法卡只注册身份、不产出 DTO。
            void RegisterOneCard(string id)
            {
                var appId = prefix + id;
                AssetMetadata? metadata;
                var source = AssetMetadataSource.Sidecar;

                if (sidecar is null)
                {
                    // 无 sidecar：按「无元数据」——合成默认元数据（版本 1.0.0），三档判定归加载器。
                    metadata = Synthesize(manifest, AssetKind.Card, id);
                    source = AssetMetadataSource.Synthesized;
                    diagnostics.Add(new AssetDiagnostic(
                        AssetDiagnosticCode.SidecarMissing,
                        manifest.Id,
                        appId,
                        cardFile.RelativePath,
                        MigrationGuidance.ForPackage(
                            scan.RootDirectory,
                            $"卡 '{id}' 缺少 sidecar（按无元数据判定）")));
                }
                else
                {
                    metadata = declaredCards.FirstOrDefault(asset =>
                        string.Equals(asset.Id, id, StringComparison.Ordinal));
                    if (metadata is null)
                    {
                        diagnostics.Add(new AssetDiagnostic(
                            AssetDiagnosticCode.IdMismatch,
                            manifest.Id,
                            appId,
                            cardFile.RelativePath,
                            $"sidecar 未声明源文件中的卡 id '{id}'（该资产非法，不生成）。"));
                        return;
                    }

                    matchedCards.Add(id);
                }

                if (!ValidateGuid(manifest, metadata, appId, cardFile.RelativePath, diagnostics))
                {
                    return;
                }

                consumed.Add(metadata.Guid);
                var registration = new AssetRegistration(
                    metadata, domain, manifest.Id, appId, source, load.Definition, null, cardFile.AbsolutePath)
                {
                    PackageGuid = manifest.Guid,
                    LoadIssues = load.Issues,
                };
                Register(registry, registration, appId, cardFile.RelativePath, diagnostics, registered);

                if (cardFile.IsHistorical)
                {
                    return; // 历史版本只注册（供 lock 定版），不进当前版 DTO 内容
                }

                if (load.Definition is null)
                {
                    return; // D40 非法项：不生成卡 DTO（字段问题已随 LoadIssues 注册）
                }

                definitions.Add(new CardDefinitionEntry(appId, load.Definition));
                inlinePrefabs.AddRange(load.Prefabs);
                foreach (var (cardIdForEffects, prefabIds) in load.EffectDeclarations)
                {
                    declarations.Add((prefix + cardIdForEffects, prefabIds));
                }
            }
        }
    }

    private static void RegisterDeclaredAssets(
        PackageScan scan,
        PackageManifest manifest,
        AssetRegistry registry,
        AssetDomain domain,
        Dictionary<string, AssetSidecar> sidecars,
        List<AssetDiagnostic> diagnostics,
        HashSet<Guid> consumed,
        List<AssetRegistration> registered,
        List<DeferredDeckAsset> deferredDecks)
    {
        var cardSources = new HashSet<string>(
            scan.CardFiles.Select(file => file.RelativePath), StringComparer.Ordinal);

        foreach (var (relativeSource, sidecar) in sidecars.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var pending = sidecar.Assets.Where(asset => !consumed.Contains(asset.Guid)).ToArray();
            if (pending.Length == 0)
            {
                continue;
            }

            var absoluteSource = Path.GetFullPath(Path.Combine(scan.RootDirectory, relativeSource));
            if (cardSources.Contains(relativeSource))
            {
                continue; // 卡 sidecar 的内含资产：消费方在卡路径处理（文本→效果产出的是内联预制体）。
            }

            if (!File.Exists(absoluteSource))
            {
                foreach (var asset in pending)
                {
                    diagnostics.Add(new AssetDiagnostic(
                        AssetDiagnosticCode.SourceMissing,
                        manifest.Id,
                        AssetAppId.Compose(manifest.Id, asset.Id),
                        relativeSource,
                        $"源文件缺失（孤儿 meta）：{relativeSource}（该资产不注册）。"));
                }

                continue;
            }

            EffectSnapshot? snapshot = null;
            string? failure = null;
            if (absoluteSource.EndsWith(".prefab.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    snapshot = PrefabJson.Deserialize(File.ReadAllText(absoluteSource));
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Text.Json.JsonException)
                {
                    failure = ex.Message;
                }
            }

            foreach (var asset in pending)
            {
                var appId = AssetAppId.Compose(manifest.Id, asset.Id);
                if (failure is not null)
                {
                    diagnostics.Add(new AssetDiagnostic(
                        AssetDiagnosticCode.FailedFile, manifest.Id, appId, relativeSource, failure));
                    continue;
                }

                if (!ValidateGuid(manifest, asset, appId, relativeSource, diagnostics))
                {
                    continue;
                }

                if (asset.Kind == AssetKind.Deck)
                {
                    // 卡组延后到第二段（R3/D9）；身份与字段越界由 Defer 记录（D59/D61：先身份后字段）。
                    consumed.Add(asset.Guid);
                    deferredDecks.Add(Defer(manifest, asset, appId, absoluteSource, relativeSource));
                    continue;
                }

                consumed.Add(asset.Guid);
                var registration = new AssetRegistration(
                    asset, domain, manifest.Id, appId, AssetMetadataSource.Sidecar, null, snapshot, absoluteSource)
                {
                    PackageGuid = manifest.Guid,
                };
                Register(registry, registration, appId, relativeSource, diagnostics, registered);
            }
        }
    }

    /// <summary>
    /// 生成卡组声明（第一段只做**身份 ＋ 字段越界**两道判定，不解析内容、不产出 DTO）。
    /// <para>顺序不可换（D61）：先身份后字段——身份都不对，谈不上「声明合不合规」。</para>
    /// </summary>
    private static DeferredDeckAsset Defer(
        PackageManifest manifest,
        AssetMetadata metadata,
        string appId,
        string absoluteSource,
        string relativeSource)
    {
        var issues = new List<AssetDiagnostic>();
        if (metadata.Anchor is not null)
        {
            issues.Add(new AssetDiagnostic(
                AssetDiagnosticCode.FieldNotAllowed,
                manifest.Id,
                appId,
                relativeSource,
                "卡组不是内含资产，不得声明 anchor（该资产非法，不生成卡组对象）。"));
        }

        if (metadata.Loader is not null)
        {
            issues.Add(new AssetDiagnostic(
                AssetDiagnosticCode.FieldNotAllowed,
                manifest.Id,
                appId,
                relativeSource,
                "卡组不参与卡位装载，loader 不适用（该资产非法，不生成卡组对象）。"));
        }

        if (metadata.ServiceKind is not null)
        {
            issues.Add(new AssetDiagnostic(
                AssetDiagnosticCode.FieldNotAllowed,
                manifest.Id,
                appId,
                relativeSource,
                "serviceKind 只对 service 种类有效（该资产非法，不生成卡组对象）。"));
        }

        return new DeferredDeckAsset(
            metadata,
            manifest.Id,
            appId,
            absoluteSource,
            relativeSource,
            manifest.Guid,
            issues.Count == 0 ? null : issues);
    }

    private static bool ValidateGuid(
        PackageManifest manifest,
        AssetMetadata metadata,
        string appId,
        string file,
        List<AssetDiagnostic> diagnostics)
    {
        if (metadata.InvalidVersion is { } rawVersion)
        {
            diagnostics.Add(new AssetDiagnostic(
                AssetDiagnosticCode.VersionInvalid,
                manifest.Id,
                appId,
                file,
                $"资产版本 '{rawVersion}' 非法（期望 1..{AssetVersion.MaxSegments} 段非负整数；该资产非法，不生成）。"));
            return false;
        }

        var derived = AssetIdentity.DeriveAssetGuid(manifest.Guid, metadata.Kind, metadata.Id, metadata.Version);
        if (derived == metadata.Guid)
        {
            return true;
        }

        diagnostics.Add(new AssetDiagnostic(
            AssetDiagnosticCode.GuidMismatch,
            manifest.Id,
            appId,
            file,
            $"sidecar 声明的 GUID '{metadata.Guid}' 与身份派生 '{derived}' 不一致（该资产非法，不生成）。"));
        return false;
    }

    private static void Register(
        AssetRegistry registry,
        AssetRegistration registration,
        string appId,
        string file,
        List<AssetDiagnostic> diagnostics,
        List<AssetRegistration> registered)
    {
        if (registry.TryRegister(registration, out var failure))
        {
            registered.Add(registration);
            return;
        }

        diagnostics.Add(new AssetDiagnostic(
            AssetDiagnosticCode.GuidMismatch,
            registration.PackageId,
            appId,
            file,
            failure ?? "资产注册被拒绝。"));
    }

    private static AssetMetadata Synthesize(PackageManifest manifest, AssetKind kind, string localId)
    {
        var version = new AssetVersion(1, 0, 0);
        return new AssetMetadata(
            AssetIdentity.DeriveAssetGuid(manifest.Guid, kind, localId, version),
            kind,
            localId,
            version,
            Anchor: null,
            Loader: null,
            DependsOn: [],
            ConflictsWith: []);
    }

    private static void AppendTextEffects(
        PackageManifest manifest,
        IReadOnlyList<CardDefinitionEntry> definitions,
        IReadOnlyDictionary<string, string> texts,
        List<EffectSnapshot> inlinePrefabs,
        List<(string CardId, IReadOnlyList<string> PrefabIds)> declarations)
    {
        var prefix = $"pack/{manifest.Id}/";
        foreach (var entry in definitions)
        {
            var rawId = entry.Id.StartsWith(prefix, StringComparison.Ordinal) ? entry.Id[prefix.Length..] : entry.Id;
            if (!texts.TryGetValue(rawId, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 卡数据体已显式声明效果（`effects` 组件的 prefabs/inline）＝作者意图优先：
            // 文本模板不再叠加。叠加会让同一张卡同时持两个效果实例，一次打出把同一段效果结算两次
            // （card.played 上有两个 handler，各自完整跑一遍）。
            if (declarations.Exists(item => string.Equals(item.CardId, entry.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            var cardType = entry.Definition.Category == CardCategory.Unit ? "unit" : "order";
            var drafts = CardTextEffectCompiler.Parse(text, cardType)
                ?? throw new InvalidDataException(
                    $"卡包 '{manifest.Id}' 卡牌 '{rawId}' 的卡面文本无法解析（生成器与解析器已漂移）：{text}");
            if (drafts.Count == 0)
            {
                continue;
            }

            var snapshot = CardTextEffectCompiler.Compile(cardType, drafts, "pack." + manifest.Id + "." + rawId);
            inlinePrefabs.Add(snapshot);
            declarations.Add((entry.Id, new[] { snapshot.Root.Id }));
        }
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
