using Orc.Game.Cards;

namespace Orc.Packages;

/// <summary>
/// **引擎级运行期卡数据目录**（S4·R5/D7·第一级）：宿主持有；注册进此处的卡随 <see cref="EngineHost.CreateSnapshot"/>
/// 冻结进**之后新建**的对局（进行中的对局不受影响）。**只追加不覆盖**（同 id 重复＝拒绝）。
/// <para>
/// S3：底层字典转由 <see cref="AssetRegistry"/> 承载（注册面是唯一字典持有者），
/// 本类型只做**读面签名兼容的转接**——不新增第三套字典、快照语义与冻结时机不变。
/// </para>
/// </summary>
public sealed class RuntimeCardCatalog
{
    private readonly AssetRegistry _registry;

    /// <summary>由资产注册表承载（注册面唯一的运行期卡字典）。</summary>
    /// <exception cref="ArgumentNullException">registry 为 null。</exception>
    public RuntimeCardCatalog(AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>已注册条目（注册序）。</summary>
    public IReadOnlyList<RuntimeCardRegistration> Entries => _registry.RuntimeCards;

    /// <summary>是否已含该 id。</summary>
    /// <exception cref="ArgumentException">id 为 null/空白。</exception>
    public bool Contains(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _registry.ContainsRuntimeCard(id);
    }

    /// <summary>注册一枚卡（只追加）。</summary>
    /// <exception cref="ArgumentNullException">entry 为 null。</exception>
    /// <exception cref="InvalidOperationException">同 id 已注册（拒绝——只追加不覆盖）。</exception>
    public void Register(RuntimeCardRegistration entry) => _registry.RegisterRuntimeCard(entry);

    /// <summary>取快照副本（供对局冻结）。</summary>
    public IReadOnlyList<RuntimeCardRegistration> Snapshot() => _registry.RuntimeCards;
}
