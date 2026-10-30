using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Triggers;
using Orc.Game.Decks;
using Orc.Packages;
using Orc.Packages.Decks;

namespace Orc.Packages.Loading;

/// <summary>
/// **卡组装载**（S7；R5/R7/R14/R15）：把「卡包目录 → 注册面 → 合并 lock → 资产加载器分层列表 →
/// 卡牌加载器整批装载 → 卡库」这条链路收敛成一个对局级的装载对象，并以
/// <see cref="IMatchCardLoader"/> 端口交给对局（依赖方向 `Orc.Packages → Orc.Game`，对局层不感知 GUID）。
/// <para>
/// 两个阶段，职责不重叠：
/// <list type="bullet">
///   <item>**准备期**（构造，对局创建前）：注册全部卡包（正式域）→ 读两域 lock → 计划分层列表缓存；
///     此阶段还没有卡库（卡库随对局创建），故不落地。</item>
///   <item>**装配期**（<see cref="Attach"/>，对局初始化内）：把卡库接到卡牌加载器 → 资产加载器装载分层列表
///     （合法／警报卡＝套件补全后的定义，含资产域）→ 按装载列表把 **应用层 ID → GUID** 定版结果写进中间层。</item>
/// </list>
/// </para>
/// <para>
/// **时序硬约束**：本类型的任何方法都**不实例化卡**、不分配对局级卡牌 ID——实例化仍在
/// <c>Match.Initialize</c> 的 <c>LoadDecksAsync</c> 内原位进行（ID 水位线语义挂在那条时序上）。
/// </para>
/// <para>单线程（对局装配期一次性使用）；不承诺并发安全。</para>
/// </summary>
public sealed class MatchCardAssembly : IMatchCardLoader
{
    private const string ManifestFileName = "manifest.json";

    private readonly List<AssetLock> _locks;
    private readonly List<PackageRegistrationResult> _registrations;
    private readonly List<string> _warnings;
    private readonly IReadOnlyList<AssetDiagnostic> _deckDiagnostics;
    private CardLoaderTrigger? _loader;
    private AssetLoader? _assetLoader;

    private MatchCardAssembly(
        AssetRegistry registry,
        AssetIdBridge bridge,
        List<AssetLock> locks,
        List<PackageRegistrationResult> registrations,
        List<string> warnings,
        IReadOnlyList<AssetDiagnostic> deckDiagnostics)
    {
        Registry = registry;
        Bridge = bridge;
        _locks = locks;
        _registrations = registrations;
        _warnings = warnings;
        _deckDiagnostics = deckDiagnostics;
    }

    /// <summary>资产注册面（进程视图；本装配独占——同一条装载链只有一份 DTO）。</summary>
    public AssetRegistry Registry { get; }

    /// <summary>中间层（应用层 ID → GUID；正式／测试两域）。</summary>
    public AssetIdBridge Bridge { get; }

    /// <summary>注册期与 lock 期的告警（不阻断装载的人读留痕）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// 本次装配的**卡组诊断**（R5/D42）：一条非法卡组＝一条 <see cref="AssetDiagnosticCode.DeckInvalid"/>，
    /// 其 <see cref="AssetDiagnostic.Violations"/> 装全部违规。读面按包投影这些条目。
    /// </summary>
    public IReadOnlyList<AssetDiagnostic> DeckDiagnostics => _deckDiagnostics;

    /// <summary>
    /// 卡组定位索引（R6/D7）：只装**本次加载计划里实际出现、且 DTO 非空**的卡组——
    /// 名称解析用的是「lock 后可用集合」，与判定所用的「注册集合」严格分开（D8）。
    /// </summary>
    public DeckCatalog Decks { get; private set; } = DeckCatalog.Empty;

    /// <summary>卡定义集（包链的 id 已前缀化为应用层 ID）——宿主用于建局前的读面/报告。</summary>
    public IReadOnlyList<CardDefinitionEntry> Definitions
        => _registrations.SelectMany(registration => registration.Content.Definitions).ToArray();

    /// <summary>内联效果预制体（含文本转换产出的效果）。</summary>
    public IReadOnlyList<EffectSnapshot> InlinePrefabs
        => _registrations.SelectMany(registration => registration.Content.InlinePrefabs).ToArray();

    /// <summary>卡 → 效果预制体声明（cardId 已前缀化）。</summary>
    public IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations
        => _registrations.SelectMany(registration => registration.Content.Declarations).ToArray();

