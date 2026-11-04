namespace Orc.Core.Model;

/// <summary>
/// 存档 moding（本会话 R1／裁决 1／Q3）：实体存档的**可序列化 DTO**——从其所属预制体出发、
/// 重建该实体所需的全部形态与状态。<b>不进资产库、不需要资产 id</b>（Q3：「引回对应预制体」靠
/// <see cref="Extends"/>，不靠额外身份；存档是对局数据，给它资产身份会污染 lock 与依赖体系）。
/// <para>
/// <b>两张独立的表</b>（裁决 9／D55）：<see cref="Slots"/> 是**配置值**（声明／绑定，
/// 恢复时经 <c>args</c> 注入初值）、<see cref="PrivateFields"/> 是**运行时状态**（当前值，
/// 恢复时在装配前写回覆盖）。允许同名（<c>slots.hp=3</c> 与 <c>privateFields.hp=1</c> 并存是**正常**的），
/// 不合并、不互相兜底；<see cref="PrivateFields"/> **含注入型字段**（表达当前状态，与出身无关）。
/// </para>
/// <para>
/// <b>实体引用</b>（S2）：<c>entity</c>／<c>entity[]</c> 型的私有字段值只支持
/// <see cref="OrchestrationEntity"/>——是 ⇒ 存 <see cref="Entity.Name"/>；不是 ⇒ 存档失败（导出时报字段名与类型）。
/// 不做任意对象序列化。恢复时按 name 重解析（同名即失败、不自动加后缀，裁决 9）。
/// </para>
/// <para>
/// <b>附件</b>（裁决 2／S4）：<see cref="Attachments"/> 是一层**嵌入**的组件 moding（不是引用 id——
/// 组件模板被删也能恢复，与 R39「origin 不是依赖」同口径），**数组顺序即身份与份数**
/// （<c>Grant</c> 允许同 prefab 多份，不按名字去重）；深度恒为 1（组件只能挂宿主，不挂组件）。
/// </para>
/// <para>本 DTO 不带 <c>origin</c>：被 <c>Instantiate</c> 消费后，**产物**照常带 <c>origin</c>
/// （<c>templateId</c> 即 <see cref="Extends"/>）——「声明」与「记录」不重复承载（Q7）。</para>
/// </summary>
public sealed class SaveModing
{
    /// <summary>创建存档 moding。</summary>
    /// <param name="extends">
    /// **结构锚点**（裁决 5／D51）：源预制体 id——<c>entity.Type.Origin?.TemplateId ?? entity.Type.Id</c>。
    /// <b>绝不用缓存键</b>（<c>inline/&lt;id&gt;/&lt;hash&gt;</c> 只存在于对局级表，跨对局必失效）；
    /// named 产物 id 同样不当锚点（它会随资产库变化，锚点恒为 origin 里的 template id）。
    /// </param>
    /// <param name="slots">资产级空位的当前配置值（键＝空位名）。</param>
    /// <param name="privateFields">私有字段当前值（键＝字段名；实体引用已转成实体名）。</param>
    /// <param name="attachments">动态挂载的组件 moding（声明序；深度恒为 1）。</param>
    /// <exception cref="ArgumentException">extends 为空或纯空白。</exception>
    public SaveModing(
        string extends,
        IReadOnlyDictionary<string, object?> slots,
        IReadOnlyDictionary<string, object?> privateFields,
        IReadOnlyList<SaveAttachment> attachments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extends);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(privateFields);
        ArgumentNullException.ThrowIfNull(attachments);

        Extends = extends.Trim();
        Slots = slots;
        PrivateFields = privateFields;
        Attachments = attachments;
    }

    /// <summary>源预制体 id（结构锚点；恢复＝引回它重新展开）。</summary>
    public string Extends { get; }

    /// <summary>资产级空位当前配置值（恢复时经 <c>args</c> 注入初值）。</summary>
    public IReadOnlyDictionary<string, object?> Slots { get; }

    /// <summary>私有字段当前运行时状态（实体引用已转成实体名；恢复时装配前写回、同名时**赢过** <see cref="Slots"/>）。</summary>
    public IReadOnlyDictionary<string, object?> PrivateFields { get; }

    /// <summary>动态挂载的组件 moding（嵌入、声明序；空数组＝无附件）。</summary>
    public IReadOnlyList<SaveAttachment> Attachments { get; }
}

/// <summary>
/// 存档 moding 的一个**嵌入附件**（裁决 2／S4）：宿主 <c>_attachments</c> 里的一份动态组件——
/// 该组件自己的 moding（<c>{ templateId, slots, privateFields }</c>）。
/// <para>
/// 嵌入而非引用 id：组件模板被删也能恢复。数组顺序即身份与份数（同 prefab 多份各自成条，不按名字去重）。
/// 附件的 <c>templateId</c> 与宿主 <see cref="SaveModing.Extends"/> 用**同一公式**
/// （<c>Origin?.TemplateId ?? Type.Id</c>），不另立一套（D52）。
/// </para>
/// </summary>
/// <param name="TemplateId">附件的源预制体 id（同 <see cref="SaveModing.Extends"/> 的公式）。</param>
/// <param name="Slots">附件的资产级空位当前配置值。</param>
/// <param name="PrivateFields">附件的私有字段当前值（实体引用已转成实体名）。</param>
public sealed record SaveAttachment(
    string TemplateId,
    IReadOnlyDictionary<string, object?> Slots,
    IReadOnlyDictionary<string, object?> PrivateFields);
