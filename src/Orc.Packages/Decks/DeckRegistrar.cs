using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Decks;

namespace Orc.Packages.Decks;

/// <summary>
/// 第一段留下的卡组声明（R3/D9）：元数据 ＋ 源文件位置。
/// <para>
/// 之所以不在第一段直接注册，是因为卡组判定必须看到**本次注册集合的全部包**——单包注册函数看不到全局。
/// </para>
/// </summary>
/// <param name="Metadata">sidecar 声明的资产元数据（GUID／版本已校验通过）。</param>
/// <param name="PackageId">所属包 id。</param>
/// <param name="AppId">应用层 ID。</param>
/// <param name="SourcePath">源文件绝对路径（那一刻存在；第二段可能已被删）。</param>
/// <param name="RelativeSource">源文件相对包根的路径（诊断用）。</param>
/// <param name="PackageGuid">包稳定身份（资产名用）。</param>
/// <param name="RejectedFields">
/// 第一段就判定的字段越界（D59）：非空＝该卡组已注定非法，第二段只登记身份与诊断，不再解析内容。
/// </param>
public sealed record DeferredDeckAsset(
    AssetMetadata Metadata,
    string PackageId,
    string AppId,
    string SourcePath,
    string RelativeSource,
    Guid PackageGuid,
    IReadOnlyList<AssetDiagnostic>? RejectedFields = null);

