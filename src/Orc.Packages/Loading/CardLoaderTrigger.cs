using System.Globalization;
using System.Runtime.ExceptionServices;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>
/// **卡牌加载器**（R5/D33/D34/I5；以**底层触发器**形态存在）：
/// 接收一批卡位清单 → 解析（中间层）→ 合法性检测套件（三档）→ 产出「**报告 ＋ 卡牌引用列表**」。
/// <para>
/// 触发通道（I5 已定）：
/// <list type="bullet">
///   <item>**不挂总线**（无 hook 声明、未 Mount）：加载是命令不是事件；唯一入口 <see cref="LoadBatchAsync"/>。</item>
///   <item>批量＝一次调用（单卡＝一元清单）；每次调用＝一次触发器执行（自带事件流留痕，便于诊断）。</item>
///   <item>专用视图 <see cref="CardLoadBatchView"/>（承载整批清单与报告收集器）——不复用任何事件视图。</item>
/// </list>
/// </para>
/// <para>
/// 两层注册面（**不得混用**）：
/// <list type="bullet">
///   <item>进程级信任边界＝<see cref="CardLoaderRegistry"/>（挂在 <see cref="AssetRegistry.CardLoaders"/> 子面）：
///     卡包元数据的 <c>loader</c> 只能引用此处已注册的名字；未注册／卡包自带 ⇒ **结构性拒绝**（整批不装载）。</item>
///   <item>对局级审计面（批次 7／§27 起）：不存在第二本登记簿——加载器实例由卡装载链本身持有，
///     审查面读的是引擎成员边图（<c>AuditGraph</c>）。</item>
/// </list>
/// </para>
/// <para>
/// 装载落地＝<see cref="IAssetLoadSink"/>（S5 的动作缝）：<see cref="Apply"/> 把条目写进卡库并**写入资产域**，
/// <see cref="Revert"/> 摘除；失败＝抛异常（由 <c>AssetLoader</c> 回滚，全有或全无）。
/// </para>
/// </summary>
public sealed class CardLoaderTrigger : IAssetLoadSink
{
    /// <summary>内置加载器名（插件注册面的默认声明名）。</summary>
    public const string DefaultLoaderName = "builtin.card";

    /// <summary>批次事件名（历史名；处理器只经 <see cref="LoadBatchAsync"/> 直调，不挂总线）。</summary>
    public const string BatchEventName = "card.load.batch";

    /// <summary>加载器名（须与注册面声明的名字一致才可被 sidecar 引用）。</summary>
    public string Name { get; }

    private readonly AssetRegistry _registry;
    private readonly AssetIdBridge _bridge;
    private readonly CardLoaderRegistry _loaders;
    private readonly CardValiditySuite _suite;
    private readonly CardLibrary _library;
    private readonly Func<AssetRegistration, string?>? _cardImageLookup;
    private readonly Func<AssetRegistration, string?>? _descriptionLookup;
    private readonly Dictionary<Guid, CardValidityResult> _evaluated = new();
    private readonly LogicEngine _host;
    private int _sequence;

    /// <summary>
    /// 创建加载器（实例级；对局初始化时构造，随后经 <see cref="RegisterIn"/> 进对局级审计面）。
    /// </summary>
    /// <param name="registry">资产注册面（同时提供进程级加载器声明面 <see cref="AssetRegistry.CardLoaders"/>）。</param>
    /// <param name="bridge">中间层（应用层 ID → 资产 GUID；**解析唯一实现**）。</param>
    /// <param name="library">卡库（装载落地目标：条目与实例都带资产域）。</param>
    /// <param name="suite">合法性检测套件（缺省＝内置三档；插件只能追加检查项）。</param>
    /// <param name="cardImageLookup">卡图路径解析（引擎无美术概念——由宿主/前端交出；缺省＝数据体 <c>image</c> 字段）。</param>
    /// <param name="descriptionLookup">描述文本解析（缺省＝数据体 <c>description</c> 字段）。</param>
    /// <param name="name">加载器名（默认 <see cref="DefaultLoaderName"/>；须与注册面声明的名字一致才可被 sidecar 引用）。</param>
    /// <exception cref="ArgumentNullException">registry/bridge/library 为 null。</exception>
    public CardLoaderTrigger(
        AssetRegistry registry,
        AssetIdBridge bridge,
        CardLibrary library,
        CardValiditySuite? suite = null,
        Func<AssetRegistration, string?>? cardImageLookup = null,
        Func<AssetRegistration, string?>? descriptionLookup = null,
        string name = DefaultLoaderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(library);

        _registry = registry;
        _bridge = bridge;
        _loaders = registry.CardLoaders;
        _library = library;
        _suite = suite ?? CardValiditySuite.BuiltIn;
        _cardImageLookup = cardImageLookup;
        _descriptionLookup = descriptionLookup;

        // 执行宿主：不挂总线、不订阅任何更新；只作为触发器执行的引擎引用（事件流留痕）。
        _host = new LogicEngine();

        // 批次执行体＝本类唯一处理器：只经 LoadBatchAsync 直调（不挂总线、不声明 hook）。
        // 批次 7（§27）：不再继承触发器——加载器本身就是那个处理器。
    }

