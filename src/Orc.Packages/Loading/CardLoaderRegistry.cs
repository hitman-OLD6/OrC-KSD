using Orc.Core;
using Orc.Game.Cards.Loading;

namespace Orc.Packages.Loading;

/// <summary>加载器来源（**信任边界的判据**）：只有插件可以声明加载器；卡包自带＝拒绝（D34）。</summary>
public enum CardLoaderSource
{
    /// <summary>插件声明（唯一合法来源；由 <c>EngineHost</c>/插件进程级装配登记）。</summary>
    Plugin = 0,

    /// <summary>卡包自带（**拒绝**：卡包是数据容器，不得携带可执行装配逻辑）。</summary>
    CardPack = 1,
}

/// <summary>
/// 一条加载器声明（进程级、信任边界内）：名称 ＋ 来源插件 ＋ 来源类别。
/// <para>名称是 sidecar <c>loader</c> 字段唯一可引用的标识；不含任何卡实例/对局状态。</para>
/// </summary>
/// <param name="Name">加载器名（非空、进程内唯一；sidecar <c>loader</c> 引用此值）。</param>
/// <param name="PluginId">声明该加载器的插件 id（非空——「谁声明的」必须可追）。</param>
/// <param name="Source">来源类别（卡包自带＝构造即拒绝）。</param>
public sealed record CardLoaderDescriptor(string Name, string PluginId, CardLoaderSource Source)
{
    /// <summary>来源 token（<c>plugin</c>／<c>cardPack</c>；诊断用）。</summary>
    public string SourceToken => Source == CardLoaderSource.Plugin ? "plugin" : "cardPack";
}

/// <summary>
/// **进程级卡牌加载器注册面**（R5/D34；S6 的第一层注册面＝**信任边界**）：
/// 插件声明加载器 → 登记在此；卡包元数据的 <c>loader</c> 字段**只能引用此处已注册的名字**。
/// <para>
/// 硬校验（拒绝即抛、不吞）：
/// <list type="bullet">
///   <item>来源类别为 <see cref="CardLoaderSource.CardPack"/> ⇒ **卡包自带加载器，拒绝**；</item>
///   <item>名称为空/空白、插件 id 为空/空白 ⇒ 拒绝；</item>
///   <item>同名重复声明 ⇒ 拒绝（注册即配置，不做覆盖）。</item>
/// </list>
/// </para>
/// <para>
/// 本面**只管声明**（名称白名单＋来源），不持有对局实例、不参与挂载、不参与事件分发。
/// 对局实际使用的加载器实例由**卡装载链**自己持有（旧的对局级登记簿已随触发器类型删除）——
/// 两层注册面**不得混用**：本面管「谁被允许」，对局面管「本局用了谁」。
/// </para>
/// </summary>
public sealed class CardLoaderRegistry
{
    private readonly Dictionary<string, CardLoaderDescriptor> _byName = new(StringComparer.Ordinal);

    /// <summary>已声明加载器（声明序）。</summary>
    public IReadOnlyList<CardLoaderDescriptor> Entries => _byName.Values.ToArray();

    /// <summary>已声明加载器数。</summary>
    public int Count => _byName.Count;

    /// <summary>
    /// 声明一个加载器（进程级；插件来源唯一合法）。
    /// </summary>
    /// <exception cref="ArgumentNullException">descriptor 为 null。</exception>
    /// <exception cref="ArgumentException">名称/插件 id 为空或空白。</exception>
    /// <exception cref="InvalidOperationException">卡包自带加载器（拒绝）；或同名重复声明。</exception>
    public void Register(CardLoaderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.PluginId);

        if (descriptor.Source != CardLoaderSource.Plugin)
        {
            throw new InvalidOperationException(
                $"加载器 '{descriptor.Name}' 声明来源为 '{descriptor.SourceToken}'——卡包不得自带加载器（D34：只能引用插件已注册的加载器）。");
        }

        if (!_byName.TryAdd(descriptor.Name, descriptor))
        {
            var existing = _byName[descriptor.Name];
            throw new InvalidOperationException(
                $"加载器名 '{descriptor.Name}' 已由插件 '{existing.PluginId}' 声明（重复声明被拒绝）。");
        }
    }

    /// <summary>是否已声明该名（名称白名单查询）。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.ContainsKey(name);
    }

    /// <summary>按名取声明。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    /// <exception cref="KeyNotFoundException">未声明的加载器名。</exception>
    public CardLoaderDescriptor Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException(
                $"加载器名 '{name}' 未注册（sidecar 的 loader 只能引用插件已注册的加载器）。");
    }

    /// <summary>按名取声明（未注册＝false，不抛）。</summary>
    public bool TryResolve(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CardLoaderDescriptor? descriptor)
    {
        descriptor = null;
        return !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out descriptor);
    }
}

/// <summary>
/// **卡牌加载批次的结构性拒绝**（D34/R5）：<c>loader</c> 引用未注册加载器、或卡包自带加载器。
/// 结构性拒绝＝**整批不装载、不生成任何卡**（与卡级「非法不生成、开局继续」严格分离）。
/// </summary>
public sealed class CardLoaderRefusedException : InvalidOperationException
{
    /// <summary>创建拒绝对（原因枚举固定为 <see cref="CardIssueReason.LoaderUnregistered"/>）。</summary>
    /// <param name="loaderName">被引用的加载器名（可空——卡包自带时无合法名）。</param>
    /// <param name="appId">声明该 loader 的资产应用层 ID（可空）。</param>
    /// <param name="message">人读说明。</param>
    public CardLoaderRefusedException(string? loaderName, string? appId, string message)
        : base(message)
    {
        LoaderName = loaderName;
        AppId = appId;
    }

    /// <summary>被引用的加载器名。</summary>
    public string? LoaderName { get; }

    /// <summary>声明该 loader 的资产应用层 ID。</summary>
    public string? AppId { get; }

    /// <summary>原因（结构性；固定 <see cref="CardIssueReason.LoaderUnregistered"/>）。</summary>
    public CardIssueReason Reason => CardIssueReason.LoaderUnregistered;
}