    /// <summary>效果库目录（按装载序）。</summary>
    public IReadOnlyList<string> EffectDirectories
        => _registrations
            .Select(registration => registration.Content.EffectsDirectory)
            .Where(directory => directory is not null)
            .Select(directory => directory!)
            .ToArray();

    /// <summary>包链的卡 id 前缀（<c>pack/{包id}/</c>；按装载序）。</summary>
    public IReadOnlyList<string> PackPrefixes
        => _registrations.Select(registration => registration.Content.Prefix).ToArray();

    /// <summary>装配期装载结果（未 <see cref="Attach"/>＝null；结构性失败＝抛异常、不留在本属性）。</summary>
    public AssetLoadResult? LoadResult { get; private set; }

    /// <summary>最近一次装载批次的报告（未装载＝null；报告不落盘，只经返回值/工具返回）。</summary>
    public CardLoadReport? LastReport { get; private set; }

    /// <summary>
    /// 本局加载台账（S9）：记录每一次批次，给出「最近一批」与「按 GUID 合并、<c>refCount</c> 累加」的累积视图。
    /// 每局一份（本类型即对局级装配），诊断工具只读此处、不复制报告模型。
    /// </summary>
    public CardLoadLedger Ledger { get; } = new();

    /// <summary>本局实际使用的卡牌加载器（未 <see cref="Attach"/>＝null）。</summary>
    public CardLoaderTrigger? Loader => _loader;

    /// <summary>
    /// 回填**实例装配阶段**的组件失败（如效果 csx 在 v4 承载视图下编译不过）到最近一份报告：
    /// 命中的资产条目降 <c>invalid</c> ＋ 追加 <c>fields</c>，对应卡位降 <c>invalid</c> ＋ 追加 <c>components</c>。
    /// <para>
    /// 语义＝**同一批的修订**（不是新批次）：只替换 <see cref="LastReport"/> 并让台账取修订后的值，
    /// 批次数与 <c>refCount</c> 不变（见 <see cref="CardLoadLedger.RecordRevision"/>）。
    /// </para>
    /// <para>
    /// 尚无报告（未装载过任何批次／独立装配路径）＝**不抛**：写一条告警留痕后返回——
    /// 报告面缺席是装配事实，不该让一张坏卡把对局创建打断（它对局照常、只是无从标注）。
    /// </para>
    /// </summary>
    /// <param name="failures">失败清单（空＝无动作）。</param>
    /// <exception cref="ArgumentNullException">failures 为 null 或含 null 项。</exception>
    public void RecordComponentFailures(IReadOnlyList<CardComponentFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            return;
        }

        if (LastReport is not { } report)
        {
            _warnings.Add(
                $"组件失败（{failures.Count} 条）无法回填：本局尚无卡牌加载报告（未装载过批次）——"
                + "失败事实仅见于引擎日志。");
            return;
        }