    /// <summary>进程级声明面（信任边界；sidecar 的 <c>loader</c> 只能引用此处名字）。</summary>
    public CardLoaderRegistry LoaderRegistry => _loaders;

    /// <summary>合法性检测套件（引擎内置三档 ＋ 插件追加项）。</summary>
    public CardValiditySuite Suite => _suite;

    /// <summary>
    /// **唯一触发入口**（直调；批量一次调用、单卡＝一元清单）：返回「报告 ＋ 卡牌引用列表」。
    /// <para>
    /// 卡级问题不阻断：非法卡不生成、对应卡位留空、其余照常（开局继续）。
    /// 结构性非法（<c>loader</c> 未注册／卡包自带加载器）＝抛 <see cref="CardLoaderRefusedException"/>（整批不装载）。
    /// </para>
    /// </summary>
    /// <param name="items">卡位清单（可为空＝空批次，产出空报告）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">items 为 null。</exception>
    /// <exception cref="CardLoaderRefusedException">结构性拒绝（loader 未注册／卡包自带加载器）。</exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    public async Task<CardLoadResult> LoadBatchAsync(
        IReadOnlyList<CardLoadItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();

        var outcome = new CardLoadBatchOutcome();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(CardLoadBatchView.Batch)] = items,
            [nameof(CardLoadBatchView.Outcome)] = outcome,
        };

        var ctx = new Context(data);
        var view = ContextViewBinder.Create<CardLoadBatchView>(ctx);
        try
        {
            await HandleBatchAsync(view, ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            ((IContextView)(object)view).Seal();
        }

        if (outcome.Failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return outcome.Result ?? throw new InvalidOperationException(
            $"卡牌加载批次未产出结果（加载器 '{Name}' 的业务段未提交结果——契约违反）。");
    }

    /// <summary>批次执行体（触发器单事件；业务失败经收集器回传，不吞）。</summary>
    private Task HandleBatchAsync(CardLoadBatchView view, Context context, CancellationToken ct)
    {
        var outcome = view.Outcome
            ?? throw new InvalidOperationException("加载批次缺少报告收集器（CardLoadBatchOutcome）。");
        var items = view.Batch ?? Array.Empty<CardLoadItem>();

        try
        {
            outcome.Complete(Execute(items, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 结构性拒绝与内部失败都经收集器回传（调用方按原异常类型抛出，栈信息经 ExceptionDispatchInfo 保真）。
            outcome.Fail(ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>批次主体：结构性预扫 → 逐卡位解析与判定 → 报告与引用列表。</summary>
    private CardLoadResult Execute(IReadOnlyList<CardLoadItem> items, CancellationToken ct)
    {
        // ① 结构性预扫（整批）：卡包元数据的 loader 只能引用进程级已注册名。
        //    卡包自带加载器＝名字不在注册面（注册面本身拒绝 CardPack 来源，见 CardLoaderRegistry.Register）。
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var registration = Resolve(item.AppId);
            if (registration?.Metadata.Loader is not { Length: > 0 } loaderName)
            {
                continue;
            }

            if (!_loaders.Contains(loaderName))
            {
                throw new CardLoaderRefusedException(
                    loaderName,
                    registration.AppId,
                    $"卡 '{registration.AppId}' 声明的加载器 '{loaderName}' 未在进程级注册面声明"
                    + "（卡包不得自带加载器，只能引用插件已注册的加载器）——结构性拒绝，整批不装载。");
            }
        }

        // ② 逐卡位解析与卡级判定（按资产去重：同资产只判定一次、只报一条）。
        var builders = new List<ItemBuilder>();
        var byGuid = new Dictionary<Guid, ItemBuilder>();
        var slots = new List<SlotPlan>(items.Count);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var registration = Resolve(item.AppId);
            if (registration is null)
            {
                slots.Add(SlotPlan.EmptySlot(item));
                continue;
            }

            if (!byGuid.TryGetValue(registration.AssetGuid, out var builder))
            {
                builder = new ItemBuilder(registration, Evaluate(registration));
                byGuid.Add(registration.AssetGuid, builder);
                builders.Add(builder);
            }

            builder.RefCount++;
            slots.Add(new SlotPlan(item, builder));
        }

        // ③ 报告（条目顺序＝首次引用顺序；卡位顺序＝输入顺序）。
        var reportItems = builders
            .Select(builder => new CardLoadReportItem(
                builder.Registration.AssetGuid,
                builder.Registration.AssetName,
                builder.Registration.AppId,
                builder.Registration.Kind,
                builder.Registration.Domain,
                builder.Validity.Tier,
                builder.Validity.Issues,
                builder.RefCount))
            .ToArray();

        var reportSlots = slots
            .Select(slot => slot.ToSlot())
            .ToArray();

        var summary = new CardLoadSummary(
            reportItems.Length,
            reportItems.Count(item => item.Tier == CardLoadTier.Valid),
            reportItems.Count(item => item.Tier == CardLoadTier.Warning),
            reportItems.Count(item => item.Tier == CardLoadTier.Invalid),
            reportItems.Sum(item => item.RefCount));

        var batchId = "batch-" + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
        var report = new CardLoadReport(batchId, reportItems, reportSlots, summary);

        // ④ 卡牌引用列表（非法不生成——非法卡无引用条目）。
        var cards = builders
            .Where(builder => builder.Validity.IsPlayable)
            .Select(builder => new CardReference(
                builder.Registration.AppId,
                builder.Registration.AssetGuid,
                builder.Registration.AssetName,
                builder.Registration.Version,
                builder.Registration.Domain,
                builder.Validity.Tier,
                builder.Validity.Definition!))
            .ToArray();

        // ⑤ 合法卡进卡库：**套件补全后**的定义（不是注册面原始 DTO）——批装载与装载段（Apply）同源，
        //    保证「装载即得可玩定义」。只写定义、不实例化（实例 ID 归对局实例化时序）。
        Install(cards.Select(card => (card.AppId, card.Domain, card.Definition)));

        return new CardLoadResult(report, cards);
    }

    /// <summary>应用层 ID → 注册条目（中间层唯一解析实现；未解析/歧义/未注册＝null）。</summary>
    private AssetRegistration? Resolve(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var resolution = _bridge.Resolve(AssetKind.Card, appId);
        return resolution.Status == IdResolutionStatus.Resolved && resolution.AssetGuid is { } guid
            ? _registry.Find(guid)
            : null;
    }

    /// <summary>
    /// 组装判定输入（源文件原文优先；缺原文＝回退注册面 DTO），并按资产 GUID 记忆判定结果——
    /// 同一资产版本只判一次，装载段（<see cref="Apply"/>）与批次拿到的是**同一个**补全定义实例。
    /// </summary>
    private CardValidityResult Evaluate(AssetRegistration registration)
    {
        if (_evaluated.TryGetValue(registration.AssetGuid, out var cached))
        {
            return cached;
        }

        var hasSidecar = registration.MetadataSource == AssetMetadataSource.Sidecar;

        var result = _suite.Evaluate(new CardValidityInput(registration.AppId)
        {
            MetadataLocalId = hasSidecar ? registration.LocalId : null,
            HasSidecar = hasSidecar,
            InvalidVersion = registration.Metadata.InvalidVersion,
            SourcePath = registration.SourcePath,
            RawJson = ReadSource(registration.SourcePath),
            RegisteredDefinition = registration.Card,
            RegistrationIssues = registration.LoadIssues,
            CardImagePath = _cardImageLookup?.Invoke(registration),
            Description = _descriptionLookup?.Invoke(registration),
            ImageFileExists = path => TryResolvePackageFile(registration, path, out var absolute)
                && File.Exists(absolute),
            EffectReferenceExists = reference => EffectExists(registration, reference),
        });

        _evaluated[registration.AssetGuid] = result;
        return result;
    }

    /// <summary>读卡数据体原文（无源文件／非卡文件／读失败＝null＝退回 DTO 路径）。</summary>
    private static string? ReadSource(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !sourcePath.EndsWith(".card.json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 效果引用是否存在于注册面：裸局部 ID 按**同包上下文**补前缀（I6 ①），全名按原样（I6 ②）。
    /// </summary>
    private bool EffectExists(AssetRegistration registration, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var appId = AssetAppId.IsAppId(reference)
            ? reference
            : AssetAppId.Compose(registration.PackageId, reference);

        // ① 注册面：旧格式 effect 资产（sidecar 声明 kind=effect）。
        if (_registry.ByAppId(appId).Any(asset => asset.Kind == AssetKind.Effect))
        {
            return true;
        }

        // ② v4-only（§23.8 第 1 项）：组件资产没有 sidecar ⇒ 不进注册面，其存在判据是
        //    包内 effects-v4/<localId>.component.json 文件就在。两条判据取并集——
        //    "引用是否解析得到"这件事与"资产以哪种格式承载"无关。
        if (!AssetAppId.TrySplit(appId, out _, out var localId))
        {
            return false;
        }

        var relative = PackageScanner.ComponentsDirectoryName + "/" + localId + ".component.json";
        return TryResolvePackageFile(registration, relative, out var absolute) && File.Exists(absolute);
    }

    /// <summary>包根相对路径解析（卡图路径相对包根——D59）。</summary>
    private static bool TryResolvePackageFile(
        AssetRegistration registration, string relative, out string absolute)
    {
        absolute = relative;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(registration.SourcePath))
        {
            return false;
        }

        // 源文件所在目录 → 逐级上溯到含 manifest.json 的包根（找不到＝用源文件目录）。
        var directory = Path.GetDirectoryName(Path.GetFullPath(registration.SourcePath));
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "manifest.json")))
            {
                absolute = Path.GetFullPath(Path.Combine(directory, relative));
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        var fallback = Path.GetDirectoryName(Path.GetFullPath(registration.SourcePath));
        if (string.IsNullOrEmpty(fallback))
        {
            return false;
        }

        absolute = Path.GetFullPath(Path.Combine(fallback, relative));
        return true;
    }

    // ── IAssetLoadSink（S5 装载动作缝：写入卡库并写入资产域） ──

    /// <summary>
    /// 装载一批条目（层序）：卡资产写进卡库并**写入资产域**（已存在＝替换定义与域）；
    /// 非卡资产（效果/触发器/handler）本批不落地（效果装载归对局装配链）。失败＝抛异常（由加载器回滚）。
    /// <para>
    /// **写入的是套件补全后的定义**（与批次报告同源），不是注册面原始 DTO——否则警报卡会以缺失字段的
    /// 原始 DTO 进卡库、实例化即坏。非法卡（任一非法项）不落条目（D40「非法不生成」）。
    /// </para>
    /// </summary>
    /// <param name="items">条目（层序；内含资产随宿主同层）。</param>
    /// <exception cref="ArgumentNullException">items 为 null。</exception>
    public void Apply(IReadOnlyList<LoadListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var playable = new List<(string AppId, AssetDomain Domain, CardDefinition Definition)>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind != AssetKind.Card)
            {
                continue;
            }

            var validity = Evaluate(item.Asset);
            if (!validity.IsPlayable)
            {
                continue;
            }

            playable.Add((item.AppId, item.Domain, validity.Definition!));
        }

        Install(playable);
    }

    /// <summary>
    /// 卡库落地（装载段与批装载的**唯一写入点**）：已存在＝替换定义与域（测试段遮蔽／重新装载），
    /// 缺失＝追加。只写定义、不实例化。
    /// </summary>
    private void Install(IEnumerable<(string AppId, AssetDomain Domain, CardDefinition Definition)> cards)
    {
        foreach (var (appId, domain, definition) in cards)
        {
            if (_library.Contains(appId))
            {
                _library.Replace(appId, definition, domain);
            }
            else
            {
                _library.Register(appId, definition, domain);
            }
        }
    }

    /// <summary>撤销一批条目（回滚或摘除旧段；幂等——未注册＝无动作）。</summary>
    /// <param name="items">条目。</param>
    /// <exception cref="ArgumentNullException">items 为 null。</exception>
    public void Revert(IReadOnlyList<LoadListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            if (item.Kind == AssetKind.Card)
            {
                _library.Remove(item.AppId);
            }
        }
    }

    /// <summary>报告条目构建器（去重累加引用计数）。</summary>
    private sealed class ItemBuilder
    {
        internal ItemBuilder(AssetRegistration registration, CardValidityResult validity)
        {
            Registration = registration;
            Validity = validity;
        }

        internal AssetRegistration Registration { get; }

        internal CardValidityResult Validity { get; }

        internal int RefCount { get; set; }
    }

    /// <summary>卡位计划（解析失败＝留空位）。</summary>
    private sealed class SlotPlan
    {
        private readonly CardLoadItem _item;
        private readonly ItemBuilder? _builder;

        internal SlotPlan(CardLoadItem item, ItemBuilder builder)
        {
            _item = item;
            _builder = builder;
        }

        private SlotPlan(CardLoadItem item)
        {
            _item = item;
            _builder = null;
        }

        internal static SlotPlan EmptySlot(CardLoadItem item) => new(item);

        internal CardLoadSlot ToSlot()
        {
            if (_builder is null)
            {
                return new CardLoadSlot(
                    PlayerId: _item.PlayerId,
                    Index: _item.Index,
                    AppId: _item.AppId,
                    AssetGuid: null,
                    Tier: CardLoadTier.Invalid,
                    FieldPaths: new[] { "appId" });
            }

            return new CardLoadSlot(
                PlayerId: _item.PlayerId,
                Index: _item.Index,
                AppId: _item.AppId,
                AssetGuid: _builder.Registration.AssetGuid,
                Tier: _builder.Validity.Tier,
                FieldPaths: _builder.Validity.FieldPaths);
        }
    }
}
