using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.EffectParsing;

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
    string Message);

/// <summary>一次包注册的产出：DTO 内容 ＋ 已注册资产 ＋ 资产级诊断。</summary>
/// <param name="Manifest">包清单。</param>
/// <param name="Content">注册面生成的 DTO 内容（供组装链使用）。</param>
/// <param name="Assets">本次注册进注册表的资产版本（注册序）。</param>
/// <param name="Diagnostics">资产级诊断（不阻断包）。</param>
public sealed record PackageRegistrationResult(
    PackageManifest Manifest,
    CardPackContent Content,
    IReadOnlyList<AssetRegistration> Assets,
    IReadOnlyList<AssetDiagnostic> Diagnostics);

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

        RegisterCards(scan, manifest, registry, domain, sidecars, diagnostics, consumed,
            registered, definitions, inlinePrefabs, declarations);

        RegisterDeclaredAssets(scan, manifest, registry, domain, sidecars, diagnostics, consumed, registered);

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
            texts);

        return new PackageRegistrationResult(manifest, content, registered, diagnostics);
    }

    /// <summary>读取全部 sidecar（manifest 元数据列表 ＋ 卡文件同目录）并按源文件相对路径索引。</summary>
    private static Dictionary<string, AssetSidecar> ReadSidecars(PackageScan scan)
    {
        var sidecars = new Dictionary<string, AssetSidecar>(StringComparer.Ordinal);
        var paths = new List<string>();

        foreach (var relative in scan.SidecarFiles)
        {
            var absolute = Path.GetFullPath(Path.Combine(scan.RootDirectory, relative));
            if (!File.Exists(absolute))
            {
                throw new InvalidDataException(MigrationGuidance.ForPackage(
                    scan.RootDirectory,
                    $"manifest 元数据列表指向的 sidecar 缺失：{relative}（包级结构性非法）"));
            }

            paths.Add(absolute);
        }

        foreach (var cardFile in scan.CardFiles)
        {
            if (cardFile.SidecarPath is { } sidecarPath)
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
        List<(string CardId, IReadOnlyList<string> PrefabIds)> declarations)
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
        List<AssetRegistration> registered)
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
            var existing = declarations.FindIndex(d => string.Equals(d.CardId, entry.Id, StringComparison.Ordinal));
            if (existing >= 0)
            {
                declarations[existing] = (entry.Id, [.. declarations[existing].PrefabIds, snapshot.Root.Id]);
            }
            else
            {
                declarations.Add((entry.Id, new[] { snapshot.Root.Id }));
            }
        }
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