        LastReport = report.WithComponentFailures(failures);
        Ledger.RecordRevision(LastReport);
    }

    /// <summary>
    /// 准备期：**注册**卡包目录（只解析元数据并生成 DTO，不实例化、不进对局），并读入两域 lock。
    /// <para>判定实现与 <see cref="Inspect"/> 同一份（<see cref="BuildOutcome"/>）；本方法只是把失败转成异常。</para>
    /// </summary>
    /// <param name="packDirectories">卡包目录（须含 <c>manifest.json</c>——加载严格按元数据）。</param>
    /// <exception cref="ArgumentException">目录列表为空或含空白项。</exception>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    /// <exception cref="InvalidDataException">目录不是卡包；或包级结构性非法（整包不注册）。</exception>
    /// <param name="packDirectories">卡包目录。</param>
    /// <param name="requiredDeckSize">
    /// 卡组要求的张数（D11：由调用方按 <c>ServerOptions.RequiredDeckSize</c> 传入；**不写死**）。
    /// </param>
    public static MatchCardAssembly Register(
        IReadOnlyList<string> packDirectories, int requiredDeckSize = DeckRules.DefaultRequiredSize)
    {
        ArgumentNullException.ThrowIfNull(packDirectories);
        if (packDirectories.Count == 0)
        {
            throw new ArgumentException("至少需要一个卡包目录。", nameof(packDirectories));
        }

        return Build(packDirectories, strict: true, requiredDeckSize).Assembly!;
    }

    /// <summary>
    /// **判定旁路**（S3）：按同一套实现回答「用这份目录集合现在开局会怎样」——纯读、不 <c>Bind</c>、不落地、**不抛**。
    /// <para>
    /// 结构性非法（目录缺失／不是卡包／包级结构性非法／lock 冲突／环依赖／无卡可装／空输入）一律走结果模型，
    /// 由调用方按受众投影；这样「试算」与「装配」不可能得出两套结论。
    /// </para>
    /// </summary>
    /// <param name="packDirectories">卡包目录（空＝合法输入，产出 <see cref="AssetLoadIssueCode.EmptyPool"/>）。</param>
    /// <param name="packDirectories">卡包目录（空＝合法输入，产出 <see cref="AssetLoadIssueCode.EmptyPool"/>）。</param>
    /// <param name="requiredDeckSize">卡组要求的张数（与 <see cref="Register"/> 同一口径）。</param>
    public static AssemblyInspection Inspect(
        IReadOnlyList<string> packDirectories, int requiredDeckSize = DeckRules.DefaultRequiredSize)
    {
        ArgumentNullException.ThrowIfNull(packDirectories);
        return Build(packDirectories, strict: false, requiredDeckSize).Inspection;
    }

    /// <summary>构建结果（严格模式的产出／非严格模式的判定结论）。</summary>
    private readonly record struct BuildOutcome(MatchCardAssembly? Assembly, AssemblyInspection Inspection);

    /// <summary>
    /// **唯一判定实现**：目录 → 注册面 → 合并 lock → 分层计划。
    /// <para>
    /// <paramref name="strict"/>＝<c>true</c> 时把失败转成与既有装配链完全等价的异常（<see cref="Register"/>）；
    /// <c>false</c> 时把同一批失败写进 <see cref="AssemblyInspection"/>（<see cref="Inspect"/>）。
    /// </para>
    /// </summary>
    private static BuildOutcome Build(IReadOnlyList<string> packDirectories, bool strict, int requiredDeckSize)
    {
        var registry = new AssetRegistry();
        var bridge = new AssetIdBridge();
        var locks = new List<AssetLock>();
        var registrations = new List<PackageRegistrationResult>();
        var warnings = new List<string>();
        var packages = new List<PackageInspection>(packDirectories.Count);

        if (packDirectories.Count == 0)
        {
            // 空集合是**合法状态**（spawn 初值本就可能为空）：不抛，给 EmptyPool ＋ 全零摘要。
            var empty = new AssetLoadIssue(
                AssetLoadIssueCode.EmptyPool,
                AssetDomain.Formal,
                AppId: null,
                "卡包集合为空：没有任何可装载的卡包目录（请先 pool.register）。",
                []);
            return new BuildOutcome(
                null,
                new AssemblyInspection(false, packages, [empty], warnings, AssemblyPlanSummary.Zero));
        }

        foreach (var rawDirectory in packDirectories)
        {
            if (strict)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(rawDirectory); // 既有入参校验（空白项＝调用方错误）
            }

            var probe = PackageDirectoryProbe.Probe(rawDirectory);
            if (probe.Manifest is not { } manifest)
            {
                packages.Add(new PackageInspection(
                    probe.Directory, probe.PackageId, probe.Version, probe.Guid, false, probe.ErrorCode, probe.Error,
                    WillRemove: true));
                if (strict)
                {
                    throw ProbeFailure(probe);
                }

                continue;
            }

            if (!probe.Ok)
            {
                // 有清单但目录仍不可用（kind 非 cardpack／id 漂移）：同样不入装配计算。
                packages.Add(new PackageInspection(
                    probe.Directory, probe.PackageId, probe.Version, probe.Guid, false, probe.ErrorCode, probe.Error,
                    WillRemove: true));
                if (strict)
                {
                    throw ProbeFailure(probe);
                }

                continue;
            }

            var directory = probe.Directory;
            warnings.AddRange(probe.EffectiveWarnings.Select(warning => $"{manifest.Id}：{warning}"));

            try
            {
                registrations.Add(PackageRegistrar.Register(directory, manifest, registry));

                foreach (var domain in new[] { AssetDomain.Formal, AssetDomain.Test })
                {
                    if (AssetLockJson.ReadPackage(directory, domain) is { } @lock)
                    {
                        locks.Add(@lock);
                    }
                    else if (domain == AssetDomain.Formal)
                    {
                        warnings.Add($"{manifest.Id}：缺少正式 lock（{Path.GetFileName(AssetLockJson.PathFor(directory, domain))}）——整包不参与加载");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException
                or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
            {
                // 包级结构性非法（sidecar／GUID 重复／lock 不可解析…）⇒ 该包不可装载；
                // 读面把它标成失效条目（错误码沿用 manifest-invalid，自由文本给原因）。
                packages.Add(new PackageInspection(
                    directory,
                    manifest.Id,
                    manifest.Version.ToString(),
                    manifest.Guid.ToString("N"),
                    false,
                    PackageProbeCodes.ManifestInvalid,
                    ex.Message,
                    WillRemove: true));
                if (strict)
                {
                    throw; // 既有异常类型与 message 原样保留
                }

                continue;
            }

            packages.Add(new PackageInspection(
                directory, manifest.Id, manifest.Version.ToString(), manifest.Guid.ToString("N"), true));
        }

        // ── 第二段：卡组注册与判定（R3/D9）──────────────────────────────────────
        // 必须在 Planner 之前完成：lock 里的卡组条目要靠注册表解析，晚一步就是一批 Unregistered。
        // 判定集合＝本次注册的全部包（跨包引用天然合法）；卡池＝已通过卡牌判定的卡（D13）。
        var deckResult = DeckRegistrar.Register(
            registry,
            registrations.SelectMany(registration => registration.Decks),
            AssetDomain.Formal,
            requiredDeckSize);

        for (var index = 0; index < packages.Count; index++)
        {
            var diagnostics = deckResult.Diagnostics
                .Where(diagnostic => string.Equals(diagnostic.PackageId, packages[index].PackageId, StringComparison.Ordinal))
                .ToArray();
            if (diagnostics.Length > 0)
            {
                packages[index] = packages[index] with { DeckDiagnostics = diagnostics };
            }
        }

        warnings.AddRange(deckResult.Warnings);

        var assembly = new MatchCardAssembly(registry, bridge, locks, registrations, warnings, deckResult.Diagnostics);
        var issues = new List<AssetLoadIssue>();

        if (assembly.Definitions.Count == 0)
        {
            var message = "卡包目录未装载到任何卡定义（检查 lock 是否覆盖已注册资产）；诊断："
                + string.Join("；", warnings.Take(6));
            if (strict)
            {
                throw new InvalidDataException(message);
            }

            issues.Add(new AssetLoadIssue(
                AssetLoadIssueCode.DependencyMissing, AssetDomain.Formal, AppId: null, message, []));
            return new BuildOutcome(
                assembly,
                new AssemblyInspection(
                    false,
                    packages,
                    issues,
                    warnings,
                    new AssemblyPlanSummary(0, 0, 0, 0, 0)));
        }

        // 定版（lock 决议）在**注册期**一次算完并写入中间层：卡组解析（裸局部 ID → 应用层 ID）不依赖对局卡库，
        // 故宿主可在建局前解析卡位；装载期只按同一份计划落地（装载器再算一次是纯函数、结果必然一致）。
        var plan = LoadListPlanner.Plan(registry, locks);
        issues.AddRange(plan.Issues);

        if (!plan.IsValid)
        {
            if (strict)
            {
                throw new InvalidDataException(
                    "卡组装载计划结构性非法（lock 冲突／环依赖）："
                    + string.Join("；", plan.Issues.Where(issue => issue.IsFatal).Select(issue => issue.ToString())));
            }
        }

        var cardCount = plan.Formal.Items.Count(item => item.Kind == AssetKind.Card)
            + plan.Test.Items.Count(item => item.Kind == AssetKind.Card);
        var hasCard = cardCount > 0;

        if (!hasCard)
        {
            var message = "lock 未覆盖任何卡资产（正式／测试两域均无可装载卡）；诊断："
                + string.Join("；", warnings.Take(6));
            if (strict)
            {
                throw new InvalidDataException(message);
            }
        }

        var summary = new AssemblyPlanSummary(
            assembly.Definitions.Count,
            plan.Formal.LayerCount,
            plan.Test.LayerCount,
            plan.Count,
            cardCount);

        if (strict)
        {
            assembly.BindPlan(plan);
        }

        assembly.Decks = DeckCatalog.Build(
            plan.Formal.Items
                .Concat(plan.Test.Items)
                .Where(item => item.Kind == AssetKind.Deck && item.Asset.Deck is not null)
                .Select(item => (item.AppId, item.Asset.Deck!)));

        var ok = packages.All(package => package.Ok) && plan.IsValid && hasCard;
        return new BuildOutcome(assembly, new AssemblyInspection(ok, packages, issues, warnings, summary));
    }

    /// <summary>把探测失败还原成与既有装配链**等价**的异常（类型 ＋ message 一致）。</summary>
    private static Exception ProbeFailure(PackageProbeResult probe)
    {
        if (probe.Cause is { } cause)
        {
            return cause;
        }

        return probe.ErrorCode switch
        {
            PackageProbeCodes.DirectoryMissing => new DirectoryNotFoundException(probe.Error),
            PackageProbeCodes.KindNotCardpack => new InvalidOperationException(probe.Error),
            PackageProbeCodes.ManifestMissing or PackageProbeCodes.ManifestInvalid or PackageProbeCodes.IdChanged
                => new InvalidDataException(probe.Error),
            _ => new IOException(probe.Error),
        };
    }

    /// <summary>
    /// 装配期（对局初始化内）：接入本局卡库 → 资产加载器装载分层列表 → 中间层写入定版结果。
    /// </summary>
    /// <param name="library">本局卡库。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidOperationException">重复接入；或装载链结构性失败（lock 冲突／环依赖／装载失败）。</exception>
    public void Attach(CardLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (_loader is not null)
        {
            throw new InvalidOperationException("卡组装载已接入过卡库（一个装载链只服务一个对局——重复接入被拒绝）。");
        }

        _loader = new CardLoaderTrigger(Registry, Bridge, library);
        // 批次 7（§27）：不存在对局级登记簿——加载器由本装配面持有即进账（审查面读引擎成员边图）。
        _assetLoader = new AssetLoader(Registry, _loader);

        // 先正式后测试（R15：两段加载；跨域绝不互相合并——由 AssetLoader/Planner 保证）。
        var result = _assetLoader.Load(_locks);
        LoadResult = result;
        if (!result.Ok || result.Plan is null)
        {
            throw new InvalidOperationException(
                $"卡组装载失败（{result.Status}）：{result.Failure ?? DescribeIssues(result)}");
        }

        BindPlan(result.Plan);
    }

    /// <summary>
    /// 整批装载（双方牌堆＝一次调用；局内新增卡＝一元清单）：解析经中间层，装载经卡牌加载器；
    /// 产出与输入同序的卡位结局（卡位留空＝非法／未注册／未入 lock），并把本批产出的卡同步进中间层。
    /// </summary>
    /// <param name="slots">卡位清单（可为空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">slots 为 null。</exception>
    /// <exception cref="InvalidOperationException">尚未 <see cref="Attach"/>（装配时序错误）。</exception>
    public async Task<CardLoadOutcome> LoadBatchAsync(
        IReadOnlyList<MatchCardSlot> slots, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (_loader is null)
        {
            throw new InvalidOperationException("卡组装载尚未接入卡库（Attach 未执行——装配时序错误）。");
        }

        var items = slots
            .Select(slot => new CardLoadItem(slot.PlayerId, slot.Index, slot.Reference))
            .ToArray();

        var result = await _loader.LoadBatchAsync(items, ct).ConfigureAwait(false);
        LastReport = result.Report;
        Ledger.Record(result.Report); // 本局累积视图（按 GUID 合并、refCount 累加）

        // 中间层同步（S7 第 6 条）：本批产出的卡（含局内单卡新增）写入 (域, 种类, 应用层 ID) → GUID。
        foreach (var card in result.Cards)
        {
            Bridge.Bind(card.Domain, AssetKind.Card, card.AppId, card.AssetGuid);
        }

        var byGuid = result.Cards.ToDictionary(card => card.AssetGuid);
        var outcomes = new CardSlotOutcome[result.Report.Slots.Count];
        for (var i = 0; i < outcomes.Length; i++)
        {
            var slot = result.Report.Slots[i];
            outcomes[i] = slot.AssetGuid is { } guid && byGuid.TryGetValue(guid, out var card)
                ? new CardSlotOutcome(CardSlotStatus.Loaded, card.AppId)
                : CardSlotOutcome.Empty;
        }

        return new CardLoadOutcome(result.BatchId, outcomes);
    }

    /// <summary>
    /// 中间层写入定版结果：装载列表的每条（含内含资产）→（域, 种类, 应用层 ID）→ GUID。
    /// 正式段先写、测试段后写——同名映射由中间层的域遮蔽表裁决（正式域解析永不看到测试映射）。
    /// </summary>
    private void BindPlan(AssetLoadPlan plan)
    {
        foreach (var domain in new[] { AssetDomain.Formal, AssetDomain.Test })
        {
            foreach (var item in plan.For(domain).Items)
            {
                Bridge.Bind(item.Domain, item.Kind, item.AppId, item.AssetGuid);
                foreach (var embedded in item.Embedded)
                {
                    Bridge.Bind(embedded.Domain, embedded.Kind, embedded.AppId, embedded.AssetGuid);
                }
            }
        }
    }

    private static string DescribeIssues(AssetLoadResult result)
        => result.Issues.Count == 0
            ? "（无问题明细）"
            : string.Join("；", result.Issues.Take(6).Select(issue => issue.ToString()));
}