/// <summary>卡组阶段（R3 第二段）的产出。</summary>
/// <param name="Decks">本次产出的卡组条目（非法＝<see cref="AssetRegistration.Deck"/> 为 null，身份仍在）。</param>
/// <param name="Diagnostics">卡组诊断（一条非法卡组＝一条 <see cref="AssetDiagnosticCode.DeckInvalid"/>）。</param>
public sealed record DeckRegistrationResult(
    IReadOnlyList<AssetRegistration> Decks,
    IReadOnlyList<AssetDiagnostic> Diagnostics)
{
    /// <summary>
    /// 不判非法、但需要留痕的告警（D32：sidecar 的 <c>dependsOn</c> 与 <c>cards</c> 重算结果不一致）。
    /// 之所以只告警不判非法：前端直写卡体是被支持的工作流，把「sidecar 缓存过期」判成资产非法，
    /// 会逼直写通道每改一次卡就回编辑器重提交。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// **卡组注册阶段**（R3 第二段；D9 的独立入口）：在全部包的第一段完成后，统一扫描本次注册集合里的卡组声明，
/// 编译并判定，然后按「合法产 DTO／非法只留身份」写进注册面。
/// <para>
/// 硬约束：本阶段**不改动**第一段的产出（既有注册条目、GUID 映射、卡 DTO 一律不动）；
/// 它只新增卡组条目，或把上一次算过的同一条目按整体重算结果替换（<see cref="AssetRegistry.TryReplace"/>）。
/// </para>
/// <para>
/// 判定所用卡池＝注册集合内**通过卡牌判定**的卡（<see cref="AssetRegistration.Card"/> 非空）——
/// 被 sidecar 判非法的卡不进池，卡组引用它即报 <c>unknown-card</c>（D13 的正确连锁，不开特例）。
/// </para>
/// </summary>
public static class DeckRegistrar
{
    /// <summary>执行卡组阶段。</summary>
    /// <param name="registry">注册面（第一段已完成；本阶段写入卡组条目）。</param>
    /// <param name="decks">第一段留下的卡组声明。</param>
    /// <param name="domain">本次注册的资产域（卡组条目随之入域）。</param>
    /// <param name="requiredDeckSize">要求张数（D11：由调用方提供，不写死；&lt;=0＝不校验）。</param>
    /// <exception cref="ArgumentNullException">registry／decks 为 null。</exception>
    public static DeckRegistrationResult Register(
        AssetRegistry registry,
        IEnumerable<DeferredDeckAsset> decks,
        AssetDomain domain,
        int requiredDeckSize = DeckRules.DefaultRequiredSize)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(decks);

        var pool = BuildPool(registry, domain);
        var materialized = decks.ToArray();
        var registered = new List<AssetRegistration>(materialized.Length);
        var diagnostics = new List<AssetDiagnostic>();
        var warnings = new List<string>();

        foreach (var deck in materialized)
        {
            if (deck.RejectedFields is { Count: > 0 } fieldIssues)
            {
                // 字段越界的卡组：身份照常登记（D6），内容不再解析——anchor 等字段在条目里被抹平，
                // 保证 IsEmbedded 恒 false（D63），planner 不会看到「内含卡组」。
                var sanitized = deck.Metadata with { Anchor = null, Loader = null, ServiceKind = null };
                registered.Add(Store(
                    registry,
                    deck,
                    sanitized,
                    domain,
                    DependsOn: [],
                    definition: null));
                diagnostics.AddRange(fieldIssues);
                continue;
            }

            if (!File.Exists(deck.SourcePath))
            {
                registered.Add(Store(registry, deck, deck.Metadata, domain, DependsOn: [], definition: null));
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.SourceMissing,
                    deck.PackageId,
                    deck.AppId,
                    deck.RelativeSource,
                    $"卡组源文件缺失：{deck.RelativeSource}（该资产不生成）。")
                {
                    Violations = null,
                });
                continue;
            }

            string json;
            try
            {
                json = File.ReadAllText(deck.SourcePath);
            }
            catch (IOException ex)
            {
                registered.Add(Store(registry, deck, deck.Metadata, domain, DependsOn: [], definition: null));
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.FailedFile,
                    deck.PackageId,
                    deck.AppId,
                    deck.RelativeSource,
                    $"卡组源文件读取失败：{ex.Message}"));
                continue;
            }

            var result = DeckAssetCompiler.Compile(
                json, deck.Metadata.Id, deck.PackageId, pool, requiredDeckSize);

            // 依赖由 `cards` 重算并注入元数据（D31/D34）：planner 只读 Metadata.DependsOn，
            // sidecar 里的 dependsOn 只是派生缓存，注册期绝不回写（D33）。
            var dependsOn = DeriveDependsOn(result.Definition);
            registered.Add(Store(registry, deck, deck.Metadata, domain, dependsOn, result.Definition));

            if (result.Definition is not null && !SameDependencies(deck, result.Definition, dependsOn))
            {
                warnings.Add(
                    $"{deck.AppId}：sidecar 的 dependsOn 与 cards 的重算结果不一致——"
                    + "已按重算结果参与拓扑（sidecar 只是派生缓存，注册期不回写）。");
            }

            if (result.Failure is { } failure)
            {
                diagnostics.Add(new AssetDiagnostic(
                    result.FailureCode,
                    deck.PackageId,
                    deck.AppId,
                    deck.RelativeSource,
                    failure));
                continue;
            }

            if (result.Violations.Count > 0)
            {
                diagnostics.Add(new AssetDiagnostic(
                    AssetDiagnosticCode.DeckInvalid,
                    deck.PackageId,
                    deck.AppId,
                    deck.RelativeSource,
                    DescribeViolations(result.Violations))
                {
                    Violations = result.Violations,
                });
            }
        }

        return new DeckRegistrationResult(registered, diagnostics) { Warnings = warnings };
    }

    /// <summary>派生依赖清单：按卡去重、引用为应用层 ID 全名、约束恒 <c>*.*.*</c>（R4/D31）。</summary>
    public static IReadOnlyList<AssetReference> DeriveDependsOn(DeckDefinition? definition)
        => definition is null
            ? Array.Empty<AssetReference>()
            : definition.Entries
                .Select(entry => entry.AppId)
                .Distinct(StringComparer.Ordinal)
                .Select(appId => new AssetReference(AssetKind.Card, appId, VersionIndependentConstraint))
                .ToArray();

    /// <summary>卡组对卡依赖的约束串（与 <see cref="AssetVersionConstraint"/> 的 <c>Any</c> 主版段一致）。</summary>
    public const string VersionIndependentConstraint = "*.*.*";

    /// <summary>
    /// <c>cards</c> 重算出的依赖与 sidecar 声明是否等价（D32 的比对口径）。
    /// 只比「卡引用」——sidecar 里若额外写了非卡种类依赖，说明作者手工干预过，同样算不一致。
    /// </summary>
    private static bool SameDependencies(
        DeferredDeckAsset deck, DeckDefinition definition, IReadOnlyList<AssetReference> derived)
    {
        var declared = deck.Metadata.DependsOn
            .Select(reference => (
                reference.Kind,
                AppId: AssetAppId.IsAppId(reference.AppId)
                    ? reference.AppId
                    : AssetAppId.Compose(deck.PackageId, reference.AppId),
                reference.VersionConstraint))
            .ToHashSet();

        var actual = derived
            .Select(reference => (reference.Kind, reference.AppId, reference.VersionConstraint))
            .ToHashSet();

        return declared.SetEquals(actual);
    }

    private static AssetRegistration Store(
        AssetRegistry registry,
        DeferredDeckAsset deck,
        AssetMetadata metadata,
        AssetDomain domain,
        IReadOnlyList<AssetReference> DependsOn,
        DeckDefinition? definition)
    {
        var registration = new AssetRegistration(
            metadata with { DependsOn = DependsOn },
            domain,
            deck.PackageId,
            deck.AppId,
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            deck.SourcePath)
        {
            PackageGuid = deck.PackageGuid,
            Deck = definition,
        };

        // 同一份卡组在「包集合变化后整体重算」时会落到同一条记录：已存在则替换（D9 允许合法↔非法翻转）。
        if (!registry.TryRegister(registration, out _))
        {
            registry.TryReplace(registration, out _);
        }

        return registry.Find(registration.AssetGuid) ?? registration;
    }

    /// <summary>
    /// 判定用卡池：注册集合内通过卡牌判定的卡（D13）。同 appId 多版本时取最高版；
    /// 跨域同名时优先本次域（卡组在正式域就不该被测试域的卡「顺手救活」）。
    /// </summary>
    private static IReadOnlyDictionary<string, CardDefinition> BuildPool(AssetRegistry registry, AssetDomain domain)
    {
        var pool = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        foreach (var group in registry.All
                     .Where(asset => asset.Kind == AssetKind.Card && asset.Card is not null)
                     .GroupBy(asset => asset.AppId, StringComparer.Ordinal))
        {
            var chosen = group
                .OrderByDescending(asset => asset.Domain == domain)
                .ThenByDescending(asset => asset.Version)
                .First();
            pool[group.Key] = chosen.Card!;
        }

        return pool;
    }

    private static string DescribeViolations(IReadOnlyList<DeckViolation> violations)
        => $"卡组不合法（{violations.Count} 项）：" + string.Join("；", violations.Select(violation => violation.Message));
}
