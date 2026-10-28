using Orc.Cards;
using Orc.Core.Model;

namespace Orc.Game.Cards;

/// <summary>
/// 词条组件基类（2C-A1；词条＝独立组件）：自含数据（标识＋可选参值）＋运行逻辑（可选覆写）＋
/// 授予/移除生命周期回调（<see cref="OnGrant"/> / <see cref="OnRevoke"/>）＋**效果组件挂载态**（v4 组件资产）。
/// 语义双轨（仅语义划分、共用同一基类与同一挂载/卸载机制——不建两套框架）：
/// 能力型＝有主动行为面（组件运行逻辑与/或效果组件——如闪击/伏击/奋战）；标记型＝无行为面（仅数据、被读取消费——
/// 如烟幕，经 <see cref="PlainKeywordComponent"/> 轻量形态）。
/// 生命周期（由体系编排、经词条管理组件 <see cref="KeywordManager"/> 驱动；序列裁定见「实现 grill 第 2 批」场 1）：
/// 授予（挂载）＝存在性置位 → 运行逻辑装载（<see cref="Mount"/>）＋效果组件装配 → <see cref="OnGrant"/>（最后）；
/// 装载链内失败＝fail-fast → 逆序整体回滚（存在性回 false、零残留、无半态）。
/// 移除（完整卸载）＝<see cref="OnRevoke"/>（先、行为面先撤）→ 运行逻辑注销（<see cref="Unmount"/>）＋效果组件卸载
/// → 存在性清除（参值不可读）。
/// 死亡注销＝同样触发 OnRevoke 序列（OnRevoke → 运行逻辑注销＋效果组件卸载）、不执行「存在性清除」步
/// （登记/参值保留、照常可读——仅行为撤销）。
/// 效果组件生命周期由体系统一驱动（词条挂载 ⇒ 组件自动装配；词条卸载 ⇒ 组件自动撤销）——
/// 词条作者只负责经 <see cref="AttachComponent"/> 持有句柄、无需手工管理。
/// </summary>
public abstract class KeywordComponent
{
    private readonly List<ComponentAttachment> _attachedComponents = new();

    /// <summary>创建词条组件（标识＋可选参值＝自含数据）。</summary>
    /// <exception cref="ArgumentException">keyword 为 null/空白（标识非空）。</exception>
    protected KeywordComponent(string keyword, int? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        Keyword = keyword;
        Value = value;
    }

    /// <summary>本组件承载的词条标识（与注册面注册键一致——装配时一致性校验）。</summary>
    public string Keyword { get; }

    /// <summary>参值（自含数据；可选——null＝「仅标识」形态；运行时改写经统一读改写口、组件自持该数据的受控变更）。</summary>
    public int? Value { get; private set; }

    /// <summary>
    /// 运行逻辑装载（可选覆写；授予链调用——加载期固有词条与运行时动态授予同一机制）：把词条行为挂到相关更新 hook
    /// （如伏击向「造成攻击伤害」注册改写）。
    /// 宿主＝引擎薄容器 <see cref="Card"/>（W3-A2 加性：词条宿主泛化至 Card——HQ 等非卡实体与单位同族承载；
    /// 与修饰机制 W3-3 泛化先例同构）。
    /// 装载上下文经提供器注入；context 为 null（独立构造场景）＝防御跳过（不抛错、功能不可用）——沿用词条装载上下文先例。
    /// </summary>
    internal virtual void Mount(Card card, KeywordLoadContext? context)
    {
    }

    /// <summary>运行逻辑注销（可选覆写；移除链/死亡注销调用）：注销装载期注册项（幂等；未装载＝无操作）。</summary>
    internal virtual void Unmount()
    {
    }

    /// <summary>
    /// 部署链收尾挂钩（扣费完成后、链返回前；仅部署路径调用）：钳击在此发起同伴选择（可选）；闪击已迁效果承载〔批 3——部署时置位、不再经本钩子〕。
    /// 默认无操作（非该类词条不参与部署链收尾）。宿主＝引擎薄容器 <see cref="Card"/>（A2 泛化）。
    /// </summary>
    internal virtual Task OnDeployChainFinalizedAsync(Card card, CancellationToken ct) => Task.CompletedTask;

    /// <summary>授予回调（作者覆写；挂载链最后一步调用——此时存在性已置位、运行逻辑与内嵌效果均已装载完成）。</summary>
    protected internal virtual void OnGrant()
    {
    }

    /// <summary>移除回调（作者覆写；移除链第一步调用——行为面先撤、数据面后清）。死亡注销同走本序列（不执行存在性清除）。</summary>
    protected internal virtual void OnRevoke()
    {
    }

    /// <summary>
    /// 词条效果组件的挂载态（v4 组件资产形态；框架装载链专用）：词条绑定的 <c>effects-v4</c> 资产在宿主实体上
    /// 装配后的句柄集合——随词条生灭（卸载/死亡注销/授予回滚逐句柄撤销，见 <see cref="DisposeAttachedComponents"/>）。
    /// </summary>
    internal IReadOnlyList<ComponentAttachment> AttachedComponents => _attachedComponents;

    /// <summary>
    /// 组件挂载态接入（v4 形态的「内嵌效果装载」；框架装载链专用）：与 <see cref="AttachDataEffect"/> 同一位置——
    /// 随词条生灭（同一条遍历链：卸载/死亡注销/回滚），但执行面是组件实体的成员边，不是 <c>Effect</c> 实例。
    /// </summary>
    /// <exception cref="ArgumentNullException">attachment 为 null。</exception>
    internal void AttachComponent(ComponentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _attachedComponents.Add(attachment);
    }

    /// <summary>组件挂载态卸载（逐句柄撤销 + 销毁组件实体；幂等——重复调用无残留）。</summary>
    internal void DisposeAttachedComponents()
    {
        for (var i = _attachedComponents.Count - 1; i >= 0; i--)
        {
            _attachedComponents[i].Dispose();
        }

        _attachedComponents.Clear();
    }

    /// <summary>
    /// 参值改写（统一读改写口的受控变更；纯存储改写——A1 不重载行为面；A2 加性：改为 virtual——
    /// 参值域型词条（如重甲/情报：下限 0、封顶 3）在组件侧钳制「写入/增改」）。
    /// </summary>
    internal virtual void OverrideValue(int? value) => Value = value;
}

/// <summary>
/// 轻量词条组件（标记型/纯被动词条的轻量形态）：仅数据、空回调、无主动逻辑——如烟幕（消费方经词条管理组件查询）。
/// </summary>
public class PlainKeywordComponent : KeywordComponent
{
    /// <summary>创建轻量词条组件（标识＋可选参值）。</summary>
    public PlainKeywordComponent(string keyword, int? value = null)
        : base(keyword, value)
    {
    }
}
